using System.IO.Compression;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Dev2Lead.Core;

public static class CvReader
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxCharacters = 40000;

    public static async Task<string> ReadAsync(string fileName, Stream source)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer)) > 0)
        {
            if (memory.Length + count > MaxBytes)
                throw new InvalidDataException("The CV must be smaller than 10 MB.");
            await memory.WriteAsync(buffer.AsMemory(0, count));
        }
        memory.Position = 0;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        string text;
        switch (extension)
        {
            case ".txt":
                using (var reader = new StreamReader(memory))
                    text = await reader.ReadToEndAsync();
                break;
            case ".pdf":
                using (var pdf = PdfDocument.Open(memory.ToArray()))
                {
                    if (pdf.NumberOfPages > 30)
                        throw new InvalidDataException("Please use a CV of 30 pages or fewer.");
                    text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
                }
                break;
            case ".docx":
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
                {
                    var entry = archive.GetEntry("word/document.xml")
                        ?? throw new InvalidDataException("This DOCX has no readable document.");
                    if (entry.Length > MaxBytes)
                        throw new InvalidDataException("The extracted document is too large.");
                    await using var xml = entry.Open();
                    var settings = new System.Xml.XmlReaderSettings
                    {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        MaxCharactersInDocument = MaxBytes,
                        Async = true
                    };
                    using var reader = System.Xml.XmlReader.Create(xml, settings);
                    var document = await XDocument.LoadAsync(reader, LoadOptions.None, CancellationToken.None);
                    XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    text = string.Join("\n", document.Descendants(word + "p")
                        .Select(p => string.Concat(p.Descendants(word + "t").Select(t => t.Value))));
                }
                break;
            default:
                throw new InvalidDataException("Choose a PDF, DOCX, or TXT file.");
        }
        text = text.Trim();
        if (text.Length < 40)
            throw new InvalidDataException("Not enough readable text. For scanned PDFs, export a text-based PDF or upload DOCX/TXT.");
        if (text.Length > MaxCharacters)
            throw new InvalidDataException("The extracted CV exceeds 40,000 characters. Please upload a shorter version.");
        return text;
    }
}
