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
};
