(function () {
    var key = "syncfactors-next-theme";
    var storedTheme = null;
    try {
        storedTheme = window.localStorage.getItem(key);
    } catch (error) {
        storedTheme = null;
    }

    var themePreference = storedTheme === "system" || storedTheme === "dark" || storedTheme === "light"
        ? storedTheme
        : "system";
    var theme = themePreference === "dark"
        ? "dark"
        : themePreference === "light"
            ? "light"
            : (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");

    document.documentElement.dataset.themePreference = themePreference;
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
})();
