using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// A topic's material as loaded for answering questions the user brought (an uploaded exam paper,
// Check an answer).
internal sealed record TopicMaterial(Topic Topic, IReadOnlyList<Material> Materials)
{
    public IEnumerable<MaterialSection> Sections => Materials.SelectMany(m => m.Sections);

    public int TotalTokens => Sections.Sum(s => s.TokenEstimate);

    public MaterialSection? Section(int id) => Sections.FirstOrDefault(s => s.Id == id);

    public Material MaterialOf(MaterialSection section) => Materials.First(m => m.Id == section.MaterialId);

    public string Label(MaterialSection section) => QuestionGenerationService.SourceLabel(MaterialOf(section), section);

    // The material context for these sections, grouped by material in reading order.
    public string Context(IEnumerable<MaterialSection> sections) =>
        Prompts.MaterialContext(sections.GroupBy(s => s.MaterialId)
            .Select(g => (MaterialOf(g.First()), (IReadOnlyList<MaterialSection>)g.OrderBy(s => s.Index).ToList())));
}

// What a web search found for one question: notes on the answer and the pages they come from.
internal sealed record ResearchNote(string Text, IReadOnlyList<WebSource> Sources);

// The steps that answering a brought question has in common: finding the sections of the topic's
// material that answer it, and looking it up on the web when they don't.
internal static class SourceFinder
{
    // Web searches allowed per question looked up; a hard cap per call keeps the bill bounded.
    public const int SearchesPerQuestion = 2;
    public const int MaxSearchesPerCall = 10;

    // Questions looked up in one web research call.
    public const int ResearchBatch = 5;

    // Notes kept per question, enough for grading and follow-ups to go on.
    private const int MaxNoteLength = 3_000;
    private const int MaxSourcesPerQuestion = 5;

    public static async Task<TopicMaterial> LoadAsync(AppDbContext db, string userId, int topicId, CancellationToken ct)
    {
        var topic = await db.Topics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == topicId && t.UserId == userId, ct)
            ?? throw new NotFoundException();
        var materials = await db.Materials.AsNoTracking()
            .Where(m => m.TopicId == topicId && m.UserId == userId)
            .Include(m => m.Sections)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        foreach (var m in materials)
            m.Sections = m.Sections.OrderBy(s => s.Index).ToList();
        return new TopicMaterial(topic, materials);
    }

    // The sections to send for answering these questions: all of them when the material fits in
    // directBudget tokens, else the ones a short call over the outlines picks (best matches first,
    // up to budget tokens). Empty when the material has nothing for any of the questions.
    public static async Task<List<MaterialSection>> PickAsync(
        IAiClient client, TopicMaterial material, IReadOnlyList<string> questions, int directBudget, int budget, CancellationToken ct)
    {
        var all = material.Sections.ToList();
        if (all.Count == 0)
            return [];
        if (material.TotalTokens <= directBudget)
            return all;

        var json = await client.CompleteJsonAsync(new AiRequest
        {
            System = Prompts.LocateSystem,
            Prompt = Prompts.LocatePrompt(material.Materials, all.Select(s => (s, material.Label(s))), questions),
            SchemaName = "section_matches",
            Schema = Prompts.LocateSchema,
            MaxTokens = 4_000,
        }, ct);

        // Every question's first choice before anyone's second, so each question gets its best
        // section when the budget runs out.
        var ranked = new List<(int Rank, int SectionId)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var match in doc.RootElement.GetProperty("matches").EnumerateArray())
            {
                var rank = 0;
                foreach (var id in match.GetProperty("section_indexes").EnumerateArray())
                    if (id.TryGetInt32(out var sectionId))
                        ranked.Add((rank++, sectionId));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiException("The AI's answer couldn't be read. Try again.");
        }

        var picked = new List<MaterialSection>();
        var tokens = 0;
        foreach (var id in ranked.OrderBy(r => r.Rank).Select(r => r.SectionId).Distinct())
        {
            if (material.Section(id) is not { } section || (picked.Count > 0 && tokens + section.TokenEstimate > budget))
                continue;
            picked.Add(section);
            tokens += section.TokenEstimate;
        }
        return picked;
    }

    // Looks the questions up on the web in one call and splits the answer per question, by the
    // markers the AI was asked to put before each part. A question it skipped gets an empty note.
    public static async Task<List<ResearchNote>> ResearchAsync(
        IAiClient client, string language, IReadOnlyList<Prompts.BroughtQuestion> questions, CancellationToken ct)
    {
        var research = await client.ResearchAsync(new WebResearchRequest
        {
            System = Prompts.ResearchSystem(language),
            Prompt = Prompts.ResearchPrompt(questions),
            MaxSearches = Math.Min(MaxSearchesPerCall, questions.Count * SearchesPerQuestion),
        }, ct);

        var starts = Enumerable.Range(1, questions.Count)
            .Select(n => research.Text.IndexOf(Prompts.ResearchMarker(n), StringComparison.Ordinal))
            .ToList();
        // A single question needs no marker to tell its part apart.
        if (questions.Count == 1 && starts[0] < 0)
            starts[0] = 0;

        var notes = new List<ResearchNote>();
        for (var i = 0; i < questions.Count; i++)
        {
            if (starts[i] < 0)
            {
                notes.Add(new ResearchNote("", []));
                continue;
            }
            var start = starts[i];
            var end = starts.Where(s => s > start).DefaultIfEmpty(research.Text.Length).Min();
            var text = research.Text[start..end].Replace(Prompts.ResearchMarker(i + 1), "").Trim();
            var sources = research.Citations
                .Where(c => c.Position >= start && c.Position < end)
                .DistinctBy(c => c.Url)
                .Take(MaxSourcesPerQuestion)
                .Select(c => new WebSource(c.Title, c.Url))
                .ToList();
            notes.Add(new ResearchNote(text.Length > MaxNoteLength ? text[..MaxNoteLength] : text, sources));
        }
        return notes;
    }

    // Upper bound for a web lookup of this many questions: the searches, plus the pages read and
    // the answers written, at the model's price. Null cost for a model without a known price.
    public static (int Searches, decimal? Cost) ResearchCost(int questions, AiModelInfo? model)
    {
        var searches = 0;
        decimal? cost = 0m;
        for (var left = questions; left > 0; left -= ResearchBatch)
        {
            var batch = Math.Min(left, ResearchBatch);
            var batchSearches = Math.Min(MaxSearchesPerCall, batch * SearchesPerQuestion);
            searches += batchSearches;
            // Search results come back as input; each search adds a few thousand tokens. The
            // answering call that follows reads the notes again.
            var input = batchSearches * 4_000 + batch * 1_500;
            var output = batch * 1_500;
            cost = model is null ? null
                : cost + model.Cost(new AiUsage(input, 0, 0, output, batchSearches));
        }
        return (searches, cost);
    }
}
