using Dev2Lead.Core;

namespace Dev2Lead.Api.Services;

public enum ConsentPurpose { CvStorage, CvAnalysis, CareerChat, LinkedInImport }
public sealed class ConsentRequiredException : Exception;

public interface IAccountService
{
    Task<AccountSettingsResponse> GetAsync(string userId, CancellationToken ct);
    Task<AccountSettingsResponse> SaveAsync(string userId, UpdateAccountSettingsRequest request, CancellationToken ct);
    Task RequireConsentAsync(string userId, ConsentPurpose purpose, CancellationToken ct);
    Task<AccountSettingsResponse> RemoveLinkedInAsync(string userId, CancellationToken ct);
}

public sealed class AccountService(IAccountRepository repository) : IAccountService
{
    public async Task<AccountSettingsResponse> GetAsync(string userId, CancellationToken ct) =>
        (await repository.GetOrCreateAsync(userId, ct)).ToResponse();

    public async Task<AccountSettingsResponse> SaveAsync(string userId, UpdateAccountSettingsRequest request, CancellationToken ct)
    {
        if (request.TranscriptUrl is null || request.Consents is null) throw new InvalidDataException("Supply account preferences.");
        var transcript = request.TranscriptUrl.Trim();
        if (transcript.Length > 0)
        {
            try { transcript = LearnTranscriptService.ValidateSource(transcript).AbsoluteUri; }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Supply a valid public Microsoft Learn transcript URL.", ex); }
        }
        var current = await repository.ReadAsync(userId, ct);
        if (current?.Version != request.Version) throw new ProfileConflictException();
        var document = current?.Document ?? new AccountDocument { UserId = userId };
        return (await repository.WriteAsync(document with
        {
            TranscriptUrl = transcript, Consents = request.Consents,
            Authorization = request.Consents.LinkedInImport ? document.Authorization : null,
            LinkedInStatus = !request.Consents.LinkedInImport && document.LinkedInStatus is "Awaiting authorization" or "Importing"
                ? "Cancelled" : document.LinkedInStatus
        }, current?.Version, ct)).ToResponse();
    }

    public async Task RequireConsentAsync(string userId, ConsentPurpose purpose, CancellationToken ct)
    {
        var consents = (await GetAsync(userId, ct)).Consents;
        var allowed = purpose switch
        {
            ConsentPurpose.CvStorage => consents.CvStorage,
            ConsentPurpose.CvAnalysis => consents.CvAnalysis,
            ConsentPurpose.CareerChat => consents.CareerChat,
            ConsentPurpose.LinkedInImport => consents.LinkedInImport,
            _ => false
        };
        if (!allowed) throw new ConsentRequiredException();
    }

    public async Task<AccountSettingsResponse> RemoveLinkedInAsync(string userId, CancellationToken ct)
    {
        var current = await repository.ReadAsync(userId, ct) ?? throw new ProfileMissingException();
        return (await repository.WriteAsync(current.Document with
        { LinkedIn = null, Authorization = null, LinkedInStatus = "Not connected" }, current.Version, ct)).ToResponse();
    }
}
