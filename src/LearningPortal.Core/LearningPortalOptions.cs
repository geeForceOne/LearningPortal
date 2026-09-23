namespace LearningPortal.Core;

// Tunables shared by the Core services. Bound from configuration ("LearningPortal" section)
// in the web host; paths come from environment variables so they can point into a volume.
public sealed class LearningPortalOptions
{
    // Where uploaded source files are kept (one folder per user).
    public string FilesRoot { get; set; } = "data/files";

    public long MaxUploadBytes { get; set; } = 50L * 1024 * 1024;

    // A topic whose materials total less than this is sent in full for generation; above it,
    // generation works section by section.
    public int TopicTokenBudget { get; set; } = 120_000;

    // Generations whose estimated input exceeds this ask the user to confirm first.
    public int LargeGenerationWarnTokens { get; set; } = 60_000;

    // Target size when a material has no usable headings and has to be split by length.
    public int SectionTargetTokens { get; set; } = 3_000;

    // Questions requested per AI call; bigger batches mean fewer calls but longer waits.
    public int QuestionsPerCall { get; set; } = 5;
}
