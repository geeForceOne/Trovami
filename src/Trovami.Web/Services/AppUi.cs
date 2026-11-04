using Trovami.Core.Models;
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

    // The pinned keywords in the navigation, and every keyword's color for the chips.
    public IReadOnlyList<KeywordCount> PinnedKeywords { get; private set; } = [];
    public IReadOnlyDictionary<string, string> KeywordColors { get; private set; } = new Dictionary<string, string>();

    internal void SetKeywords(IReadOnlyList<KeywordCount> keywords)
    {
        PinnedKeywords = keywords.Where(k => k.Pinned).ToList();
        KeywordColors = keywords.Where(k => k.Color is not null).ToDictionary(k => k.Keyword, k => k.Color!);
    }

    // The last page before an item page, base-relative, for the item's Close; null after a direct link.
    public string? BeforeItem { get; internal set; }

    // The kind of the last location added, the next one's default, until the page reloads.
    public LocationKind? LastLocationKind { get; set; }

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
