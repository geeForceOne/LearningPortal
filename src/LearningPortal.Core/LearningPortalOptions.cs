using LearningPortal.Core.Ai;

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

    // Generations and analyses whose estimated input would cost more than this (US dollars, at
    // the chosen model's price) ask the user to confirm first.
    public decimal LargeGenerationWarnCost { get; set; } = 1.00m;

    // The same guard for a custom model ID with no known price, measured in tokens instead.
    public int LargeGenerationWarnTokens { get; set; } = 60_000;

    // Whether sending this much material to this model should be confirmed first.
    public bool IsLargeGeneration(int inputTokens, AiModelInfo? model) =>
        model is null ? inputTokens > LargeGenerationWarnTokens : model.InputCost(inputTokens) > LargeGenerationWarnCost;

    // Target size when a material has no usable headings and has to be split by length.
    public int SectionTargetTokens { get; set; } = 3_000;

    // Questions requested per AI call; bigger batches mean fewer calls but longer waits.
    public int QuestionsPerCall { get; set; } = 5;
}
