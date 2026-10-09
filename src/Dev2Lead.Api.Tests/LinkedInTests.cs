using System.Security.Claims;
using Dev2Lead.Api.Endpoints;
using Dev2Lead.Api.Services;
using Dev2Lead.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dev2Lead.Api.Tests;

public sealed class LinkedInTests
{
    private static string User(string subject) => ProfileEndpoints.AccountKey(
        new ClaimsPrincipal(new ClaimsIdentity([new("sub", subject)], "test")));

    [Fact]
    public async Task ConnectRequiresSavedPermission()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<ConsentRequiredException>(() => fixture.LinkedIn.ConnectAsync(User("alice"), default));
        Assert.Equal(0, fixture.Identity.Imports);
    }

    [Fact]
    public async Task AuthorizedBasicIdentityIsStoredForOnlyTheGoogleOwner()
    {
        var fixture = new Fixture();
        var state = await fixture.Start("alice");
        await fixture.LinkedIn.CompleteAsync(state, "authorization-code", false, default);
        var account = await fixture.Accounts.GetAsync(User("alice"), default);
        Assert.Equal("Imported", account.LinkedInStatus);
        Assert.Equal("LinkedIn Example", account.LinkedIn!.Name);
        Assert.Null((await fixture.Accounts.GetAsync(User("bob"), default)).LinkedIn);
        Assert.Null(fixture.Repository.Documents[User("alice")].Document.Authorization);
        Assert.Equal(1, fixture.Identity.Imports);
    }

    [Fact]
    public async Task CallbackCannotBeReplayed()
    {
        var fixture = new Fixture();
        var state = await fixture.Start("alice");
        await fixture.LinkedIn.CompleteAsync(state, "code", false, default);
        await Assert.ThrowsAsync<LinkedInIdentityException>(() => fixture.LinkedIn.CompleteAsync(state, "code", false, default));
        Assert.Equal(1, fixture.Identity.Imports);
    }

    [Fact]
    public async Task StateCannotBeMovedToAnotherGoogleAccount()
    {
        var fixture = new Fixture();
        var state = await fixture.Start("alice");
        await fixture.Start("bob");
        var other = User("bob") + "." + state.Split('.')[1];
        await Assert.ThrowsAsync<LinkedInIdentityException>(() => fixture.LinkedIn.CompleteAsync(other, "code", false, default));
        Assert.Equal(0, fixture.Identity.Imports);
    }

    [Fact]
    public async Task ExpiredStateCannotImport()
    {
        var fixture = new Fixture();
        var state = await fixture.Start("alice");
        var current = fixture.Repository.Documents[User("alice")];
        await fixture.Repository.WriteAsync(current.Document with
        { Authorization = current.Document.Authorization! with { ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1) } }, current.Version, default);
        await Assert.ThrowsAsync<LinkedInIdentityException>(() => fixture.LinkedIn.CompleteAsync(state, "code", false, default));
        Assert.Equal(0, fixture.Identity.Imports);
    }

    [Fact]
    public async Task RevokedConsentCancelsPendingAuthorization()
    {
        var fixture = new Fixture();
        var state = await fixture.Start("alice");
        var saved = await fixture.Accounts.GetAsync(User("alice"), default);
        await fixture.Accounts.SaveAsync(User("alice"), new("", new(), saved.Version), default);
        await Assert.ThrowsAsync<LinkedInIdentityException>(() => fixture.LinkedIn.CompleteAsync(state, "code", false, default));
        Assert.Equal("Cancelled", (await fixture.Accounts.GetAsync(User("alice"), default)).LinkedInStatus);
    }

    [Fact]
    public async Task DenialIsExplicitAndDoesNotSaveAnIdentity()
    {
        var fixture = new Fixture();
        var state = await fixture.Start("alice");
        await Assert.ThrowsAsync<LinkedInIdentityException>(() => fixture.LinkedIn.CompleteAsync(state, null, true, default));
        var saved = await fixture.Accounts.GetAsync(User("alice"), default);
        Assert.Equal("Cancelled", saved.LinkedInStatus);
        Assert.Null(saved.LinkedIn);
    }

    [Fact]
    public async Task RemovingImportedIdentityPreservesAccountSettings()
    {
        var fixture = new Fixture();
        var state = await fixture.Start("alice");
        await fixture.LinkedIn.CompleteAsync(state, "code", false, default);
        var saved = await fixture.Accounts.RemoveLinkedInAsync(User("alice"), default);
        Assert.Null(saved.LinkedIn);
        Assert.True(saved.Consents.LinkedInImport);
    }

    private sealed class Fixture
    {
        public TestAccountRepository Repository { get; } = new();
        public AccountService Accounts { get; }
        public Identity Identity { get; } = new();
        public LinkedInService LinkedIn { get; }
        public Fixture()
        {
            Accounts = new(Repository);
            LinkedIn = new(Repository, Accounts, Identity, NullLogger<LinkedInService>.Instance);
        }
        public async Task<string> Start(string subject)
        {
            var userId = User(subject);
            var current = await Accounts.GetAsync(userId, default);
            await Accounts.SaveAsync(userId, new(current.TranscriptUrl, new(LinkedInImport: true), current.Version), default);
            await LinkedIn.ConnectAsync(userId, default);
            return Identity.State;
        }
    }
    private sealed class Identity : ILinkedInIdentityClient
    {
        public string State { get; private set; } = "";
        public int Imports { get; private set; }
        public string AuthorizationUrl(string state, string nonce) { State = state; return "https://www.linkedin.com/oauth/v2/authorization?state=" + state; }
        public Task<LinkedInProfile> ImportAsync(string code, string nonce, CancellationToken ct)
        {
            Imports++;
            return Task.FromResult(new LinkedInProfile("linkedin-subject", "LinkedIn Example", "person@example.test", true, null, DateTimeOffset.UtcNow));
        }
    }
}
