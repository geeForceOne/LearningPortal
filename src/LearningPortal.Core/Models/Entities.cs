using Microsoft.AspNetCore.Identity;

namespace LearningPortal.Core.Models;

// People sign in with their email. New accounts get their email as the (internal) UserName too;
// accounts from before that keep their own UserName, which still works as a login.
public sealed class AppUser : IdentityUser
{
    public const int MaxDisplayNameLength = 100;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // For the admin statistics (see UserActivityService). Null until first recorded.
    public DateTime? LastLoginAt { get; set; }
    public DateTime? LastActiveAt { get; set; }

    // Optional; what the app calls the person. See ShownName for the fallback.
    public string? DisplayName { get; set; }

    // The newest version whose "what's new" banner this user has closed (or the version running when
    // they first signed in after the banner existed). Null until then.
    public string? SeenVersion { get; set; }

    // Admins only: email me when an invited person sets their password. Each admin decides for themselves.
    public bool NotifyInviteAccepted { get; set; } = true;
    // Admins only: an email when someone requests an account (when requests are open).
    public bool NotifyAccountRequests { get; set; } = true;

    // The name shown in the UI: the display name, else the part of the email before the "@",
    // else the username (older accounts without an email).
    public string ShownName => ShownNameFor(DisplayName, Email, UserName);

    public static string ShownNameFor(string? displayName, string? email, string? userName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var at = email.IndexOf('@');
            return at > 0 ? email[..at] : email;
        }
        return userName ?? "";
    }
}

// One row per user. API keys are stored as Data Protection ciphertext, never in the clear.
// A null model means that provider's default model.
public sealed class UserSettings
{
    public string UserId { get; set; } = "";
    public string? ClaudeApiKeyProtected { get; set; }
    public string? OpenAiApiKeyProtected { get; set; }
    // Optional expiry dates the user entered for their keys (providers don't expose them to the
    // key itself), for the reminder banner. KeyReminderDismissedOn hides the banner for that day.
    public DateOnly? ClaudeKeyExpiresOn { get; set; }
    public DateOnly? OpenAiKeyExpiresOn { get; set; }
    public DateOnly? KeyReminderDismissedOn { get; set; }

    // Advanced mode: separate defaults for outlining uploads and for questions and grading, and
    // a model picker on the upload and generation screens. Simple mode: Provider/Model for everything.
    public bool AdvancedModels { get; set; }
    public AiProvider Provider { get; set; } = AiProvider.Claude;
    public string? Model { get; set; }
    public AiProvider AnalysisProvider { get; set; } = AiProvider.Claude;
    public string? AnalysisModel { get; set; }
    public AiProvider GenerationProvider { get; set; } = AiProvider.Claude;
    public string? GenerationModel { get; set; }

    public Theme Theme { get; set; } = Theme.Dark;
}

