(function () {
    var forms = [
        {
            form: document.querySelector("[data-worker-lookup-form]"),
            button: document.querySelector("[data-worker-lookup-button]"),
            label: "Looking up...",
            message: "Refreshing the saved preview snapshot."
        },
        {
            form: document.querySelector("[data-worker-apply-form]"),
            button: document.querySelector("[data-worker-apply-button]"),
            label: "Applying...",
            message: "Submitting the reviewed preview to Active Directory."
        }
    ];
    var filterButtons = Array.prototype.slice.call(document.querySelectorAll("[data-preview-filter]"));
    var diffRows = Array.prototype.slice.call(document.querySelectorAll("[data-preview-diff-body] .diff-row"));

    function emitToast(detail) {
        window.dispatchEvent(new CustomEvent("syncfactors:toast", { detail: detail }));
    }

    forms.forEach(function (state) {
        if (!state.form || !state.button) {
            return;
        }

        state.form.addEventListener("submit", function () {
            state.button.disabled = true;
            state.button.classList.add("is-loading");
            state.button.setAttribute("aria-busy", "true");

            var label = state.button.querySelector(".button-label");
            if (label) {
                label.textContent = state.label;
            }

            var surface = state.form.closest(".panel");
            if (surface) {
                surface.classList.add("preview-loading");
            }

            emitToast({
                title: "Preview Pipeline",
                tone: "good",
                message: state.message,
                duration: 2600
            });
        });
    });

    if (!filterButtons.length || !diffRows.length) {
        return;
    }

    function applyFilter(mode) {
        filterButtons.forEach(function (button) {
            button.setAttribute("aria-pressed", String(button.getAttribute("data-preview-filter") === mode));
        });

        diffRows.forEach(function (row) {
            var state = row.getAttribute("data-diff-state");
            var isRisk = row.getAttribute("data-diff-risk") === "true";
            var matches = mode === "all" ||
                (mode === "changed" && state === "changed") ||
                (mode === "unchanged" && state === "unchanged") ||
                (mode === "risk" && isRisk);

            row.classList.toggle("is-filtered", !matches);
            row.classList.toggle("is-emphasis", mode === "risk" && isRisk);
        });
    }

    filterButtons.forEach(function (button) {
        button.addEventListener("click", function () {
            applyFilter(button.getAttribute("data-preview-filter") || "all");
        });
    });
})();
