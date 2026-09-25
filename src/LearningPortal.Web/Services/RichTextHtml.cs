using Markdig;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;

namespace LearningPortal.Web.Services;

// Turns AI-written text (questions, options, answers, explanations, feedback) into HTML. The AI
// writes a small Markdown subset: paragraphs, lists, **emphasis**, `inline code` and fenced code
// blocks. The text can echo uploaded material, so it's treated as untrusted:
// - raw HTML is escaped, not passed through;
// - links, autolinks and images aren't parsed at all, so nothing can point at javascript: URLs or
//   load remote images; they simply stay as the text that was written;
// - headings and indented code blocks are off, so older plain-text questions with a leading "#" or
//   indentation still read as they did.
// Single line breaks are kept as line breaks, as in the plain-text display this replaced.
public static class RichTextHtml
{
    private static readonly MarkdownPipeline Pipeline = Build();

    public static string ToHtml(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : Markdown.ToHtml(text.Trim(), Pipeline);

    private static MarkdownPipeline Build()
    {
        var builder = new MarkdownPipelineBuilder().DisableHtml();
        builder.BlockParsers.TryRemove<HeadingBlockParser>();
        builder.BlockParsers.TryRemove<IndentedCodeBlockParser>();
        builder.InlineParsers.TryRemove<LinkInlineParser>();
        builder.InlineParsers.TryRemove<AutolinkInlineParser>();
        builder.Extensions.Add(new Markdig.Extensions.Hardlines.SoftlineBreakAsHardlineExtension());
        return builder.Build();
    }
}
