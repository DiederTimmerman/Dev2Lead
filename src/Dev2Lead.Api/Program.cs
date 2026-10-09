using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Core;
using Azure.Storage.Blobs;
using Dev2Lead.Api.Endpoints;
using Dev2Lead.Api.Middleware;
using Dev2Lead.Api.Services;
using Dev2Lead.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Azure.Cosmos;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
string Required(string key) => builder.Configuration[key] is { Length: > 0 } value ? value :
    throw new InvalidOperationException($"Configure {key} in backend User Secrets or environment variables.");
var clientId = Required("Google:ClientId");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = "https://accounts.google.com";
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidAudience = clientId,
        ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
        ValidateAudience = true, ValidateIssuer = true, ValidateLifetime = true, RequireSignedTokens = true,
        NameClaimType = "name", ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();
builder.Services.AddExceptionHandler<CloudExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 11 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("account", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        { Status = 429, Title = "Too many requests", Detail = "Wait a minute before retrying." },
            options: null, contentType: "application/problem+json", cancellationToken: ct);
    };
});
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(120) });
builder.Services.AddSingleton<CareerCoach>();
builder.Services.AddSingleton<IGrowthAgent, GrowthAgent>();
builder.Services.AddSingleton<ICvExtractionAgent, CvExtractionAgent>();
var aiSettings = new AzureSettings(Required("AzureOpenAI:Endpoint"), Required("AzureOpenAI:Deployment"), Required("AzureOpenAI:ApiKey"));
aiSettings.Validate();
builder.Services.AddSingleton(aiSettings);
builder.Services.AddSingleton<TokenCredential>(_ => builder.Environment.IsDevelopment()
    ? new AzureCliCredential(new AzureCliCredentialOptions { TenantId = Required("Azure:TenantId") })
    : new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = builder.Configuration["Azure:TenantId"] }));
builder.Services.AddSingleton(services =>
{
    var endpoint = Required("Cosmos:Endpoint");
    var options = new CosmosClientOptions
    {
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    };
    return builder.Configuration["Cosmos:Key"] is { Length: > 0 } key
        ? new CosmosClient(endpoint, key, options)
        : new CosmosClient(endpoint, services.GetRequiredService<TokenCredential>(), options);
});
builder.Services.AddSingleton(_ => new BlobServiceClient(Required("Storage:ConnectionString")));
builder.Services.AddSingleton<IProfileRepository, CosmosProfileRepository>();
builder.Services.AddSingleton<IAccountRepository, CosmosAccountRepository>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddSingleton<ILinkedInIdentityClient, LinkedInIdentityClient>();
builder.Services.AddScoped<ILinkedInService, LinkedInService>();
builder.Services.AddSingleton<ICvBlobStore, CvBlobStore>();
builder.Services.AddScoped<IProfileService, ProfileService>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", (CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return TypedResults.Ok(new { status = "running" }); })
    .WithSummary("Check that the API process is running; this does not verify storage availability.");
app.MapProfile();
app.MapAccountSettings();
app.MapLinkedIn();
app.Run();

public partial class Program;
