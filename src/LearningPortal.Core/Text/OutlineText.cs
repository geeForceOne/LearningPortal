using System.Text.RegularExpressions;

namespace LearningPortal.Core.Text;

// A material's outline as the AI writes it (Prompts.OutlinePrompt): one line per section that
// starts with the section index in square brackets, maybe a line on top (the programming
// language). Splitting it by section lets an outline written in several calls, or resumed after
// a failed one, be put back together in section order.
public sealed partial record OutlineText(IReadOnlyList<string> Preamble, IReadOnlyDictionary<int, string> Lines)
{
    public static OutlineText Parse(string? outline)
    {
        var preamble = new List<string>();
        var lines = new Dictionary<int, string>();
        int? current = null;
        foreach (var raw in (outline ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
                continue;
            if (SectionStart().Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var index))
            {
                current = index;
                // A repeated index adds to its line rather than replacing it.
                lines[index] = lines.TryGetValue(index, out var before) ? before + "\n" + line.Trim() : line.Trim();
            }
            else if (current is { } open)
                lines[open] += "\n" + line.TrimEnd();
            else
                preamble.Add(line.Trim());
        }
        return new OutlineText(preamble, lines);
    }

    // Preamble first, then the section lines in index order, then any outline that didn't use
    // section numbers at all.
    public static string Compose(IEnumerable<string> preamble, IReadOnlyDictionary<int, string> lines, IEnumerable<string> loose) =>
        string.Join("\n", preamble
            .Concat(lines.OrderBy(l => l.Key).Select(l => l.Value))
            .Concat(loose)).Trim();

    [GeneratedRegex(@"^\s*\[(\d+)\]")]
    private static partial Regex SectionStart();
}
