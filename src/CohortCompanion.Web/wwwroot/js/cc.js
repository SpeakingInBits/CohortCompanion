// Small browser helpers called from Blazor through JS interop.
window.cc = {
    // Returns highlighted, HTML-escaped code, or null when highlight.js can't handle the language.
    highlightCode(code, language) {
        if (!window.hljs) {
            return null;
        }

        try {
            return language && hljs.getLanguage(language)
                ? hljs.highlight(code, { language, ignoreIllegals: true }).value
                : hljs.highlightAuto(code).value;
        } catch {
            return null;
        }
    },

    // Lets an image be pasted into a drop zone by handing it to the zone's file input.
    enableImagePaste(zone) {
        zone.addEventListener("paste", (event) => {
            const input = zone.querySelector("input[type=file]");
            const files = event.clipboardData?.files;
            if (input && files?.length) {
                input.files = files;
                input.dispatchEvent(new Event("change", { bubbles: true }));
                event.preventDefault();
            }
        });
    },

    // Keeps the phone's screen on during a live session, so it doesn't lock halfway through a slide. Browsers
    // drop the lock whenever the page is hidden, so it's taken again on return. Not every browser supports it.
    async keepScreenOn(on) {
        wakeLockWanted = on;
        if (on) {
            await requestWakeLock();
        } else {
            await wakeLock?.release();
            wakeLock = null;
        }
    },

    // The browser's IANA time zone, such as America/Chicago, so the server can show times on the viewer's clock.
    timeZone() {
        return Intl.DateTimeFormat().resolvedOptions().timeZone ?? null;
    },

    // Makes the browser ask before closing or reloading the tab, while an editor has unsaved changes.
    warnBeforeLeaving(on) {
        window.removeEventListener("beforeunload", preventUnload);
        if (on) {
            window.addEventListener("beforeunload", preventUnload);
        }
    },

    // Lets the keyboard or a presentation clicker drive the host page from anywhere on it, not only once some part
    // of the page has focus. Keys typed into a field are left alone, and so is Space on a focused button, which
    // already presses that button. Returns a handle whose dispose() stops listening.
    listenForHostKeys(dotNet) {
        const keys = new Set(["ArrowRight", "ArrowLeft", "PageDown", "PageUp", " "]);
        const handler = (event) => {
            if (!keys.has(event.key) || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.repeat) {
                return;
            }

            const target = event.target instanceof Element ? event.target : null;
            if (target?.closest("input, textarea, select, [contenteditable]:not([contenteditable=false])")) {
                return;
            }

            if (event.key === " " && target?.closest("button, a, summary")) {
                return;
            }

            event.preventDefault();
            dotNet.invokeMethodAsync("OnHostKey", event.key);
        };

        document.addEventListener("keydown", handler);
        return { dispose: () => document.removeEventListener("keydown", handler) };
    },

    // Lets a finger drag the current slide sideways. Anything that isn't clearly a sideways drag stays with
    // the browser, so scrolling a long slide, pinch zoom, and scrolling a wide code block all keep working.
    attachSwipe(stage, dotNet) {
        const commitRatio = 0.22;
        const commitSpeed = 0.5; // px per ms, for a quick flick
        const edgeResistance = 0.25;
        const movedEnough = 10;

        let gesture = null;

        const scrollsSideways = (node) => {
            for (; node && node !== stage; node = node.parentElement) {
                if (node.scrollWidth > node.clientWidth + 1 && /auto|scroll/.test(getComputedStyle(node).overflowX)) {
                    return true;
                }
            }

            return false;
        };

        const canMove = (dx) => stage.dataset[dx < 0 ? "canForward" : "canBack"] === "true";

        const settle = (slide) => {
            slide.style.transition = "transform 0.28s cubic-bezier(0.22, 1, 0.36, 1)";
            slide.style.transform = "";
        };

        const onStart = (event) => {
            const slide = stage.querySelector(".pres-slide");
            gesture = event.touches.length === 1 && slide && !scrollsSideways(event.target)
                ? { slide, x: event.touches[0].clientX, y: event.touches[0].clientY, time: event.timeStamp, dx: 0, dragging: false }
                : null;
        };

        const onMove = (event) => {
            if (!gesture) {
                return;
            }

            if (event.touches.length > 1) {
                settle(gesture.slide);
                gesture = null;
                return;
            }

            const dx = event.touches[0].clientX - gesture.x;
            const dy = event.touches[0].clientY - gesture.y;

            if (!gesture.dragging) {
                if (Math.abs(dx) < movedEnough && Math.abs(dy) < movedEnough) {
                    return;
                }

                if (Math.abs(dy) > Math.abs(dx)) {
                    gesture = null;
                    return;
                }

                gesture.dragging = true;
                gesture.slide.style.transition = "none";
            }

            gesture.dx = dx;
            gesture.slide.style.transform = `translateX(${canMove(dx) ? dx : dx * edgeResistance}px)`;
        };

        const onEnd = (event) => {
            const finished = gesture;
            gesture = null;
            if (!finished?.dragging) {
                return;
            }

            const { slide, dx } = finished;
            const speed = Math.abs(dx) / Math.max(1, event.timeStamp - finished.time);
            const farEnough = Math.abs(dx) > stage.clientWidth * commitRatio || speed > commitSpeed;

            if (!farEnough || !canMove(dx)) {
                settle(slide);
                return;
            }

            slide.style.transition = "transform 0.16s ease-in, opacity 0.16s ease-in";
            slide.style.transform = `translateX(${Math.sign(dx) * stage.clientWidth}px)`;
            slide.style.opacity = "0";

            setTimeout(async () => {
                await dotNet.invokeMethodAsync("OnSwiped", dx < 0 ? 1 : -1);

                // Normally the slide has been replaced by now. If it wasn't, put it back.
                if (slide.isConnected) {
                    slide.style.opacity = "";
                    settle(slide);
                }
            }, 150);
        };

        stage.addEventListener("touchstart", onStart, { passive: true });
        stage.addEventListener("touchmove", onMove, { passive: true });
        stage.addEventListener("touchend", onEnd, { passive: true });
        stage.addEventListener("touchcancel", onEnd, { passive: true });

        return {
            dispose() {
                stage.removeEventListener("touchstart", onStart);
                stage.removeEventListener("touchmove", onMove);
                stage.removeEventListener("touchend", onEnd);
                stage.removeEventListener("touchcancel", onEnd);
            },
        };
    },
};

let wakeLock = null;
let wakeLockWanted = false;

// Browsers show their own "Leave site?" wording; asking at all is all a page can do.
function preventUnload(event) {
    event.preventDefault();
    event.returnValue = "";
}

async function requestWakeLock() {
    try {
        wakeLock = await navigator.wakeLock?.request("screen");
    } catch {
        // Refused, for example on low battery. The session still works.
    }
}

document.addEventListener("visibilitychange", () => {
    if (wakeLockWanted && document.visibilityState === "visible") {
        requestWakeLock();
    }
});

// Enhanced navigation swaps the page content without a page load, so the entrance animation has to be replayed.
document.addEventListener("DOMContentLoaded", () => {
    window.Blazor?.addEventListener("enhancedload", () => {
        if (!matchMedia("(prefers-reduced-motion: reduce)").matches) {
            document.querySelector("article.content")?.animate(
                [{ opacity: 0, transform: "translateY(8px)" }, { opacity: 1, transform: "none" }],
                { duration: 280, easing: "cubic-bezier(0.22, 1, 0.36, 1)" });
        }
    });
});
