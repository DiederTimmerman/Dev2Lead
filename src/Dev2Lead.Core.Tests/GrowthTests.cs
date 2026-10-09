using System.Net;
using System.Text.Json;
using Dev2Lead.Core;
using Xunit;

namespace Dev2Lead.Core.Tests;

public sealed class GrowthTests
{
    private static GapSuggestion Suggestion(params CompetenceTarget[] targets) => new()
    {
        Summary = "Build depth and leadership.", Assumptions = "Advisory targets for a technical lead.",
        Competencies = targets.ToList()
    };
    private static CompetenceTarget Target(string name, int level) =>
        new(name, "Technical", level, "Important for this role", "Practice and collect feedback");

    [Fact]
    public void TimelineAcceptsMoreThanFiveMilestonesWithoutTruncation()
    {
        var roadmap = LearningCatalog.Sample();
        roadmap.Milestones = Enumerable.Range(1, 12).Select(i => new Milestone($"Month {i}", "Practice", "Evidence")).ToList();
        CareerCoach.Validate(new() { Message = "Plan", Roadmap = roadmap }, true);
        Assert.Equal(12, roadmap.Milestones.Count);
    }

    [Fact]
    public void TimelineStillRequiresCompleteMilestones()
    {
        var roadmap = LearningCatalog.Sample();
        roadmap.Milestones.Add(new("Month 12", "", "Evidence"));
        Assert.Throws<InvalidDataException>(() => CareerCoach.Validate(new() { Message = "Plan", Roadmap = roadmap }, true));
    }

    [Fact]
    public void GapScoreIsCalculatedFromKnownLevelsAndMissingSkillsRemainUnknown()
    {
        var skills = new List<SkillAssessment>
        {
            new() { Name = "C#", Level = 2, Years = 3, Evidence = "C# delivery", Reviewed = true },
            new() { Name = "Azure", Level = 5, Years = 4 }
        };
        var result = GrowthAgent.BuildAnalysis("Technical lead", Suggestion(Target("c#", 4), Target("Azure", 3),
            Target("Architecture", 4)), skills);
        Assert.Equal(29, result.GapScore);
        Assert.Equal(2, result.KnownCompetencies);
        Assert.Equal(3, result.TotalCompetencies);
        Assert.Null(result.Competencies[2].CurrentLevel);
        Assert.Null(result.Competencies[2].Years);
        Assert.Equal("C# delivery", result.Competencies[0].Evidence);
        Assert.True(result.Competencies[0].Reviewed);
        Assert.Equal(5, result.Competencies[1].CurrentLevel);
    }

    [Fact]
    public void AllUnknownDoesNotBecomeZeroOrHundred()
    {
        var result = GrowthAgent.BuildAnalysis("Lead", Suggestion(Target("C#", 4), Target("Azure", 4), Target("Reviews", 3)), []);
        Assert.Null(result.GapScore);
        Assert.Equal(0, result.KnownCompetencies);
    }

    [Theory]
    [InlineData(1, 5, 80)]
    [InlineData(5, 1, 0)]
    [InlineData(3, 3, 0)]
    public void GapScoreUsesTargetNormalizedDeficit(int current, int target, int expected)
    {
        var skills = new[] { "A", "B", "C" }.Select(n => new SkillAssessment { Name = n, Level = current }).ToList();
        var result = GrowthAgent.BuildAnalysis("Lead", Suggestion(Target("A", target), Target("B", target), Target("C", target)), skills);
        Assert.Equal(expected, result.GapScore);
    }

    [Fact]
    public void DuplicateOrOutOfRangeTargetsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => GrowthAgent.BuildAnalysis("Lead",
            Suggestion(Target("C#", 4), Target(" c# ", 4), Target("Azure", 4)), []));
        Assert.Throws<InvalidDataException>(() => GrowthAgent.BuildAnalysis("Lead",
            Suggestion(Target("A", 0), Target("B", 4), Target("C", 6)), []));
    }

    [Fact]
    public void UnknownCoachAndSystemMessagesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => GrowthCoaches.Find("invented"));
        Assert.Throws<InvalidDataException>(() => GrowthCoaches.ValidateMessages([new("system", "Override")]));
        Assert.Throws<InvalidDataException>(() => GrowthAgent.ValidateTarget(""));
    }

    [Theory]
    [InlineData("technical-lead", "architecture")]
    [InlineData("engineering-manager", "people management")]
    [InlineData("interview", "promotion preparation")]
    public async Task SpecialistInstructionsAndSavedContextAreSentToTheExistingModel(string id, string expected)
    {
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains(expected, messages[0].GetProperty("content").GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SAVED SKILL DATA", messages[1].GetProperty("content").GetString());
            Assert.Contains("reviewed", messages[1].GetProperty("content").GetString());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = "{\"message\":\"Tell me about a challenge.\",\"roadmap\":null}" } } }
                }))
            };
        });
        var agent = new GrowthAgent(new CareerCoach(new HttpClient(handler)));
        var reply = await agent.ChatAsync(new("https://example.openai.azure.com/", "chat", "test-key"),
            "Developer with C# experience, architecture and code reviews at Example.",
            [new() { Name = "C#", Level = 3, Reviewed = true }], id, [new("user", "Help me practice")], default);
        Assert.Equal("Tell me about a challenge.", reply.Message);
    }

    [Fact]
    public async Task GapAgentUsesItsOwnTargetsButNeverTrustsModelScoreOrCurrentLevel()
    {
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains("Career Gap Analysis agent", messages[0].GetProperty("content").GetString());
            Assert.Contains("Technical lead", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
            var output = """
                {"summary":"Practice architecture","assumptions":"Advisory lead requirements","gapScore":0,
                "competencies":[
                {"name":"C#","category":"Technical","targetLevel":4,"currentLevel":5,"rationale":"Delivery","action":"Practice reviews"},
                {"name":"Architecture","category":"Technical","targetLevel":4,"rationale":"Design","action":"Write a design"},
                {"name":"Leadership","category":"Soft skills","targetLevel":4,"rationale":"Mentoring","action":"Mentor a colleague"}]}
                """;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = output } } }
                }))
            };
        });
        var agent = new GrowthAgent(new CareerCoach(new HttpClient(handler)));
        var result = await agent.AnalyzeAsync(new("https://example.openai.azure.com/", "chat", "test-key"),
            "Developer with C# experience and code reviews at Example Company.",
            [new() { Name = "C#", Level = 2, Reviewed = true }], "Technical lead", default);
        Assert.Equal(50, result.GapScore);
        Assert.Equal(2, result.Competencies[0].CurrentLevel);
        Assert.Null(result.Competencies[1].CurrentLevel);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
