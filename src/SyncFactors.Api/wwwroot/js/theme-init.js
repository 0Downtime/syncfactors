(function () {
    var key = "syncfactors-next-theme";
    var storedTheme = null;
    try {
        storedTheme = globalThis.localStorage.getItem(key);
    } catch (error) {
        globalThis.console.debug("Theme preference could not be read.", error);
        storedTheme = null;
    }

    var themePreference = storedTheme === "system" || storedTheme === "dark" || storedTheme === "light"
        ? storedTheme
        : "system";
    var theme = themePreference === "dark"
        ? "dark"
        : themePreference === "light"
            ? "light"
            : (globalThis.matchMedia && globalThis.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");

    document.documentElement.dataset.themePreference = themePreference;
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
})();
