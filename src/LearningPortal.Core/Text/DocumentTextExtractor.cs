using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LearningPortal.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LearningPortal.Core.Text;

public sealed class UnsupportedMaterialException(string message) : Exception(message);

// Turns an uploaded file into plain text once, at upload time. Word headings come out as
// Markdown headings so the sectioner can split on them the same way it splits Markdown.
public static class DocumentTextExtractor
{
    public static MaterialKind KindFromFileName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => MaterialKind.Pdf,
            ".docx" => MaterialKind.Word,
            ".tex" or ".latex" => MaterialKind.Latex,
            ".md" or ".markdown" => MaterialKind.Markdown,
            ".txt" => MaterialKind.Text,
            _ => throw new UnsupportedMaterialException(
                $"\"{fileName}\" isn't a supported file type. Upload a PDF, Word (.docx), LaTeX (.tex), Markdown (.md) or text (.txt) file."),
        };

    public static readonly string[] SupportedExtensions = [".pdf", ".docx", ".tex", ".latex", ".md", ".markdown", ".txt"];

    public static async Task<string> ExtractAsync(Stream content, MaterialKind kind, CancellationToken ct)
    {
        // PdfPig and OpenXml both want a seekable stream; uploads usually aren't.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var text = kind switch
        {
            MaterialKind.Pdf => ExtractPdf(buffer, ct),
            MaterialKind.Word => ExtractDocx(buffer, ct),
            _ => await new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEndAsync(ct),
        };

        return Normalize(text);
    }

    private static string ExtractPdf(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            using var document = PdfDocument.Open(stream);
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine(ContentOrderTextExtractor.GetText(page));
                sb.AppendLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UnsupportedMaterialException($"The PDF couldn't be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(sb.ToString()))
            throw new UnsupportedMaterialException(
                "No text was found in this PDF. It may be a scanned image; export it with a text layer and try again.");

        return sb.ToString();
    }

    private static string ExtractDocx(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new UnsupportedMaterialException("The Word document has no content.");

            foreach (var element in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();
                switch (element)
                {
                    case Paragraph p:
                        AppendParagraph(sb, p);
                        break;
                    case Table table:
                        foreach (var row in table.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim());
                            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                        }
                        sb.AppendLine();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnsupportedMaterialException)
        {
            throw new UnsupportedMaterialException($"The Word document couldn't be read: {ex.Message}");
        }

        return sb.ToString();
    }

    private static void AppendParagraph(StringBuilder sb, Paragraph p)
    {
        var text = p.InnerText.Trim();
        if (text.Length == 0)
        {
            sb.AppendLine();
            return;
        }

        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        var level = style switch
        {
            "Title" => 1,
            _ when style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(style.AsSpan("Heading".Length), out var n) => Math.Clamp(n, 1, 6),
            _ => 0,
        };

        if (level > 0)
        {
            sb.AppendLine();
            sb.Append('#', level).Append(' ').AppendLine(text);
        }
        else
        {
            sb.AppendLine(text);
        }
    }

    // Collapses runs of blank lines and trailing spaces so token estimates aren't inflated by
    // layout whitespace.
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd());
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (++blank > 1) continue;
            }
            else
            {
                blank = 0;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
