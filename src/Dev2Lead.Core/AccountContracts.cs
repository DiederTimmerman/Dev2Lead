namespace Dev2Lead.Core;

/// <summary>Explicit permissions for future CV storage, AI analysis, coaching and LinkedIn import.</summary>
public sealed record AccountConsents(bool CvStorage = false, bool CvAnalysis = false, bool CareerChat = false, bool LinkedInImport = false);

/// <summary>Basic LinkedIn identity imported with authorization; not employment history.</summary>
public sealed record LinkedInProfile(string Subject, string Name, string Email, bool EmailVerified, string? PictureUrl, DateTimeOffset ImportedOn);

/// <summary>Account-scoped preferences and imported identity, independent of an uploaded CV.</summary>
public sealed record AccountSettingsResponse(string TranscriptUrl, AccountConsents Consents, LinkedInProfile? LinkedIn,
    string LinkedInStatus, string? Version);

/// <summary>Concurrency-checked account preferences update.</summary>
public sealed record UpdateAccountSettingsRequest(string TranscriptUrl, AccountConsents Consents, string? Version);

/// <summary>A short-lived authorization URL for importing the signed-in account's LinkedIn identity.</summary>
public sealed record LinkedInConnectResponse(string AuthorizationUrl);
