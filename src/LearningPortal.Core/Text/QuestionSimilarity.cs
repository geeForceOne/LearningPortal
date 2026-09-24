using System.Globalization;
using System.Text;

namespace LearningPortal.Core.Text;

// Cheap near-duplicate detection for question prompts, without an AI call. Catches repeats and
// light rewordings (same words, different order or punctuation); a question that asks the same
// thing in entirely different words needs the prompt's "don't repeat" list instead.
// Works for any language: it compares words, ignoring case and punctuation.
public sealed class QuestionSimilarity
{
    // Share of distinct words two prompts must have in common (Jaccard) to count as the same question.
    public const double DuplicateThreshold = 0.9;

    // Shorter prompts have too few words for the overlap to mean anything; only exact matches count.
    private const int MinWordsForOverlap = 4;

    private readonly List<HashSet<string>> _known = [];
    private readonly HashSet<string> _knownExact = [];

    public QuestionSimilarity(IEnumerable<string> prompts)
    {
        foreach (var p in prompts)
            Add(p);
    }

    public void Add(string prompt)
    {
        var words = Words(prompt);
        _knownExact.Add(string.Join(' ', words));
        if (words.Count >= MinWordsForOverlap)
            _known.Add(words.ToHashSet());
    }

    public bool IsDuplicate(string prompt)
    {
        var words = Words(prompt);
        if (words.Count == 0 || _knownExact.Contains(string.Join(' ', words)))
            return true;
        if (words.Count < MinWordsForOverlap)
            return false;

        var set = words.ToHashSet();
        foreach (var other in _known)
        {
            // Sets this different in size can't reach the threshold; skip the intersection.
            if (Math.Min(set.Count, other.Count) < DuplicateThreshold * Math.Max(set.Count, other.Count))
                continue;
            var shared = set.Count(other.Contains);
            if (shared / (double)(set.Count + other.Count - shared) >= DuplicateThreshold)
                return true;
        }
        return false;
    }

    // Lower-cased words of letters, digits and combining marks. Marks stay: in many scripts (Hindi,
    // Thai, ...) they change the word, so dropping them would merge different questions.
    // Languages written without spaces (Chinese, Japanese) come out as one long word, so for them
    // only exact repeats are caught.
    internal static List<string> Words(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var isMark = CharUnicodeInfo.GetUnicodeCategory(c)
                is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            sb.Append(char.IsLetterOrDigit(c) || isMark ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
