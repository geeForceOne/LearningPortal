using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Core.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LearningPortal.Core.Services;

public sealed record MaterialDetail(Material Material, IReadOnlyList<MaterialSection> Sections);

// A newly stored material and its size, so the caller can say what analyzing it will cost.
public sealed record AddedMaterial(int Id, int TokenEstimate);

// Ingests study material: stores the file, extracts its text once, splits it into sections and
// asks the AI for an outline. Everything later (generation, grading) works from the stored text.
public sealed class MaterialService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    LearningPortalOptions options,
    ILogger<MaterialService> logger)
{
    public async Task<AddedMaterial> AddFileAsync(
        string userId, int topicId, string fileName, Stream content, long sizeBytes,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (sizeBytes > options.MaxUploadBytes)
            throw new UnsupportedMaterialException(
                $"\"{fileName}\" is {sizeBytes / 1024.0 / 1024.0:0.#} MB. Files can be at most {options.MaxUploadBytes / 1024 / 1024} MB.");

        var kind = DocumentTextExtractor.KindFromFileName(fileName);
        await EnsureTopicAsync(userId, topicId, ct);

        progress?.Report($"Saving {fileName}...");
        var relativePath = Path.Combine(userId, $"{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}");
        var fullPath = Path.Combine(options.FilesRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using (var file = File.Create(fullPath))
            await content.CopyToAsync(file, ct);

        string text;
        try
        {
            progress?.Report($"Reading the text in {fileName}...");
            await using var stored = File.OpenRead(fullPath);
            text = await DocumentTextExtractor.ExtractAsync(stored, kind, ct);
        }
        catch
        {
            TryDeleteFile(options, relativePath);
            throw;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            TryDeleteFile(options, relativePath);
            throw new UnsupportedMaterialException($"No text was found in \"{fileName}\".");
        }

        return await StoreAsync(userId, topicId, Path.GetFileNameWithoutExtension(fileName), kind, fileName, relativePath, sizeBytes, text, ct);
    }

    public async Task<AddedMaterial> AddTextAsync(string userId, int topicId, string title, string text, CancellationToken ct)
    {
        await EnsureTopicAsync(userId, topicId, ct);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            throw new UnsupportedMaterialException("The pasted text is empty.");

        return await StoreAsync(userId, topicId, title, MaterialKind.Text, null, null, trimmed.Length, trimmed, ct);
    }

    private async Task<AddedMaterial> StoreAsync(
        string userId, int topicId, string title, MaterialKind kind, string? originalName, string? storedPath,
        long size, string text, CancellationToken ct)
    {
        var drafts = MaterialSectioner.Split(text, title, options.SectionTargetTokens);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = new Material
        {
            UserId = userId,
            TopicId = topicId,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled material" : title.Trim(),
            Kind = kind,
            OriginalFileName = originalName,
            StoredFilePath = storedPath,
            SizeBytes = size,
            ExtractedText = text,
            TokenEstimate = TokenEstimator.Estimate(text),
            Sections = drafts.Select((d, i) => new MaterialSection
            {
                Index = i + 1,
                Heading = d.Heading,
                Text = d.Text,
                TokenEstimate = TokenEstimator.Estimate(d.Text),
            }).ToList(),
        };
        db.Materials.Add(material);
        await TouchTopicAsync(db, topicId, ct);
        await db.SaveChangesAsync(ct);
        return new AddedMaterial(material.Id, material.TokenEstimate);
    }

    // The "analysis" step: a short AI outline of the material, stored for reuse. Failure is
    // recorded on the material rather than thrown, so the upload itself still counts. choice is
    // the model picked on the upload screen (advanced mode); null uses the uploads default.
    //
    // Nothing paid for is thrown away: each call covers only the sections still missing, a
    // cut-off answer keeps the sections it finished, and after a failure the finished sections'
    // outline lines are saved, so "Try again" picks up where the last run stopped.
    public async Task AnalyzeAsync(string userId, int materialId, AiChoice? choice, IProgress<string>? progress, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await db.Materials
            .Include(m => m.Sections)
            .Include(m => m.Topic)
            .FirstOrDefaultAsync(m => m.Id == materialId && m.UserId == userId, ct) ?? throw new NotFoundException();

        var sections = material.Sections.OrderBy(s => s.Index).ToList();
        var saved = OutlineText.Parse(material.Outline);
        var lines = new Dictionary<int, string>();
        var preamble = new List<string>();
        var loose = new List<string>();
        var done = new HashSet<int>();

        // A failed run saved the lines of the sections it finished; those that also have their
        // concepts are kept. A finished analysis ("Analyze it again") starts over.
        if (material.AnalysisStatus == AnalysisStatus.Failed)
        {
            foreach (var s in sections)
                if (saved.Lines.TryGetValue(s.Index, out var line) && s.ConceptsJson is not null)
                {
                    lines[s.Index] = line;
                    done.Add(s.Index);
                }
            if (done.Count > 0)
                preamble.AddRange(saved.Preamble);
        }

        try
        {
            var connection = await settings.GetConnectionAsync(userId, AiTask.Analysis, choice, ct);
            var client = AiClientFactory.Create(connection);

            while (sections.Where(s => !done.Contains(s.Index)).ToList() is { Count: > 0 } pending)
            {
                var batch = BatchByTokens(pending, options.TopicTokenBudget, OutlineSectionsPerCall)[0];
                progress?.Report(batch.Count == sections.Count
                    ? $"Analyzing {material.Title}..."
                    : $"Analyzing {material.Title} ({done.Count} of {sections.Count} sections done)...");

                var request = new AiRequest
                {
                    System = Prompts.OutlineSystem(material.Topic!.Language),
                    Prompt = Prompts.OutlinePrompt(material.Title, batch),
                    SchemaName = "material_outline",
                    Schema = Prompts.OutlineSchema,
                    MaxTokens = OutlineMaxTokens,
                };
                string json;
                var cut = false;
                try
                {
                    json = await client.CompleteJsonAsync(request, ct);
                }
                catch (AiCutOffException ex) when (SalvageOutline(ex.Partial) is { } salvaged)
                {
                    json = salvaged;
                    cut = true;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // A whole answer must have its outline; a cut-off one may have stopped before it.
                var outlineText = cut
                    ? root.TryGetProperty("outline", out var o) ? o.GetString() : null
                    : root.GetProperty("outline").GetString();
                var answer = OutlineText.Parse(outlineText);
                var withConcepts = ReadConcepts(root, batch);
                // A whole answer finishes its sections, also those it gave no concepts; a cut-off
                // one only those whose concepts it got through.
                var finished = cut ? withConcepts : batch.Select(s => s.Index).ToHashSet();
                if (finished.Count == 0)
                    throw new AiCutOffException("The AI's answer was cut off before it finished a section.");
                if (cut)
                    logger.LogInformation("Outline of material {MaterialId} was cut off; kept {Kept} of {Sent} sections", material.Id, finished.Count, batch.Count);

                foreach (var index in finished)
                    if (answer.Lines.TryGetValue(index, out var line))
                        lines[index] = line;
                if (!cut && answer.Lines.Count == 0 && !string.IsNullOrWhiteSpace(outlineText))
                    loose.Add(outlineText.Trim());
                if (preamble.Count == 0)
                    preamble.AddRange(answer.Preamble);
                done.UnionWith(finished);
            }

            material.Outline = OutlineText.Compose(preamble, lines, loose);
            material.OutlineModel = connection.Model;
            material.AnalysisStatus = AnalysisStatus.Done;
            material.AnalysisError = null;
        }
        catch (Exception ex) when (ex is AiException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (ex is not AiException || ex is AiCutOffException)
                logger.LogWarning(ex, "Outline of material {MaterialId} ({Sections} sections, {Tokens} tokens) failed with {Done} sections done", material.Id, sections.Count, material.TokenEstimate, done.Count);
            // Keep what this run finished for "Try again", and the earlier lines of the rest.
            if (done.Count > 0)
            {
                foreach (var (index, line) in saved.Lines)
                    lines.TryAdd(index, line);
                material.Outline = OutlineText.Compose(preamble.Count > 0 ? preamble : saved.Preamble, lines, []);
            }
            material.AnalysisStatus = AnalysisStatus.Failed;
            material.AnalysisError = ex switch
            {
                AiCutOffException => "The AI's outline was cut off because it ran too long. Try again: the sections it finished are kept.",
                AiException => ex.Message,
                _ => "The AI's outline couldn't be read.",
            };
        }

        await db.SaveChangesAsync(CancellationToken.None);
        // Question source labels use the outline's subjects for sections without a heading.
        if (material.AnalysisStatus == AnalysisStatus.Done)
            await RefreshSourceLabelsAsync(db, materialId, CancellationToken.None);
    }

    // The concepts to explain, per section (see Prompts.OutlinePrompt). A section the AI left out
    // keeps what it had, so Explain it back falls back to the outline line for it. Returns the
    // sections it found concepts for.
    private static HashSet<int> ReadConcepts(JsonElement root, IReadOnlyList<MaterialSection> sections)
    {
        var found = new HashSet<int>();
        if (!root.TryGetProperty("concepts", out var lists) || lists.ValueKind != JsonValueKind.Array)
            return found;
        foreach (var item in lists.EnumerateArray())
        {
            if (!item.TryGetProperty("section_index", out var index) || !index.TryGetInt32(out var sectionIndex)
                || !item.TryGetProperty("concepts", out var concepts) || concepts.ValueKind != JsonValueKind.Array
                || sections.FirstOrDefault(s => s.Index == sectionIndex) is not { } section)
                continue;
            var cleaned = concepts.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object)
                .Select(c => new
                {
                    name = Clip(c.TryGetProperty("name", out var n) ? n.GetString() : null, ExplainSession.MaxConceptLength),
                    task = Clip(c.TryGetProperty("task", out var t) ? t.GetString() : null, SectionConcept.MaxTaskLength),
                })
                .Where(c => c.name.Length > 0)
                .DistinctBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxConceptsPerSection)
                .ToList();
            section.ConceptsJson = JsonSerializer.Serialize(cleaned);
            found.Add(sectionIndex);
        }
        return found;
    }

    // Turns a cut-off outline answer into valid JSON holding only what was complete: everything
    // up to the last section entry in "concepts" that was closed. The schema puts "outline"
    // first, so it's whole by then. Null when no section entry was finished.
    internal static string? SalvageOutline(string? partial)
    {
        if (string.IsNullOrEmpty(partial))
            return null;
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        var cut = -1;
        for (var i = 0; i < partial.Length; i++)
        {
            var c = partial[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[': stack.Push(c); break;
                case '}' or ']':
                    if (stack.Count == 0)
                        return null;
                    var open = stack.Pop();
                    // A section entry: an object closed straight inside the root's "concepts" array.
                    if (open == '{' && c == '}' && stack.Count == 2 && stack.Peek() == '[')
                        cut = i + 1;
                    break;
            }
        }
        return cut < 0 ? null : partial[..cut] + "]}";
    }

    private const int MaxConceptsPerSection = 8;

    // The outline answer grows with the section count (an outline line plus up to 6 concepts with
    // a task each), not with the text's size: 34 sections of a dense legal text ran past 12,000
    // output tokens. Capping the sections per call keeps each answer well inside the limit.
    private const int OutlineSectionsPerCall = 20;
    private const int OutlineMaxTokens = 20_000;

    private static string Clip(string? text, int max) =>
        text is null ? "" : text.Trim() is var t && t.Length <= max ? t : text.Trim()[..max];

    public async Task<MaterialDetail> GetAsync(string userId, int materialId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await db.Materials.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == materialId && m.UserId == userId, ct) ?? throw new NotFoundException();
        var sections = await db.MaterialSections.AsNoTracking()
            .Where(s => s.MaterialId == materialId)
            .OrderBy(s => s.Index)
            .ToListAsync(ct);
        return new MaterialDetail(material, sections);
    }

    // Questions store where they came from as text ("Title / Section"), and sections of material
    // without headings are named after the material's first title. Both are brought back in step
    // here: for material renamed before 1.11.0 (a file's first title is its file name), and for
    // labels written before 1.10.0 ("Title / Title (part 3)"). A no-op once they're fixed.
    public async Task RefreshSourceLabelsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var files = await db.Materials.AsNoTracking()
            .Where(m => m.OriginalFileName != null)
            .Select(m => new { m.Id, m.Title, m.OriginalFileName })
            .ToListAsync(ct);
        foreach (var file in files)
        {
            var firstTitle = Path.GetFileNameWithoutExtension(file.OriginalFileName!);
            if (string.Equals(firstTitle, file.Title, StringComparison.OrdinalIgnoreCase))
                continue;
            var headings = await db.MaterialSections.Where(s => s.MaterialId == file.Id).Select(s => new { s.Id, s.Heading }).ToListAsync(ct);
            foreach (var h in headings)
            {
                var retitled = SectionNames.Retitle(h.Heading, firstTitle, file.Title);
                if (retitled != h.Heading)
                    await db.MaterialSections.Where(s => s.Id == h.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Heading, retitled), ct);
            }
        }

        await RefreshSourceLabelsAsync(db, null, ct);
    }

    private static async Task RefreshSourceLabelsAsync(AppDbContext db, int? materialId, CancellationToken ct)
    {
        var questions = await db.Questions
            .Where(q => q.SourceSectionId != null && q.SourceMaterialId != null && (materialId == null || q.SourceMaterialId == materialId))
            .ToListAsync(ct);
        if (questions.Count == 0)
            return;

        var materialIds = questions.Select(q => q.SourceMaterialId!.Value).Distinct().ToList();
        var materials = await db.Materials.AsNoTracking()
            .Where(m => materialIds.Contains(m.Id))
            .Select(m => new Material
            {
                Id = m.Id, Title = m.Title, Outline = m.Outline,
                Sections = m.Sections.Select(s => new MaterialSection { Id = s.Id, MaterialId = s.MaterialId, Index = s.Index, Heading = s.Heading }).ToList(),
            })
            .ToDictionaryAsync(m => m.Id, ct);

        foreach (var q in questions)
        {
            if (materials.TryGetValue(q.SourceMaterialId!.Value, out var material)
                && material.Sections.FirstOrDefault(s => s.Id == q.SourceSectionId) is { } section)
            {
                var label = QuestionGenerationService.SourceLabel(material, section);
                if (q.SourceLabel != label)
                    q.SourceLabel = label;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // Renaming also renames what was named after the material: its sections without a heading of
    // their own, and the "From ..." label on the questions written from it.
    public async Task RenameAsync(string userId, int materialId, string title, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await db.Materials.Include(m => m.Sections)
            .FirstOrDefaultAsync(m => m.Id == materialId && m.UserId == userId, ct) ?? throw new NotFoundException();
        var oldTitle = material.Title;
        material.Title = title.Trim();
        foreach (var section in material.Sections)
            section.Heading = SectionNames.Retitle(section.Heading, oldTitle, material.Title);
        await db.SaveChangesAsync(ct);
        await RefreshSourceLabelsAsync(db, materialId, ct);
    }

    public async Task DeleteAsync(string userId, int materialId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await db.Materials.FirstOrDefaultAsync(m => m.Id == materialId && m.UserId == userId, ct) ?? throw new NotFoundException();
        var path = material.StoredFilePath;
        db.Materials.Remove(material);
        await TouchTopicAsync(db, material.TopicId, ct);
        await db.SaveChangesAsync(ct);
        if (path is not null)
            TryDeleteFile(options, path);
    }

    internal static List<List<MaterialSection>> BatchByTokens(IReadOnlyList<MaterialSection> sections, int budget, int maxSections = int.MaxValue)
    {
        var batches = new List<List<MaterialSection>>();
        var current = new List<MaterialSection>();
        var tokens = 0;
        foreach (var s in sections)
        {
            if (current.Count > 0 && (tokens + s.TokenEstimate > budget || current.Count >= maxSections))
            {
                batches.Add(current);
                current = [];
                tokens = 0;
            }
            current.Add(s);
            tokens += s.TokenEstimate;
        }
        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    internal static void TryDeleteFile(LearningPortalOptions options, string relativePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(options.FilesRoot, relativePath));
            var root = Path.GetFullPath(options.FilesRoot);
            if (full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full))
                File.Delete(full);
        }
        catch (IOException)
        {
            // A leftover file is harmless; the database row is what matters.
        }
    }

    private async Task EnsureTopicAsync(string userId, int topicId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Topics.AnyAsync(t => t.Id == topicId && t.UserId == userId, ct))
            throw new NotFoundException();
    }

    private static async Task TouchTopicAsync(AppDbContext db, int topicId, CancellationToken ct)
    {
        var topic = await db.Topics.FindAsync([topicId], ct);
        if (topic is not null)
            topic.UpdatedAt = DateTime.UtcNow;
    }
}