// App-wide outgoing mail settings, edited by the admin. A single row (Id 1); no row or an empty
// Host means email is off. The SMTP password is Data Protection ciphertext, never in the clear.
public sealed class EmailSettings
{
    public int Id { get; set; } = 1;
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public EmailSecurity Security { get; set; } = EmailSecurity.Auto;
    public string? UserName { get; set; }
    public string? PasswordProtected { get; set; }
    public string? From { get; set; }
    public string? FromName { get; set; }
    // The address people use to reach the app; links in emails point here.
    public string? PublicUrl { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Topic
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    // The language questions, answers, explanations and grading feedback are written in.
    public string Language { get; set; } = "English";
    // Set by the user. Only programming topics get code questions, in the share each exam asks for.
    public bool IsProgramming { get; set; }
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
    // A study note the AI wrote for a gap the gap finder found (from general knowledge, not the
    // user's sources), so the UI can say it may contain mistakes.
    public bool IsAiWritten { get; set; }
    public string? OriginalFileName { get; set; }
    // Relative to the configured files root; null for pasted text.
    public string? StoredFilePath { get; set; }
    public long SizeBytes { get; set; }
    public string ExtractedText { get; set; } = "";
    public int TokenEstimate { get; set; }
    // The AI's short outline of the material (sections and key concepts), as plain text.
    public string? Outline { get; set; }
    // The model ID that wrote the outline.
    public string? OutlineModel { get; set; }
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
    // True when the question works with code ("what does this print"); false for theory. The AI
    // tags its own questions; the user's own questions count as code when they hold a code block.
    public bool IsCode { get; set; }
    // The model ID that wrote the question; null for the user's own and for older questions.
    public string? GeneratedByModel { get; set; }
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
    public int ReusePercent { get; set; } = Services.ExamService.DefaultReusePercent;
    // Optional guidance from the user for every AI generation for this exam ("focus on XY").
    public string? Instructions { get; set; }
    // Programming topics only: the share of questions that work with code (0-100); the rest are theory.
    public int CodePercent { get; set; } = Services.ExamService.DefaultCodePercent;
    // Materials of the topic this exam leaves out: the AI doesn't write from them and bank questions
    // from them aren't reused. Null or empty means every material, including ones added later.
    public List<int>? ExcludedMaterialIds { get; set; }
    // The topic's "Weak questions" practice: one per topic, its questions rebuilt from the ones most
    // often answered wrong each time it's started. Not listed with the topic's exams or edited.
    public bool IsPractice { get; set; }
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
    public List<FollowUp> FollowUps { get; set; } = [];

    public bool IsAnswered => AnsweredAt is not null;

    public IReadOnlyList<int> OptionOrderIds => ParseIds(OptionOrder);
    public IReadOnlyList<int> SelectedIds => ParseIds(SelectedOptionIds);

    public static string JoinIds(IEnumerable<int> ids) => string.Join(',', ids);

    private static List<int> ParseIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
}

// A question the user asked the AI about a revealed answer ("why isn't B right?"), and its reply.
// Kept with the attempt's answer so it's still there when the attempt is reviewed.
public sealed class FollowUp
{
    public const int MaxPerAnswer = 3;
    public const int MaxQuestionLength = 500;

    public int Id { get; set; }
    public int AttemptAnswerId { get; set; }
    public int Order { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AttemptAnswer? AttemptAnswer { get; set; }
}

// "Explain it back": the user explains one concept from their material in their own words, the AI
// asks one or two questions about it like a curious student, and then says what was right and what
// was missing. Kept per topic so earlier explanations can be looked at again.
public sealed class ExplainSession
{
    public const int MaxConceptLength = 300;
    public const int MaxExplanationLength = 2_500;
    public const int MaxReplyLength = 1_000;

    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int TopicId { get; set; }
    // The section the concept comes from; its text is what the AI checks the explanation against.
    public int? SectionId { get; set; }
    public string Concept { get; set; } = "";
    public string? Explanation { get; set; }
    // The AI's questions and the user's replies, as JSON string arrays.
    public string? QuestionsJson { get; set; }
    public string? RepliesJson { get; set; }
    public int? Score { get; set; }
    public string? Feedback { get; set; }
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public Topic? Topic { get; set; }

    public IReadOnlyList<string> Questions => Parse(QuestionsJson);
    public IReadOnlyList<string> Replies => Parse(RepliesJson);

    private static List<string> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
}

// App-wide switches the admin sets. A single row (Id 1); no row means everything is off.
public sealed class SiteSettings
{
    public int Id { get; set; } = 1;
    // Lets people ask for an account from the sign-in page; an admin approves each request.
    public bool AllowAccountRequests { get; set; }
}

// Someone asking for an account. Approving it creates the account and sends the normal invite;
// declining it sends a short email. Either way the request is then deleted, and unanswered ones
// are removed after AccountRequestService.KeepFor.
public sealed class AccountRequest
{
    public const int MaxEmailLength = 256;
    public const int MaxMessageLength = 1_000;

    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
