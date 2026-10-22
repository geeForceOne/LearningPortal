using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Core.Text;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// An uploaded exam paper, stored and ready to read. Text is null for a scanned PDF, which the AI
// reads from its page images instead (more tokens per page).
public sealed record PaperUpload(string FileName, string StoredPath, string? Text, int Pages, int TokenEstimate)
{
    public bool Scanned => Text is null;
}

// A question copied out of a paper that hasn't become a bank question yet. Checked: the topic's
// material was already tried and doesn't answer it, so it waits for the user's choice (web search
// or the AI's own knowledge). Unchecked ones are left when reading was stopped partway.
public sealed record PaperQuestion(int Index, QuestionType Type, string Prompt, List<PaperOption> Options, string? GivenAnswer, bool Checked)
{
    public bool HasGivenAnswer => Type == QuestionType.MultipleChoice ? Options.Any(o => o.Marked) : !string.IsNullOrWhiteSpace(GivenAnswer);

    internal Prompts.BroughtQuestion ToBrought(string? research = null) =>
        new(Prompt, Options.Select(o => (o.Text, o.Marked)).ToList(), GivenAnswer, research);
}

public sealed record PaperOption(string Text, bool Marked);

// Answered: questions that went into the exam. Pending: ones waiting for the user's choice.
public sealed record PaperResult(int Answered, int Pending);

// "Upload your own exam": Recall copies the questions out of an exam paper the user uploads and
// builds an exam from them. Answers the paper gives are kept; the rest are worked out from the
// topic's material. Questions the material doesn't answer wait on the exam until the user picks a
// web search or the AI's own knowledge for them (AnswerPendingAsync), or leaves them out.
public sealed class PaperImportService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    LearningPortalOptions options)
{
    // Rough input per page of a scanned paper read from its images.
    public const int ScannedTokensPerPage = 2_500;

    // Both providers take PDFs up to about 32 MB per request once encoded; leave room for that.
    public const long MaxScannedBytes = 20L * 1024 * 1024;

