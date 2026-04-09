(function () {
    const storageKey = "syncfactors-next-theme";
    const root = document.documentElement;
    const toggle = document.getElementById("theme-toggle");

    function announceTheme(theme) {
        window.dispatchEvent(new CustomEvent("syncfactors:themechange", {
            detail: { theme }
        }));
    }

    function applyTheme(theme, options = {}) {
        const resolvedTheme = theme === "dark" ? "dark" : "light";
        const shouldPersist = options.persist === true;
        const shouldAnnounce = options.announce !== false;

        root.dataset.theme = resolvedTheme;
        root.style.colorScheme = resolvedTheme;

        if (toggle) {
            toggle.setAttribute("aria-pressed", String(resolvedTheme === "dark"));
            toggle.setAttribute("title", resolvedTheme === "dark" ? "Dark mode enabled" : "Light mode enabled");
        }

        if (shouldPersist) {
            try {
                window.localStorage.setItem(storageKey, resolvedTheme);
            } catch (error) {
                return;
            }
        }

        if (shouldAnnounce) {
            announceTheme(resolvedTheme);
        }
    }

    applyTheme(root.dataset.theme === "dark" ? "dark" : "light", { announce: false });

    if (!toggle) {
        return;
    }

    toggle.addEventListener("click", function () {
        const nextTheme = root.dataset.theme === "dark" ? "light" : "dark";
        applyTheme(nextTheme, { persist: true });
    });
})();
