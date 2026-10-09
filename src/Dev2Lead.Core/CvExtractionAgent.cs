using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dev2Lead.Core;

public interface ICvExtractionAgent
{
    Task<CvAnalysis> ExtractAsync(AzureSettings settings, string cv, CancellationToken cancellationToken);
}

public sealed class CvExtractionAgent(CareerCoach coach) : ICvExtractionAgent
{
    public async Task<CvAnalysis> ExtractAsync(AzureSettings settings, string cv, CancellationToken cancellationToken)
    {
        if (cv.Length is < 40 or > CvReader.MaxCharacters)
            throw new InvalidDataException("The CV does not contain an acceptable amount of readable text.");
        var prompt = SkillsCoach.Instructions + """

            Extend the same JSON response with an "experiences" array in addition to "skills".
            Each experience: {"role":"job title","organization":"employer","startDate":"2020-01",
            "endDate":"2022-06","isCurrent":false,"summary":"concise responsibilities",
            "evidence":"verbatim excerpt from the CV proving this role"}.
            Use yyyy-MM or yyyy for dates only when evidenced; null for unknown dates.
            Set isCurrent true only for an explicitly current/present role; its endDate must be null.
            Extract at most 50 actual work roles; do not turn education, ambitions or courses into jobs.
            Empty experiences is valid if no work history is stated. Do not invent employers or dates.
            """;
        var json = await coach.CompleteJsonAsync(settings,
            [new("system", prompt + $"\nCurrent date: {DateTime.UtcNow:yyyy-MM-dd}"), new("user", "CV DATA:\n" + cv)], cancellationToken);
        var analysis = JsonSerializer.Deserialize<CvAnalysis>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The extraction agent returned no analysis.");
        Validate(analysis, cv);
        SkillsCoach.InitializeSuggestions(analysis.Skills);
        return analysis;
    }

    public static void Validate(CvAnalysis analysis, string cv)
    {
        if (analysis.Skills is null || analysis.Experiences is null || analysis.Experiences.Count > 50)
            throw new InvalidDataException("The agent returned an invalid experience/skills profile.");
        SkillsCoach.ValidateSuggestions(analysis.Skills, cv);
        foreach (var experience in analysis.Experiences)
        {
            if (experience is null || string.IsNullOrWhiteSpace(experience.Role) || experience.Role.Length > 200
                || string.IsNullOrWhiteSpace(experience.Organization) || experience.Organization.Length > 200
                || experience.Summary is null || experience.Summary.Length > 2000
                || string.IsNullOrWhiteSpace(experience.Evidence) || experience.Evidence.Length > 2000
                || !cv.Contains(experience.Evidence, StringComparison.OrdinalIgnoreCase)
                || !ValidDate(experience.StartDate) || !ValidDate(experience.EndDate)
                || (experience.IsCurrent && experience.EndDate is not null)
                || (experience.StartDate is not null && experience.EndDate is not null
                    && string.CompareOrdinal(EarliestDate(experience.StartDate), LatestDate(experience.EndDate)) > 0))
                throw new InvalidDataException("An extracted work experience is missing valid CV evidence or contains invalid dates.");
        }
    }

    private static string EarliestDate(string date) => date.Length == 4 ? date + "-01" : date;
    private static string LatestDate(string date) => date.Length == 4 ? date + "-12" : date;
    private static bool ValidDate(string? date) => date is null
        || Regex.IsMatch(date, @"\A(19|20)\d{2}(-(0[1-9]|1[0-2]))?\z");
}
