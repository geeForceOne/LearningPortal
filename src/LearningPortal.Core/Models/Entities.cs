using Microsoft.AspNetCore.Identity;

namespace LearningPortal.Core.Models;

public sealed class AppUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// One row per user. API keys are stored as Data Protection ciphertext, never in the clear.
public sealed class UserSettings
{
    public string UserId { get; set; } = "";
    public AiProvider Provider { get; set; } = AiProvider.Claude;
    public string? ClaudeApiKeyProtected { get; set; }
    public string? ClaudeModel { get; set; }
    public string? OpenAiApiKeyProtected { get; set; }
    public string? OpenAiModel { get; set; }
}

public sealed class Topic
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    // The language questions, answers, explanations and grading feedback are written in.
    public string Language { get; set; } = "English";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Material> Materials { get; set; } = [];
    public List<Exam> Exams { get; set; } = [];
    public List<Question> Questions { get; set; } = [];
}

public sealed class Material
{
    public int Id { get; set; }
    public int TopicId { get; set; }
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public MaterialKind Kind { get; set; }
    public string? OriginalFileName { get; set; }
    // Relative to the configured files root; null for pasted text.
    public string? StoredFilePath { get; set; }
    public long SizeBytes { get; set; }
    public string ExtractedText { get; set; } = "";
    public int TokenEstimate { get; set; }
    // The AI's short outline of the material (sections and key concepts), as plain text.
    public string? Outline { get; set; }
    public AnalysisStatus AnalysisStatus { get; set; } = AnalysisStatus.Pending;
    public string? AnalysisError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Topic? Topic { get; set; }
    public List<MaterialSection> Sections { get; set; } = [];
}

public sealed class MaterialSection
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public int Index { get; set; }
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
    public int TokenEstimate { get; set; }

    public Material? Material { get; set; }
}

// A question in a topic's bank. Exams reference bank questions rather than owning copies, so a
// question generated once can be reused by any later exam of the same type and difficulty.
public sealed class Question
{
    public int Id { get; set; }
    public int TopicId { get; set; }
    public string UserId { get; set; } = "";
    public QuestionType Type { get; set; }
    public Difficulty Difficulty { get; set; }
    public string Prompt { get; set; } = "";
    // Multiple choice only: true when more than one option is correct ("choose all that apply").
    public bool AllowsMultiple { get; set; }
    // Written only: the model answer the grader compares against.
    public string? ReferenceAnswer { get; set; }
    public string Explanation { get; set; } = "";
    public int? SourceMaterialId { get; set; }
    public int? SourceSectionId { get; set; }
    public string? SourceLabel { get; set; }
    public bool IsUserAuthored { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Topic? Topic { get; set; }
    public List<QuestionOption> Options { get; set; } = [];
    public MaterialSection? SourceSection { get; set; }
}

public sealed class QuestionOption
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public int Order { get; set; }
    public string Text { get; set; } = "";
    public bool IsCorrect { get; set; }
}

public sealed class Exam
{
    public int Id { get; set; }
    public int TopicId { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public int QuestionCount { get; set; }
    public ExamType Type { get; set; }
    public Difficulty Difficulty { get; set; }
    // How much of a fill may come from the topic's question bank (0-100); the rest is newly written.
    public int ReusePercent { get; set; } = 100;
    // Optional guidance from the user for every AI generation for this exam ("focus on XY").
    public string? Instructions { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Topic? Topic { get; set; }
    public List<ExamQuestion> Questions { get; set; } = [];
    public List<Attempt> Attempts { get; set; } = [];
}

public sealed class ExamQuestion
{
    public int ExamId { get; set; }
    public int QuestionId { get; set; }
    public int Order { get; set; }

    public Exam? Exam { get; set; }
    public Question? Question { get; set; }
}

public sealed class Attempt
{
    public int Id { get; set; }
    public int ExamId { get; set; }
    public string UserId { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    // Time actually spent answering, accumulated per answer so leaving and resuming later
    // doesn't count the time away.
    public int ActiveSeconds { get; set; }
    // Average of the per-question scores (0-100), set when the attempt is finished.
    public double? ScorePercent { get; set; }

    public Exam? Exam { get; set; }
    public List<AttemptAnswer> Answers { get; set; } = [];
}

// One row per question in the attempt, created up front in the attempt's shuffled order and
// filled in as the user answers.
public sealed class AttemptAnswer
{
    public int Id { get; set; }
    public int AttemptId { get; set; }
    public int QuestionId { get; set; }
    public int Position { get; set; }
    // Comma-separated option ids in the order they're shown for this attempt.
    public string OptionOrder { get; set; } = "";
    // Comma-separated option ids the user picked.
    public string? SelectedOptionIds { get; set; }
    public string? WrittenAnswer { get; set; }
    public int? Score { get; set; }
    public string? Feedback { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public bool Revealed { get; set; }

    public Attempt? Attempt { get; set; }
    public Question? Question { get; set; }

    public bool IsAnswered => AnsweredAt is not null;

    public IReadOnlyList<int> OptionOrderIds => ParseIds(OptionOrder);
    public IReadOnlyList<int> SelectedIds => ParseIds(SelectedOptionIds);

    public static string JoinIds(IEnumerable<int> ids) => string.Join(',', ids);

    private static List<int> ParseIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
}
