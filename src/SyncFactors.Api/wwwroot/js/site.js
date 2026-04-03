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
            window.localStorage.setItem(key, theme);
        } catch (error) {
            return;
        }
    }

    applyTheme(root.dataset.theme === "dark" ? "dark" : "light");

    toggle.addEventListener("click", function () {
        var nextTheme = root.dataset.theme === "dark" ? "light" : "dark";
        applyTheme(nextTheme);
        persistTheme(nextTheme);
    });
})();
