using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LearningPortal.Core.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using WText = DocumentFormat.OpenXml.Wordprocessing.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LearningPortal.Core.Text;

public class UnsupportedMaterialException(string message) : Exception(message);

// A PDF without a text layer, usually a scan. Material needs text, but an exam paper can still be
// read by the AI from the page images.
public sealed class ScannedPdfException(string message) : UnsupportedMaterialException(message);

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

    public static int PdfPageCount(Stream stream)
    {
        try
        {
            using var document = PdfDocument.Open(stream);
            return document.NumberOfPages;
        }
        catch (Exception ex)
        {
            throw new UnsupportedMaterialException($"The PDF couldn't be read: {ex.Message}");
        }
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
            throw new ScannedPdfException(
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

            var lists = new ListNumbers(doc.MainDocumentPart.NumberingDefinitionsPart?.Numbering);
            AppendBlocks(sb, body, lists, headings: true, ct);
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

    // ---------- Word ----------

    // What an unfilled fill-in field (still showing "Click or tap here to enter text.") becomes.
    // On an exam paper it marks where the student writes.
    public const string BlankField = "[answer field]";

    // Paragraphs, tables and content controls in reading order. Headings become Markdown headings
    // only outside tables, so text laid out in a table never changes how material is sectioned.
    private static void AppendBlocks(StringBuilder sb, OpenXmlElement parent, ListNumbers lists, bool headings, CancellationToken ct)
    {
        foreach (var element in parent.ChildElements)
        {
            ct.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph p:
                    AppendParagraph(sb, p, lists, headings);
                    break;
                case Table table:
                    AppendTable(sb, table, lists, ct);
                    break;
                case SdtBlock sdt when ShowsPlaceholder(sdt.SdtProperties):
                    sb.AppendLine(BlankField);
                    break;
                case SdtBlock { SdtContentBlock: { } content }:
                    AppendBlocks(sb, content, lists, headings, ct);
                    break;
            }
        }
    }

    // A table of short cells stays one "| a | b |" line per row. A cell holding several
    // paragraphs (often a layout table around a long text) would be squeezed onto one line that
    // way, so such a table is written out cell by cell instead, each cell's lines kept.
    private static void AppendTable(StringBuilder sb, Table table, ListNumbers lists, CancellationToken ct)
    {
        var rows = Unwrap<TableRow>(table).Select(row => Unwrap<TableCell>(row).ToList()).ToList();
        var shortCells = rows.SelectMany(r => r).All(cell =>
            !cell.Descendants<Table>().Any()
            && cell.Descendants<Paragraph>().Count(p => WordText(p).Trim().Length > 0) <= 1);

        foreach (var row in rows)
        {
            var cells = row.Select(cell =>
            {
                var cellText = new StringBuilder();
                AppendBlocks(cellText, cell, lists, headings: false, ct);
                return cellText.ToString().Trim();
            }).ToList();

            if (shortCells)
            {
                sb.AppendLine("| " + string.Join(" | ", cells.Select(c => c.ReplaceLineEndings(" "))) + " |");
                continue;
            }
            foreach (var cell in cells.Where(c => c.Length > 0))
            {
                sb.AppendLine(cell);
                sb.AppendLine();
            }
        }
        sb.AppendLine();
    }

    // Rows and cells can sit inside content controls (a repeating section, say).
    private static IEnumerable<T> Unwrap<T>(OpenXmlElement parent) where T : OpenXmlElement
    {
        foreach (var child in parent.ChildElements)
        {
            if (child is T match)
                yield return match;
            else if (child is SdtElement sdt)
                foreach (var content in sdt.ChildElements.Where(e => e is not SdtProperties and not SdtEndCharProperties))
                    foreach (var inner in Unwrap<T>(content))
                        yield return inner;
        }
    }

    private static bool ShowsPlaceholder(SdtProperties? properties) =>
        properties?.GetFirstChild<ShowingPlaceholder>() is not null;

    // The text as Word shows it. Unlike InnerText, it keeps line breaks and leaves out deleted
    // revisions, field codes and the fallback copy of a text box.
    private static string WordText(OpenXmlElement element)
    {
        var sb = new StringBuilder();
        AppendWordText(sb, element);
        return sb.ToString();
    }

    private static void AppendWordText(StringBuilder sb, OpenXmlElement element)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                case WText t: sb.Append(t.Text); break;
                case TabChar: sb.Append(' '); break;
                case Break or CarriageReturn: sb.Append('\n'); break;
                case NoBreakHyphen: sb.Append('-'); break;
                case SdtRun sdt when ShowsPlaceholder(sdt.SdtProperties): sb.Append(BlankField); break;
                case DeletedText or FieldCode or AlternateContentFallback or ParagraphProperties or RunProperties: break;
                case Paragraph nested: sb.Append('\n'); AppendWordText(sb, nested); break; // a text box's paragraphs
                default: AppendWordText(sb, child); break;
            }
        }
    }

    private static void AppendParagraph(StringBuilder sb, Paragraph p, ListNumbers lists, bool headings)
    {
        // Every list paragraph counts, empty or not, so later numbers match what Word shows.
        var prefix = lists.Prefix(p);
        var text = WordText(p).Trim();
        if (text.Length == 0)
        {
            sb.AppendLine();
            return;
        }

        var style = headings ? p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "" : "";
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
            sb.Append('#', level).Append(' ').AppendLine(text.ReplaceLineEndings(" "));
        }
        else
        {
            sb.Append(prefix).AppendLine(text);
        }
    }

    // The bullet or number Word shows before a list paragraph, from the list's level definitions
    // ("%1.", "(%2)", "%1.%2"). Numbers are counted per list in reading order; an item restarts the
    // levels below it. A list without definitions gets a plain bullet.
    private sealed class ListNumbers(Numbering? numbering)
    {
        private readonly Dictionary<(int List, int Level), int> _counts = [];

        public string Prefix(Paragraph p)
        {
            var properties = p.ParagraphProperties?.NumberingProperties;
            var list = properties?.NumberingId?.Val?.Value ?? 0;
            if (list == 0) // 0 means "not a list"
                return "";
            var level = Math.Clamp(properties!.NumberingLevelReference?.Val?.Value ?? 0, 0, 8);

            var definition = LevelOf(list, level);
            _counts[(list, level)] = _counts.TryGetValue((list, level), out var n)
                ? n + 1
                : definition?.StartNumberingValue?.Val?.Value ?? 1;
            foreach (var deeper in _counts.Keys.Where(k => k.List == list && k.Level > level).ToList())
                _counts.Remove(deeper);

            // Four spaces per level: Markdown needs three or more to nest under a numbered item.
            var indent = new string(' ', 4 * level);
            var format = definition?.NumberingFormat?.Val;
            if (definition is null || format is null || format == NumberFormatValues.Bullet)
                return indent + "- ";
            if (format == NumberFormatValues.None)
                return indent;

            var pattern = definition.LevelText?.Val?.Value ?? $"%{level + 1}.";
            var shown = Regex.Replace(pattern, @"%([1-9])", m =>
            {
                var at = m.Groups[1].Value[0] - '1';
                return _counts.TryGetValue((list, at), out var count) ? Format(count, LevelOf(list, at)?.NumberingFormat?.Val) : "";
            }).Trim();
            return shown.Length == 0 ? indent : indent + shown + " ";
        }

        private Level? LevelOf(int list, int level)
        {
            var abstractId = numbering?.Elements<NumberingInstance>()
                .FirstOrDefault(i => i.NumberID?.Value == list)?.AbstractNumId?.Val?.Value;
            return numbering?.Elements<AbstractNum>()
                .FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId)?
                .Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
        }

        private static string Format(int n, EnumValue<NumberFormatValues>? format)
        {
            if (format is null || n < 1)
                return n.ToString(CultureInfo.InvariantCulture);
            if (format == NumberFormatValues.LowerLetter)
                return new string((char)('a' + (n - 1) % 26), (n - 1) / 26 + 1);
            if (format == NumberFormatValues.UpperLetter)
                return new string((char)('A' + (n - 1) % 26), (n - 1) / 26 + 1);
            if (format == NumberFormatValues.LowerRoman)
                return Roman(n).ToLowerInvariant();
            if (format == NumberFormatValues.UpperRoman)
                return Roman(n);
            return n.ToString(CultureInfo.InvariantCulture);
        }

        private static string Roman(int n)
        {
            var sb = new StringBuilder();
            foreach (var (value, numeral) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
                         (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
                for (; n >= value; n -= value)
                    sb.Append(numeral);
            return sb.ToString();
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
