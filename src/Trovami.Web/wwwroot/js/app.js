// Small helpers called from Blazor through JS interop.
window.trovami = {
    scrollToTop: () => window.scrollTo({ top: 0, behavior: "instant" }),

    // The photo cropper: a rectangle over the photo, as fractions of it. Dragging a corner resizes it,
    // dragging inside moves it, dragging anywhere else draws a new one.
    cropStart: (frame) => {
        const box = frame.querySelector(".crop-box");
        const limit = (v) => Math.min(1, Math.max(0, v));
        let area = { x: 0.1, y: 0.1, w: 0.8, h: 0.8 };
        let drag = null;
        const draw = () => {
            box.style.left = area.x * 100 + "%";
            box.style.top = area.y * 100 + "%";
            box.style.width = area.w * 100 + "%";
            box.style.height = area.h * 100 + "%";
        };
        const point = (e) => {
            const r = frame.getBoundingClientRect();
            return { x: limit((e.clientX - r.left) / r.width), y: limit((e.clientY - r.top) / r.height), width: r.width, height: r.height };
        };
        const between = (a, b) => ({ x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(a.x - b.x), h: Math.abs(a.y - b.y) });
        frame.onpointerdown = (e) => {
            const p = point(e);
            const reach = e.pointerType === "touch" ? 32 : 18;
            const near = (cx, cy) => Math.abs(p.x - cx) * p.width <= reach && Math.abs(p.y - cy) * p.height <= reach;
            const right = area.x + area.w, bottom = area.y + area.h;
            const before = area;
            if (near(area.x, area.y)) drag = { anchor: { x: right, y: bottom }, before };
            else if (near(right, area.y)) drag = { anchor: { x: area.x, y: bottom }, before };
            else if (near(area.x, bottom)) drag = { anchor: { x: right, y: area.y }, before };
            else if (near(right, bottom)) drag = { anchor: { x: area.x, y: area.y }, before };
            else if (p.x > area.x && p.x < right && p.y > area.y && p.y < bottom) drag = { offset: { x: p.x - area.x, y: p.y - area.y }, before };
            else drag = { anchor: { x: p.x, y: p.y }, before };
            frame.setPointerCapture(e.pointerId);
            e.preventDefault();
        };
        frame.onpointermove = (e) => {
            if (!drag) return;
            const p = point(e);
            area = drag.anchor
                ? between(drag.anchor, p)
                : { ...area, x: limit(Math.min(p.x - drag.offset.x, 1 - area.w)), y: limit(Math.min(p.y - drag.offset.y, 1 - area.h)) };
            draw();
        };
        frame.onpointerup = frame.onpointercancel = () => {
            // A click or a tiny drag isn't a selection: the one before it stays.
            if (drag && (area.w < 0.02 || area.h < 0.02)) { area = drag.before; draw(); }
            drag = null;
        };
        frame.cropArea = () => area;
        draw();
    },
    cropArea: (frame) => frame.cropArea(),

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

    // True where the location page shows one column, the same width as in app.css.
    isOneColumn: () => matchMedia("(max-width: 700px)").matches,

    // Scrolls an element into view.
    scrollIntoView: (selector) => document.querySelector(selector)?.scrollIntoView({ block: "nearest", behavior: "smooth" }),
};

// Left and Right press the item page's previous and next buttons, so they behave just like those.
// Not while typing (the arrows move the cursor there), in a dialog, or while cropping a photo.
document.addEventListener("keydown", (e) => {
    if ((e.key !== "ArrowLeft" && e.key !== "ArrowRight") || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
    if (e.target.closest?.("input, textarea, select, [contenteditable]")) return;
    if (document.querySelector(".modal, .crop-frame")) return;
    document.querySelector(`.item-pager button[aria-label="${e.key === "ArrowLeft" ? "Previous" : "Next"} item"]`)?.click();
});

// Ctrl+C presses the item page's Crop photo button. Copying comes first: not with text selected or
// in a field. Not in a dialog either. While cropping the button is gone, so it does nothing then, and
// applying a crop, which can't be undone, always takes the Crop button.
document.addEventListener("keydown", (e) => {
    if (e.key.toLowerCase() !== "c" || !e.ctrlKey || e.altKey || e.metaKey || e.shiftKey || e.repeat) return;
    if (e.target.closest?.("input, textarea, select, [contenteditable]")) return;
    if (document.querySelector(".modal") || !window.getSelection().isCollapsed) return;
    const crop = document.querySelector("[data-crop-start]");
    if (!crop || crop.disabled) return;
    e.preventDefault();
    crop.click();
});

// The height left beside a phone's keyboard, so a dialog and its fixed buttons stay above it.
if (window.visualViewport) {
    const setViewportHeight = () => document.documentElement.style.setProperty("--viewport-h", window.visualViewport.height + "px");
    window.visualViewport.addEventListener("resize", setViewportHeight);
    setViewportHeight();
}
