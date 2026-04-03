(function () {
    var key = "syncfactors-next-theme";
    var storedTheme = null;
    try {
        storedTheme = window.localStorage.getItem(key);
    } catch (error) {
        storedTheme = null;
    }

    var theme = storedTheme === "dark" || storedTheme === "light"
        ? storedTheme
        : (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");

    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
})();
