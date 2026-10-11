using LearningPortal.Web.Components.Shared;

namespace LearningPortal.Web.Services;

// The layout's shared dialogs, cascaded to every page so each one can await a confirmation or
// show a toast without declaring its own modal markup.
public sealed class AppUi
{
    public ConfirmDialog Confirm { get; internal set; } = default!;
    public PromptDialog Prompt { get; internal set; } = default!;
    public Toast Toast { get; internal set; } = default!;

    // The key reminder above the page, so Settings can refresh it when the keys in use change.
    public KeyExpiryBanner KeyBanner { get; internal set; } = default!;
}
