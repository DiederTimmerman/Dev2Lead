using Dev2Lead.Core;

namespace Dev2Lead.Services;

public sealed class CoachSession
{
    public List<ConversationMessage> Messages { get; } = [];
    public string Draft { get; set; } = "";
}

public sealed class GrowthWorkspace
{
    public Dictionary<string, CoachSession> Sessions { get; } = GrowthCoaches.All.ToDictionary(c => c.Id, _ => new CoachSession());
    public string TargetRole { get; set; } = "Technical lead";
    public GapAnalysisResponse? Analysis { get; set; }

    public void Clear()
    {
        foreach (var session in Sessions.Values) { session.Messages.Clear(); session.Draft = ""; }
        Analysis = null;
        TargetRole = "Technical lead";
    }
}
