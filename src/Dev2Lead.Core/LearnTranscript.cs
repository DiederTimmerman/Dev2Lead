using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dev2Lead.Core;

public sealed record LearnCertification(string Title, string Status, DateTimeOffset EarnedOn, DateTimeOffset? ExpiresOn);
public sealed record LearnModule(string Title, string? Url, DateTimeOffset CompletedOn, int DurationMinutes);
public sealed record LearnAward(string Title, string Category, string? Url, string ImageUrl, DateTimeOffset EarnedOn);
public sealed record LearnTranscript(string UserName, string SourceUrl, DateTimeOffset RetrievedOn,
    IReadOnlyList<LearnCertification> ActiveCertifications, IReadOnlyList<LearnCertification> HistoricalCertifications,
    IReadOnlyList<LearnModule> Modules, IReadOnlyList<LearnAward> Awards, int LearningPathsCompleted, int TrainingMinutes);

public sealed class LearnTranscriptService(HttpClient http)
{
    public static Uri ValidateSource(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "learn.microsoft.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Paste a public Microsoft Learn transcript sharing URL.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 5 && Regex.IsMatch(segments[0], "^[a-z]{2}-[a-z]{2}$"))
            segments = segments.Skip(1).ToArray();
        if (segments.Length != 4 || segments[0] != "users" || segments[2] != "transcript"
            || !Regex.IsMatch(segments[1], "^[a-zA-Z0-9_-]+$") || !Regex.IsMatch(segments[3], "^[a-zA-Z0-9_-]+$"))
            throw new InvalidOperationException("Use the shared transcript URL: https://learn.microsoft.com/en-us/users/USERNAME/transcript/SHARE-ID");
        return uri;
    }

    public static string LearnUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Microsoft Learn returned a missing achievement link.");
        var uri = new Uri(new Uri("https://learn.microsoft.com/"), value);
        if (uri.Scheme != "https" || uri.Host != "learn.microsoft.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Microsoft Learn returned an unexpected link origin.");
        return uri.AbsoluteUri;
    }

    public async Task<LearnTranscript> LoadAsync(string source, CancellationToken cancellationToken = default)
    {
        var uri = ValidateSource(source);
        var shareId = uri.AbsolutePath.TrimEnd('/').Split('/')[^1];
        using var transcriptResponse = await http.GetAsync(
            $"https://learn.microsoft.com/api/profiles/transcript/share/{Uri.EscapeDataString(shareId)}", cancellationToken);
        EnsureSuccess(transcriptResponse, "transcript");
        using var transcript = await JsonDocument.ParseAsync(await transcriptResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = transcript.RootElement;
        var docsId = RequiredString(root, "docsId");
        if (!Guid.TryParse(docsId, out var profileId))
            throw new InvalidDataException("Microsoft Learn returned an invalid profile identifier.");
        var certifications = root.GetProperty("certificationData");
        var active = ReadCertifications(certifications.GetProperty("activeCertifications"));
        var historical = ReadCertifications(certifications.GetProperty("historicalCertifications"));
        var modules = root.GetProperty("modulesCompleted").EnumerateArray().Select(m => new LearnModule(
            RequiredString(m, "title"), OptionalLearnUrl(m, "url"),
            m.GetProperty("completedOn").GetDateTimeOffset(), m.GetProperty("durationInMinutes").GetInt32()))
            .OrderByDescending(m => m.CompletedOn).ToList();
        if (modules.Count != root.GetProperty("totalModulesCompleted").GetInt32()
            || active.Count != certifications.GetProperty("totalActiveCertifications").GetInt32()
            || historical.Count != certifications.GetProperty("totalHistoricalCertifications").GetInt32())
            throw new InvalidDataException("Microsoft Learn returned an incomplete transcript. Please refresh or open the original transcript.");
        using var awardsResponse = await http.GetAsync($"https://learn.microsoft.com/api/achievements/user/{profileId}", cancellationToken);
        EnsureSuccess(awardsResponse, "badges");
        using var awardsDocument = await JsonDocument.ParseAsync(await awardsResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var awardRoot = awardsDocument.RootElement;
        var awards = awardRoot.GetProperty("achievements").EnumerateArray().Select(a => new LearnAward(
            RequiredString(a, "title"), RequiredString(a, "category"),
            OptionalLearnUrl(a, "url"), LearnUrl(RequiredString(a, "imageUrl")),
            a.GetProperty("grantedOn").GetDateTimeOffset())).OrderByDescending(a => a.EarnedOn).ToList();
        if (awards.Count != awardRoot.GetProperty("totalCount").GetInt32())
            throw new InvalidDataException("Microsoft Learn returned a partial badge collection. Open your Learn profile for the full collection.");
        return new(RequiredString(root, "userName"), uri.AbsoluteUri, DateTimeOffset.Now, active, historical, modules, awards,
            root.GetProperty("totalLearningPathsCompleted").GetInt32(), root.GetProperty("totalTrainingMinutes").GetInt32());
    }

    private static List<LearnCertification> ReadCertifications(JsonElement array) => array.EnumerateArray()
        .Select(c => new LearnCertification(RequiredString(c, "name"), RequiredString(c, "status"),
            c.GetProperty("dateEarned").GetDateTimeOffset(),
            c.TryGetProperty("expiration", out var expiry) && expiry.ValueKind != JsonValueKind.Null ? expiry.GetDateTimeOffset() : null))
        .OrderByDescending(c => c.EarnedOn).ToList();

    private static string RequiredString(JsonElement element, string field) =>
        element.GetProperty(field).GetString() is { Length: > 0 } text ? text :
            throw new InvalidDataException($"Microsoft Learn returned a missing {field} field.");

    private static string? OptionalLearnUrl(JsonElement element, string field)
    {
        if (!element.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Microsoft Learn returned an invalid achievement URL.");
        return value.GetString() is { Length: > 0 } url ? LearnUrl(url) : null;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string section)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException($"Microsoft Learn could not load your {section} (HTTP {(int)response.StatusCode}). Check the sharing link and profile visibility, then retry.",
            null, response.StatusCode);
    }
}
