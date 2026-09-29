using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed record Gap(string Title, string Why);

// The gap finder: the AI compares a topic's material (its outlines) with what the subject usually
// covers and lists what's missing; for each gap it can write a short study note, added to the topic
// as material marked "written by the AI". Both come from the AI's general knowledge, not the user's
// sources, so the UI says they can be wrong.
public sealed class GapService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    MaterialService materials)
{
    // choice is the model picked on the page (advanced mode); null uses the questions default.
    public async Task<IReadOnlyList<Gap>> FindAsync(string userId, int topicId, AiChoice? choice, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var topic = await db.Topics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == topicId && t.UserId == userId, ct)
            ?? throw new NotFoundException();
        var outlines = await db.Materials.AsNoTracking()
            .Where(m => m.UserId == userId && m.TopicId == topicId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Title, m.Outline })
            .ToListAsync(ct);
        if (outlines.Count == 0)
            throw new InvalidOperationException("Add material to this topic first.");

        var json = await AiClientFactory.Create(await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct)).CompleteJsonAsync(new AiRequest
        {
            System = Prompts.GapsSystem(topic.Language),
            Prompt = Prompts.GapsPrompt(topic.Name, topic.Description, outlines.Select(o => (o.Title, o.Outline))),
            SchemaName = "gaps",
            Schema = Prompts.GapsSchema,
            MaxTokens = 3_000,
        }, ct);

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("gaps").EnumerateArray()
                .Select(g => new Gap(g.GetProperty("title").GetString()?.Trim() ?? "", g.GetProperty("why").GetString()?.Trim() ?? ""))
                .Where(g => g.Title.Length > 0)
                .Take(10)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiException("The AI's answer couldn't be read. Try again.");
        }
    }

    // Writes a study note for one gap and adds it to the topic as material. Returns the new material.
    public async Task<AddedMaterial> WriteNoteAsync(string userId, int topicId, Gap gap, AiChoice? choice, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var topic = await db.Topics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == topicId && t.UserId == userId, ct)
            ?? throw new NotFoundException();

        var json = await AiClientFactory.Create(await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct)).CompleteJsonAsync(new AiRequest
        {
            System = Prompts.NoteSystem(topic.Language),
            Prompt = Prompts.NotePrompt(topic.Name, gap.Title, gap.Why),
            SchemaName = "study_note",
            Schema = Prompts.NoteSchema,
            MaxTokens = 4_000,
        }, ct);

        string note;
        try
        {
            using var doc = JsonDocument.Parse(json);
            note = doc.RootElement.GetProperty("note").GetString()?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiException("The AI's note couldn't be read. Try again.");
        }
        if (note.Length == 0)
            throw new AiException("The AI returned an empty note. Try again.");

        var title = $"AI note: {gap.Title}";
        var added = await materials.AddTextAsync(userId, topicId, title.Length > 300 ? title[..300] : title, note, CancellationToken.None);
        await db.Materials.Where(m => m.Id == added.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.IsAiWritten, true), CancellationToken.None);
        return added;
    }
}
