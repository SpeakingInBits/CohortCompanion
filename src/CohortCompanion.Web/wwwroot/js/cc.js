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
};
