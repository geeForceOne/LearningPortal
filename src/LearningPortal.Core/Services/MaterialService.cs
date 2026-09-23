using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Core.Text;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed record MaterialDetail(Material Material, IReadOnlyList<MaterialSection> Sections);

// A newly stored material and its size, so the caller can say what analysing it will cost.
public sealed record AddedMaterial(int Id, int TokenEstimate);

// Ingests study material: stores the file, extracts its text once, splits it into sections and
// asks the AI for an outline. Everything later (generation, grading) works from the stored text.
public sealed class MaterialService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    LearningPortalOptions options)
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
    // recorded on the material rather than thrown, so the upload itself still counts.
    public async Task AnalyzeAsync(string userId, int materialId, IProgress<string>? progress, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await db.Materials
            .Include(m => m.Sections)
            .Include(m => m.Topic)
            .FirstOrDefaultAsync(m => m.Id == materialId && m.UserId == userId, ct) ?? throw new NotFoundException();

        try
        {
            var client = AiClientFactory.Create(await settings.GetConnectionAsync(userId, ct));
            var sections = material.Sections.OrderBy(s => s.Index).ToList();
            var batches = BatchByTokens(sections, options.TopicTokenBudget);
            var outlines = new List<string>();

            for (var i = 0; i < batches.Count; i++)
            {
                progress?.Report(batches.Count == 1
                    ? $"Analysing {material.Title}..."
                    : $"Analysing {material.Title} (part {i + 1} of {batches.Count})...");

                var json = await client.CompleteJsonAsync(new AiRequest
                {
                    System = Prompts.OutlineSystem(material.Topic!.Language),
                    Prompt = Prompts.OutlinePrompt(material.Title, batches[i]),
                    SchemaName = "material_outline",
                    Schema = Prompts.OutlineSchema,
                    MaxTokens = 8_000,
                }, ct);

                using var doc = JsonDocument.Parse(json);
                outlines.Add(doc.RootElement.GetProperty("outline").GetString() ?? "");
            }

            material.Outline = string.Join("\n", outlines).Trim();
            material.AnalysisStatus = AnalysisStatus.Done;
            material.AnalysisError = null;
        }
        catch (Exception ex) when (ex is AiException or JsonException or KeyNotFoundException)
        {
            material.AnalysisStatus = AnalysisStatus.Failed;
            material.AnalysisError = ex is AiException ? ex.Message : "The AI's outline couldn't be read.";
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

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

    public async Task RenameAsync(string userId, int materialId, string title, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await db.Materials.FirstOrDefaultAsync(m => m.Id == materialId && m.UserId == userId, ct) ?? throw new NotFoundException();
        material.Title = title.Trim();
        await db.SaveChangesAsync(ct);
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

    internal static List<List<MaterialSection>> BatchByTokens(IReadOnlyList<MaterialSection> sections, int budget)
    {
        var batches = new List<List<MaterialSection>>();
        var current = new List<MaterialSection>();
        var tokens = 0;
        foreach (var s in sections)
        {
            if (current.Count > 0 && tokens + s.TokenEstimate > budget)
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
