namespace LearningPortal.Core.Models;

public enum AiProvider
{
    Claude,
    OpenAi,
}

public enum MaterialKind
{
    Pdf,
    Word,
    Latex,
    Markdown,
    Text,
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
