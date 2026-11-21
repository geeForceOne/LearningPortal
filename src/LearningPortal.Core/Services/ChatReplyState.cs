using LearningPortal.Core.Ai;

namespace LearningPortal.Core.Services;

// Reads the marker line a reply from the material starts with (see Prompts.ChatSectionsMarker)
// while the reply streams in, and gives the text the student sees without it.
public sealed record ChatReplyState(bool Decided, bool NotCovered, IReadOnlyList<int> SectionIds, string Visible)
{
    // Long enough for any marker; a reply that has no "]]" by then has no marker.
    private const int MaxMarkerLength = 300;

    public static ChatReplyState Parse(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return new(false, false, [], "");

        // A reply that doesn't open with "[[" has no marker: all of it is the answer.
        var opener = trimmed.Length >= 2 ? trimmed[..2] : trimmed;
        if (!"[[".StartsWith(opener, StringComparison.Ordinal))
            return new(true, false, [], text.Trim());

        var end = trimmed.IndexOf("]]", StringComparison.Ordinal);
        if (end < 0)
            return trimmed.Length > MaxMarkerLength ? new(true, false, [], text.Trim()) : new(false, false, [], "");

        var marker = trimmed[..(end + 2)];
        var rest = trimmed[(end + 2)..].Trim();
        if (marker.Replace(" ", "").Equals(Prompts.ChatNotCoveredMarker.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            return new(true, true, [], "");

        var ids = new List<int>();
        if (marker.StartsWith(Prompts.ChatSectionsMarker, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in marker[Prompts.ChatSectionsMarker.Length..^2].Split(',', ' ', ';'))
                if (int.TryParse(part, out var id) && !ids.Contains(id))
                    ids.Add(id);
        }
        return new(true, false, ids, rest);
    }
}
