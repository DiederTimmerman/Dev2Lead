using System.Net;
using System.Text.Json.Serialization;
using Dev2Lead.Core;
using Microsoft.Azure.Cosmos;

namespace Dev2Lead.Api.Services;

public sealed record LinkedInAuthorization(string StateHash, string Nonce, DateTimeOffset ExpiresOn);

public sealed record AccountDocument
{
    [JsonPropertyName("id")] public string Id { get; init; } = "account";
    [JsonPropertyName("userId")] public required string UserId { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public string TranscriptUrl { get; init; } = "";
    public AccountConsents Consents { get; init; } = new();
    public LinkedInProfile? LinkedIn { get; init; }
    public string LinkedInStatus { get; init; } = "Not connected";
    public LinkedInAuthorization? Authorization { get; init; }
}

public sealed record StoredAccount(AccountDocument Document, string Version)
{
    public AccountSettingsResponse ToResponse() => new(Document.TranscriptUrl, Document.Consents, Document.LinkedIn,
        Document.LinkedInStatus, Version);
}

public interface IAccountRepository
{
    Task<StoredAccount?> ReadAsync(string userId, CancellationToken ct);
    Task<StoredAccount> GetOrCreateAsync(string userId, CancellationToken ct);
    Task<StoredAccount> WriteAsync(AccountDocument document, string? version, CancellationToken ct);
}

public sealed class AccountStorageUnavailableException(Exception inner)
    : Exception("Account storage could not be reached.", inner);

public sealed class CosmosAccountRepository(CosmosClient client, IConfiguration configuration) : IAccountRepository
{
    private Container Container => client.GetContainer(configuration["Cosmos:Database"]!, configuration["Cosmos:Container"]!);
    public Task<StoredAccount?> ReadAsync(string userId, CancellationToken ct) =>
        HandleTransportAsync(() => ReadCoreAsync(userId, ct));

    public async Task<StoredAccount> GetOrCreateAsync(string userId, CancellationToken ct)
    {
        var current = await ReadAsync(userId, ct);
        if (current is not null) return current;

        try
        {
            return await WriteAsync(new AccountDocument { UserId = userId }, null, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var raced = await ReadAsync(userId, ct);
            if (raced is not null) return raced;
            throw;
        }
    }

    private async Task<StoredAccount?> ReadCoreAsync(string userId, CancellationToken ct)
    {
        await Container.ReadContainerAsync(cancellationToken: ct);
        try
        {
            var result = await Container.ReadItemAsync<AccountDocument>("account", new PartitionKey(userId), cancellationToken: ct);
            return new(result.Resource, result.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.SubStatusCode == 0) { return null; }
    }
    public Task<StoredAccount> WriteAsync(AccountDocument document, string? version, CancellationToken ct) =>
        HandleTransportAsync<StoredAccount>(async () =>
    {
        var result = version is null
            ? await Container.CreateItemAsync(document, new PartitionKey(document.UserId), cancellationToken: ct)
            : await Container.ReplaceItemAsync(document, "account", new PartitionKey(document.UserId),
                new ItemRequestOptions { IfMatchEtag = version }, ct);
        return new(result.Resource, result.ETag);
    });

    private static async Task<T> HandleTransportAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (HttpRequestException ex) { throw new AccountStorageUnavailableException(ex); }
    }
}
