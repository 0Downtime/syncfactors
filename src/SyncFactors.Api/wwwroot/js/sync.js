(function () {
    var dialog = document.querySelector("[data-delete-dialog]");
    var openButton = document.querySelector("[data-open-delete-dialog]");
    var closeButtons = document.querySelectorAll("[data-close-delete-dialog]");
    var input = document.querySelector("[data-delete-confirmation-input]");
    var hidden = document.querySelector("[data-delete-confirmation-hidden]");
    var submit = document.querySelector("[data-submit-delete-dialog]");

    if (!dialog || !openButton || !input || !hidden || !submit) {
        return;
    }

    var requiredText = dialog.getAttribute("data-delete-required-text") || "";

    function syncConfirmationState() {
        var value = input.value || "";
        hidden.value = value;
        submit.disabled = value.trim() !== requiredText;
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
})();
