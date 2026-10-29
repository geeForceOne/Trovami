// Animates the logo (Components/Shared/BrandMark.razor). Four motions, picked at random for each
// play and never the same twice in a row:
//   drop   - the pin falls onto the box and bounces, a ring spreads where it lands;
//   ping   - the dot in the pin's head pulses and rings spread from the box, "here it is";
//   wobble - the pin sways on its tip like a needle settling;
//   open   - the box's lid pops up and the pin rises out of it.
// The header and sign-in logos play one motion when hovered or focused; a mark with
// data-motion="once" plays one shortly after it appears, and one with data-motion="loop" keeps
// playing while it's on the page. Nothing moves when the user asks for reduced motion. Only the
// Web Animations API is used: no attribute Blazor renders is changed.
(() => {
    const LOOP_PAUSE = 600;
    const reduced = matchMedia("(prefers-reduced-motion: reduce)");
    const playing = new WeakSet();
    const started = new WeakSet();
    let lastMotion = null;
    const backOut = "cubic-bezier(.34,1.56,.64,1)";

    const parts = svg => {
        const at = (el, origin) => { if (el) { el.style.transformBox = "fill-box"; el.style.transformOrigin = origin; } return el; };
        return {
            pin: at(svg.querySelector(".bm-pin"), "center bottom"),
            dot: at(svg.querySelector(".bm-dot"), "center"),
            box: at(svg.querySelector(".bm-box"), "center bottom"),
            lid: at(svg.querySelector(".bm-lid"), "left center"),
            ping: at(svg.querySelector(".bm-ping"), "center"),
        };
    };

    const done = anims => Promise.all(anims.filter(Boolean).map(a => a.finished.catch(() => { })));
    const ring = (el, delay = 0) => el.animate(
        [{ transform: "scale(.3)", opacity: 0.9 }, { transform: "scale(1.25)", opacity: 0 }],
        { duration: 700, delay, easing: "ease-out" });

    const MOTIONS = {
        drop: m => done([
            m.pin.animate([
                { transform: "translateY(-28px)", opacity: 0 },
                { transform: "translateY(0)", opacity: 1, offset: 0.55, easing: "ease-out" },
                { transform: "translateY(-6px)", offset: 0.75, easing: "ease-in" },
                { transform: "translateY(0)" },
            ], { duration: 800, easing: "ease-in" }),
            m.box.animate([{ transform: "scaleY(1)" }, { transform: "scaleY(.9)" }, { transform: "scaleY(1)" }], { duration: 300, delay: 420 }),
            ring(m.ping, 430),
        ]),
        ping: m => done([
            m.dot.animate([{ transform: "scale(1)" }, { transform: "scale(1.6)" }, { transform: "scale(1)" }], { duration: 500, iterations: 2, easing: "ease-in-out" }),
            ring(m.ping, 100),
            ring(m.ping, 550),
        ]),
        wobble: m => done([
            m.pin.animate([
                { transform: "rotate(0)" }, { transform: "rotate(-14deg)" }, { transform: "rotate(10deg)" },
                { transform: "rotate(-6deg)" }, { transform: "rotate(3deg)" }, { transform: "rotate(0)" },
            ], { duration: 1000, easing: "ease-in-out" }),
        ]),
        open: m => done([
            m.lid?.animate([{ transform: "rotate(0)" }, { transform: "rotate(-24deg) translateY(-3px)" }, { transform: "rotate(0)" }], { duration: 800, easing: "ease-in-out" }),
            m.pin.animate([{ transform: "translateY(10px) scale(.6)", opacity: 0 }, { transform: "translateY(0) scale(1)", opacity: 1 }],
                { duration: 650, delay: 150, easing: backOut, fill: "backwards" }),
            ring(m.ping, 500),
        ]),
    };

    const pick = () => {
        const names = Object.keys(MOTIONS).filter(n => n !== lastMotion);
        return lastMotion = names[Math.floor(Math.random() * names.length)];
    };

    async function play(svg) {
        if (reduced.matches || playing.has(svg) || !svg.isConnected) return;
        playing.add(svg);
        try { await MOTIONS[pick()](parts(svg)); }
        catch { }
        finally { playing.delete(svg); }
    }

    async function loop(svg) {
        while (svg.isConnected && svg.dataset.motion === "loop" && !reduced.matches) {
            await play(svg);
            await new Promise(r => setTimeout(r, LOOP_PAUSE));
        }
        started.delete(svg);
    }

    function scan() {
        for (const svg of document.querySelectorAll("svg.brand-mark[data-motion]")) {
            if (started.has(svg)) continue;
            started.add(svg);
            if (svg.dataset.motion === "loop") loop(svg);
            else setTimeout(() => play(svg), 350);
        }
    }

    const trigger = e => {
        const host = e.target.closest?.(".brand, .auth-brand");
        // Only when the pointer comes in from outside, not when it moves between the logo's parts.
        if (!host || (e.relatedTarget && host.contains(e.relatedTarget))) return;
        const svg = host.querySelector("svg.brand-mark");
        if (svg) play(svg);
    };
    document.addEventListener("pointerover", trigger);
    document.addEventListener("focusin", trigger);

    new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", scan); else scan();
})();
