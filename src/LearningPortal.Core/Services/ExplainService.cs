using System.Text.Json;
using System.Text.RegularExpressions;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// A concept the user can explain: one line of a material's outline (a section's subject and key
// concepts), or the section's heading when the material has no outline.
public sealed record ExplainConcept(int SectionId, string MaterialTitle, string Label, string State);

public sealed record ExplainSummary(int Id, string Concept, int? Score, DateTime CreatedAt, bool Completed);

// "Explain it back": the user explains a concept in their own words, the AI asks one or two
// questions about it like a curious beginner, and then assesses the whole explanation. Only the
// concept's own section of the material goes to the AI.
public sealed partial class ExplainService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    StatisticsService statistics)
{
    public async Task<IReadOnlyList<ExplainConcept>> GetConceptsAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Topics.AnyAsync(t => t.Id == topicId && t.UserId == userId, ct))
            throw new NotFoundException();
        var outlines = await db.Materials.AsNoTracking()
            .Where(m => m.UserId == userId && m.TopicId == topicId)
            .Select(m => new { m.Id, m.Outline })
            .ToDictionaryAsync(m => m.Id, m => m.Outline, ct);

        var concepts = new List<ExplainConcept>();
        foreach (var material in await statistics.GetSectionMapAsync(userId, topicId, ct))
        {
            var lines = OutlineLines(outlines.GetValueOrDefault(material.MaterialId));
            foreach (var s in material.Sections)
            {
                var label = lines.GetValueOrDefault(s.Index) ?? (string.IsNullOrWhiteSpace(s.Heading) ? $"Part {s.Index + 1}" : s.Heading.Trim());
                concepts.Add(new ExplainConcept(s.SectionId, material.Title, Truncate(label), StateOf(s)));
            }
        }
        return concepts;
    }

    // Picks a concept at random, favouring the ones the user does worst on or hasn't practised.
    public async Task<ExplainConcept?> SurpriseAsync(string userId, int topicId, CancellationToken ct = default)
    {
        var concepts = await GetConceptsAsync(userId, topicId, ct);
        if (concepts.Count == 0)
            return null;
        var weights = concepts.Select(c => c.State switch { "weak" => 4, "new" or "empty" => 3, "shaky" => 2, _ => 1 }).ToList();
        var pick = Random.Shared.Next(weights.Sum());
        for (var i = 0; i < concepts.Count; i++)
        {
            pick -= weights[i];
            if (pick < 0)
                return concepts[i];
        }
        return concepts[^1];
    }

    public async Task<int> StartAsync(string userId, int topicId, int sectionId, string concept, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.MaterialSections.AnyAsync(s => s.Id == sectionId && s.Material!.UserId == userId && s.Material.TopicId == topicId, ct))
            throw new NotFoundException();
        var session = new ExplainSession { UserId = userId, TopicId = topicId, SectionId = sectionId, Concept = Truncate(concept.Trim()) };
        db.ExplainSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    public async Task<ExplainSession> GetAsync(string userId, int sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExplainSessions.AsNoTracking().Include(s => s.Topic)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct) ?? throw new NotFoundException();
    }

    public async Task<IReadOnlyList<ExplainSummary>> ListAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ExplainSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.TopicId == topicId && s.Explanation != null)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ExplainSummary(s.Id, s.Concept, s.Score, s.CreatedAt, s.CompletedAt != null))
            .ToListAsync(ct);
    }

    public async Task DeleteAsync(string userId, int sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ExplainSessions.Where(s => s.Id == sessionId && s.UserId == userId).ExecuteDeleteAsync(ct);
    }

    // Saves the explanation and has the AI ask its questions about it.
    // choice is the model picked on the page (advanced mode); null uses the questions default.
    public async Task<ExplainSession> ExplainAsync(string userId, int sessionId, string explanation, AiChoice? choice, CancellationToken ct)
    {
        var text = explanation.Trim();
        if (text.Length == 0)
            throw new ArgumentException("Write your explanation first.");
        if (text.Length > ExplainSession.MaxExplanationLength)
            throw new ArgumentException($"Keep the explanation under {ExplainSession.MaxExplanationLength} characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await LoadAsync(db, userId, sessionId, ct);
        if (session.Questions.Count > 0)
            throw new InvalidOperationException("This explanation already has its questions.");
        var (language, source) = await ContextAsync(db, session, ct);

        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var json = await AiClientFactory.Create(connection).CompleteJsonAsync(new AiRequest
        {
            System = Prompts.ExplainQuestionsSystem(language),
            Prompt = Prompts.ExplainPrompt(session.Concept, source, text),
            SchemaName = "explain_questions",
            Schema = Prompts.ExplainQuestionsSchema,
            MaxTokens = 2_000,
        }, ct);

        List<string> questions;
        try
        {
            using var doc = JsonDocument.Parse(json);
            questions = doc.RootElement.GetProperty("questions").EnumerateArray()
                .Select(q => q.GetString()?.Trim() ?? "").Where(q => q.Length > 0).Take(2).ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiException("The AI's questions couldn't be read. Try again.");
        }
        if (questions.Count == 0)
            throw new AiException("The AI didn't ask anything. Try again.");

        session.Explanation = text;
        session.QuestionsJson = JsonSerializer.Serialize(questions);
        session.Model = connection.Model;
        await db.SaveChangesAsync(CancellationToken.None);
        return session;
    }

    // Saves the replies to the AI's questions and has it assess the whole explanation.
    public async Task<ExplainSession> ReplyAsync(string userId, int sessionId, IReadOnlyList<string> replies, AiChoice? choice, CancellationToken ct)
    {
        var cleaned = replies.Select(r => r.Trim()).ToList();
        if (cleaned.Any(r => r.Length > ExplainSession.MaxReplyLength))
            throw new ArgumentException($"Keep each reply under {ExplainSession.MaxReplyLength} characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await LoadAsync(db, userId, sessionId, ct);
        if (session.Explanation is null || session.Questions.Count == 0)
            throw new InvalidOperationException("Write your explanation first.");
        if (session.CompletedAt is not null)
            throw new InvalidOperationException("This explanation has already been assessed.");
        var (language, source) = await ContextAsync(db, session, ct);

        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var json = await AiClientFactory.Create(connection).CompleteJsonAsync(new AiRequest
        {
            System = Prompts.ExplainAssessmentSystem(language),
            Prompt = Prompts.ExplainPrompt(session.Concept, source, session.Explanation, session.Questions, cleaned),
            SchemaName = "explain_assessment",
            Schema = Prompts.ExplainAssessmentSchema,
            MaxTokens = 4_000,
        }, ct);

        try
        {
            using var doc = JsonDocument.Parse(json);
            session.Score = Math.Clamp(doc.RootElement.GetProperty("score").GetInt32(), 0, 100);
            session.Feedback = doc.RootElement.GetProperty("feedback").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AiException("The AI's assessment couldn't be read. Try again.");
        }

        session.RepliesJson = JsonSerializer.Serialize(cleaned);
        session.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        return session;
    }

    private static async Task<ExplainSession> LoadAsync(AppDbContext db, string userId, int sessionId, CancellationToken ct) =>
        await db.ExplainSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct) ?? throw new NotFoundException();

    private static async Task<(string Language, string? Source)> ContextAsync(AppDbContext db, ExplainSession session, CancellationToken ct)
    {
        var language = await db.Topics.Where(t => t.Id == session.TopicId).Select(t => t.Language).FirstAsync(ct);
        var source = session.SectionId is { } sid
            ? await db.MaterialSections.Where(s => s.Id == sid).Select(s => s.Text).FirstOrDefaultAsync(ct)
            : null;
        return (language, source);
    }

    // Outline lines look like "[3] Subject: key concepts" (see Prompts.OutlinePrompt).
    private static Dictionary<int, string> OutlineLines(string? outline)
    {
        var lines = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(outline))
            return lines;
        foreach (Match m in OutlineLine().Matches(outline))
        {
            var index = int.Parse(m.Groups[1].Value);
            var text = m.Groups[2].Value.Trim().Trim('*').Trim();
            if (text.Length > 0)
                lines.TryAdd(index, text);
        }
        return lines;
    }

    [GeneratedRegex(@"^\s*(?:[-*]\s*)?\[(\d+)\]\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex OutlineLine();

    private static string StateOf(SectionScore s) => s.QuestionCount == 0 ? "empty"
        : s.AverageScore is not { } avg ? "new"
        : avg >= VerdictRules.CorrectThreshold ? "solid"
        : avg >= 50 ? "shaky"
        : "weak";

    private static string Truncate(string s) => s.Length <= ExplainSession.MaxConceptLength ? s : s[..(ExplainSession.MaxConceptLength - 3)] + "...";
}
