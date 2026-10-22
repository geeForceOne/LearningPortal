namespace LearningPortal.Core.Models;

public enum AiProvider
{
    Claude,
    OpenAi,
}

// The color theme. System follows the device's light/dark setting.
public enum Theme
{
    Dark,
    Light,
    System,
}

public enum MaterialKind
{
    Pdf,
    Word,
    Latex,
    Markdown,
    Text,
    // Added after the others: stored as a number, so existing rows keep their meaning.
    PowerPoint,
}

public enum AnalysisStatus
{
    Pending,
    Done,
    Failed,
}

public enum QuestionType
{
    MultipleChoice,
    Written,
}

// What an exam asks for. Mixed means the exam holds both kinds, split roughly in half.
// Where a question's answer comes from. Material: the topic's own material (the source section says
// where). Paper: the exam paper the user uploaded gave it. Web: the AI looked it up on the web
// (WebSourcesJson lists the pages). General: the AI answered from its own knowledge. Web and
// General answers aren't checked against the user's sources, so the UI says they may be wrong.
public enum AnswerSource
{
    Material,
    Paper,
    Web,
    General,
}

public enum ExamType
{
    MultipleChoice,
    Written,
    Mixed,
}

public enum Difficulty
{
    Easy,
    Medium,
    Hard,
}

// How a written answer's 0-100 score is presented. The thresholds are a product decision:
// 80+ is correct, anything above zero is partial credit, zero is incorrect.
public enum Verdict
{
    Incorrect,
    Partial,
    Correct,
}

public static class VerdictRules
{
    public const int CorrectThreshold = 80;

    public static Verdict FromScore(int score) => score switch
    {
        >= CorrectThreshold => Verdict.Correct,
        > 0 => Verdict.Partial,
        _ => Verdict.Incorrect,
    };
}

public enum EmailSecurity
{
    // STARTTLS when the server offers it; implicit TLS on port 465.
    Auto,
    // TLS from the first byte (usually port 465).
    SslOnConnect,
    // Plain connection upgraded with STARTTLS (usually port 587); refuses servers without it.
    StartTls,
    // No encryption. Only for a relay on the same machine or private network.
    None,
}
