using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Core.Text;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// What generating for a topic depends on: its material size, the user's priced model, and how
// many questions its bank already holds (for the saturation hint).
public sealed record GenerationCostBasis(int TopicTokens, AiModelInfo? Model, int BankQuestions)
{
    public bool BankSaturated => QuestionGenerationService.BankSaturated(BankQuestions, TopicTokens);
}

// Model is the user's chosen model when its price is known, so the UI can show a rough cost.
public sealed record GenerationEstimate(int Calls, int InputTokensPerCall, bool WholeTopic, AiModelInfo? Model)
{
    public int TotalInputTokens => Calls * InputTokensPerCall;

    // Input only, without caching discounts: an upper bound for the material, not the full bill.
    public decimal? InputCost => Model?.InputCost(TotalInputTokens);
}

// Asks the AI for new bank questions. When the whole topic fits the token budget, every call
// sends all of it (identical prefix, so it's cached after the first call). When it doesn't, each
// call sends the outlines plus the sections the bank covers least, so questions spread across
// the whole topic over time.
public sealed class QuestionGenerationService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    LearningPortalOptions options)
{
    private const int SystemAndPromptOverheadTokens = 1_500;

    // Rough amount of material per distinct question it can support. Study text holds about one
    // testable fact or idea per 100-200 tokens; past that many questions, new ones mostly re-ask
    // what the bank already covers, however the prompt is worded.
    public const int TokensPerDistinctQuestion = 150;

    // True when the bank already holds about as many questions as the material can support.
    public static bool BankSaturated(int bankQuestions, int topicTokens) =>
        topicTokens > 0 && bankQuestions >= Math.Max(1, topicTokens / TokensPerDistinctQuestion);

    // Upper bound for the material sent in one call when the topic exceeds the budget.
    private int SectionBudget => Math.Max(options.SectionTargetTokens * 2, options.TopicTokenBudget / 4);

    public async Task<GenerationEstimate> EstimateAsync(string userId, int topicId, int questionCount, CancellationToken ct = default) =>
        Estimate(await CostBasisAsync(userId, topicId, ct), questionCount);

