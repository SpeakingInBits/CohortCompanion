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
};
