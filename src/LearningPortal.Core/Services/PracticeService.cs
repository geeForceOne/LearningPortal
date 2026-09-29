using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// "Practice weak questions": each topic has one practice exam whose questions are rebuilt from the
// ones most often answered wrong every time it's started (or from one section, via the concept map). It only reuses existing questions, so it
// never calls the AI. A practice run can be kept as a normal exam.
public sealed class PracticeService(
    IDbContextFactory<AppDbContext> dbFactory,
    StatisticsService statistics,
    AttemptService attempts)
{
    public const int DefaultSize = 10;
    public const int MaxSize = 30;
    public const string ExamName = "Weak questions";

    public async Task<int> CountWeakAsync(string userId, int topicId, CancellationToken ct = default) =>
        (await statistics.GetWeakQuestionsAsync(userId, topicId, ct)).Count;

    // Fills the topic's practice exam with its weakest questions and starts a new attempt on it.
    // Returns the attempt's id.
    public async Task<int> StartAsync(string userId, int topicId, int size, CancellationToken ct = default)
    {
        size = Math.Clamp(size, 1, MaxSize);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Topics.AnyAsync(t => t.Id == topicId && t.UserId == userId, ct))
            throw new NotFoundException();

        var weak = (await statistics.GetWeakQuestionsAsync(userId, topicId, ct)).Take(size).Select(w => w.QuestionId).ToList();
        if (weak.Count == 0)
            throw new InvalidOperationException("There are no weak questions in this topic yet. Questions you answer wrong or only partly right show up here.");
        return await StartWithAsync(db, userId, topicId, weak, ExamName, ct);
    }

    // Practice on one section of the material (from the concept map): its weakest questions first.
    public async Task<int> StartSectionAsync(string userId, int topicId, int sectionId, int size, CancellationToken ct = default)
    {
        size = Math.Clamp(size, 1, MaxSize);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var heading = await db.MaterialSections
            .Where(s => s.Id == sectionId && s.Material!.UserId == userId && s.Material.TopicId == topicId)
            .Select(s => s.Heading)
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException();
        var ids = (await statistics.RankSectionQuestionsAsync(userId, topicId, sectionId, ct)).Take(size).ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("This section has no questions yet. Questions written from it show up here.");
        var name = string.IsNullOrWhiteSpace(heading) ? "Practice" : $"Practice: {heading.Trim()}";
        return await StartWithAsync(db, userId, topicId, ids, name.Length > 200 ? name[..200] : name, ct);
    }

    // Fills the topic's practice exam with these questions, under this name, and starts an attempt.
    private async Task<int> StartWithAsync(AppDbContext db, string userId, int topicId, List<int> questionIds, string name, CancellationToken ct)
    {
        var exam = await db.Exams.FirstOrDefaultAsync(e => e.UserId == userId && e.TopicId == topicId && e.IsPractice, ct);
        if (exam is null)
        {
            exam = new Exam { UserId = userId, TopicId = topicId, Name = name, IsPractice = true, Difficulty = Difficulty.Medium };
            db.Exams.Add(exam);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await db.ExamQuestions.Where(eq => eq.ExamId == exam.Id).ExecuteDeleteAsync(ct);
        }

        var types = await db.Questions.Where(q => questionIds.Contains(q.Id)).Select(q => q.Type).ToListAsync(ct);
        exam.Name = name;
        exam.QuestionCount = questionIds.Count;
        exam.Type = TypeOf(types);
        exam.UpdatedAt = DateTime.UtcNow;
        for (var i = 0; i < questionIds.Count; i++)
            db.ExamQuestions.Add(new ExamQuestion { ExamId = exam.Id, QuestionId = questionIds[i], Order = i });
        await db.SaveChangesAsync(ct);

        return await attempts.StartNewAsync(userId, exam.Id, ct);
    }

    // Keeps the questions of one practice attempt as a normal exam that can be repeated and edited.
    // Returns the new exam's id.
    public async Task<int> SaveAsExamAsync(string userId, int attemptId, string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Give the exam a name.");
        if (trimmed.Length > 200)
            throw new ArgumentException("Keep the name under 200 characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var attempt = await db.Attempts.AsNoTracking()
            .Include(a => a.Exam!).ThenInclude(e => e.Topic)
            .Include(a => a.Answers).ThenInclude(x => x.Question)
            .FirstOrDefaultAsync(a => a.Id == attemptId && a.UserId == userId && a.Exam!.IsPractice, ct) ?? throw new NotFoundException();

        var questions = attempt.Answers.OrderBy(a => a.Position).Select(a => a.Question!).ToList();
        var exam = new Exam
        {
            UserId = userId,
            TopicId = attempt.Exam!.TopicId,
            Name = trimmed,
            QuestionCount = questions.Count,
            Type = TypeOf(questions.Select(q => q.Type)),
            Difficulty = questions.GroupBy(q => q.Difficulty).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key,
            // Programming topics: match the saved questions, so a later "fill" keeps the same mix.
            CodePercent = attempt.Exam.Topic!.IsProgramming
                ? (int)Math.Round(100.0 * questions.Count(q => q.IsCode) / questions.Count)
                : ExamService.DefaultCodePercent,
        };
        for (var i = 0; i < questions.Count; i++)
            exam.Questions.Add(new ExamQuestion { QuestionId = questions[i].Id, Order = i });
        db.Exams.Add(exam);
        await db.SaveChangesAsync(ct);
        return exam.Id;
    }

    private static ExamType TypeOf(IEnumerable<QuestionType> types)
    {
        var distinct = types.Distinct().ToList();
        return distinct.Count > 1 ? ExamType.Mixed
            : distinct[0] == QuestionType.Written ? ExamType.Written
            : ExamType.MultipleChoice;
    }
}
