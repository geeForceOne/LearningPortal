using System.Security.Claims;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace LearningPortal.Web.Components;

// Base for signed-in pages: resolves the current user's id (every Core call is scoped by it)
// and runs long operations with a status line, cancellation and user-facing error handling.
public abstract class AppPage : ComponentBase, IDisposable
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    [Inject]
    protected NavigationManager Nav { get; set; } = default!;

    private string? _userId;
    private CancellationTokenSource? _busyCts;
    private readonly CancellationTokenSource _pageCts = new();

    // Status text while a long operation runs; null when idle.
    protected string? BusyMessage { get; private set; }
    protected bool IsBusy => BusyMessage is not null;

    // Cancelled when the user leaves the page.
    protected CancellationToken PageToken => _pageCts.Token;

    protected async Task<string> UserIdAsync()
    {
        if (_userId is not null)
            return _userId;

        var state = AuthState is null ? null : await AuthState;
        _userId = state?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Not signed in.");
        return _userId;
    }

    // Runs work that reports progress. Returns false when it failed or was cancelled; the error
    // (already in user-facing words) goes to onError.
    protected async Task<bool> RunBusyAsync(
        string initialMessage,
        Func<IProgress<string>, CancellationToken, Task> work,
        Action<string> onError)
    {
        _busyCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        BusyMessage = initialMessage;
        StateHasChanged();

        var progress = new Progress<string>(message =>
        {
            BusyMessage = message;
            _ = InvokeAsync(StateHasChanged);
        });

        try
        {
            await work(progress, _busyCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is AiException or ArgumentException or InvalidOperationException or NotFoundException
                                       or LearningPortal.Core.Text.UnsupportedMaterialException
                                       or LearningPortal.Core.Email.EmailException)
        {
            onError(ex.Message);
            return false;
        }
        finally
        {
            BusyMessage = null;
            _busyCts?.Dispose();
            _busyCts = null;
            StateHasChanged();
        }
    }

    protected void CancelBusy() => _busyCts?.Cancel();

    public virtual void Dispose()
    {
        _pageCts.Cancel();
        _pageCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
