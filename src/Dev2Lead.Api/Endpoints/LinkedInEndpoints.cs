using System.Security.Claims;
using Dev2Lead.Api.Services;

namespace Dev2Lead.Api.Endpoints;

public static class LinkedInEndpoints
{
    public static void MapLinkedIn(this WebApplication app)
    {
        app.MapPost("/api/linkedin/connect", async (ClaimsPrincipal user, ILinkedInService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ConnectAsync(ProfileEndpoints.AccountKey(user), ct)))
            .RequireAuthorization().RequireRateLimiting("account")
            .WithSummary("Start consented LinkedIn OpenID Connect authorization for the signed-in Google account.");
        app.MapGet("/api/linkedin/callback", async (HttpRequest request, ILinkedInService service, CancellationToken ct) =>
        {
            var state = request.Query["state"].ToString();
            if (request.Query["state"].Count != 1 || request.Query["code"].Count > 1 || request.Query["error"].Count > 1)
                throw new LinkedInIdentityException();
            await service.CompleteAsync(state, request.Query["code"].ToString(), request.Query.ContainsKey("error"), ct);
            return TypedResults.Text("LinkedIn profile imported. Return to Dev2Lead and click Refresh profile.", "text/plain");
        }).AllowAnonymous().RequireRateLimiting("account")
            .WithSummary("Complete a single-use, state-bound LinkedIn authorization; never return profile data to the browser.");
    }
}
