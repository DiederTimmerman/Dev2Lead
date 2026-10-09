using System.Net;
using Dev2Lead.Core;
using Microsoft.Azure.Cosmos;

namespace Dev2Lead.Api.Services;

public interface IProfileService
{
    Task<CareerProfileResponse?> GetAsync(string userId, CancellationToken ct);
    Task<CareerProfileResponse> UploadAsync(string userId, string name, byte[] content, string? version, CancellationToken ct);
    Task<CareerProfileResponse> AnalyzeAsync(string userId, AnalyzeProfileRequest request, CancellationToken ct);
    Task<CareerProfileResponse> SaveSkillsAsync(string userId, UpdateSkillsRequest request, CancellationToken ct);
    Task DeleteAsync(string userId, string version, CancellationToken ct);
    Task<(Stream Stream, string Name)> OpenOriginalAsync(string userId, CancellationToken ct);
    Task<CoachReply> ChatAsync(string userId, ChatProfileRequest request, CancellationToken ct);
    Task<CoachReply> GrowthChatAsync(string userId, GrowthChatRequest request, CancellationToken ct);
    Task<GapAnalysisResponse> GapAsync(string userId, GapAnalysisRequest request, CancellationToken ct);
}

public sealed class ProfileConflictException : Exception;
public sealed class ProfileMissingException : Exception;

