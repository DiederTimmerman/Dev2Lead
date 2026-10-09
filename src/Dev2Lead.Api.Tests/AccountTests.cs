using Dev2Lead.Api.Services;
using Dev2Lead.Core;
using Xunit;

namespace Dev2Lead.Api.Tests;

public sealed class AccountTests
{
    private const string Transcript = "https://learn.microsoft.com/en-us/users/diedertimmerman/transcript/dl9e1cq3814l0xe";

    [Fact]
    public async Task NewAccountHasNoImplicitConsentOrGlobalTranscript()
    {
        var repository = new TestAccountRepository();
        var service = new AccountService(repository);
        var account = await service.GetAsync("alice", default);
        Assert.Equal("", account.TranscriptUrl);
        Assert.Equal(new AccountConsents(), account.Consents);
        Assert.NotNull(account.Version);
        Assert.Contains("alice", repository.Documents.Keys);
        await Assert.ThrowsAsync<ConsentRequiredException>(() => service.RequireConsentAsync("alice", ConsentPurpose.CvStorage, default));
    }

    [Fact]
    public async Task TranscriptAndConsentsPersistWithoutCvAndStayUserScoped()
    {
        var repository = new TestAccountRepository();
        var service = new AccountService(repository);
        var saved = await service.SaveAsync("alice", new(Transcript, new(true, false, true, false), null), default);
        Assert.Equal(Transcript, (await new AccountService(repository).GetAsync("alice", default)).TranscriptUrl);
        Assert.True(saved.Consents.CvStorage);
        Assert.False(saved.Consents.CvAnalysis);
        Assert.Equal("", (await service.GetAsync("bob", default)).TranscriptUrl);
        await service.RequireConsentAsync("alice", ConsentPurpose.CareerChat, default);
        await Assert.ThrowsAsync<ConsentRequiredException>(() => service.RequireConsentAsync("bob", ConsentPurpose.CareerChat, default));
    }

    [Fact]
    public async Task RevocationIsCheckedServerSideForFutureRequests()
    {
        var service = new AccountService(new TestAccountRepository());
        var saved = await service.SaveAsync("alice", new("", new(true, true, true, true), null), default);
        await service.SaveAsync("alice", new("", new(), saved.Version), default);
        foreach (var purpose in Enum.GetValues<ConsentPurpose>())
            await Assert.ThrowsAsync<ConsentRequiredException>(() => service.RequireConsentAsync("alice", purpose, default));
    }

    [Fact]
    public async Task StalePreferencesCannotOverwriteOtherChanges()
    {
        var service = new AccountService(new TestAccountRepository());
        var saved = await service.SaveAsync("alice", new(Transcript, new(), null), default);
        await service.SaveAsync("alice", new(Transcript, new(true), saved.Version), default);
        await Assert.ThrowsAsync<ProfileConflictException>(() => service.SaveAsync("alice", new("", new(), saved.Version), default));
        Assert.Equal(Transcript, (await service.GetAsync("alice", default)).TranscriptUrl);
    }

    [Theory]
    [InlineData("https://example.com/transcript")]
    [InlineData("not a url")]
    public async Task InvalidTranscriptUrlIsRejected(string value)
    {
        var service = new AccountService(new TestAccountRepository());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveAsync("alice", new(value, new(), null), default));
    }
}
