using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LearningPortal.Core.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LearningPortal.Core.Text;

public sealed class UnsupportedMaterialException(string message) : Exception(message);

// Turns an uploaded file into plain text once, at upload time. Word headings and PowerPoint
// slides come out as Markdown headings so the sectioner can split on them the same way it splits
// Markdown.
public static class DocumentTextExtractor
{
    public static MaterialKind KindFromFileName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => MaterialKind.Pdf,
            ".docx" => MaterialKind.Word,
            ".pptx" => MaterialKind.PowerPoint,
            ".tex" or ".latex" => MaterialKind.Latex,
            ".md" or ".markdown" => MaterialKind.Markdown,
            ".txt" => MaterialKind.Text,
            // The pre-2007 binary formats have no reader here; the fix on the user's side is one "Save as".
            ".ppt" => throw new UnsupportedMaterialException(
                $"\"{fileName}\" is an old PowerPoint file (.ppt). Open it in PowerPoint and save it as .pptx or PDF, then upload that."),
            ".doc" => throw new UnsupportedMaterialException(
                $"\"{fileName}\" is an old Word file (.doc). Open it in Word and save it as .docx or PDF, then upload that."),
            _ => throw new UnsupportedMaterialException(
                $"\"{fileName}\" isn't a supported file type. Upload a PDF, Word (.docx), PowerPoint (.pptx), LaTeX (.tex), Markdown (.md) or text (.txt) file."),
        };

    public static readonly string[] SupportedExtensions = [".pdf", ".docx", ".pptx", ".tex", ".latex", ".md", ".markdown", ".txt"];

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
            MaterialKind.PowerPoint => ExtractPptx(buffer, ct),
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

    // One "## Slide N: Title" section per visible slide: its text as bullets (indented by level),
    // its tables as rows, then the speaker notes, which often carry the actual explanation.
    // Pictures, charts and diagrams have no text to read and are skipped.
    private static string ExtractPptx(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        try
        {
            using var doc = PresentationDocument.Open(stream, false);
            var presentation = doc.PresentationPart
                ?? throw new UnsupportedMaterialException("The presentation has no slides.");
            var slideIds = presentation.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];

            var number = 0;
            foreach (var slideId in slideIds)
            {
                ct.ThrowIfCancellationRequested();
                if (slideId.RelationshipId?.Value is not { } relId || presentation.GetPartById(relId) is not SlidePart part)
                    continue;
                number++; // Counts hidden slides too, so the numbers match what PowerPoint shows.
                var slide = part.Slide;
                if (slide is null || slide.Show?.Value == false)
                    continue;

                string? title = null;
                var body = new StringBuilder();
                foreach (var shape in slide.Descendants<P.Shape>())
                {
                    var placeholder = PlaceholderType(shape);
                    if (IsSlideChrome(placeholder))
                        continue;
                    if (placeholder == P.PlaceholderValues.Title || placeholder == P.PlaceholderValues.CenteredTitle)
                    {
                        title ??= ShapeText(shape, bullets: false).ReplaceLineEndings(" ").Trim();
                        continue;
                    }
                    body.Append(ShapeText(shape, bullets: true));
                }
                foreach (var table in slide.Descendants<A.Table>())
                {
                    foreach (var row in table.Elements<A.TableRow>())
                        body.AppendLine("| " + string.Join(" | ", row.Elements<A.TableCell>().Select(c => CellText(c))) + " |");
                    body.AppendLine();
                }

                var notes = new StringBuilder();
                foreach (var shape in part.NotesSlidePart?.NotesSlide?.Descendants<P.Shape>() ?? [])
                {
                    if (PlaceholderType(shape) == P.PlaceholderValues.Body)
                        notes.Append(ShapeText(shape, bullets: false));
                }

                if (string.IsNullOrWhiteSpace(title) && body.Length == 0 && notes.Length == 0)
                    continue;

                sb.AppendLine();
                sb.AppendLine(string.IsNullOrWhiteSpace(title) ? $"## Slide {number}" : $"## Slide {number}: {title}");
                sb.Append(body);
                if (notes.ToString().Trim() is { Length: > 0 } noteText)
                    sb.AppendLine().AppendLine("Speaker notes:").AppendLine(noteText);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnsupportedMaterialException)
        {
            throw new UnsupportedMaterialException($"The PowerPoint file couldn't be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(sb.ToString()))
            throw new UnsupportedMaterialException(
                "No text was found in this presentation. If the slides are pictures, there's nothing the AI can read from them.");

        return sb.ToString();
    }

    private static P.PlaceholderValues? PlaceholderType(P.Shape shape)
    {
        var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
        if (placeholder is null)
            return null;
        // A placeholder without a type is a body/content placeholder.
        return placeholder.Type?.Value ?? P.PlaceholderValues.Body;
    }

    // Slide numbers, dates, footers and headers repeat on every slide and say nothing about the content.
    private static bool IsSlideChrome(P.PlaceholderValues? type) =>
        type == P.PlaceholderValues.SlideNumber || type == P.PlaceholderValues.DateAndTime
        || type == P.PlaceholderValues.Footer || type == P.PlaceholderValues.Header;

    private static string ShapeText(P.Shape shape, bool bullets)
    {
        var sb = new StringBuilder();
        foreach (var paragraph in shape.TextBody?.Elements<A.Paragraph>() ?? [])
        {
            var text = ParagraphText(paragraph);
            if (text.Length == 0)
                continue;
            if (bullets)
                sb.Append(' ', 2 * (paragraph.ParagraphProperties?.Level?.Value ?? 0)).Append("- ");
            sb.AppendLine(text);
        }
        return sb.ToString();
    }

    // Runs, fields and line breaks in order; a:br is a soft line break inside one paragraph.
    private static string ParagraphText(A.Paragraph paragraph)
    {
        var sb = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case A.Run run: sb.Append(run.Text?.Text); break;
                case A.Field field: sb.Append(field.Text?.Text); break;
                case A.Break: sb.Append(' '); break;
            }
        }
        return sb.ToString().Trim();
    }

    private static string CellText(A.TableCell cell) =>
        string.Join(" ", cell.TextBody?.Elements<A.Paragraph>().Select(ParagraphText).Where(t => t.Length > 0) ?? []);

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
