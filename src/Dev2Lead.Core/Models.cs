namespace Dev2Lead.Core;

public sealed record LearningResource(string Id, string Title, string Provider, string Url, string Kind);
public sealed record ConversationMessage(string Role, string Content);
public sealed record FocusPoint(int Rank, string Title, string Why, string Action, string ResourceId);
public sealed record Milestone(string Period, string Title, string Outcome);

public sealed class CareerRoadmap
{
    public string TargetRole { get; set; } = "";
    public string Summary { get; set; } = "";
    public string EstimatedTime { get; set; } = "";
    public string Assumptions { get; set; } = "";
    public List<FocusPoint> FocusPoints { get; set; } = [];
    public List<Milestone> Milestones { get; set; } = [];
}

public sealed class CoachReply
{
    public string Message { get; set; } = "";
    public CareerRoadmap? Roadmap { get; set; }
}

public sealed record AzureSettings(string Endpoint, string Deployment, string ApiKey)
{
    public Uri Validate()
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Use an HTTPS Azure OpenAI resource URL or a Foundry project URL on services.ai.azure.com.");
        var path = uri.AbsolutePath.TrimEnd('/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var foundry = uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);
        var projectPath = foundry && segments.Length == 3 && segments[0] == "api" && segments[1] == "projects";
        if (path.Length != 0 && !(foundry && path == "/openai/v1") && !projectPath)
            throw new InvalidOperationException("Use the resource root URL, its /openai/v1/ URL, or a Foundry /api/projects/PROJECT URL.");
        if (string.IsNullOrWhiteSpace(Deployment) || string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Enter your deployment name and API key in AI settings.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }
}
