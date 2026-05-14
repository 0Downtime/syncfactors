(function () {
    const key = "syncfactors-next-theme";
    let storedTheme = null;
    try {
        storedTheme = globalThis.localStorage.getItem(key);
    } catch (error) {
        globalThis.console.debug("Theme preference could not be read.", error);
        storedTheme = null;
    }

    const themePreference = storedTheme === "system" || storedTheme === "dark" || storedTheme === "light"
        ? storedTheme
        : "system";
    const prefersDark = globalThis.matchMedia?.("(prefers-color-scheme: dark)")?.matches === true;
    let theme = "light";
    if (themePreference === "dark" || (themePreference === "system" && prefersDark)) {
        theme = "dark";
    }

    document.documentElement.dataset.themePreference = themePreference;
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
})();
