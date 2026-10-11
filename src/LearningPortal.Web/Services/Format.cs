using System.Globalization;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Models;
using LearningPortal.Core.Services;
using LearningPortal.Core.Text;

namespace LearningPortal.Web.Services;

// Display wording for values that appear on several pages, so they read the same everywhere.
public static class Format
{
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)span.TotalMinutes} min ago",
            < 60 * 24 => $"{(int)span.TotalHours} h ago",
            < 60 * 24 * 2 => "yesterday",
            < 60 * 24 * 30 => $"{(int)span.TotalDays} days ago",
            _ => utc.ToLocalTime().ToString("d MMM yyyy"),
        };
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    // A calendar day: "4 Oct 2026".
    public static string Day(DateOnly day) => day.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Duration(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min {seconds % 60:00} s",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60:00} min",
    };

    public static string Percent(double? value) => value is null ? "-" : $"{Math.Round(value.Value):0}%";

    public static string Tokens(int tokens) => TokenEstimator.Format(tokens);

    // The confirmation shown before a generation that sends a lot of material.
    public static string LargeGeneration(GenerationEstimate e, SettingsView? settings = null)
    {
        var text = $"This takes about {Plural(e.Calls, "AI request", "AI requests")} and sends roughly {Tokens(e.TotalInputTokens)} of material in total";
        if (e is { Model: { } model, InputCost: { } cost })
            text += $", roughly {Money(cost)} on {model.Name} plus a little for the AI's reply (less if the provider caches the material).";
        else
            text += " (less if the provider caches it).";
        return text + $" {BilledTo(settings)} Go ahead?";
    }

    // Who pays for a costly AI action: the user's own key, or their grant on the admin's key.
    public static string BilledTo(SettingsView? settings) =>
        settings is { UsingShared: true } ? "That counts against the key from your admin." : "That's billed to your API key.";

    // An exact dollar amount, for balances and spend ("$0.00" rather than Money's "less than $0.01").
    public static string Usd(decimal amount) => "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);

    // A grant's limits in a few words: "$5.00 a month, until 31 Dec 2026".
    public static string GrantLimits(SharedKeyGrantView g)
    {
        var balance = g.Balance is { } b ? Usd(b) + (g.Monthly ? " a month" : " in total") : null;
        var until = g.ExpiresOn is { } date ? $"{(balance is null ? "Until" : "until")} {Day(date)}" : null;
        return balance is null && until is null ? "Unlimited" : string.Join(", ", new[] { balance, until }.Where(p => p is not null));
    }

    // Where a grant stands right now, as a sentence or two for the person using it.
    public static string GrantStatus(SharedKeyGrantView g)
    {
        switch (g.State)
        {
            case GrantState.Expired:
                return $"It expired on {Day(g.ExpiresOn!.Value)}.";
            case GrantState.UsedUp when g.Monthly:
                return $"This month's balance of {Usd(g.Balance!.Value)} is used up. It's full again on {Day(SharedKeyGrantView.NextReset)}.";
            case GrantState.UsedUp:
                return $"The balance of {Usd(g.Balance!.Value)} is used up.";
        }
        var balance = g.Balance is not { } b ? "No spending limit."
            : g.Monthly ? $"{Usd(g.Remaining!.Value)} of {Usd(b)} left this month; it's full again on {Day(SharedKeyGrantView.NextReset)}."
            : $"{Usd(g.Remaining!.Value)} of {Usd(b)} left.";
        return balance + (g.ExpiresOn is { } date ? $" Works until {Day(date)}." : " No expiry date.");
    }

    // Costs below this aren't worth mentioning in the UI; above it, AI actions show tokens and cost.
    // With a custom model (no known price), token count decides instead.
    public const decimal NotableCost = 0.10m;
    public const int NotableTokensWithoutPrice = 20_000;

    // "74k tokens, roughly $0.37 on Claude Opus 5" when that's worth showing, otherwise null.
    public static string? CostHint(int tokens, AiModelInfo? model)
    {
        if (model is null)
            return tokens >= NotableTokensWithoutPrice ? $"about {Tokens(tokens)}" : null;
        var cost = model.InputCost(tokens);
        return cost >= NotableCost ? $"about {Tokens(tokens)}, roughly {Money(cost)} on {model.Name}" : null;
    }

    public static string? CostHint(GenerationEstimate e) => e.Calls == 0 ? null : CostHint(e.TotalInputTokens, e.Model);

    // "roughly $0.37 on Claude Opus 5" for analyzing material of this size, or null when the
    // chosen model's price isn't known (a custom model ID).
    public static string? AnalysisCost(int tokens, AiModelInfo? model) =>
        model is null ? null
        : model.InputCost(tokens) < 0.01m ? $"under $0.01 on {model.Name}"
        : $"roughly {Money(model.InputCost(tokens))} on {model.Name}";

    public static string Money(decimal amount) =>
        amount < 0.01m ? "less than $0.01" : "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    public static string Label(ExamType type) => type switch
    {
        ExamType.MultipleChoice => "Multiple choice",
        ExamType.Written => "Written",
        _ => "Mixed",
    };

    public static string Label(QuestionType type) => type == QuestionType.MultipleChoice ? "Multiple choice" : "Written";

    public static string Label(Difficulty difficulty) => difficulty.ToString();

    public static string Label(MaterialKind kind) => kind switch
    {
        MaterialKind.Pdf => "PDF",
        MaterialKind.Word => "Word",
        MaterialKind.PowerPoint => "PowerPoint",
        MaterialKind.Latex => "LaTeX",
        MaterialKind.Markdown => "Markdown",
        _ => "Text",
    };

    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    public static string VerdictClass(double score) => VerdictRules.FromScore((int)Math.Round(score)).ToString().ToLowerInvariant();
}