    private const int AnswerOverheadTokens = 3_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Stores the paper and reads its text. Throws UnsupportedMaterialException for a file Recall
    // can't use.
    public async Task<PaperUpload> PrepareAsync(
        string userId, int topicId, string fileName, Stream content, long sizeBytes, CancellationToken ct)
    {
        if (sizeBytes > options.MaxUploadBytes)
            throw new UnsupportedMaterialException(
                $"\"{fileName}\" is {sizeBytes / 1024.0 / 1024.0:0.#} MB. Files can be at most {options.MaxUploadBytes / 1024 / 1024} MB.");
        var kind = DocumentTextExtractor.KindFromFileName(fileName);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            if (!await db.Topics.AnyAsync(t => t.Id == topicId && t.UserId == userId, ct))
                throw new NotFoundException();

        var relativePath = Path.Combine(userId, $"{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}");
        var fullPath = Path.Combine(options.FilesRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using (var file = File.Create(fullPath))
            await content.CopyToAsync(file, ct);

        try
        {
            string text;
            try
            {
                await using var stored = File.OpenRead(fullPath);
                text = await DocumentTextExtractor.ExtractAsync(stored, kind, ct);
            }
            catch (ScannedPdfException)
            {
                if (sizeBytes > MaxScannedBytes)
                    throw new UnsupportedMaterialException(
                        $"\"{fileName}\" has no text layer, so the AI has to read its pages as images, and that works up to {MaxScannedBytes / 1024 / 1024} MB. Scan it at a lower resolution, or split it.");
                await using var stored = File.OpenRead(fullPath);
                var pages = DocumentTextExtractor.PdfPageCount(stored);
                return new PaperUpload(fileName, relativePath, null, pages, pages * ScannedTokensPerPage);
            }

            if (string.IsNullOrWhiteSpace(text))
                throw new UnsupportedMaterialException($"No text was found in \"{fileName}\".");
            var tokens = TokenEstimator.Estimate(text);
            if (tokens > options.TopicTokenBudget)
                throw new UnsupportedMaterialException(
                    $"\"{fileName}\" is about {TokenEstimator.Format(tokens)}, too long for one exam paper. Split it into smaller papers.");
            return new PaperUpload(fileName, relativePath, text, 0, tokens);
        }
        catch
        {
            MaterialService.TryDeleteFile(options, relativePath);
            throw;
        }
    }

    // For an upload the user decided not to go on with.
    public void Discard(PaperUpload upload) => MaterialService.TryDeleteFile(options, upload.StoredPath);

    // Roughly what reading the paper and answering its questions sends: the paper twice (once to
    // read it, once as the questions), and the topic's material once per batch of questions,
    // cached after the first. Used for the cost hint and the large-generation check.
    public async Task<GenerationEstimate> EstimateAsync(string userId, int topicId, PaperUpload upload, AiChoice? choice, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await db.MaterialSections
            .Where(s => s.Material!.TopicId == topicId && s.Material.UserId == userId)
            .SumAsync(s => (int?)s.TokenEstimate, ct) ?? 0;
        var model = choice is not null ? AiModels.Find(choice) : await settings.GetPricedModelAsync(userId, AiTask.Generation, ct);

        // About 300 tokens of paper per question; batches of QuestionsPerCall.
        var paperText = upload.Scanned ? upload.Pages * 600 : upload.TokenEstimate;
        var batches = Math.Max(1, (int)Math.Ceiling(paperText / 300.0 / options.QuestionsPerCall));
        var perBatch = Math.Min(material, options.TopicTokenBudget) + paperText / batches + AnswerOverheadTokens;
        var calls = 1 + batches;
        var total = upload.TokenEstimate + batches * perBatch;
        return new GenerationEstimate(calls, total / calls, material <= options.TopicTokenBudget, model);
    }

    // Creates the exam the paper becomes; ReadAsync then fills it. name defaults to the file name.
    public async Task<int> CreateExamAsync(string userId, int topicId, string? name, PaperUpload upload, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Topics.AnyAsync(t => t.Id == topicId && t.UserId == userId, ct))
            throw new NotFoundException();
        var paperName = Path.GetFileNameWithoutExtension(upload.FileName);
        var exam = new Exam
        {
            UserId = userId,
            TopicId = topicId,
            Name = Clip(string.IsNullOrWhiteSpace(name) ? paperName : name.Trim(), 200),
            ImportedFrom = Clip(paperName, 300),
            PaperFilePath = upload.StoredPath,
            Type = ExamType.Mixed,
            Difficulty = Difficulty.Medium,
            QuestionCount = 0,
        };
        db.Exams.Add(exam);
        await db.SaveChangesAsync(ct);
        return exam.Id;
    }

