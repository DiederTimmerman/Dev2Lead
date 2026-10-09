using System.Security.Claims;
using Dev2Lead.Api.Services;
using Dev2Lead.Core;

namespace Dev2Lead.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountSettings(this WebApplication app)
    {
        var api = app.MapGroup("/api/account").RequireAuthorization().RequireRateLimiting("account");
        api.MapGet("/settings", async (ClaimsPrincipal user, IAccountService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetAsync(ProfileEndpoints.AccountKey(user), ct)))
            .WithSummary("Read the signed-in user's transcript link, saved consent, and imported LinkedIn identity.");
        api.MapPut("/settings", async (UpdateAccountSettingsRequest request, ClaimsPrincipal user, IAccountService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SaveAsync(ProfileEndpoints.AccountKey(user), request, ct)))
            .WithSummary("Save account preferences without requiring an uploaded CV; all consent defaults to false.");
        api.MapDelete("/linkedin", async (ClaimsPrincipal user, IAccountService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RemoveLinkedInAsync(ProfileEndpoints.AccountKey(user), ct)))
            .WithSummary("Remove stored LinkedIn identity and invalidate any pending import.");
    }
}
