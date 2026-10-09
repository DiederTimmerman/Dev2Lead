using System.Text;
using Dev2Lead.Core;

namespace Dev2Lead.Services;

public sealed class DesktopServices
{
    public async Task OpenLearnUrlAsync(string url)
    {
        var safeUrl = LearnTranscriptService.LearnUrl(url);
        if (!await Launcher.Default.OpenAsync(new Uri(safeUrl)))
            throw new IOException("Windows could not open your browser. Check that a default browser is configured.");
    }

    public async Task OpenLinkedInAuthorizationAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "www.linkedin.com"
            || uri.AbsolutePath != "/oauth/v2/authorization" || uri.UserInfo.Length > 0)
            throw new InvalidDataException("The backend returned an invalid LinkedIn authorization URL.");
        if (!await Launcher.Default.OpenAsync(uri))
            throw new IOException("Windows could not open LinkedIn authorization in your browser.");
    }

    public async Task<(string Name, string Text, byte[] Bytes)?> PickCvAsync()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Choose your CV",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".pdf", ".docx", ".txt"]
            })
        });
        if (file is null) return null;
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(bytes)) > 0)
        {
            if (buffer.Length + count > CvReader.MaxBytes) throw new InvalidDataException("The CV must be smaller than 10 MB.");
            await buffer.WriteAsync(bytes.AsMemory(0, count));
        }
        var content = buffer.ToArray();
        using var readable = new MemoryStream(content);
        var text = await Task.Run(() => CvReader.ReadAsync(file.FileName, readable));
        return (file.FileName, text, content);
    }

    public async Task OpenResourceAsync(string id)
    {
        var resource = LearningCatalog.Find(id);
        if (!await Launcher.Default.OpenAsync(new Uri(resource.Url)))
            throw new IOException("Windows could not open your browser. Check that a default browser is configured.");
    }

    public async Task<string> ExportAsync(CareerRoadmap roadmap, bool sample)
    {
        var document = new StringBuilder()
            .AppendLine("# Dev2Lead career roadmap")
            .AppendLine(sample ? "\nILLUSTRATIVE SAMPLE - not a personal assessment.\n" : "\nAI-generated guidance - not a guarantee of career outcomes.\n")
            .AppendLine($"## {roadmap.TargetRole}")
            .AppendLine(roadmap.Summary)
            .AppendLine($"\nEstimated time: {roadmap.EstimatedTime}")
            .AppendLine($"\nAssumptions: {roadmap.Assumptions}")
            .AppendLine("\n## Your five focus points");
        foreach (var point in roadmap.FocusPoints)
        {
            var resource = LearningCatalog.Find(point.ResourceId);
            document.AppendLine($"\n### {point.Rank}. {point.Title}")
                .AppendLine(point.Why)
                .AppendLine($"\nFirst action: {point.Action}")
                .AppendLine($"\nStudy: [{resource.Title}]({resource.Url})");
        }
        document.AppendLine("\n## Timeline");
        foreach (var milestone in roadmap.Milestones)
            document.AppendLine($"\n### {milestone.Period}: {milestone.Title}\n{milestone.Outcome}");
        var path = Path.Combine(FileSystem.Current.AppDataDirectory, $"Dev2Lead-roadmap-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, document.ToString());
        if (!await Launcher.Default.OpenAsync(new OpenFileRequest("Your Dev2Lead roadmap", new ReadOnlyFile(path))))
            throw new IOException($"Roadmap saved to {path}, but Windows could not open it.");
        return path;
    }
}
