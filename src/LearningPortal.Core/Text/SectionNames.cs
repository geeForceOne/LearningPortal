using System.Text.RegularExpressions;

namespace LearningPortal.Core.Text;

// One line of a material's AI outline: "[3] Subject: key concepts" (see Prompts.OutlinePrompt).
public sealed record OutlineLine(string Line, string Subject, string? Concepts);

public sealed record SectionName(string Name, string? Concepts);

// Names a material's sections for the UI. A heading names its section when it tells it apart from
// the others; material without headings (a PDF, pasted text) gets the material's title on every
// section, split as "Title (part 2)", so those sections take the subject from the outline instead.
public static partial class SectionNames
{
    private const int MaxSubjectLength = 90;

    public static Dictionary<int, OutlineLine> ParseOutline(string? outline)
    {
        var lines = new Dictionary<int, OutlineLine>();
        if (string.IsNullOrWhiteSpace(outline))
            return lines;
        foreach (Match m in OutlineLineRegex().Matches(outline))
        {
            var index = int.Parse(m.Groups[1].Value);
            var text = m.Groups[2].Value.Trim().Trim('*').Trim();
            if (text.Length > 0)
                lines.TryAdd(index, Split(text));
        }
        return lines;
    }

    // Names keyed by section index.
    public static Dictionary<int, SectionName> For(string materialTitle, string? outline, IEnumerable<(int Index, string Heading)> sections)
    {
        var list = sections.ToList();
        var outlineLines = ParseOutline(outline);
        var baseCounts = list.GroupBy(s => BaseHeading(s.Heading), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var names = new Dictionary<int, SectionName>();
        foreach (var (index, heading) in list)
        {
            var line = outlineLines.GetValueOrDefault(index);
            var baseHeading = BaseHeading(heading);
            var isTitle = baseHeading.Equals(materialTitle.Trim(), StringComparison.OrdinalIgnoreCase);
            var telling = baseHeading.Length > 0 && !isTitle && baseCounts[baseHeading] == 1;

            var name = telling ? heading.Trim()
                : line?.Subject ?? (baseHeading.Length > 0 && !isTitle ? heading.Trim() : $"Part {index}");
            names[index] = new SectionName(name, line?.Concepts);
        }
        return names;
    }

    // A heading that was the material's title ("Notes", "Notes (part 2)") follows the title when
    // the material is renamed; a heading of its own stays.
    public static string Retitle(string heading, string oldTitle, string newTitle)
    {
        var trimmed = heading.Trim();
        var baseHeading = BaseHeading(trimmed);
        return baseHeading.Equals(oldTitle.Trim(), StringComparison.OrdinalIgnoreCase)
            ? newTitle.Trim() + trimmed[baseHeading.Length..]
            : heading;
    }

    // The key concepts of an outline line ("Body acceptance, STI risk factors, and the Three
    // Assurances.") one by one, for material analyzed before the AI listed them itself. Commas
    // inside brackets don't split.
    public static List<string> SplitConcepts(string? concepts)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(concepts))
            return result;

        var text = concepts.Trim().TrimEnd('.');
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ',';
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth = Math.Max(0, depth - 1);
            else if ((c is ',' or ';' && depth == 0) || i == text.Length)
            {
                var part = text[start..i].Trim();
                foreach (var joiner in Joiners)
                {
                    if (part.StartsWith(joiner, StringComparison.OrdinalIgnoreCase))
                        part = part[joiner.Length..].TrimStart();
                }
                if (part.Length > 1 && !result.Contains(part, StringComparer.OrdinalIgnoreCase))
                    result.Add(char.ToUpper(part[0]) + part[1..]);
                start = i + 1;
            }
        }
        return result;
    }

    // What a list's last item starts with after the comma, in the languages topics are mostly in.
    private static readonly string[] Joiners = ["and ", "or ", "und ", "oder "];

    // "Bonds (part 2)" -> "Bonds"
    private static string BaseHeading(string heading) => PartSuffixRegex().Replace(heading.Trim(), "");

    // Subject and key concepts, split at the first ":" or dash; "**Subject**" counts as the subject too.
    private static OutlineLine Split(string text)
    {
        var bold = BoldSubjectRegex().Match(text);
        string subject, rest;
        if (bold.Success)
        {
            subject = bold.Groups[1].Value;
            rest = text[bold.Length..];
        }
        else
        {
            var cut = SeparatorRegex().Match(text);
            subject = cut.Success ? text[..cut.Index] : text;
            rest = cut.Success ? text[cut.Index..] : "";
        }

        subject = subject.Replace("**", "").Trim().TrimEnd(':', '.', ',').Trim();
        rest = rest.TrimStart(':', '-', '–', '—', ' ', '*').Trim();
        if (subject.Length == 0)
            subject = text.Replace("**", "");
        if (subject.Length > MaxSubjectLength)
            subject = subject[..(MaxSubjectLength - 1)].TrimEnd() + "…";
        return new OutlineLine(text, subject, rest.Length > 0 ? rest.Replace("**", "") : null);
    }

    [GeneratedRegex(@"^\s*(?:[-*]\s*)?\[(\d+)\]\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex OutlineLineRegex();

    [GeneratedRegex(@"\s*\(part \d+\)$", RegexOptions.IgnoreCase)]
    private static partial Regex PartSuffixRegex();

    [GeneratedRegex(@"^\*\*(.+?)\*\*")]
    private static partial Regex BoldSubjectRegex();

    [GeneratedRegex(@":|\s[-–—]\s")]
    private static partial Regex SeparatorRegex();
}
