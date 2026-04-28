(function () {
    const storageKey = "syncfactors-next-theme";
    const root = document.documentElement;
    const toggle = document.getElementById("theme-toggle");
    const themeOptions = Array.prototype.slice.call(document.querySelectorAll("[data-theme-option]"));
    const topbar = document.querySelector("[data-topbar]");
    const toastRegion = document.querySelector("[data-toast-region]");
    const reduceMotionQuery = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
    const colorSchemeQuery = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

    function motionAllowed() {
        return !reduceMotionQuery || !reduceMotionQuery.matches;
    }

    function isThemePreference(value) {
        return value === "system" || value === "light" || value === "dark";
    }

    function resolveTheme(preference) {
        if (preference === "dark") {
            return "dark";
        }

        if (preference === "light") {
            return "light";
        }

        return colorSchemeQuery && colorSchemeQuery.matches ? "dark" : "light";
    }

    function announceTheme(theme, preference) {
        window.dispatchEvent(new CustomEvent("syncfactors:themechange", {
            detail: { theme, preference }
        }));
    }

    function updateThemeToggle(preference) {
        themeOptions.forEach(function (option) {
            option.setAttribute("aria-pressed", String(option.dataset.themeOption === preference));
        });
    }

    function applyTheme(themePreference, options = {}) {
        const preference = isThemePreference(themePreference) ? themePreference : "system";
        const resolvedTheme = resolveTheme(preference);
        const shouldPersist = options.persist === true;
        const shouldAnnounce = options.announce !== false;

        root.dataset.themePreference = preference;
        root.dataset.theme = resolvedTheme;
        root.style.colorScheme = resolvedTheme;

        if (toggle) {
            toggle.setAttribute("data-theme-selection", preference);
        }

        updateThemeToggle(preference);

        if (shouldPersist) {
            try {
                window.localStorage.setItem(storageKey, preference);
            } catch (error) {
                // Ignore storage failures in restricted contexts.
            }
        }

        if (shouldAnnounce) {
            announceTheme(resolvedTheme, preference);
        }
    }

    function handleSystemThemeChange() {
        if ((root.dataset.themePreference || "system") !== "system") {
            return;
        }

        applyTheme("system");
    }

    function syncTopbar() {
        if (!topbar) {
            return;
        }

        topbar.classList.toggle("is-condensed", window.scrollY > 18);
    }

    function initializeNavMenus() {
        const navMenus = Array.prototype.slice.call(document.querySelectorAll("[data-nav-menu]"));

        if (!navMenus.length) {
            return;
        }

        document.addEventListener("click", function (event) {
            navMenus.forEach(function (menu) {
                if (!menu.open || menu.contains(event.target)) {
                    return;
                }

                menu.open = false;
            });
        });

        document.addEventListener("keydown", function (event) {
            if (event.key !== "Escape") {
                return;
            }

            navMenus.forEach(function (menu) {
                menu.open = false;
            });
        });

        navMenus.forEach(function (menu) {
            const links = Array.prototype.slice.call(menu.querySelectorAll("a"));
            links.forEach(function (link) {
                link.addEventListener("click", function () {
                    menu.open = false;
                });
            });
        });
    }

    function decorateSurface(surface) {
        surface.classList.add("reveal-on-scroll");
    }

    function initializeSurfaceReveals() {
        const surfaces = Array.prototype.slice.call(document.querySelectorAll(
            ".hero, .analytics-card, .connection-card, .run-health-card, .sync-status-card, .preview-inspector-card"));

        if (!surfaces.length) {
            return;
        }

        surfaces.forEach(decorateSurface);

        if (!motionAllowed() || !("IntersectionObserver" in window)) {
            surfaces.forEach(function (surface) {
                surface.classList.add("is-visible");
            });
            return;
        }

        const observer = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) {
                    entry.target.classList.add("is-visible");
                    observer.unobserve(entry.target);
                }
            });
        }, {
            rootMargin: "0px 0px -8% 0px",
            threshold: 0.16
        });

        surfaces.forEach(function (surface) {
            observer.observe(surface);
        });
    }

    function removeTransientNode(node) {
        if (!node || !node.parentNode) {
            return;
        }

        if (!motionAllowed() || !node.animate) {
            node.remove();
            return;
        }

        const animation = node.animate(
            [
                { opacity: 1, transform: "translateY(0) scale(1)" },
                { opacity: 0, transform: "translateY(12px) scale(0.98)" }
            ],
            {
                duration: 220,
                easing: "cubic-bezier(0.2, 0.8, 0.2, 1)",
                fill: "forwards"
            });

        animation.addEventListener("finish", function () {
            node.remove();
        });
    }

    function showToast(detail) {
        if (!toastRegion || !detail || !detail.message) {
            return;
        }

        const tone = detail.tone === "bad" || detail.tone === "warn" || detail.tone === "good"
            ? detail.tone
            : "good";

        const toast = document.createElement("section");
        toast.className = "toast " + tone;
        toast.setAttribute("role", "status");

        if (detail.title) {
            const title = document.createElement("strong");
            title.textContent = detail.title;
            toast.appendChild(title);
        }

        const message = document.createElement("p");
        message.textContent = detail.message;
        toast.appendChild(message);
        toastRegion.appendChild(toast);

        if (motionAllowed() && toast.animate) {
            toast.animate(
                [
                    { opacity: 0, transform: "translateY(18px) scale(0.98)" },
                    { opacity: 1, transform: "translateY(0) scale(1)" }
                ],
                {
                    duration: 260,
                    easing: "cubic-bezier(0.22, 1, 0.36, 1)",
                    fill: "both"
                });
        }

        window.setTimeout(function () {
            removeTransientNode(toast);
        }, typeof detail.duration === "number" ? detail.duration : 3800);
    }

    function initializeTransientCallouts() {
        const transientCallouts = Array.prototype.slice.call(document.querySelectorAll("[data-auto-dismiss-ms]"));

        transientCallouts.forEach(function (callout) {
            const dismissAfterMs = Number(callout.getAttribute("data-auto-dismiss-ms"));
            if (!Number.isFinite(dismissAfterMs) || dismissAfterMs < 0) {
                return;
            }

            window.setTimeout(function () {
                removeTransientNode(callout);
            }, dismissAfterMs);
        });
    }

    function initializeVersionCopy() {
        const versionButtons = Array.prototype.slice.call(document.querySelectorAll("[data-copy-version]"));
        if (!versionButtons.length) {
            return;
        }

        versionButtons.forEach(function (button) {
            const version = button.getAttribute("data-copy-version") || "";
            if (!version) {
                return;
            }

            button.addEventListener("click", async function () {
                try {
                    await navigator.clipboard.writeText(version);
                    showToast({
                        title: "Version Copied",
                        tone: "good",
                        message: version,
                        duration: 2400
                    });
                } catch (error) {
                    showToast({
                        title: "Copy Failed",
                        tone: "warn",
                        message: version,
                        duration: 3200
                    });
                }
            });
        });
    }

    function bootstrapCalloutToasts() {
        const seenMessages = new Set();
        const callouts = Array.prototype.slice.call(document.querySelectorAll(".callout.good, .callout.warn, .callout.danger"));

        callouts.slice(0, 2).forEach(function (callout) {
            const message = (callout.textContent || "").trim();
            if (!message || seenMessages.has(message)) {
                return;
            }

            seenMessages.add(message);
            showToast({
                title: callout.classList.contains("danger") ? "Attention Required" : callout.classList.contains("warn") ? "Heads Up" : "Updated",
                tone: callout.classList.contains("danger") ? "bad" : callout.classList.contains("warn") ? "warn" : "good",
                message
            });
        });
    }

    applyTheme(root.dataset.themePreference, { announce: false });

    if (themeOptions.length) {
        themeOptions.forEach(function (option) {
            option.addEventListener("click", function () {
                applyTheme(option.dataset.themeOption, { persist: true });
            });
        });
    }

    if (colorSchemeQuery) {
        if (typeof colorSchemeQuery.addEventListener === "function") {
            colorSchemeQuery.addEventListener("change", handleSystemThemeChange);
        } else if (typeof colorSchemeQuery.addListener === "function") {
            colorSchemeQuery.addListener(handleSystemThemeChange);
        }
    }

    syncTopbar();
    window.addEventListener("scroll", syncTopbar, { passive: true });
    initializeNavMenus();
    initializeSurfaceReveals();
    bootstrapCalloutToasts();
    initializeTransientCallouts();
    initializeVersionCopy();

    window.addEventListener("syncfactors:toast", function (event) {
        showToast(event.detail || {});
    });
})();
