using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

// Adding a single question to an exam the user picked, or to a new one they named: "Save to an
// exam" on a checked answer and "Turn this into a question" in a topic chat.
internal static class ExamTargets
{
    // The exam the question goes into: the one given (a normal exam of this topic), or a new
    // written exam with this name, added to the context but not saved yet.
    public static async Task<Exam> ResolveAsync(
        AppDbContext db, string userId, int topicId, int? examId, string? newExamName, Difficulty difficulty, CancellationToken ct)
    {
        if (examId is { } id)
        {
            return await db.Exams.Include(e => e.Questions)
                .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId && e.TopicId == topicId && !e.IsPractice, ct)
                ?? throw new NotFoundException();
        }

        var name = newExamName?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException("Give the new exam a name.");
        var exam = new Exam
        {
            UserId = userId, TopicId = topicId, Name = name.Length > 200 ? name[..200] : name,
            Type = ExamType.Written, Difficulty = difficulty, QuestionCount = 0,
        };
        db.Exams.Add(exam);
        return exam;
    }

    // Appends the question to the exam, growing its question count to fit.
    public static void Add(Exam exam, Question question)
    {
        if (question.Id != 0 && exam.Questions.Any(eq => eq.QuestionId == question.Id))
            throw new InvalidOperationException("This question is already in that exam.");
        var order = exam.Questions.Count == 0 ? 0 : exam.Questions.Max(eq => eq.Order) + 1;
        exam.Questions.Add(new ExamQuestion { Exam = exam, Question = question, Order = order });
        exam.QuestionCount = Math.Max(exam.QuestionCount, exam.Questions.Count);
        exam.UpdatedAt = DateTime.UtcNow;
    }
}
