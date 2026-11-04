// Small helpers called from Blazor through JS interop.
window.trovami = {
    scrollToTop: () => window.scrollTo({ top: 0, behavior: "instant" }),

    // Per-browser preferences. Storage can be unavailable (private mode, blocked site data),
    // so failures fall back to the default instead of breaking the page.
    getPref: (key) => {
        try { return localStorage.getItem("trovami." + key); } catch { return null; }
    },
    setPref: (key, value) => {
        try { localStorage.setItem("trovami." + key, value); } catch { }
    },

    // The browser's language and time zone, for a new user's first settings.
    browserLocale: () => [
        navigator.language || "en",
        (() => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ""; } catch { return ""; } })(),
    ],

    // Resolves to false when the browser refuses (no permission, insecure context).
    copyText: async (text) => {
        try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
    },

    // Saves a file the server sent as a stream.
    download: async (fileName, contentType, streamRef) => {
        const buffer = await streamRef.arrayBuffer();
        const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },

    // Opens the browser's date picker of a (hidden) date input; older browsers just focus it.
    showPicker: (input) => {
        try { input.showPicker(); } catch { input.focus(); }
    },

    // Calls the page's OnVisibleAsync whenever its tab is shown again, so what changed meanwhile
    // (in another tab, by the phone) appears. Ends by itself once the page is gone.
    onVisible: (page) => {
        const changed = () => {
            if (document.visibilityState !== "visible") return;
            page.invokeMethodAsync("OnVisibleAsync").catch(() => document.removeEventListener("visibilitychange", changed));
        };
        document.addEventListener("visibilitychange", changed);
    },

    print: () => window.print(),

    // Opens the file picker of a hidden <input type="file">.
    pickFile: (input) => input?.click(),

    focus: (selector) => document.querySelector(selector)?.focus(),

    // Floats a keyword's suggestions over everything (a dialog scrolls what extends past it) next to
    // its field: below it, or above when it doesn't fit there and there's more room above. Uses the
    // visual viewport, which leaves out a phone's keyboard. Kept in place while the page scrolls.
    placeKeywordList: (listId, inputId) => {
        const list = document.getElementById(listId);
        const box = document.getElementById(inputId)?.closest(".keywords");
        if (!list || !box) return;
        if (list.showPopover && !list.matches(":popover-open")) list.showPopover();
        const place = () => {
            if (!list.isConnected) {
                window.removeEventListener("scroll", window.trovami.keywordListPlace, true);
                window.visualViewport?.removeEventListener("resize", window.trovami.keywordListPlace);
                window.trovami.keywordListPlace = null;
                return;
            }
            const gap = 4, edge = 8;
            const rect = box.getBoundingClientRect();
            const height = window.visualViewport?.height ?? window.innerHeight;
            list.style.maxHeight = "";
            list.style.left = rect.left + "px";
            list.style.width = rect.width + "px";
            const wanted = list.offsetHeight;
            const below = height - rect.bottom - gap - edge;
            const above = rect.top - gap - edge;
            // On a phone it goes wherever there's more room, as below it would cover Cancel and Save.
            const up = above > below && (wanted > below || window.innerWidth <= 700);
            const room = Math.max(up ? above : below, 60);
            list.style.maxHeight = Math.min(wanted, room) + "px";
            list.style.top = (up ? rect.top - gap - Math.min(wanted, room) : rect.bottom + gap) + "px";
        };
        if (window.trovami.keywordListPlace) {
            window.removeEventListener("scroll", window.trovami.keywordListPlace, true);
            window.visualViewport?.removeEventListener("resize", window.trovami.keywordListPlace);
        }
        window.trovami.keywordListPlace = place;
        window.addEventListener("scroll", place, true);
        window.visualViewport?.addEventListener("resize", place);
        place();
    },

    // Scrolls an element into view.
    scrollIntoView: (selector) => document.querySelector(selector)?.scrollIntoView({ block: "nearest", behavior: "smooth" }),
};

// The height left beside a phone's keyboard, so a dialog and its fixed buttons stay above it.
if (window.visualViewport) {
    const setViewportHeight = () => document.documentElement.style.setProperty("--viewport-h", window.visualViewport.height + "px");
    window.visualViewport.addEventListener("resize", setViewportHeight);
    setViewportHeight();
}
