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

    // Scrolls an element into view.
    scrollIntoView: (selector) => document.querySelector(selector)?.scrollIntoView({ block: "nearest", behavior: "smooth" }),
};
