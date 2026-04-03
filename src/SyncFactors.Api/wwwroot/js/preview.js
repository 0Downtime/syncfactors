(function () {
    var states = [
        {
            form: document.querySelector("[data-worker-lookup-form]"),
            button: document.querySelector("[data-worker-lookup-button]"),
            label: "Looking up..."
        },
        {
            form: document.querySelector("[data-worker-apply-form]"),
            button: document.querySelector("[data-worker-apply-button]"),
            label: "Applying..."
        }
    ];

    states.forEach(function (state) {
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
        });
    });
})();
