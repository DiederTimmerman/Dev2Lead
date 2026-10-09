using System.Net;
using Azure;
using Dev2Lead.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;

namespace Dev2Lead.Api.Middleware;

public sealed class CloudExceptionHandler(ILogger<CloudExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, detail) = exception switch
        {
            AccountStorageUnavailableException => (503, "Account permissions could not be loaded or saved because Cosmos DB is unreachable. Check the API's Cosmos endpoint, DNS/network access and account configuration, then retry."),
            ConsentRequiredException => (403, "Enable and save the required permission in account settings before continuing."),
            LinkedInConfigurationException => (503, "The backend's LinkedIn client or registered callback configuration is missing or invalid."),
            LinkedInIdentityException => (422, "LinkedIn authorization was denied, expired, invalid, or rejected. Restart import and check the app's OpenID Connect product and registered callback."),
            ProfileMissingException => (404, "No CV is stored for this account."),
            ProfileConflictException => (409, "The profile changed or is being processed. Refresh it before retrying."),
            CosmosException ex when ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict =>
                (409, "Another device changed your profile. Refresh before saving again."),
            InvalidDataException => (422, "The CV or extracted profile is invalid. Check the file, input values, or retry AI extraction."),
            System.Text.Json.JsonException or KeyNotFoundException => (502, "The AI returned an unexpected response. Your saved CV remains available; retry extraction."),
            UglyToad.PdfPig.Core.PdfDocumentFormatException or System.Xml.XmlException => (422, "This document is unreadable. Upload a text-based PDF, DOCX, or TXT."),
            UnauthorizedAccessException => (401, "Sign in with Google to continue."),
            CosmosException or RequestFailedException => (503, "Cloud storage is unavailable. Check backend credentials, permissions, and database/container setup."),
            HttpRequestException => (502, "The AI service could not complete the request. Your saved CV remains available for retry."),
            OperationCanceledException when !context.RequestAborted.IsCancellationRequested => (504, "Processing timed out. Refresh your profile and retry."),
            _ => (500, "The backend could not complete the request. Contact the operator with the request trace ID.")
        };
        logger.LogError("Backend request failed with {ExceptionType}; status {Status}; trace {TraceId}", exception.GetType().Name, status, context.TraceIdentifier);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        { Status = status, Title = "Dev2Lead request failed", Detail = detail, Extensions = { ["traceId"] = context.TraceIdentifier } },
            options: null, contentType: "application/problem+json", cancellationToken: ct);
        return true;
    }
}
