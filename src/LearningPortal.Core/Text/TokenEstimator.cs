namespace LearningPortal.Core.Text;

// A cheap, provider-neutral estimate: roughly four characters per token for Latin-script text.
// It's only used for budgeting and for the numbers shown to the user, never for billing.
public static class TokenEstimator
{
    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    public static string Format(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M tokens",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k tokens",
        _ => $"{tokens} tokens",
    };
}
