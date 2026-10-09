using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Dev2Lead.Core;
using Xunit;

namespace Dev2Lead.Core.Tests;

public sealed class CoreTests
{
    private const string Cv = "Alex Example\nSoftware developer with five years of C#, Azure and mentoring experience.";
    private static readonly AzureSettings Settings = new("https://example.openai.azure.com/", "career-chat", "test-key");

    [Fact]
    public void SampleHasExactlyFiveRankedPrioritiesAndRealResources()
    {
        var sample = LearningCatalog.Sample();
        CareerCoach.Validate(new() { Message = "Your plan", Roadmap = sample }, true);
        Assert.Equal([1, 2, 3, 4, 5], sample.FocusPoints.Select(p => p.Rank));
        Assert.All(sample.FocusPoints, p => Assert.StartsWith("https://", LearningCatalog.Find(p.ResourceId).Url));
        Assert.Contains("Illustrative sample", sample.Assumptions);
    }

    [Fact]
    public void InvalidRoadmapIsRejectedRatherThanDisplayed()
    {
        var roadmap = LearningCatalog.Sample();
        roadmap.FocusPoints.RemoveAt(0);
        Assert.Throws<InvalidDataException>(() => CareerCoach.Validate(new() { Message = "Plan", Roadmap = roadmap }, true));
    }

    [Fact]
    public void DuplicateRanksAreRejected()
    {
        var roadmap = LearningCatalog.Sample();
        roadmap.FocusPoints[1] = roadmap.FocusPoints[1] with { Rank = 1 };
        Assert.Throws<InvalidDataException>(() => CareerCoach.Validate(new() { Message = "Plan", Roadmap = roadmap }, true));
    }

    [Fact]
    public void UnknownResourcesAreRejected()
    {
        var roadmap = LearningCatalog.Sample();
        roadmap.FocusPoints[0] = roadmap.FocusPoints[0] with { ResourceId = "invented-course" };
        Assert.Throws<InvalidDataException>(() => CareerCoach.Validate(new() { Message = "Plan", Roadmap = roadmap }, true));
    }

    [Fact]
    public void NullFocusPointFromModelIsRejected()
    {
        var roadmap = LearningCatalog.Sample();
        roadmap.FocusPoints[0] = null!;
        Assert.Throws<InvalidDataException>(() => CareerCoach.Validate(new() { Message = "Plan", Roadmap = roadmap }, true));
    }

    [Fact]
    public void RoadmapRequestCannotSilentlyReturnOnlyChat()
    {
        Assert.Throws<InvalidDataException>(() => CareerCoach.Validate(new() { Message = "I need more information" }, true));
        CareerCoach.Validate(new() { Message = "What role interests you?" }, false);
    }

