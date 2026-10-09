using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Dev2Lead.Core;
using Microsoft.Azure.Cosmos;

namespace Dev2Lead.Api.Services;

public sealed record ProfileDocument
{
    [JsonPropertyName("id")] public string Id { get; init; } = "profile";
    [JsonPropertyName("userId")] public required string UserId { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public required string FileName { get; init; }
    public required string CvText { get; init; }
    public required string BlobName { get; init; }
    public DateTimeOffset UploadedOn { get; init; }
    public string AnalysisStatus { get; init; } = "Pending";
    public DateTimeOffset? AnalysisStartedOn { get; init; }
    public IReadOnlyList<WorkExperience> Experiences { get; init; } = [];
    public IReadOnlyList<SkillValue> Skills { get; init; } = [];
    public IReadOnlyList<string> ObsoleteBlobNames { get; init; } = [];

    [JsonIgnore] public bool HasOriginalCv => !string.IsNullOrEmpty(BlobName);

    public static ProfileDocument Empty(string userId) => new()
    {
        UserId = userId,
        FileName = "",
        CvText = "",
        BlobName = "",
        AnalysisStatus = "Empty"
    };
}

public sealed record StoredProfile(ProfileDocument Document, string Version)
{
    public CareerProfileResponse ToResponse() => new(Document.FileName, Document.CvText, Document.UploadedOn,
        Document.AnalysisStatus, Document.Experiences, Document.Skills, Version);
}

public interface IProfileRepository
{
    Task<StoredProfile?> ReadAsync(string userId, CancellationToken ct);
    Task<StoredProfile> GetOrCreateAsync(string userId, CancellationToken ct);
    Task<StoredProfile> WriteAsync(ProfileDocument profile, string? version, CancellationToken ct);
    Task DeleteAsync(string userId, string version, CancellationToken ct);
}

public sealed class CosmosProfileRepository(CosmosClient client, IConfiguration configuration) : IProfileRepository
{
    private Container Container => client.GetContainer(configuration["Cosmos:Database"]!, configuration["Cosmos:Container"]!);
    public async Task<StoredProfile?> ReadAsync(string userId, CancellationToken ct)
    {
        await Container.ReadContainerAsync(cancellationToken: ct);
        try
        {
            var result = await Container.ReadItemAsync<ProfileDocument>("profile", new PartitionKey(userId), cancellationToken: ct);
            return new(result.Resource, result.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.SubStatusCode == 0)
        {
            // A missing profile is expected; container availability is checked before serving requests.
            return null;
        }
    }

    public async Task<StoredProfile> GetOrCreateAsync(string userId, CancellationToken ct)
    {
        var current = await ReadAsync(userId, ct);
        if (current is not null) return current;

        try
        {
            return await WriteAsync(ProfileDocument.Empty(userId), null, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var raced = await ReadAsync(userId, ct);
            if (raced is not null) return raced;
            throw;
        }
    }

    public async Task<StoredProfile> WriteAsync(ProfileDocument profile, string? version, CancellationToken ct)
    {
        var result = version is null
            ? await Container.CreateItemAsync(profile, new PartitionKey(profile.UserId), cancellationToken: ct)
            : await Container.ReplaceItemAsync(profile, "profile", new PartitionKey(profile.UserId),
                new ItemRequestOptions { IfMatchEtag = version }, ct);
        return new(result.Resource, result.ETag);
    }
    public Task DeleteAsync(string userId, string version, CancellationToken ct) =>
        Container.DeleteItemAsync<ProfileDocument>("profile", new PartitionKey(userId),
            new ItemRequestOptions { IfMatchEtag = version }, ct);
}

public interface ICvBlobStore
{
    Task PutAsync(string name, byte[] content, CancellationToken ct);
    Task<Stream> OpenAsync(string name, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}

public sealed class CvBlobStore(BlobServiceClient service, IConfiguration configuration) : ICvBlobStore
{
    private BlobContainerClient Container => service.GetBlobContainerClient(configuration["Storage:Container"]!);
    public async Task PutAsync(string name, byte[] content, CancellationToken ct)
    {
        using var stream = new MemoryStream(content, writable: false);
        await Container.GetBlobClient(name).UploadAsync(stream,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" } }, ct);
    }
    public Task<Stream> OpenAsync(string name, CancellationToken ct) =>
        Container.GetBlobClient(name).OpenReadAsync(cancellationToken: ct);
    public async Task DeleteAsync(string name, CancellationToken ct) =>
        await Container.GetBlobClient(name).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
}