    // What a generation estimate depends on besides the question count. Load it once per page and
    // call Estimate as the count changes.
    public async Task<GenerationCostBasis> CostBasisAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var total = await db.MaterialSections
            .Where(s => s.Material!.TopicId == topicId && s.Material.UserId == userId)
            .SumAsync(s => (int?)s.TokenEstimate, ct) ?? 0;
        var bank = await db.Questions.CountAsync(q => q.TopicId == topicId && q.UserId == userId, ct);
        return new GenerationCostBasis(total, await settings.GetPricedModelAsync(userId, ct), bank);
    }

    public GenerationEstimate Estimate(GenerationCostBasis basis, int questionCount)
    {
        var calls = (int)Math.Ceiling(Math.Max(questionCount, 0) / (double)options.QuestionsPerCall);
        var whole = basis.TopicTokens <= options.TopicTokenBudget;
        var perCall = (whole ? basis.TopicTokens : Math.Min(basis.TopicTokens, SectionBudget) + 2_000) + SystemAndPromptOverheadTokens;
        return new GenerationEstimate(calls, perCall, whole, basis.Model);
    }

    // Generates and stores new bank questions. May return fewer than asked if the AI keeps
    // producing unusable questions; the caller reports the shortfall. The code share of the mix
    // only applies to programming topics; other topics get theory questions only.
    public async Task<List<Question>> GenerateAsync(
        string userId, int topicId, QuestionMix mix, Difficulty difficulty, string? instructions,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (mix.Total <= 0)
            return [];

        var client = AiClientFactory.Create(await settings.GetConnectionAsync(userId, ct));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var topic = await db.Topics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == topicId && t.UserId == userId, ct)
            ?? throw new NotFoundException();
        var programming = topic.IsProgramming;
        var materials = await db.Materials.AsNoTracking()
            .Where(m => m.TopicId == topicId && m.UserId == userId)
            .Include(m => m.Sections)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var allSections = materials.SelectMany(m => m.Sections).ToList();
        if (allSections.Count == 0)
            throw new AiException("This topic has no material yet. Add a file or paste some text first.");

        var sectionById = allSections.ToDictionary(s => s.Id);
        var materialById = materials.ToDictionary(m => m.Id);
        var totalTokens = allSections.Sum(s => s.TokenEstimate);
        var wholeTopic = totalTokens <= options.TopicTokenBudget;

        // Oldest first, so the end of the list is the most recent.
        var existing = await db.Questions.AsNoTracking()
            .Where(q => q.TopicId == topicId && q.UserId == userId)
            .OrderBy(q => q.Id)
            .Select(q => new ExistingQuestion(q.Prompt, q.SourceSectionId))
            .ToListAsync(ct);
        var similarity = new QuestionSimilarity(existing.Select(q => q.Prompt));
        var coverage = existing.Where(q => q.SourceSectionId is not null)
            .GroupBy(q => q.SourceSectionId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var wholeContext = wholeTopic
            ? Prompts.MaterialContext(materials.Select(m => (m, (IReadOnlyList<MaterialSection>)m.Sections.OrderBy(s => s.Index).ToList())))
            : null;

        var created = new List<Question>();
        var skippedRepeats = 0;
        var mcLeft = mix.MultipleChoice;
        var writtenLeft = mix.Written;
        var codeLeft = programming ? Math.Clamp(mix.Code, 0, mix.Total) : 0;
        var theoryLeft = mix.Total - codeLeft;
        var target = mix.Total;
        var maxCalls = (int)Math.Ceiling(target / (double)options.QuestionsPerCall) + 2;

        for (var call = 0; call < maxCalls && mcLeft + writtenLeft > 0; call++)
        {
            ct.ThrowIfCancellationRequested();

            var batch = Math.Min(options.QuestionsPerCall, mcLeft + writtenLeft);
            var batchMc = (int)Math.Round(batch * (mcLeft / (double)(mcLeft + writtenLeft)));
            var batchWritten = batch - batchMc;
            var batchCode = (int)Math.Round(batch * (codeLeft / (double)(codeLeft + theoryLeft)));

            string context;
            string? focus = null;
            HashSet<int>? sectionsSent = null;
            if (wholeTopic)
            {
                context = wholeContext!;
            }
            else
            {
                var picked = PickLeastCovered(allSections, coverage, SectionBudget);
                sectionsSent = picked.Select(s => s.Id).ToHashSet();
                context = Prompts.OutlinesContext(materials) + "\n" + Prompts.MaterialContext(
                    picked.GroupBy(s => s.MaterialId)
                        .Select(g => (materialById[g.Key], (IReadOnlyList<MaterialSection>)g.OrderBy(s => s.Index).ToList())));
                focus = "Base the questions only on the full sections included below; the outlines are for orientation.";
            }

            progress?.Report($"Writing questions {created.Count + 1}-{created.Count + batch} of {target}..."
                + (skippedRepeats > 0 ? $" (skipped {skippedRepeats} that repeated existing questions)" : ""));
            var avoid = AvoidList(existing, created, sectionsSent);

            var json = await client.CompleteJsonAsync(new AiRequest
            {
                System = Prompts.GenerationSystem(topic.Language, programming),
                Context = context,
                Prompt = Prompts.GenerationPrompt(
                    batchMc, batchWritten, programming ? batchCode : null, difficulty, avoid, focus, instructions, topic.Language),
                SchemaName = "exam_questions",
                Schema = Prompts.QuestionsSchema,
                CacheKey = $"topic-{topicId}",
            }, ct);

            foreach (var q in ParseQuestions(json, difficulty, sectionById, materialById))
            {
                if (!programming)
                    q.IsCode = false;
                // Keep to the requested mix; surplus of one kind is dropped rather than skewing the exam.
                if (q.Type == QuestionType.MultipleChoice ? mcLeft <= 0 : writtenLeft <= 0)
                    continue;
                if (q.IsCode ? codeLeft <= 0 : theoryLeft <= 0)
                    continue;
                // A repeat of a bank question (or of one from this run) is dropped; the spare
                // calls above ask for a replacement.
                if (similarity.IsDuplicate(q.Prompt))
                {
                    skippedRepeats++;
                    continue;
                }

                q.TopicId = topicId;
                q.UserId = userId;
                db.Questions.Add(q);
                created.Add(q);
                similarity.Add(q.Prompt);
                if (q.SourceSectionId is { } sid)
                    coverage[sid] = coverage.GetValueOrDefault(sid) + 1;
                if (q.Type == QuestionType.MultipleChoice) mcLeft--; else writtenLeft--;
                if (q.IsCode) codeLeft--; else theoryLeft--;
            }

            // Save per batch so a later failure doesn't throw away questions already paid for.
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return created;
    }

    private sealed record ExistingQuestion(string Prompt, int? SourceSectionId);

    // The "don't repeat these" list for one call, most relevant first until the size cap: this
    // run's questions, then bank questions from the sections being sent, then the rest of the bank,
    // newest first. The AI is most likely to repeat what it just wrote or what covers the same text.
    private static List<string> AvoidList(List<ExistingQuestion> bank, List<Question> thisRun, HashSet<int>? sectionsSent)
    {
        var ordered = thisRun.AsEnumerable().Reverse().Select(q => q.Prompt)
            .Concat(Enumerable.Reverse(bank)
                .Where(q => sectionsSent is not null && q.SourceSectionId is { } id && sectionsSent.Contains(id))
                .Select(q => q.Prompt))
            .Concat(Enumerable.Reverse(bank).Select(q => q.Prompt));

        var list = new List<string>();
        var seen = new HashSet<string>();
        var chars = 0;
        foreach (var prompt in ordered)
        {
            var line = Prompts.AvoidLine(prompt);
            if (!seen.Add(line))
                continue;
            if (chars + line.Length > MaxAvoidChars)
                break;
            list.Add(line);
            chars += line.Length;
        }
        return list;
    }

    // Cap on the avoid list, about 6k tokens: roughly 120-300 questions depending on their length.
    private const int MaxAvoidChars = 24_000;

    private static List<MaterialSection> PickLeastCovered(
        List<MaterialSection> sections, Dictionary<int, int> coverage, int budget)
    {
        var picked = new List<MaterialSection>();
        var tokens = 0;
        foreach (var s in sections.OrderBy(s => coverage.GetValueOrDefault(s.Id)).ThenBy(_ => Random.Shared.Next()))
        {
            if (picked.Count > 0 && tokens + s.TokenEstimate > budget)
                continue;
            picked.Add(s);
            tokens += s.TokenEstimate;
            if (tokens >= budget)
                break;
        }
        return picked;
    }

    private static List<Question> ParseQuestions(
        string json, Difficulty difficulty,
        Dictionary<int, MaterialSection> sectionById, Dictionary<int, Material> materialById)
    {
        var result = new List<Question>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new AiException("The AI's answer wasn't in the expected format. Try again.");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in list.EnumerateArray())
            {
                var q = TryParseQuestion(item, difficulty);
                if (q is null)
                    continue;

                if (item.TryGetProperty("section_index", out var idx) && idx.TryGetInt32(out var sectionId)
                    && sectionById.TryGetValue(sectionId, out var section))
                {
                    q.SourceSectionId = section.Id;
                    q.SourceMaterialId = section.MaterialId;
                    q.SourceLabel = SourceLabel(materialById[section.MaterialId], section);
                }
                result.Add(q);
            }
        }
        return result;
    }

    internal static string SourceLabel(Material material, MaterialSection section) =>
        section.Heading == material.Title ? material.Title : $"{material.Title} / {section.Heading}";

    internal static Question? TryParseQuestion(JsonElement item, Difficulty difficulty)
    {
        string Str(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

        var prompt = Str("prompt");
        var explanation = Str("explanation");
        if (prompt.Length == 0 || explanation.Length == 0)
            return null;

        var type = Str("type") == "written" ? QuestionType.Written : QuestionType.MultipleChoice;
        var q = new Question
        {
            Type = type, Difficulty = difficulty, Prompt = prompt, Explanation = explanation, IsCode = Str("kind") == "code",
        };

        if (type == QuestionType.Written)
        {
            q.ReferenceAnswer = Str("reference_answer");
            return q.ReferenceAnswer.Length > 0 ? q : null;
        }

        if (!item.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;

        var order = 0;
        foreach (var o in options.EnumerateArray())
        {
            var text = o.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
            if (text.Length == 0)
                continue;
            var correct = o.TryGetProperty("correct", out var c) && c.ValueKind == JsonValueKind.True;
            q.Options.Add(new QuestionOption { Order = order++, Text = text, IsCorrect = correct });
        }

        var correctCount = q.Options.Count(o => o.IsCorrect);
        if (q.Options.Count < 2 || correctCount == 0 || correctCount == q.Options.Count)
            return null;

        q.AllowsMultiple = correctCount > 1;
        return q;
    }
}
