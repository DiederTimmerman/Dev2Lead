using Dev2Lead.Api.Services;

namespace Dev2Lead.Api.Tests;

internal sealed class TestAccountRepository : IAccountRepository
{
    public Dictionary<string, StoredAccount> Documents { get; } = [];
    public bool TransportUnavailable { get; set; }
    private int version;
    public Task<StoredAccount?> ReadAsync(string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (TransportUnavailable) throw new AccountStorageUnavailableException(new HttpRequestException("Test DNS failure"));
        return Task.FromResult(Documents.GetValueOrDefault(userId));
    }
    public async Task<StoredAccount> GetOrCreateAsync(string userId, CancellationToken ct) =>
        await ReadAsync(userId, ct) ?? await WriteAsync(new AccountDocument { UserId = userId }, null, ct);
    public Task<StoredAccount> WriteAsync(AccountDocument document, string? expectedVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (TransportUnavailable) throw new AccountStorageUnavailableException(new HttpRequestException("Test DNS failure"));
        if (Documents.GetValueOrDefault(document.UserId)?.Version != expectedVersion) throw new ProfileConflictException();
        var saved = new StoredAccount(document, $"\"account-{++version}\"");
        Documents[document.UserId] = saved;
        return Task.FromResult(saved);
    }
}