public sealed class ProfileService(IProfileRepository repository, ICvBlobStore blobs, ICvExtractionAgent agent,
    CareerCoach coach, AzureSettings settings, ILogger<ProfileService> logger, IAccountService accounts,
    IGrowthAgent growth) : IProfileService
{
    public async Task<CareerProfileResponse?> GetAsync(string userId, CancellationToken ct)
    {
        var profile = await repository.GetOrCreateAsync(userId, ct);
        return profile.Document.HasOriginalCv ? profile.ToResponse() : null;
    }

    private async Task<StoredProfile> RequireAsync(string userId, CancellationToken ct)
    {
        var profile = await repository.ReadAsync(userId, ct);
        if (profile is null || !profile.Document.HasOriginalCv) throw new ProfileMissingException();
        return profile;
    }

    private static void Match(StoredProfile? current, string? version)
    {
        if (current?.Version != version || (current is not null && string.IsNullOrWhiteSpace(version)))
            throw new ProfileConflictException();
    }

    private static bool ActiveAnalysis(ProfileDocument profile) =>
        profile.AnalysisStatus == "Analyzing" && profile.AnalysisStartedOn > DateTimeOffset.UtcNow.AddMinutes(-5);

    public async Task<CareerProfileResponse> UploadAsync(string userId, string name, byte[] content, string? version, CancellationToken ct)
    {
        await accounts.RequireConsentAsync(userId, ConsentPurpose.CvStorage, ct);
        if (content.Length is 0 or > CvReader.MaxBytes || string.IsNullOrWhiteSpace(name) || name.Length > 250
            || Path.GetFileName(name) != name || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl))
            throw new InvalidDataException("Choose a PDF, DOCX or TXT CV smaller than 10 MB.");
        using var stream = new MemoryStream(content, writable: false);
        var text = await CvReader.ReadAsync(name, stream);
        var stored = await repository.ReadAsync(userId, ct);
        var current = stored?.Document.HasOriginalCv == true ? stored : null;
        Match(current, string.IsNullOrEmpty(version) ? null : version);
        if (current?.Document.AnalysisStatus == "Deleting") throw new ProfileConflictException();
        if (current is not null && ActiveAnalysis(current.Document))
            throw new ProfileConflictException();
        var blobName = $"{userId}/{Guid.NewGuid():N}";
        await blobs.PutAsync(blobName, content, ct);
        StoredProfile saved;
        try
        {
            saved = await repository.WriteAsync(new ProfileDocument
            {
                UserId = userId, FileName = name, CvText = text, BlobName = blobName, UploadedOn = DateTimeOffset.UtcNow,
                ObsoleteBlobNames = current is null ? [] : [.. current.Document.ObsoleteBlobNames, current.Document.BlobName]
            }, stored?.Version, ct);
        }
        catch (Exception ex) when (ex is CosmosException or ProfileConflictException or OperationCanceledException)
        {
            logger.LogWarning("CV metadata save failed; removing the newly uploaded original");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await blobs.DeleteAsync(blobName, cleanup.Token);
            throw;
        }
        if (current is not null)
        {
            foreach (var obsolete in saved.Document.ObsoleteBlobNames) await blobs.DeleteAsync(obsolete, ct);
            saved = await repository.WriteAsync(saved.Document with { ObsoleteBlobNames = [] }, saved.Version, ct);
        }
        return saved.ToResponse();
    }

    public async Task<CareerProfileResponse> AnalyzeAsync(string userId, AnalyzeProfileRequest request, CancellationToken ct)
    {
        if (!request.Consent) throw new InvalidDataException("Explicit permission to process your CV with AI is required.");
        await accounts.RequireConsentAsync(userId, ConsentPurpose.CvAnalysis, ct);
        var current = await RequireAsync(userId, ct);
        Match(current, request.Version);
        if (current.Document.AnalysisStatus == "Deleting") throw new ProfileConflictException();
        if (ActiveAnalysis(current.Document))
            throw new ProfileConflictException();
        var analyzing = await repository.WriteAsync(current.Document with
        {
            AnalysisStatus = "Analyzing", AnalysisStartedOn = DateTimeOffset.UtcNow
        }, current.Version, ct);
        CvAnalysis analysis;
        try { analysis = await agent.ExtractAsync(settings, current.Document.CvText, ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException
            or KeyNotFoundException or OperationCanceledException)
        {
            logger.LogWarning("CV extraction failed; the saved original remains available for retry");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await repository.WriteAsync(analyzing.Document with { AnalysisStatus = "Failed" }, analyzing.Version, cleanup.Token);
            throw;
        }
        return (await repository.WriteAsync(analyzing.Document with
        {
            AnalysisStatus = "Ready", Experiences = analysis.Experiences, Skills = analysis.Skills.Select(SkillValue.From).ToList()
        }, analyzing.Version, ct)).ToResponse();
    }

    public async Task<CareerProfileResponse> SaveSkillsAsync(string userId, UpdateSkillsRequest request, CancellationToken ct)
    {
        if (request.Skills is null || request.Skills.Count > 60 || request.Skills.Any(s => s is null))
            throw new InvalidDataException("Supply up to 60 valid reviewed skills.");
        SkillProfile.Validate(request.Skills.Select(s => s.ToAssessment()).ToList(), requireReviewed: true);
        var current = await RequireAsync(userId, ct);
        Match(current, request.Version);
        if (ActiveAnalysis(current.Document) || current.Document.AnalysisStatus == "Deleting") throw new ProfileConflictException();
        return (await repository.WriteAsync(current.Document with
        {
            Skills = request.Skills,
            AnalysisStatus = current.Document.AnalysisStatus == "Analyzing" ? "Failed" : current.Document.AnalysisStatus
        }, current.Version, ct)).ToResponse();
    }

    public async Task DeleteAsync(string userId, string version, CancellationToken ct)
    {
        var current = await RequireAsync(userId, ct);
        Match(current, version);
        if (ActiveAnalysis(current.Document)) throw new ProfileConflictException();
        var deleting = await repository.WriteAsync(current.Document with { AnalysisStatus = "Deleting" }, current.Version, ct);
        foreach (var obsolete in deleting.Document.ObsoleteBlobNames) await blobs.DeleteAsync(obsolete, ct);
        await blobs.DeleteAsync(current.Document.BlobName, ct);
        await repository.DeleteAsync(userId, deleting.Version, ct);
    }

    public async Task<(Stream Stream, string Name)> OpenOriginalAsync(string userId, CancellationToken ct)
    {
        var current = await RequireAsync(userId, ct);
        if (current.Document.AnalysisStatus == "Deleting") throw new ProfileConflictException();
        return (await blobs.OpenAsync(current.Document.BlobName, ct), current.Document.FileName);
    }

    public async Task<CoachReply> ChatAsync(string userId, ChatProfileRequest request, CancellationToken ct)
    {
        await accounts.RequireConsentAsync(userId, ConsentPurpose.CareerChat, ct);
        if (!request.Consent || request.Messages is null || request.Messages.Count > 24
            || request.Messages.Any(m => m is null || m.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(m.Content) || m.Content.Length > 6000))
            throw new InvalidDataException("Provide consent and up to 24 valid conversation messages.");
        var current = await RequireAsync(userId, ct);
        if (current.Document.AnalysisStatus == "Deleting") throw new ProfileConflictException();
        return await coach.AskAsync(settings, current.Document.CvText, request.Messages, request.GenerateRoadmap, ct);
    }

    private async Task<StoredProfile> GrowthProfileAsync(string userId, string version, CancellationToken ct)
    {
        await accounts.RequireConsentAsync(userId, ConsentPurpose.CareerChat, ct);
        var current = await RequireAsync(userId, ct);
        Match(current, version);
        if (current.Document.AnalysisStatus == "Deleting" || ActiveAnalysis(current.Document))
            throw new ProfileConflictException();
        return current;
    }

    public async Task<CoachReply> GrowthChatAsync(string userId, GrowthChatRequest request, CancellationToken ct)
    {
        GrowthCoaches.Find(request.CoachId);
        GrowthCoaches.ValidateMessages(request.Messages);
        var current = await GrowthProfileAsync(userId, request.Version, ct);
        return await growth.ChatAsync(settings, current.Document.CvText,
            current.Document.Skills.Select(s => s.ToAssessment()).ToList(), request.CoachId, request.Messages, ct);
    }

    public async Task<GapAnalysisResponse> GapAsync(string userId, GapAnalysisRequest request, CancellationToken ct)
    {
        GrowthAgent.ValidateTarget(request.TargetRole);
        var current = await GrowthProfileAsync(userId, request.Version, ct);
        return await growth.AnalyzeAsync(settings, current.Document.CvText,
            current.Document.Skills.Select(s => s.ToAssessment()).ToList(), request.TargetRole, ct);
    }
}
