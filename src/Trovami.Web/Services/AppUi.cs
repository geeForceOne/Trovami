using Trovami.Core.Services;
using Trovami.Web.Components.Shared;

namespace Trovami.Web.Services;

// The layout's shared dialogs and the household being shown, cascaded to every page so each one
// can await a confirmation, show a toast or scope its queries without declaring its own.
public sealed class AppUi
{
    public ConfirmDialog Confirm { get; internal set; } = default!;
    public PromptDialog Prompt { get; internal set; } = default!;
    public Toast Toast { get; internal set; } = default!;

    // The household the pages show; null while the person isn't in any (the home page then offers
    // to create one).
    public HouseholdView? Household { get; internal set; }
    public IReadOnlyList<HouseholdView> Households { get; internal set; } = [];

    // Waiting for the person to accept or decline.
    public IReadOnlyList<InviteView> Invites { get; internal set; } = [];

    // The Inbox count in the navigation.
    public int InboxCount { get; internal set; }

    // Raised after the household changed (switched, renamed, created) or the Inbox count did;
    // pages that show household data reload.
    public event Func<Task>? Changed;

    // Set by the layout: reloads households, invites and the Inbox count, then raises Changed.
    internal Func<Task>? Reload { get; set; }

    public async Task RefreshAsync()
    {
        if (Reload is not null)
            await Reload();
    }

    internal async Task RaiseChangedAsync()
    {
        if (Changed is { } handlers)
            foreach (var h in handlers.GetInvocationList().Cast<Func<Task>>())
                await h();
    }
}
