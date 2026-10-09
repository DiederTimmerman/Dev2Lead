namespace Dev2Lead.Core;

/// <summary>An extracted work role with its source evidence.</summary>
public sealed record WorkExperience(string Role, string Organization, string? StartDate, string? EndDate,
    bool IsCurrent, string Summary, string Evidence);

/// <summary>A saved skill assessment, including provisional AI values.</summary>
public sealed record SkillValue(string Name, string Category, int? Level, decimal? Years, string Evidence, bool Reviewed)
{
    public static SkillValue From(SkillAssessment skill) => new(skill.Name, skill.Category, skill.Level, skill.Years, skill.Evidence, skill.Reviewed);
    public SkillAssessment ToAssessment() => new() { Name = Name, Category = Category, Level = Level, Years = Years, Evidence = Evidence, Reviewed = Reviewed };
}

/// <summary>The current signed-in user's stored CV and extracted profile.</summary>
public sealed record CareerProfileResponse(string FileName, string CvText, DateTimeOffset UploadedOn,
    string AnalysisStatus, IReadOnlyList<WorkExperience> Experiences, IReadOnlyList<SkillValue> Skills, string Version);

/// <summary>Explicit consent to analyze a saved CV at a particular version.</summary>
public sealed record AnalyzeProfileRequest(string Version, bool Consent);

/// <summary>A reviewed skills update guarded against concurrent modifications.</summary>
public sealed record UpdateSkillsRequest(string Version, IReadOnlyList<SkillValue> Skills);

/// <summary>Career-coaching input using the CV stored for the authenticated account.</summary>
public sealed record ChatProfileRequest(IReadOnlyList<ConversationMessage> Messages, bool GenerateRoadmap, bool Consent);

/// <summary>The identity derived by the server from a validated Google token.</summary>
public sealed record AccountResponse(string Name, string Email);

public sealed record CvAnalysis(IReadOnlyList<WorkExperience> Experiences, List<SkillAssessment> Skills);
