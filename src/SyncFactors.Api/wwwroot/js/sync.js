(function () {
    var dialog = document.querySelector("[data-delete-dialog]");
    var openButton = document.querySelector("[data-open-delete-dialog]");
    var closeButtons = Array.prototype.slice.call(document.querySelectorAll("[data-close-delete-dialog]"));
    var input = document.querySelector("[data-delete-confirmation-input]");
    var hidden = document.querySelector("[data-delete-confirmation-hidden]");
    var submit = document.querySelector("[data-submit-delete-dialog]");
    var countdownValue = document.querySelector("[data-next-run-countdown-value]");
    var countdownRing = document.querySelector("[data-next-run-countdown]");
    var countdownSource = document.querySelector("[data-next-run-at]");
    var forms = Array.prototype.slice.call(document.querySelectorAll("[data-sync-form]"));

    function emitToast(detail) {
        window.dispatchEvent(new CustomEvent("syncfactors:toast", { detail: detail }));
    }

    forms.forEach(function (form) {
        var button = form.querySelector("[data-sync-submit]");
        if (!button) {
            return;
        }

        form.addEventListener("submit", function () {
            var label = button.querySelector(".button-label");
            button.disabled = true;
            button.classList.add("is-loading");
            button.setAttribute("aria-busy", "true");
            form.classList.add("sync-form-busy");

            if (label) {
                label.textContent = form.getAttribute("data-sync-action") === "schedule" ? "Saving..." : "Submitting...";
            }

            emitToast({
                title: "Sync Control",
                tone: "good",
                message: "Submitting the requested control-plane action.",
                duration: 2600
            });
        });
    });

    function updateCountdown() {
        if (!countdownValue || !countdownRing || !countdownSource) {
            return;
        }

        var iso = countdownSource.getAttribute("data-next-run-at");
        if (!iso) {
            countdownValue.textContent = "Idle";
            countdownRing.style.setProperty("--ring-progress", "0turn");
            return;
        }

        var nextRunAt = new Date(iso);
        if (Number.isNaN(nextRunAt.getTime())) {
            countdownValue.textContent = "Unknown";
            countdownRing.style.setProperty("--ring-progress", "0turn");
            return;
        }

        var remainingMs = nextRunAt.getTime() - Date.now();
        if (remainingMs <= 0) {
            countdownValue.textContent = "Due";
            countdownRing.style.setProperty("--ring-progress", "1turn");
            return;
        }

        var totalMinutes = Math.max(1, Math.round(remainingMs / 60000));
        var hours = Math.floor(totalMinutes / 60);
        var minutes = totalMinutes % 60;
        var display = hours > 0 ? hours + "h " + minutes + "m" : minutes + "m";
        var progress = Math.max(0.08, Math.min(0.98, remainingMs / (24 * 60 * 60 * 1000)));

        countdownValue.textContent = display;
        countdownRing.style.setProperty("--ring-progress", progress.toFixed(3) + "turn");
    }

    if (countdownValue) {
        updateCountdown();
        window.setInterval(updateCountdown, 30000);
    }

    if (!dialog || !openButton || !input || !hidden || !submit) {
        return;
    }

    var requiredText = dialog.getAttribute("data-delete-required-text") || "";

    function syncConfirmationState() {
        var value = input.value || "";
        hidden.value = value;
        submit.disabled = value.trim() !== requiredText;
    }

    function shakeDialog() {
        dialog.classList.remove("is-shaking");
        void dialog.offsetWidth;
        dialog.classList.add("is-shaking");
        window.setTimeout(function () {
            dialog.classList.remove("is-shaking");
        }, 450);
    }

    openButton.addEventListener("click", function () {
        input.value = "";
        syncConfirmationState();
        dialog.showModal();
        input.focus();
    });

    closeButtons.forEach(function (button) {
        button.addEventListener("click", function () {
            dialog.close();
        });
    });

    input.addEventListener("input", syncConfirmationState);

    submit.addEventListener("click", function (event) {
        if (submit.disabled) {
            event.preventDefault();
            shakeDialog();
            emitToast({
                title: "Delete Guardrail",
                tone: "warn",
                message: "The confirmation phrase must match before the destructive action can be queued.",
                duration: 3200
            });
        }
    });
})();
