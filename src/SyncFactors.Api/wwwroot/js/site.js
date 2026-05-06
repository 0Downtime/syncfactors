(function () {
    var key = "syncfactors-next-theme";
    var root = document.documentElement;
    var toggle = document.getElementById("theme-toggle");

    if (!toggle) {
        return;
    }

    function applyTheme(theme) {
        root.dataset.theme = theme;
        root.style.colorScheme = theme;
        toggle.setAttribute("aria-pressed", String(theme === "dark"));
        toggle.setAttribute("title", theme === "dark" ? "Dark mode enabled" : "Light mode enabled");
    }

    function persistTheme(theme) {
        try {
            globalThis.localStorage.setItem(key, theme);
        } catch (error) {
            globalThis.console.debug("Theme preference could not be persisted.", error);
        }
    }

    applyTheme(root.dataset.theme === "dark" ? "dark" : "light");

    toggle.addEventListener("click", function () {
        var nextTheme = root.dataset.theme === "dark" ? "light" : "dark";
        applyTheme(nextTheme);
        persistTheme(nextTheme);
    });
})();
