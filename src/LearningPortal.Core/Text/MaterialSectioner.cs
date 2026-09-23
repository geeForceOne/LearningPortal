using System.Text;
using System.Text.RegularExpressions;

namespace LearningPortal.Core.Text;

public sealed record SectionDraft(string Heading, string Text);

// Splits a material's text into sections that can be sent to the AI on their own: at headings
// (Markdown or LaTeX) where the material has them, by size where it doesn't. Tiny sections are
// merged into their neighbour and oversized ones are cut at paragraph breaks, so every section
// lands near the target size.
public static partial class MaterialSectioner
{
    [GeneratedRegex(@"^(#{1,3})\s+(.+)$")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^\\(chapter|section|subsection)\*?\{(.+?)\}")]
    private static partial Regex LatexHeading();

    public static List<SectionDraft> Split(string text, string fallbackTitle, int targetTokens)
    {
        var raw = SplitAtHeadings(text, fallbackTitle);
        var minTokens = targetTokens / 6;
        var maxTokens = targetTokens * 2;

        var merged = new List<SectionDraft>();
        foreach (var section in raw)
        {
            if (merged.Count > 0 && TokenEstimator.Estimate(merged[^1].Text) < minTokens)
            {
                var prev = merged[^1];
                merged[^1] = prev with { Text = prev.Text + "\n\n" + section.Heading + "\n" + section.Text };
            }
            else
            {
                merged.Add(section);
            }
        }

        var result = new List<SectionDraft>();
        foreach (var section in merged)
        {
            if (TokenEstimator.Estimate(section.Text) <= maxTokens)
            {
                if (!string.IsNullOrWhiteSpace(section.Text))
                    result.Add(section);
                continue;
            }

            var parts = SplitBySize(section.Text, targetTokens);
            for (var i = 0; i < parts.Count; i++)
                result.Add(new SectionDraft(parts.Count > 1 ? $"{section.Heading} (part {i + 1})" : section.Heading, parts[i]));
        }

        return result;
    }

    private static List<SectionDraft> SplitAtHeadings(string text, string fallbackTitle)
    {
        var sections = new List<SectionDraft>();
        var heading = fallbackTitle;
        var body = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            var found = MatchHeading(line);
            if (found is not null)
            {
                if (body.ToString().Trim().Length > 0)
                    sections.Add(new SectionDraft(heading, body.ToString().Trim()));
                heading = found;
                body.Clear();
                continue;
            }
            body.Append(line).Append('\n');
        }

        if (body.ToString().Trim().Length > 0)
            sections.Add(new SectionDraft(heading, body.ToString().Trim()));

        return sections;
    }

    private static string? MatchHeading(string line)
    {
        var md = MarkdownHeading().Match(line);
        if (md.Success)
            return md.Groups[2].Value.Trim();

        var tex = LatexHeading().Match(line.TrimStart());
        return tex.Success ? tex.Groups[2].Value.Trim() : null;
    }

    private static List<string> SplitBySize(string text, int targetTokens)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in text.Split("\n\n"))
        {
            if (current.Length > 0 && TokenEstimator.Estimate(current.ToString()) + TokenEstimator.Estimate(paragraph) > targetTokens)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            current.Append(paragraph).Append("\n\n");
        }
        if (current.ToString().Trim().Length > 0)
            parts.Add(current.ToString().Trim());
        return parts;
    }
}
