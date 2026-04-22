(function () {
    "use strict";

    const state = {
        workers: [],
        selectedWorkerId: null,
        filter: "",
        mode: "create"
    };

    const endpoints = {
        workers: "/api/admin/workers",
        reset: "/api/admin/reset"
    };

    const elements = {
        filterInput: document.querySelector("[data-filter-input]"),
        workerList: document.querySelector("[data-worker-list]"),
        form: document.querySelector("[data-editor-form]"),
        editorTitle: document.querySelector("[data-editor-title]"),
        editorMode: document.querySelector("[data-editor-mode]"),
        toast: document.querySelector("[data-toast]"),
        sourceFixturePath: document.querySelector("[data-meta='sourceFixturePath']"),
        runtimeFixturePath: document.querySelector("[data-meta='runtimeFixturePath']"),
        workerCount: document.querySelector("[data-meta='workerCount']"),
        filteredCount: document.querySelector("[data-meta='filteredCount']")
    };

    let filterTimer = null;

    function escapeHtml(value) {
        return String(value || "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function todayValue() {
        return new Date().toISOString().slice(0, 10);
    }

    function blankWorker() {
        return {
            personIdExternal: "",
            personId: "",
            perPersonUuid: "",
            userName: "",
            userId: "",
            email: "",
            emailType: "",
            firstName: "",
            lastName: "",
            preferredName: "",
            displayName: "",
            startDate: todayValue(),
            employmentStatus: "A",
            lifecycleState: "active",
            endDate: "",
            firstDateWorked: "",
            lastDateWorked: "",
            latestTerminationDate: "",
            activeEmploymentsCount: "",
            isContingentWorker: "",
            lastModifiedDateTime: "",
            company: "",
            companyId: "",
            department: "",
            departmentName: "",
            departmentId: "",
            departmentCostCenter: "",
            jobTitle: "",
            position: "",
            payGrade: "",
            managerId: "",
            businessUnit: "",
            businessUnitId: "",
            division: "",
            divisionId: "",
            costCenter: "",
            costCenterDescription: "",
            costCenterId: "",
            employeeClass: "",
            employeeType: "",
            peopleGroup: "",
            leadershipLevel: "",
            region: "",
            geozone: "",
            bargainingUnit: "",
            unionJobCode: "",
            cintasUniformCategory: "",
            cintasUniformAllotment: "",
            twoCharCountryCode: "",
            location: {
                name: "",
                address: "",
                city: "",
                zipCode: "",
                customString4: ""
            },
            businessPhoneNumber: "",
            businessPhoneAreaCode: "",
            businessPhoneCountryCode: "",
            businessPhoneExtension: "",
            cellPhoneNumber: "",
            cellPhoneAreaCode: "",
            cellPhoneCountryCode: "",
            scenarioTags: [],
            response: {
                forceUnauthorized: false,
                forceNotFound: false,
                forceMalformedPayload: false,
                forceEmptyResults: false
            }
        };
    }

    async function requestJson(url, options) {
        const response = await fetch(url, {
            headers: {
                "Accept": "application/json",
                "Content-Type": "application/json"
            },
            ...options
        });

        const hasJson = response.headers.get("content-type")?.includes("application/json");
        const payload = hasJson ? await response.json() : null;
        if (!response.ok) {
            throw new Error(payload && payload.error ? payload.error : ("Request failed with HTTP " + response.status + "."));
        }

        return payload;
    }

    function showToast(message, tone) {
        if (!elements.toast) {
            return;
        }

        elements.toast.hidden = false;
        elements.toast.className = "toast toast-" + (tone || "info");
        elements.toast.textContent = message;
        window.clearTimeout(elements.toast._timer);
        elements.toast._timer = window.setTimeout(function () {
            elements.toast.hidden = true;
        }, 3200);
    }

    function renderState(payload) {
        state.workers = Array.isArray(payload.workers) ? payload.workers : [];
        elements.sourceFixturePath.textContent = payload.sourceFixturePath || "n/a";
        elements.runtimeFixturePath.textContent = payload.runtimeFixturePath || "n/a";
        elements.workerCount.textContent = String(payload.totalWorkers || 0);
        elements.filteredCount.textContent = String(payload.filteredWorkers || 0) + " shown";

        if (state.selectedWorkerId && !state.workers.some(function (worker) { return worker.personIdExternal === state.selectedWorkerId; })) {
            state.selectedWorkerId = null;
        }

        if (!state.selectedWorkerId && state.workers.length > 0 && state.mode !== "create") {
            state.selectedWorkerId = state.workers[0].personIdExternal;
        }

        renderWorkerList();
    }

    function renderWorkerList() {
        const html = state.workers.map(function (worker) {
            const selected = worker.personIdExternal === state.selectedWorkerId ? " is-selected" : "";
            const tags = Array.isArray(worker.scenarioTags) ? worker.scenarioTags.join(", ") : "";
            return [
                '<button type="button" class="worker-list-item' + selected + '" data-worker-id="' + escapeHtml(worker.personIdExternal) + '">',
                '<span class="worker-name">' + escapeHtml(worker.displayName || worker.userId || worker.personIdExternal) + "</span>",
                '<span class="worker-meta">' + escapeHtml(worker.personIdExternal + " • " + (worker.userId || "")) + "</span>",
                '<span class="worker-meta">' + escapeHtml((worker.employmentStatus || "") + " • " + (worker.lifecycleState || "")) + "</span>",
                '<span class="worker-meta">' + escapeHtml([worker.company, worker.department, tags].filter(Boolean).join(" • ")) + "</span>",
                "</button>"
            ].join("");
        }).join("");

        elements.workerList.innerHTML = html || '<p class="empty-state">No workers matched the current filter.</p>';
    }

    function setEditor(worker, mode) {
        const resolvedWorker = worker || blankWorker();
        state.mode = mode;
        state.selectedWorkerId = mode === "edit" ? (resolvedWorker.personIdExternal || null) : null;
        elements.editorTitle.textContent = mode === "edit"
            ? ((resolvedWorker.displayName || (resolvedWorker.firstName || "") + " " + (resolvedWorker.lastName || "")).trim() || resolvedWorker.personIdExternal || "Worker")
            : "New worker";
        elements.editorMode.textContent = mode === "edit" ? "Edit" : "Create";

        Array.from(elements.form.elements).forEach(function (field) {
            if (!field.name) {
                return;
            }

            const value = readWorkerValue(resolvedWorker, field.name);
            if (field.type === "checkbox") {
                field.checked = Boolean(value);
                return;
            }

            if (field.tagName === "TEXTAREA" && field.name === "scenarioTags") {
                field.value = Array.isArray(resolvedWorker.scenarioTags) ? resolvedWorker.scenarioTags.join(", ") : "";
                return;
            }

            field.value = value == null ? "" : String(value);
        });

        renderWorkerList();
    }

    function readWorkerValue(worker, path) {
        if (path === "scenarioTags") {
            return worker.scenarioTags || [];
        }

        return path.split(".").reduce(function (current, segment) {
            return current && Object.prototype.hasOwnProperty.call(current, segment) ? current[segment] : null;
        }, worker);
    }

    function buildWorkerPayload() {
        const payload = blankWorker();

        Array.from(elements.form.elements).forEach(function (field) {
            if (!field.name) {
                return;
            }

            if (field.name === "scenarioTags") {
                payload.scenarioTags = String(field.value || "")
                    .split(/[\n,]/)
                    .map(function (value) { return value.trim(); })
                    .filter(Boolean);
                return;
            }

            assignPayloadValue(payload, field.name, field.type === "checkbox" ? Boolean(field.checked) : normalizeValue(field.value));
        });

        return payload;
    }

    function assignPayloadValue(target, path, value) {
        const segments = path.split(".");
        let current = target;
        for (let index = 0; index < segments.length - 1; index += 1) {
            const segment = segments[index];
            current[segment] = current[segment] || {};
            current = current[segment];
        }

        current[segments[segments.length - 1]] = value;
    }

    function normalizeValue(value) {
        const trimmed = String(value || "").trim();
        return trimmed === "" ? null : trimmed;
    }

    async function loadWorkers(keepSelection) {
        const search = state.filter ? ("?filter=" + encodeURIComponent(state.filter)) : "";
        const payload = await requestJson(endpoints.workers + search, { method: "GET" });
        renderState(payload);

        if (!keepSelection && state.workers.length > 0 && state.mode !== "create") {
            await loadWorker(state.workers[0].personIdExternal);
        }
    }

    async function loadWorker(workerId) {
        const payload = await requestJson(endpoints.workers + "/" + encodeURIComponent(workerId), { method: "GET" });
        setEditor(payload.worker, payload.mode || "edit");
    }

    async function saveWorker(event) {
        event.preventDefault();

        const payload = buildWorkerPayload();
        const isCreate = state.mode === "create" || !state.selectedWorkerId;
        const url = isCreate
            ? endpoints.workers
            : endpoints.workers + "/" + encodeURIComponent(state.selectedWorkerId);
        const method = isCreate ? "POST" : "PUT";
        const result = await requestJson(url, { method: method, body: JSON.stringify(payload) });
        setEditor(result.worker, "edit");
        await loadWorkers(true);
        showToast(result.message, "good");
    }

    async function runAction(action) {
        if (action === "new") {
            setEditor(blankWorker(), "create");
            return;
        }

        if (action === "reset") {
            if (!window.confirm("Reset the runtime worker state back to the seeded population?")) {
                return;
            }

            const result = await requestJson(endpoints.reset, { method: "POST", body: "{}" });
            setEditor(blankWorker(), "create");
            await loadWorkers(false);
            showToast(result.message, "good");
            return;
        }

        if (!state.selectedWorkerId) {
            showToast("Select a worker first.", "warn");
            return;
        }

        if (action === "delete" && !window.confirm("Delete the selected worker from the runtime population?")) {
            return;
        }

        const url = endpoints.workers + "/" + encodeURIComponent(state.selectedWorkerId) +
            (action === "delete" ? "" : "/" + action);
        const method = action === "delete" ? "DELETE" : "POST";
        const body = action === "clone" ? JSON.stringify({ sourceWorkerId: state.selectedWorkerId }) : "{}";
        const result = await requestJson(url, { method: method, body: body });

        if (action === "delete") {
            state.selectedWorkerId = null;
            setEditor(blankWorker(), "create");
            await loadWorkers(false);
            showToast(result.message, "good");
            return;
        }

        setEditor(result.worker, "edit");
        await loadWorkers(true);
        showToast(result.message, "good");
    }

    function bindEvents() {
        elements.filterInput.addEventListener("input", function () {
            window.clearTimeout(filterTimer);
            filterTimer = window.setTimeout(async function () {
                state.filter = elements.filterInput.value.trim();
                await loadWorkers(true);
            }, 180);
        });

        elements.workerList.addEventListener("click", async function (event) {
            const button = event.target.closest("[data-worker-id]");
            if (!button) {
                return;
            }

            const workerId = button.getAttribute("data-worker-id");
            if (!workerId) {
                return;
            }

            await loadWorker(workerId);
        });

        elements.form.addEventListener("submit", function (event) {
            saveWorker(event).catch(function (error) {
                showToast(error.message, "warn");
            });
        });

        document.querySelectorAll("[data-action]").forEach(function (button) {
            button.addEventListener("click", function () {
                const action = button.getAttribute("data-action");
                runAction(action).catch(function (error) {
                    showToast(error.message, "warn");
                });
            });
        });
    }

    async function init() {
        bindEvents();
        setEditor(blankWorker(), "create");
        await loadWorkers(false);
    }

    init().catch(function (error) {
        showToast(error.message, "warn");
    });
})();
