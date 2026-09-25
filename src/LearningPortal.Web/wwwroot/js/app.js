// Small helpers called from Blazor through JS interop.
window.learningPortal = {
    scrollToTop: () => window.scrollTo({ top: 0, behavior: "instant" }),

    // Per-browser preferences. Storage can be unavailable (private mode, blocked site data),
    // so failures fall back to the default instead of breaking the page.
    getPref: (key) => {
        try { return localStorage.getItem("lp." + key); } catch { return null; }
    },
    setPref: (key, value) => {
        try { localStorage.setItem("lp." + key, value); } catch { }
    },

    // Resolves to false when the browser refuses (no permission, insecure context).
    copyText: async (text) => {
        try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
    },

    // Syntax colouring for the code blocks inside one RichText element. Blocks already coloured are
    // skipped; an unknown language falls back to auto-detection, which is harmless if it's wrong.
    highlightCode: (element) => {
        if (!element || !window.hljs) return;
        for (const block of element.querySelectorAll("pre code:not([data-highlighted])")) {
            try { window.hljs.highlightElement(block); } catch { }
        }
    },
};