    public async Task<IReadOnlyList<PaperQuestion>> PendingAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.Exams.Where(e => e.Id == examId && e.UserId == userId).Select(e => e.PendingQuestionsJson).FirstOrDefaultAsync(ct);
        return ReadPending(json);
    }

    // Reads the paper (unless that's done already) and answers its questions from the topic's
    // material. Safe to run again after it was stopped: it picks up the questions not tried yet.
    public async Task<PaperResult> ReadAsync(string userId, int examId, AiChoice? choice, IProgress<string>? progress, CancellationToken ct)
    {
        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var client = AiClientFactory.Create(connection);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadAsync(db, userId, examId, ct);

        if (exam.PaperFilePath is { } path)
        {
            progress?.Report("Reading the exam paper...");
            var questions = await ReadPaperAsync(client, exam, path, ct);
            if (questions.Count == 0)
                throw new AiException("Recall found no questions in this paper. Is it the right file?");

            // Kept as pending first, so a stop partway loses nothing that was paid for.
            exam.PendingQuestionsJson = JsonSerializer.Serialize(questions, Json);
            exam.PaperFilePath = null;
            exam.Type = TypeOf(questions.Select(q => q.Type));
            exam.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            MaterialService.TryDeleteFile(options, path);
        }

        var pending = ReadPending(exam.PendingQuestionsJson);
        var toTry = pending.Where(q => !q.Checked).ToList();
        var answered = 0;
        if (toTry.Count > 0)
        {
            var material = await SourceFinder.LoadAsync(db, userId, exam.TopicId, ct);
            var wholeTopic = material.TotalTokens <= options.TopicTokenBudget;
            for (var i = 0; i < toTry.Count; i += options.QuestionsPerCall)
            {
                ct.ThrowIfCancellationRequested();
                var batch = toTry.Skip(i).Take(options.QuestionsPerCall).ToList();
                progress?.Report($"Answering questions {i + 1}-{i + batch.Count} of {toTry.Count} from your material...");

                var sections = await SourceFinder.PickAsync(
                    client, material, batch.Select(q => q.Prompt).ToList(), options.TopicTokenBudget, options.TopicTokenBudget, ct);
                var results = sections.Count == 0 && !batch.Any(q => q.HasGivenAnswer)
                    ? []
                    : await AnswerAsync(client, material, batch, Prompts.AnswerBasis.Material, sections.Count > 0 ? material.Context(sections) : null,
                        wholeTopic ? $"topic-{exam.TopicId}" : null, ct);

                foreach (var (question, index) in batch.Select((q, n) => (q, n)))
                {
                    var answer = results.GetValueOrDefault(index + 1);
                    var bank = answer is null ? null : BuildQuestion(question, answer, material, Prompts.AnswerBasis.Material, null, connection.Model, exam);
                    if (bank is not null)
                    {
                        AddToExam(db, exam, bank, question.Index);
                        pending.Remove(question);
                        answered++;
                    }
                    else
                    {
                        pending[pending.IndexOf(question)] = question with { Checked = true };
                    }
                }

                SavePending(exam, pending);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        return new PaperResult(answered, pending.Count);
    }

    // Answers the questions the material didn't, from a web search or the AI's own knowledge, as
    // the user chose. Ones the AI still can't answer stay pending.
    public async Task<PaperResult> AnswerPendingAsync(
        string userId, int examId, bool searchWeb, AiChoice? choice, IProgress<string>? progress, CancellationToken ct)
    {
        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var client = AiClientFactory.Create(connection);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadAsync(db, userId, examId, ct);
        var pending = ReadPending(exam.PendingQuestionsJson);
        var waiting = pending.Where(q => q.Checked).ToList();
        if (waiting.Count == 0)
            return new PaperResult(0, pending.Count);

        var material = await SourceFinder.LoadAsync(db, userId, exam.TopicId, ct);
        var basis = searchWeb ? Prompts.AnswerBasis.Research : Prompts.AnswerBasis.General;
        var step = searchWeb ? SourceFinder.ResearchBatch : options.QuestionsPerCall;
        var answered = 0;
        for (var i = 0; i < waiting.Count; i += step)
        {
            ct.ThrowIfCancellationRequested();
            var batch = waiting.Skip(i).Take(step).ToList();
            var range = $"{i + 1}-{i + batch.Count} of {waiting.Count}";

            List<ResearchNote>? notes = null;
            if (searchWeb)
            {
                progress?.Report($"Searching the web for questions {range}...");
                notes = await SourceFinder.ResearchAsync(client, material.Topic.Language, batch.Select(q => q.ToBrought()).ToList(), ct);
            }

            progress?.Report($"Answering questions {range}...");
            var brought = batch.Select((q, n) => q.ToBrought(notes?[n].Text)).ToList();
            var results = await AnswerAsync(client, material, brought, basis, null, null, ct);
            foreach (var (question, index) in batch.Select((q, n) => (q, n)))
            {
                var answer = results.GetValueOrDefault(index + 1);
                var bank = answer is null ? null : BuildQuestion(question, answer, material, basis, notes?[index], connection.Model, exam);
                if (bank is null)
                    continue;
                AddToExam(db, exam, bank, question.Index);
                pending.Remove(question);
                answered++;
            }

            SavePending(exam, pending);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return new PaperResult(answered, pending.Count);
    }

    // Leaves the waiting questions out of the exam.
    public async Task DropPendingAsync(string userId, int examId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exam = await LoadAsync(db, userId, examId, ct);
        exam.PendingQuestionsJson = null;
        exam.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Upper bound for looking these questions up: searches and their cost on the model.
    public async Task<(int Searches, decimal? Cost, AiModelInfo? Model)> ResearchCostAsync(
        string userId, int questions, AiChoice? choice, CancellationToken ct = default)
    {
        var model = choice is not null ? AiModels.Find(choice) : await settings.GetPricedModelAsync(userId, AiTask.Generation, ct);
        var (searches, cost) = SourceFinder.ResearchCost(questions, model);
        return (searches, cost, model);
    }

    private async Task<List<PaperQuestion>> ReadPaperAsync(IAiClient client, Exam exam, string path, CancellationToken ct)
    {
        var fullPath = Path.Combine(options.FilesRoot, path);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException("The uploaded paper is gone. Delete this exam and upload the paper again.");

        var kind = DocumentTextExtractor.KindFromFileName(fullPath);
        string? text = null;
        AiPdf? pdf = null;
        try
        {
            await using var stored = File.OpenRead(fullPath);
            text = await DocumentTextExtractor.ExtractAsync(stored, kind, ct);
        }
        catch (ScannedPdfException)
        {
            pdf = new AiPdf(Path.GetFileName(fullPath), await File.ReadAllBytesAsync(fullPath, ct));
        }

        var json = await client.CompleteJsonAsync(new AiRequest
        {
            System = Prompts.PaperSystem,
            Prompt = Prompts.PaperPrompt(text),
            Pdf = pdf,
            SchemaName = "exam_paper",
            Schema = Prompts.PaperSchema,
            MaxTokens = 32_000,
        }, ct);

        var questions = new List<PaperQuestion>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.GetProperty("questions").EnumerateArray())
            {
                var prompt = item.GetProperty("prompt").GetString()?.Trim() ?? "";
                if (prompt.Length == 0)
                    continue;
                var options = item.GetProperty("options").EnumerateArray()
                    .Select(o => new PaperOption(o.GetProperty("text").GetString()?.Trim() ?? "", o.GetProperty("marked_correct").GetBoolean()))
                    .Where(o => o.Text.Length > 0)
                    .ToList();
                var type = item.GetProperty("type").GetString() == "multiple_choice" && options.Count >= 2
                    ? QuestionType.MultipleChoice : QuestionType.Written;
                var given = item.GetProperty("given_answer").GetString()?.Trim();
                questions.Add(new PaperQuestion(questions.Count, type, prompt,
                    type == QuestionType.MultipleChoice ? options : [],
                    string.IsNullOrEmpty(given) ? null : given, false));
                if (questions.Count == ExamService.MaxQuestions)
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiException("The AI's reading of the paper couldn't be used. Try again.");
        }
        return questions;
    }

    private sealed record Answer(
        bool Covered, int SectionId, List<int> CorrectOptions, string Reference, string Explanation, bool IsCode, Difficulty Difficulty);

    // Answers by question number (1-based, in the batch's order).
    private static async Task<Dictionary<int, Answer>> AnswerAsync(
        IAiClient client, TopicMaterial material, IReadOnlyList<PaperQuestion> batch, Prompts.AnswerBasis basis,
        string? context, string? cacheKey, CancellationToken ct) =>
        await AnswerAsync(client, material, batch.Select(q => q.ToBrought()).ToList(), basis, context, cacheKey, ct);

    private static async Task<Dictionary<int, Answer>> AnswerAsync(
        IAiClient client, TopicMaterial material, IReadOnlyList<Prompts.BroughtQuestion> batch, Prompts.AnswerBasis basis,
        string? context, string? cacheKey, CancellationToken ct)
    {
        var json = await client.CompleteJsonAsync(new AiRequest
        {
            System = Prompts.AnswersSystem(material.Topic.Language, material.Topic.IsProgramming, basis),
            Context = context,
            Prompt = Prompts.AnswersPrompt(batch),
            SchemaName = "answers",
            Schema = Prompts.AnswersSchema,
            CacheKey = cacheKey,
        }, ct);

        var answers = new Dictionary<int, Answer>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var a in doc.RootElement.GetProperty("answers").EnumerateArray())
            {
                answers[a.GetProperty("number").GetInt32()] = new Answer(
                    a.GetProperty("covered").GetBoolean(),
                    a.GetProperty("section_index").GetInt32(),
                    a.GetProperty("correct_options").EnumerateArray().Where(o => o.TryGetInt32(out _)).Select(o => o.GetInt32()).ToList(),
                    a.GetProperty("reference_answer").GetString()?.Trim() ?? "",
                    a.GetProperty("explanation").GetString()?.Trim() ?? "",
                    a.GetProperty("kind").GetString() == "code",
                    a.GetProperty("difficulty").GetString() switch { "easy" => Difficulty.Easy, "hard" => Difficulty.Hard, _ => Difficulty.Medium });
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AiException("The AI's answers couldn't be read. Try again.");
        }
        return answers;
    }

    // The bank question for a paper question and its answer, or null when the answer can't be used
    // (not covered by the material without an answer from the paper, or incomplete). The paper's
    // own answer always wins over the AI's.
    private static Question? BuildQuestion(
        PaperQuestion paper, Answer answer, TopicMaterial material, Prompts.AnswerBasis basis, ResearchNote? note, string model, Exam exam)
    {
        if (answer.Explanation.Length == 0)
            return null;
        if (basis == Prompts.AnswerBasis.Material && !answer.Covered && !paper.HasGivenAnswer)
            return null;

        var section = material.Section(answer.SectionId);
        var q = new Question
        {
            TopicId = exam.TopicId,
            UserId = exam.UserId,
            Type = paper.Type,
            Difficulty = answer.Difficulty,
            Prompt = paper.Prompt,
            Explanation = answer.Explanation,
            IsCode = material.Topic.IsProgramming && answer.IsCode,
            GeneratedByModel = model,
            ImportedFrom = exam.ImportedFrom,
            AnswerSource = paper.HasGivenAnswer ? AnswerSource.Paper
                : basis switch
                {
                    Prompts.AnswerBasis.Material => AnswerSource.Material,
                    Prompts.AnswerBasis.Research => AnswerSource.Web,
                    _ => AnswerSource.General,
                },
        };

        if (section is not null && (answer.Covered || basis == Prompts.AnswerBasis.Material))
        {
            q.SourceSectionId = section.Id;
            q.SourceMaterialId = section.MaterialId;
            q.SourceLabel = material.Label(section);
        }
        if (note is not null)
        {
            q.SourceNote = note.Text.Length > 0 ? note.Text : null;
            q.WebSourcesJson = WebSource.ToJson(note.Sources);
        }

        if (paper.Type == QuestionType.Written)
        {
            q.ReferenceAnswer = paper.GivenAnswer ?? answer.Reference;
            return q.ReferenceAnswer.Length > 0 ? q : null;
        }

        var correct = paper.HasGivenAnswer
            ? paper.Options.Select((o, i) => (o, i)).Where(x => x.o.Marked).Select(x => x.i).ToHashSet()
            : answer.CorrectOptions.Where(i => i >= 0 && i < paper.Options.Count).ToHashSet();
        if (correct.Count == 0 || correct.Count == paper.Options.Count)
            return null;
        q.Options = paper.Options.Select((o, i) => new QuestionOption { Order = i, Text = o.Text, IsCorrect = correct.Contains(i) }).ToList();
        q.AllowsMultiple = correct.Count > 1;
        return q;
    }

    // Puts the question in the exam at its place in the paper, and keeps the exam's count and
    // difficulty in step with what it holds.
    private static void AddToExam(AppDbContext db, Exam exam, Question question, int paperIndex)
    {
        db.Questions.Add(question);
        exam.Questions.Add(new ExamQuestion { Exam = exam, Question = question, Order = paperIndex });
        exam.QuestionCount = exam.Questions.Count;
        exam.Difficulty = exam.Questions.Select(eq => eq.Question!.Difficulty)
            .GroupBy(d => d).OrderByDescending(g => g.Count()).ThenBy(g => g.Key == Difficulty.Medium ? 0 : 1).First().Key;
        exam.UpdatedAt = DateTime.UtcNow;
    }

    private static ExamType TypeOf(IEnumerable<QuestionType> types)
    {
        var distinct = types.Distinct().ToList();
        return distinct.Count != 1 ? ExamType.Mixed
            : distinct[0] == QuestionType.MultipleChoice ? ExamType.MultipleChoice : ExamType.Written;
    }

    private static List<PaperQuestion> ReadPending(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<PaperQuestion>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SavePending(Exam exam, List<PaperQuestion> pending)
    {
        exam.PendingQuestionsJson = pending.Count == 0 ? null : JsonSerializer.Serialize(pending, Json);
        exam.UpdatedAt = DateTime.UtcNow;
    }

    private static async Task<Exam> LoadAsync(AppDbContext db, string userId, int examId, CancellationToken ct) =>
        await db.Exams
            .Include(e => e.Questions).ThenInclude(eq => eq.Question)
            .FirstOrDefaultAsync(e => e.Id == examId && e.UserId == userId && e.ImportedFrom != null, ct)
        ?? throw new NotFoundException();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
