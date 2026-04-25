(function () {
    "use strict";

    const themeStorageKey = "syncfactors-next-theme";
    const root = document.documentElement;
    const colorSchemeQuery = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;

    const state = {
        workers: [],
        provisioningBuckets: [],
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
        filteredCount: document.querySelector("[data-meta='filteredCount']"),
        bucketKinds: document.querySelector("[data-meta='bucketKinds']"),
        bucketSummary: document.querySelector("[data-bucket-summary]"),
        bucketCompare: document.querySelector("[data-bucket-compare]"),
        compareMockLabel: document.querySelector("[data-compare-mock-label]"),
        compareMockKey: document.querySelector("[data-compare-mock-key]"),
        comparePlannerLabel: document.querySelector("[data-compare-planner-label]"),
        comparePlannerKey: document.querySelector("[data-compare-planner-key]"),
        comparePlannerStatus: document.querySelector("[data-compare-planner-status]"),
        comparePlannerReason: document.querySelector("[data-compare-planner-reason]"),
        comparePlannerError: document.querySelector("[data-compare-planner-error]"),
        lifecycleStateSelect: document.querySelector("[data-lifecycle-state-select]"),
        themeToggle: document.getElementById("theme-toggle"),
        themeOptions: Array.prototype.slice.call(document.querySelectorAll("[data-theme-option]"))
    };

    let filterTimer = null;

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

    function updateThemeToggle(preference) {
        elements.themeOptions.forEach(function (option) {
            option.setAttribute("aria-pressed", String(option.dataset.themeOption === preference));
        });
    }

    function applyTheme(themePreference) {
        const preference = isThemePreference(themePreference) ? themePreference : "system";
        const resolvedTheme = resolveTheme(preference);
        root.dataset.themePreference = preference;
        root.dataset.theme = resolvedTheme;
        root.style.colorScheme = resolvedTheme;

        if (!elements.themeToggle) {
            return;
        }

        elements.themeToggle.setAttribute("data-theme-selection", preference);
        updateThemeToggle(preference);
    }

    function persistTheme(themePreference) {
        try {
            window.localStorage.setItem(themeStorageKey, themePreference);
        } catch (error) {
            return;
        }
    }

    function handleSystemThemeChange() {
        if ((root.dataset.themePreference || "system") !== "system") {
            return;
        }

        applyTheme("system");
    }

    function bindThemeToggle() {
        applyTheme(root.dataset.themePreference);

        if (!elements.themeOptions.length) {
            return;
        }

        elements.themeOptions.forEach(function (option) {
            option.addEventListener("click", function () {
                const nextTheme = option.dataset.themeOption;
                applyTheme(nextTheme);
                persistTheme(nextTheme);
            });
        });

        if (!colorSchemeQuery) {
            return;
        }

        if (typeof colorSchemeQuery.addEventListener === "function") {
            colorSchemeQuery.addEventListener("change", handleSystemThemeChange);
        } else if (typeof colorSchemeQuery.addListener === "function") {
            colorSchemeQuery.addListener(handleSystemThemeChange);
        }
    }

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
            employmentStatus: "64300",
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
        state.provisioningBuckets = Array.isArray(payload.provisioningBuckets) ? payload.provisioningBuckets : [];
        elements.sourceFixturePath.textContent = payload.sourceFixturePath || "n/a";
        elements.runtimeFixturePath.textContent = payload.runtimeFixturePath || "n/a";
        elements.workerCount.textContent = String(payload.totalWorkers || 0);
        elements.filteredCount.textContent = String(payload.filteredWorkers || 0) + " shown";
        if (elements.bucketKinds) {
            elements.bucketKinds.textContent = String(state.provisioningBuckets.length) + " buckets";
        }

        if (state.selectedWorkerId && !state.workers.some(function (worker) { return worker.personIdExternal === state.selectedWorkerId; })) {
            state.selectedWorkerId = null;
        }

        if (!state.selectedWorkerId && state.workers.length > 0 && state.mode !== "create") {
            state.selectedWorkerId = state.workers[0].personIdExternal;
        }

        renderBucketSummary();
        renderWorkerList();
    }

    function bucketClassName(bucket) {
        return String(bucket || "unknown")
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/^-+|-+$/g, "") || "unknown";
    }

    function renderBucketSummary() {
        if (!elements.bucketSummary) {
            return;
        }

        if (!state.provisioningBuckets.length) {
            elements.bucketSummary.innerHTML = '<p class="empty-state">No bucket data available.</p>';
            return;
        }

        elements.bucketSummary.innerHTML = state.provisioningBuckets.map(function (bucket) {
            const bucketLabel = bucket.label || bucket.bucket || "Unknown";
            const bucketValue = bucket.bucket || "unknown";
            return [
                '<article class="bucket-card">',
                '<span class="bucket-chip bucket-chip-' + escapeHtml(bucketClassName(bucketValue)) + '">' + escapeHtml(bucketLabel) + "</span>",
                '<strong>' + escapeHtml(String(bucket.count || 0)) + "</strong>",
                '<span class="worker-meta">' + escapeHtml(bucketValue) + "</span>",
                "</article>"
            ].join("");
        }).join("");
    }

    function renderWorkerList() {
        const html = state.workers.map(function (worker) {
            const selected = worker.personIdExternal === state.selectedWorkerId ? " is-selected" : "";
            const tags = Array.isArray(worker.scenarioTags) ? worker.scenarioTags.join(", ") : "";
            const provisioningBucket = worker.provisioningBucket || "unknown";
            const provisioningBucketLabel = worker.provisioningBucketLabel || provisioningBucket;
            return [
                '<button type="button" class="worker-list-item' + selected + '" data-worker-id="' + escapeHtml(worker.personIdExternal) + '">',
                '<span class="worker-name">' + escapeHtml(worker.displayName || worker.userId || worker.personIdExternal) + "</span>",
                '<span class="worker-meta">' + escapeHtml(worker.personIdExternal + " • " + (worker.userId || "")) + "</span>",
                '<span class="worker-meta">' + escapeHtml((worker.employmentStatus || "") + " • " + (worker.lifecycleState || "")) + "</span>",
                '<div class="worker-chip-row"><span class="bucket-chip bucket-chip-' + escapeHtml(bucketClassName(provisioningBucket)) + '">' + escapeHtml(provisioningBucketLabel) + '</span><span class="worker-meta">' + escapeHtml(provisioningBucket) + "</span></div>",
                '<span class="worker-meta">' + escapeHtml([worker.company, worker.department, tags].filter(Boolean).join(" • ")) + "</span>",
                "</button>"
            ].join("");
        }).join("");

        elements.workerList.innerHTML = html || '<p class="empty-state">No workers matched the current filter.</p>';
    }

    function setBucketChip(element, bucket, label) {
        if (!element) {
            return;
        }

        element.className = "bucket-chip bucket-chip-" + bucketClassName(bucket);
        element.textContent = label || bucket || "Unknown";
    }

    function setComparisonState(comparison) {
        if (!elements.bucketCompare) {
            return;
        }

        const mockBucket = comparison && comparison.mockBucket ? comparison.mockBucket : null;
        const plannerBucket = comparison && comparison.plannerBucket ? comparison.plannerBucket : null;
        const plannerStatus = plannerBucket && plannerBucket.status ? plannerBucket.status : "idle";
        const plannerBucketKey = plannerBucket && plannerBucket.bucket ? plannerBucket.bucket : "unknown";
        const plannerBucketLabel = plannerBucket && plannerBucket.label ? plannerBucket.label : (
            plannerStatus === "available" ? plannerBucketKey : "Unavailable");

        setBucketChip(
            elements.compareMockLabel,
            mockBucket && mockBucket.bucket ? mockBucket.bucket : "unknown",
            mockBucket && mockBucket.label ? mockBucket.label : "Not loaded");
        if (elements.compareMockKey) {
            elements.compareMockKey.textContent = mockBucket && mockBucket.bucket ? mockBucket.bucket : "n/a";
        }

        setBucketChip(elements.comparePlannerLabel, plannerBucketKey, plannerBucketLabel);
        if (elements.comparePlannerKey) {
            elements.comparePlannerKey.textContent = plannerBucket && plannerBucket.bucket ? plannerBucket.bucket : "n/a";
        }

        if (elements.comparePlannerStatus) {
            elements.comparePlannerStatus.textContent = plannerStatus === "available"
                ? "Loaded from current sync config and AD planner."
                : plannerStatus === "error"
                    ? "Planner compare failed."
                    : "Planner compare unavailable.";
        }

        if (elements.comparePlannerReason) {
            const reason = plannerBucket && plannerBucket.reason
                ? plannerBucket.reason + (plannerBucket.reviewCaseType ? (" Review case: " + plannerBucket.reviewCaseType + ".") : "")
                : "";
            elements.comparePlannerReason.hidden = !reason;
            elements.comparePlannerReason.textContent = reason;
        }

        if (elements.comparePlannerError) {
            const error = plannerBucket && plannerBucket.error ? plannerBucket.error : "";
            elements.comparePlannerError.hidden = !error;
            elements.comparePlannerError.textContent = error;
        }
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

        if (mode !== "edit") {
            setComparisonState(null);
        }

        if (elements.lifecycleStateSelect) {
            elements.lifecycleStateSelect.value = resolveSimulatedLifecycleOption(resolvedWorker);
        }

        renderWorkerList();
    }

    function resolveSimulatedLifecycleOption(worker) {
        const lifecycleState = String(worker.lifecycleState || "").toLowerCase();
        const employmentStatus = String(worker.employmentStatus || "").toUpperCase();
        if (lifecycleState === "preboarding" || lifecycleState === "prehire") {
            return "prehire";
        }

        if (lifecycleState === "paid-leave" || employmentStatus === "U" || employmentStatus === "64304") {
            return "paid-leave";
        }

        if (lifecycleState === "unpaid-leave" || employmentStatus === "64303") {
            return "unpaid-leave";
        }

        if (lifecycleState === "terminated" || employmentStatus === "T" || employmentStatus === "I" || employmentStatus === "64308") {
            return "terminated";
        }

        return "active-started";
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
        switch (path) {
            case "location.name":
                ensureLocation(target).name = value;
                return;
            case "location.address":
                ensureLocation(target).address = value;
                return;
            case "location.city":
                ensureLocation(target).city = value;
                return;
            case "location.zipCode":
                ensureLocation(target).zipCode = value;
                return;
            case "location.customString4":
                ensureLocation(target).customString4 = value;
                return;
            case "response.forceUnauthorized":
                ensureResponse(target).forceUnauthorized = value;
                return;
            case "response.forceNotFound":
                ensureResponse(target).forceNotFound = value;
                return;
            case "response.forceMalformedPayload":
                ensureResponse(target).forceMalformedPayload = value;
                return;
            case "response.forceEmptyResults":
                ensureResponse(target).forceEmptyResults = value;
                return;
            default:
                if (!isSafeTopLevelField(path)) {
                    throw new Error("Unsupported payload path.");
                }

                target[path] = value;
        }
    }

    function ensureLocation(target) {
        if (!target.location || typeof target.location !== "object") {
            target.location = Object.create(null);
        }

        return target.location;
    }

    function ensureResponse(target) {
        if (!target.response || typeof target.response !== "object") {
            target.response = Object.create(null);
        }

        return target.response;
    }

    function isSafeTopLevelField(path) {
        return path.indexOf(".") === -1 &&
            path !== "__proto__" &&
            path !== "prototype" &&
            path !== "constructor";
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
        setComparisonState(payload.bucketComparison || null);
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

    async function applyLifecycleState() {
        if (!state.selectedWorkerId) {
            showToast("Select a worker first.", "warn");
            return;
        }

        const lifecycleState = elements.lifecycleStateSelect ? elements.lifecycleStateSelect.value : "";
        const result = await requestJson(
            endpoints.workers + "/" + encodeURIComponent(state.selectedWorkerId) + "/lifecycle-state",
            { method: "POST", body: JSON.stringify({ lifecycleState: lifecycleState }) });
        setEditor(result.worker, "edit");
        await loadWorkers(true);
        await loadWorker(result.worker.personIdExternal || state.selectedWorkerId);
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

        if (action === "apply-lifecycle-state") {
            await applyLifecycleState();
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
        bindThemeToggle();
        bindEvents();
        setEditor(blankWorker(), "create");
        await loadWorkers(false);
    }

    init().catch(function (error) {
        showToast(error.message, "warn");
    });
})();
