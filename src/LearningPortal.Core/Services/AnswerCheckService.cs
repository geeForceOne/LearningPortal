using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed record AnswerCheckSummary(int Id, string Question, int? Score, bool Pending, AnswerSource? AnswerSource, DateTime CreatedAt);

// "Check an answer": the user asks a question of their own and gives their answer. Recall finds the
// sections of the topic's material that answer it and scores the answer against them. When the
// material doesn't cover the question, the check waits for the user's choice: a web search, or an
// answer from the AI's own knowledge. Checks are kept per topic and can become exam questions.
public sealed class AnswerCheckService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings)
{
    // Material up to this size is sent whole; above it, a short call over the outlines picks the
    // sections first, so a check doesn't send a big topic for one question.
    public const int DirectTokens = 20_000;

    // The sections sent for one check, at most.
    private const int SectionTokens = 12_000;

    public async Task<IReadOnlyList<AnswerCheckSummary>> ListAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AnswerChecks.AsNoTracking()
            .Where(c => c.UserId == userId && c.TopicId == topicId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new AnswerCheckSummary(c.Id, c.Question, c.Score, c.AnswerSource == null, c.AnswerSource, c.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<AnswerCheck> GetAsync(string userId, int checkId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AnswerChecks.AsNoTracking().Include(c => c.Topic)
            .FirstOrDefaultAsync(c => c.Id == checkId && c.UserId == userId, ct) ?? throw new NotFoundException();
    }

    // Checks the answer against the topic's material. Returns the stored check: scored, or pending
    // when the material doesn't cover the question.
    public async Task<int> CheckAsync(string userId, int topicId, string question, string answer, AiChoice? choice, CancellationToken ct)
    {
        question = question.Trim();
        answer = answer.Trim();
        if (question.Length == 0)
            throw new ArgumentException("Type the question first.");
        if (question.Length > AnswerCheck.MaxQuestionLength)
            throw new ArgumentException($"Keep the question under {AnswerCheck.MaxQuestionLength} characters.");
        if (answer.Length == 0)
            throw new ArgumentException("Type your answer first.");
        if (answer.Length > AttemptService.MaxWrittenAnswerLength)
            throw new ArgumentException($"Keep your answer under {AttemptService.MaxWrittenAnswerLength} characters.");

        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var client = AiClientFactory.Create(connection);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var material = await SourceFinder.LoadAsync(db, userId, topicId, ct);
        var check = new AnswerCheck { UserId = userId, TopicId = topicId, Question = question, Answer = answer, Model = connection.Model };

        var sections = await SourceFinder.PickAsync(client, material, [question], DirectTokens, SectionTokens, ct);
        if (sections.Count > 0)
        {
            var result = await AskAsync(client, material, check, Prompts.AnswerBasis.Material, material.Context(sections), null, ct);
            if (result.Covered)
                Apply(check, result, Models.AnswerSource.Material, material, null);
        }

        db.AnswerChecks.Add(check);
        await db.SaveChangesAsync(CancellationToken.None);
        return check.Id;
    }

    // Finishes a check the material didn't cover: from a web search, or the AI's own knowledge.
    public async Task FinishAsync(string userId, int checkId, bool searchWeb, AiChoice? choice, IProgress<string>? progress, CancellationToken ct)
    {
        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var client = AiClientFactory.Create(connection);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var check = await db.AnswerChecks.FirstOrDefaultAsync(c => c.Id == checkId && c.UserId == userId, ct) ?? throw new NotFoundException();
        if (!check.IsPending)
            return;
        var material = await SourceFinder.LoadAsync(db, userId, check.TopicId, ct);

        ResearchNote? note = null;
        if (searchWeb)
        {
            progress?.Report("Searching the web...");
            note = (await SourceFinder.ResearchAsync(client, material.Topic.Language, [new Prompts.BroughtQuestion(check.Question, [], null)], ct))[0];
        }

        progress?.Report("Checking your answer...");
        var basis = searchWeb ? Prompts.AnswerBasis.Research : Prompts.AnswerBasis.General;
        var result = await AskAsync(client, material, check, basis, null, note?.Text, ct);
        Apply(check, result, searchWeb ? Models.AnswerSource.Web : Models.AnswerSource.General, material, note);
        check.Model = connection.Model;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // Turns the check into a written question in the topic's bank and adds it to an exam: the one
    // given, or a new exam with this name. Returns the exam's id.
    public async Task<int> SaveToExamAsync(string userId, int checkId, int? examId, string? newExamName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var check = await db.AnswerChecks.FirstOrDefaultAsync(c => c.Id == checkId && c.UserId == userId, ct) ?? throw new NotFoundException();
        if (check.IsPending || string.IsNullOrWhiteSpace(check.ReferenceAnswer))
            throw new InvalidOperationException("Finish checking the answer first.");

        Exam exam;
        if (examId is { } id)
        {
            exam = await db.Exams.Include(e => e.Questions)
                .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId && e.TopicId == check.TopicId && !e.IsPractice, ct)
                ?? throw new NotFoundException();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(newExamName))
                throw new ArgumentException("Give the new exam a name.");
            exam = new Exam
            {
                UserId = userId, TopicId = check.TopicId, Name = newExamName.Trim().Length > 200 ? newExamName.Trim()[..200] : newExamName.Trim(),
                Type = ExamType.Written, Difficulty = check.Difficulty, QuestionCount = 0,
            };
            db.Exams.Add(exam);
        }

        Question question;
        if (check.SavedQuestionId is { } saved && await db.Questions.FirstOrDefaultAsync(q => q.Id == saved, ct) is { } existing)
        {
            if (exam.Questions.Any(eq => eq.QuestionId == existing.Id))
                throw new InvalidOperationException("This question is already in that exam.");
            question = existing;
        }
        else
        {
            question = new Question
            {
                TopicId = check.TopicId,
                UserId = userId,
                Type = QuestionType.Written,
                Difficulty = check.Difficulty,
                Prompt = check.Question,
                ReferenceAnswer = check.ReferenceAnswer,
                Explanation = check.Explanation ?? "",
                SourceMaterialId = check.SourceMaterialId,
                SourceSectionId = check.SourceSectionId,
                SourceLabel = check.SourceLabel,
                IsCode = check.IsCode,
                GeneratedByModel = check.Model,
                AnswerSource = check.AnswerSource!.Value,
                WebSourcesJson = check.WebSourcesJson,
                SourceNote = check.SourceNote,
            };
            db.Questions.Add(question);
        }

        var order = exam.Questions.Count == 0 ? 0 : exam.Questions.Max(eq => eq.Order) + 1;
        var count = exam.Questions.Count + 1;
        exam.Questions.Add(new ExamQuestion { Exam = exam, Question = question, Order = order });
        exam.QuestionCount = Math.Max(exam.QuestionCount, count);
        exam.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        check.SavedQuestionId = question.Id;
        await db.SaveChangesAsync(ct);
        return exam.Id;
    }

    public async Task DeleteAsync(string userId, int checkId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AnswerChecks.Where(c => c.Id == checkId && c.UserId == userId).ExecuteDeleteAsync(ct);
    }

    // Upper bound for finishing a check with a web search: its searches and their cost.
    public async Task<(int Searches, decimal? Cost, AiModelInfo? Model)> ResearchCostAsync(string userId, AiChoice? choice, CancellationToken ct = default)
    {
        var model = choice is not null ? AiModels.Find(choice) : await settings.GetPricedModelAsync(userId, AiTask.Generation, ct);
        var (searches, cost) = SourceFinder.ResearchCost(1, model);
        return (searches, cost, model);
    }

    private sealed record Result(
        bool Covered, int SectionId, string Reference, string Explanation, bool IsCode, Difficulty Difficulty, int Score, string Feedback);

    private static async Task<Result> AskAsync(
        IAiClient client, TopicMaterial material, AnswerCheck check, Prompts.AnswerBasis basis, string? context, string? research, CancellationToken ct)
    {
        var json = await client.CompleteJsonAsync(new AiRequest
        {
            System = Prompts.CheckSystem(material.Topic.Language, material.Topic.IsProgramming, basis),
            Context = context,
            Prompt = Prompts.CheckPrompt(check.Question, check.Answer, research),
            SchemaName = "answer_check",
            Schema = Prompts.CheckSchema,
            MaxTokens = 8_000,
        }, ct);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var result = new Result(
                r.GetProperty("covered").GetBoolean(),
                r.GetProperty("section_index").GetInt32(),
                r.GetProperty("reference_answer").GetString()?.Trim() ?? "",
                r.GetProperty("explanation").GetString()?.Trim() ?? "",
                r.GetProperty("kind").GetString() == "code",
                r.GetProperty("difficulty").GetString() switch { "easy" => Difficulty.Easy, "hard" => Difficulty.Hard, _ => Difficulty.Medium },
                Math.Clamp(r.GetProperty("score").GetInt32(), 0, 100),
                r.GetProperty("feedback").GetString()?.Trim() ?? "");
            // An answer without its parts is no answer; for the material that means "not covered".
            return result.Reference.Length == 0 || result.Feedback.Length == 0 ? result with { Covered = false } : result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AiException("The AI's answer couldn't be read. Try again.");
        }
    }

    private static void Apply(AnswerCheck check, Result result, AnswerSource source, TopicMaterial material, ResearchNote? note)
    {
        if (result.Reference.Length == 0 || result.Feedback.Length == 0)
            throw new AiException("The AI couldn't answer this question. Try rewording it.");
        check.AnswerSource = source;
        check.ReferenceAnswer = result.Reference;
        check.Explanation = result.Explanation;
        check.Score = result.Score;
        check.Feedback = result.Feedback;
        check.IsCode = material.Topic.IsProgramming && result.IsCode;
        check.Difficulty = result.Difficulty;
        if (source == Models.AnswerSource.Material && material.Section(result.SectionId) is { } section)
        {
            check.SourceSectionId = section.Id;
            check.SourceMaterialId = section.MaterialId;
            check.SourceLabel = material.Label(section);
        }
        if (note is not null)
        {
            check.SourceNote = note.Text.Length > 0 ? note.Text : null;
            check.WebSourcesJson = WebSource.ToJson(note.Sources);
        }
    }
}
