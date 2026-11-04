using System.Security.Claims;
using Trovami.Core;
using Trovami.Core.Email;
using Trovami.Core.Ai;
using Trovami.Core.Services;
using Trovami.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Trovami.Web.Components;

// Base for signed-in pages: the current user's id (every Core call is scoped by it), the shared
// dialogs and current household, and running an action with its error shown as a toast.
public abstract class AppPage : ComponentBase, IDisposable
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthState { get; set; }

    [CascadingParameter]
    protected AppUi Ui { get; set; } = default!;

    [Inject]
    protected NavigationManager Nav { get; set; } = default!;

    private string? _userId;
    private readonly CancellationTokenSource _pageCts = new();

    // Canceled when the user leaves the page.
    protected CancellationToken PageToken => _pageCts.Token;

    // True while RunAsync is working, to disable buttons.
    protected bool Busy { get; private set; }

    protected async Task<string> UserIdAsync()
    {
        if (_userId is not null)
            return _userId;

        var state = AuthState is null ? null : await AuthState;
        _userId = state?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Not signed in.");
        return _userId;
    }

    protected async Task<bool> IsAdminAsync()
    {
        var state = AuthState is null ? null : await AuthState;
        return state?.User.IsInRole(Roles.Admin) == true;
    }

    // Runs an action; a problem the person can fix (or something that's gone) shows as a toast.
    // Returns false when it didn't work.
    protected async Task<bool> RunAsync(Func<Task> work)
    {
        if (Busy) return false;
        Busy = true;
        StateHasChanged();
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InputException or NotFoundException or EmailException or InvalidOperationException or AiException)
        {
            Ui.Toast.Error(ErrorText(ex));
            return false;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }

    // Like RunAsync, for a save that happens alongside whatever else the page is doing: it neither
    // waits for Busy nor sets it.
    protected async Task<bool> TryAsync(Func<Task> work)
    {
        try
        {
            await work();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InputException or NotFoundException or InvalidOperationException)
        {
            Ui.Toast.Error(ErrorText(ex));
            return false;
        }
    }

    // Core's errors are already in words for people.
    protected static string ErrorText(Exception ex) => ex.Message;

    // The household the pages show; null while the person isn't in one.
    protected HouseholdView? Household => Ui.Household;

    private Func<Task>? _onChanged;

    // Reloads the page's data whenever the household changes (switched in the header, renamed,
    // Inbox count updated). Unsubscribed when the page goes away.
    protected void ReloadOnHouseholdChange(Func<Task> reload)
    {
        _onChanged = () => InvokeAsync(async () =>
        {
            await reload();
            StateHasChanged();
        });
        Ui.Changed += _onChanged;
    }

    public virtual void Dispose()
    {
        if (_onChanged is not null)
            Ui.Changed -= _onChanged;
        _pageCts.Cancel();
        _pageCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
