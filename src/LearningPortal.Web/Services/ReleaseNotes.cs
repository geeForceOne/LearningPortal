using System.Text.RegularExpressions;
using LearningPortal.Core;
using Markdig;

namespace LearningPortal.Web.Services;

// The release notes page's content: the repository's CHANGELOG.md, embedded into the app at build
// time (see the .csproj) so a running instance shows the notes up to its own version, with no
// network access. Rendered once; raw HTML in the file is escaped, not passed through.
public static class ReleaseNotes
{
    private static readonly Lazy<string> RenderedHtml = new(Render);

    // Written in CHANGELOG.md in place of a version number; the page shows the running version and
    // its build date there instead, e.g. "1.3.0 - 2026-09-25".
    private const string CurrentVersionHeading = "## Current version";

    private static string RunningHeading() =>
        "## " + AppInfo.Version + (AppInfo.BuildDate is { } date ? " - " + date : "");

    // The notes as HTML, with the running version's heading tagged.
    public static string Html
    {
        get
        {
            var html = RenderedHtml.Value;
            // "<h2>1.2.0 - 2026-09-24</h2>" -> the same, plus a small "Running now" tag.
            var heading = new Regex($@"<h2>({Regex.Escape(AppInfo.Version)}(?:\s[^<]*)?)</h2>");
            return heading.Replace(html, "<h2>$1<span class=\"current-tag\">Running now</span></h2>", 1);
        }
    }

    private static string Render()
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return "<p>No release notes are included in this build.</p>";
        using var reader = new StreamReader(stream);
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
        return Markdown.ToHtml(reader.ReadToEnd().Replace(CurrentVersionHeading, RunningHeading()), pipeline);
    }
}
