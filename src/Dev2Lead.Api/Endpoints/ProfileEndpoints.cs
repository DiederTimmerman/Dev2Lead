using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dev2Lead.Api.Services;
using Dev2Lead.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Dev2Lead.Api.Endpoints;

public static class ProfileEndpoints
{
    public static string AccountKey(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject) || !principal.Identity!.IsAuthenticated)
            throw new UnauthorizedAccessException("A validated Google identity is required.");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("google:" + subject)));
    }

    public static void MapProfile(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("account");
        api.MapGet("/account", (ClaimsPrincipal user, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return TypedResults.Ok(new AccountResponse(user.FindFirstValue("name") ?? "Google user", user.FindFirstValue("email") ?? ""));
        }).WithSummary("Get the verified Google account identity.");

        api.MapGet("/profile", async Task<Results<Ok<CareerProfileResponse>, NotFound>> (
            ClaimsPrincipal user, IProfileService service, CancellationToken ct) =>
        {
            var profile = await service.GetAsync(AccountKey(user), ct);
            return profile is null ? TypedResults.NotFound() : TypedResults.Ok(profile);
        }).WithSummary("Get only the authenticated user's current stored profile.");

        api.MapPost("/profile/cv", async Task<Created<CareerProfileResponse>> (
            IFormFile file, [FromForm] bool consent, [FromForm] string? version,
            ClaimsPrincipal user, IProfileService service, CancellationToken ct) =>
        {
            if (!consent) throw new InvalidDataException("Consent to private cloud CV storage is required.");
            if (file.Length is 0 or > CvReader.MaxBytes) throw new InvalidDataException("The CV must be smaller than 10 MB.");
            await using var input = file.OpenReadStream();
            using var buffer = new MemoryStream();
            var bytes = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(bytes, ct)) > 0)
            {
                if (buffer.Length + count > CvReader.MaxBytes) throw new InvalidDataException("The CV must be smaller than 10 MB.");
                await buffer.WriteAsync(bytes.AsMemory(0, count), ct);
            }
            var profile = await service.UploadAsync(AccountKey(user), file.FileName, buffer.ToArray(), version, ct);
            return TypedResults.Created("/api/profile", profile);
        }).DisableAntiforgery().WithSummary("Store a CV privately in Blob Storage and readable text in Cosmos DB.")
            .WithDescription("Bearer authentication only; consent is required. Supply version for replacement. AI processing is a separate consented action.");

        api.MapPost("/profile/analyze", async (AnalyzeProfileRequest request, ClaimsPrincipal user,
            IProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AnalyzeAsync(AccountKey(user), request, ct)))
            .WithSummary("Extract work experience and initial skill ratings/years, and save them to Cosmos DB.");

        api.MapPut("/profile/skills", async (UpdateSkillsRequest request, ClaimsPrincipal user,
            IProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SaveSkillsAsync(AccountKey(user), request, ct)))
            .WithSummary("Save reviewed skill edits with optimistic concurrency.");

        api.MapDelete("/profile", async (HttpRequest request, ClaimsPrincipal user,
            IProfileService service, CancellationToken ct) =>
        {
            var version = request.Headers.IfMatch.ToString();
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("Supply the current profile version in If-Match.");
            await service.DeleteAsync(AccountKey(user), version, ct);
            return TypedResults.NoContent();
        }).WithSummary("Delete the authenticated user's stored profile and private original CV.");

        api.MapGet("/profile/cv/original", async (ClaimsPrincipal user, IProfileService service, CancellationToken ct) =>
        {
            var file = await service.OpenOriginalAsync(AccountKey(user), ct);
            return TypedResults.File(file.Stream, "application/octet-stream", file.Name);
        }).WithSummary("Download the original CV through authentication; no public Blob URL is exposed.");

        api.MapPost("/profile/chat", async (ChatProfileRequest request, ClaimsPrincipal user,
            IProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ChatAsync(AccountKey(user), request, ct)))
            .WithSummary("Coach the authenticated user using their stored CV and backend AI configuration.");

        api.MapPost("/profile/coaches/chat", async (GrowthChatRequest request, ClaimsPrincipal user,
            IProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GrowthChatAsync(AccountKey(user), request, ct)))
            .WithSummary("Practice with a specialist growth coach using saved skills and career coaching consent.");

        api.MapPost("/profile/gap-analysis", async (GapAnalysisRequest request, ClaimsPrincipal user,
            IProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GapAsync(AccountKey(user), request, ct)))
            .WithSummary("Generate advisory target competencies and calculate an evidence-based gap score.");
    }
}