    [Theory]
    [InlineData("http://example.openai.azure.com/")]
    [InlineData("https://attacker.test/")]
    [InlineData("https://example.openai.azure.com.attacker.test/")]
    [InlineData("https://example.openai.azure.com/openai/")]
    [InlineData("https://example.openai.azure.com/?query=1")]
    [InlineData("https://example.services.ai.azure.com.attacker.test/api/projects/demo")]
    [InlineData("https://example.services.ai.azure.com/api/projects/")]
    [InlineData("https://example.services.ai.azure.com/api/projects/demo/extra")]
    [InlineData("https://example.services.ai.azure.com:8443/")]
    public void InvalidAzureEndpointsAreRejected(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => new AzureSettings(endpoint, "deployment", "key").Validate());
    }

    [Theory]
    [InlineData("https://example.services.ai.azure.com/")]
    [InlineData("https://example.services.ai.azure.com/openai/v1/")]
    [InlineData("https://example.services.ai.azure.com/api/projects/my-project")]
    [InlineData("https://example.services.ai.azure.com/api/projects/my-project/")]
    public void FoundryEndpointsNormalizeToResourceRoot(string endpoint)
    {
        Assert.Equal("https://example.services.ai.azure.com/",
            new AzureSettings(endpoint, "gpt-5-nano", "test-key").Validate().AbsoluteUri);
    }

    [Fact]
    public async Task FoundryProjectUsesV1ModelRequestWithoutProjectPath()
    {
        var settings = new AzureSettings("https://example.services.ai.azure.com/api/projects/my-project", "gpt-5-nano", "test-key");
        var handler = new FakeHandler(async request =>
        {
            Assert.Equal("https://example.services.ai.azure.com/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("gpt-5-nano", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));
            Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
            return Reply(new() { Message = "What is your target role?" });
        });
        var reply = await new CareerCoach(new HttpClient(handler)).AskAsync(settings, Cv, [], false);
        Assert.Equal("What is your target role?", reply.Message);
    }

    [Fact]
    public async Task TxtCvIsExtractedLocally()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Cv));
        Assert.Equal(Cv, await CvReader.ReadAsync("cv.TXT", stream));
    }

    [Theory]
    [InlineData("short", "cv.txt")]
    [InlineData(Cv, "cv.exe")]
    public async Task UnreadableAndUnsupportedCvsFailExplicitly(string text, string name)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await Assert.ThrowsAsync<InvalidDataException>(() => CvReader.ReadAsync(name, stream));
    }

    [Fact]
    public async Task OversizeInputIsRejected()
    {
        using var stream = new MemoryStream(new byte[CvReader.MaxBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => CvReader.ReadAsync("cv.txt", stream));
    }

    [Fact]
    public async Task OversizeExtractedTextIsRejected()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', CvReader.MaxCharacters + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => CvReader.ReadAsync("cv.txt", stream));
    }

    [Fact]
    public async Task DocxParagraphTextIsExtractedWithoutFormatting()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                <w:body><w:p><w:r><w:t>Alex Example</w:t></w:r></w:p>
                <w:p><w:r><w:t>Software developer with five years of C#, Azure and mentoring experience.</w:t></w:r></w:p>
                </w:body></w:document>
                """);
        }
        memory.Position = 0;
        Assert.Equal(Cv, await CvReader.ReadAsync("cv.docx", memory));
    }

    [Fact]
    public async Task PdfTextIsExtracted()
    {
        using var stream = new MemoryStream(CreatePdf(Cv.Replace("\n", " ")));
        var extracted = await CvReader.ReadAsync("cv.pdf", stream);
        Assert.Contains("Alex Example", extracted);
        Assert.Contains("mentoring experience", extracted);
    }

    [Fact]
    public async Task ScannedPdfWithNoTextIsRejected()
    {
        using var stream = new MemoryStream(CreatePdf(""));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CvReader.ReadAsync("scan.pdf", stream));
        Assert.Contains("scanned", error.Message);
    }

    [Fact]
    public async Task AzureRequestUsesDeploymentJsonModeAndCv()
    {
        string? body = null;
        Uri? uri = null;
        var handler = new FakeHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            uri = request.RequestUri;
            Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
            return Reply(new() { Message = "How many hours can you study per week?" });
        });
        var coach = new CareerCoach(new HttpClient(handler));
        var result = await coach.AskAsync(Settings, Cv, [new("user", "I want to be a lead developer")], false);
        Assert.Contains("hours", result.Message);
        Assert.Equal("/openai/deployments/career-chat/chat/completions", uri!.AbsolutePath);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("json_object", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains("Alex Example", body);
        Assert.Contains("lead developer", body);
    }

    [Fact]
    public async Task AzureRoadmapResponsePassesContract()
    {
        var coach = new CareerCoach(new HttpClient(new FakeHandler(_ => Task.FromResult(
            Reply(new() { Message = "Here is your roadmap", Roadmap = LearningCatalog.Sample() })))));
        var result = await coach.AskAsync(Settings, Cv, [new("user", "Lead developer, 5 hours weekly")], true);
        Assert.Equal(5, result.Roadmap!.FocusPoints.Count);
    }

    [Theory]
    [InlineData(401, "credentials")]
    [InlineData(404, "deployment")]
    [InlineData(429, "rate-limiting")]
    [InlineData(400, "JSON mode")]
    [InlineData(500, "HTTP 500")]
    public async Task AzureFailuresAreExplicitAndDoNotBecomeFakeSuccess(int status, string expected)
    {
        var coach = new CareerCoach(new HttpClient(new FakeHandler(_ =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)))));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => coach.AskAsync(Settings, Cv, [], false));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task TruncatedModelResponseIsRejected()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"finish_reason":"length","message":{"content":"{}"}}]}""")
        };
        var coach = new CareerCoach(new HttpClient(new FakeHandler(_ => Task.FromResult(response))));
        await Assert.ThrowsAsync<InvalidDataException>(() => coach.AskAsync(Settings, Cv, [], true));
    }

    private static HttpResponseMessage Reply(CoachReply reply) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(reply, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } }
        }))
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static byte[] CreatePdf(string text)
    {
        var content = $"BT /F1 12 Tf 40 750 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1000 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"
        ];
        var result = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(result.ToString()));
            result.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(result.ToString());
        result.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) result.Append($"{offset:D10} 00000 n \n");
        result.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(result.ToString());
    }
}
