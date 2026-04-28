import * as signalR from "@microsoft/signalr";
import * as echarts from "echarts/core";
import { BarChart, PieChart } from "echarts/charts";
import { GraphicComponent, GridComponent, LegendComponent, TooltipComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import { animate, stagger } from "motion";

echarts.use([BarChart, PieChart, GraphicComponent, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer]);

(function () {
    const dashboardPollIntervalMs = 15000;
    const progressAnimationDurationMs = 700;
    const runsPageSize = 25;
    const reduceMotionQuery = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
    const supportsViewTransitions = typeof document.startViewTransition === "function";
    const usDateTimeFormatter = new Intl.DateTimeFormat("en-US", {
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "numeric",
        minute: "2-digit",
        hour12: true
    });
    const usChartDateTimeFormatter = new Intl.DateTimeFormat("en-US", {
        month: "2-digit",
        day: "2-digit",
        hour: "numeric",
        minute: "2-digit",
        hour12: true
    });
    const countFormatter = new Intl.NumberFormat("en-US");
    const percentFormatter = new Intl.NumberFormat("en-US", {
        maximumFractionDigits: 1
    });
    const bucketDefinitions = [
        { key: "creates", label: "Creates", tone: "good", runMix: true },
        { key: "updates", label: "Updates", tone: "accent", runMix: true },
        { key: "enables", label: "Enables", tone: "info", runMix: true },
        { key: "disables", label: "Disables", tone: "warn", runMix: true },
        { key: "graveyardMoves", label: "Graveyard Moves", tone: "dim", runMix: true },
        { key: "deletions", label: "Deletions", tone: "bad", runMix: true },
        { key: "quarantined", label: "Quarantined", tone: "warn", runMix: true },
        { key: "manualReview", label: "Manual Review", tone: "warn", runMix: true },
        { key: "conflicts", label: "Conflicts", tone: "bad", runMix: true },
        { key: "guardrailFailures", label: "Guardrails", tone: "dim", runMix: true },
        { key: "unchanged", label: "Unchanged", tone: "neutral", runMix: false }
    ];
    const bucketLabelToDefinition = Object.fromEntries(bucketDefinitions.map(function (definition) {
        return [definition.label, definition];
    }));

    const elements = {
        root: document.querySelector("[data-connection-health]"),
        statusRoot: document.querySelector("[data-dashboard-status]"),
        railPanel: document.querySelector(".dashboard-rail"),
        runsPanel: document.querySelector(".recent-runs-panel"),
        runsBody: document.querySelector("[data-runs-body]"),
        runsTable: document.querySelector("[data-runs-table]"),
        runsEmpty: document.querySelector("[data-runs-empty]"),
        runsEmptyMessage: document.querySelector("[data-runs-empty] p"),
        runsPagination: document.querySelector("[data-runs-pagination]"),
        runsPaginationSummary: document.querySelector("[data-runs-pagination-summary]"),
        runsPreviousButton: document.querySelector("[data-runs-page-previous]"),
        runsNextButton: document.querySelector("[data-runs-page-next]"),
        checkedMessage: document.querySelector("[data-dashboard-checked]"),
        statusError: document.querySelector("[data-status-error]"),
        attention: document.querySelector("[data-dashboard-attention]"),
        signalRoot: document.querySelector("[data-dashboard-signal]"),
        statusLine: document.querySelector("[data-status-line]"),
        statusCaption: document.querySelector("[data-status-caption]"),
        liveBadge: document.querySelector("[data-live-status-badge]"),
        liveCaption: document.querySelector("[data-live-status-caption]"),
        progressRoot: document.querySelector(".dashboard-progress"),
        progressFill: document.querySelector("[data-progress-fill]"),
        progressCaption: document.querySelector("[data-progress-caption]"),
        progressCopy: document.querySelector("[data-progress-copy]"),
        progressCancel: document.querySelector("[data-progress-cancel]"),
        progressCancelButton: document.querySelector("[data-progress-cancel-button]"),
        progressCancelLabel: document.querySelector("[data-progress-cancel-label]"),
        scheduleTitle: document.querySelector("[data-dashboard-schedule-title]"),
        scheduleBadge: document.querySelector("[data-dashboard-schedule-badge]"),
        scheduleCountdown: document.querySelector("[data-dashboard-schedule-countdown]"),
        scheduleCountdownValue: document.querySelector("[data-dashboard-schedule-countdown-value]"),
        scheduleNextRun: document.querySelector("[data-dashboard-schedule-next-run-at]"),
        scheduleInterval: document.querySelector("[data-dashboard-schedule-interval]"),
        scheduleLastRun: document.querySelector("[data-dashboard-schedule-last-run]"),
        runsChart: document.querySelector("[data-runs-chart]"),
        runsChartEmpty: document.querySelector("[data-runs-chart-empty]"),
        bucketChart: document.querySelector("[data-buckets-chart]"),
        bucketChartEmpty: document.querySelector("[data-buckets-chart-empty]"),
        bucketChartMeta: document.querySelector("[data-buckets-chart-meta]"),
        bucketChartSummary: document.querySelector("[data-buckets-summary]"),
        filterCaption: document.querySelector("[data-runs-filter-caption]"),
        clearFilterButton: document.querySelector("[data-clear-runs-filter]"),
        timelineTitle: document.querySelector("[data-run-timeline-title]"),
        timelineSummary: document.querySelector("[data-run-timeline-summary]"),
        timelineList: document.querySelector("[data-run-timeline]"),
        timelineEmpty: document.querySelector("[data-run-timeline-empty]")
    };

    if (!elements.root || !elements.statusRoot) {
        return;
    }

    const healthMenu = elements.root.querySelector(".connection-health-menu");
    const healthList = elements.root.querySelector("[data-health-list]");
    const overallBadge = elements.root.querySelector("[data-health-overall-badge]");
    const probeOrder = ["SuccessFactors", "Active Directory", "Worker Service", "SQLite"];
    const initialHealthProbesEnabled = (elements.root.getAttribute("data-health-probes-enabled") || "true").toLowerCase() !== "false";
    const healthPollIntervalMs = Math.max(
        1000,
        parseInt(elements.root.getAttribute("data-health-probe-interval-ms") || "45000", 10) || 45000);
    const valueNodes = {
        status: elements.statusRoot.querySelector("[data-status-value]"),
        stage: elements.statusRoot.querySelector("[data-stage-value]"),
        runId: elements.statusRoot.querySelector("[data-run-id-value]"),
        worker: elements.statusRoot.querySelector("[data-worker-value]"),
        progress: elements.statusRoot.querySelector("[data-progress-value]"),
        lastAction: elements.statusRoot.querySelector("[data-last-action-value]"),
        started: elements.statusRoot.querySelector("[data-started-at-value]"),
        completed: elements.statusRoot.querySelector("[data-completed-at-value]"),
        lastUpdated: elements.statusRoot.querySelector("[data-last-updated-value]")
    };

    const defaultRunsEmptyMessage = elements.runsEmptyMessage ? elements.runsEmptyMessage.textContent : "";

    let healthTimerId = null;
    let dashboardTimerId = null;
    let reconnectTimerId = null;
    let isLoadingHealth = false;
    let isLoadingDashboard = false;
    let connection = null;
    let connectionStartPromise = null;
    let runsChartInstance = null;
    let bucketChartInstance = null;
    let latestDashboardSnapshot = null;
    let latestHealthSnapshot = null;
    let latestScheduleSnapshot = null;
    let latestProgressPercent = 0;
    let selectedRunId = null;
    let selectedBucketKey = null;
    let hoveredRunId = null;
    let hoveredBucketKey = null;
    let scheduleTimerId = null;
    let currentRunsPage = 1;

    if (elements.clearFilterButton) {
        elements.clearFilterButton.addEventListener("click", function () {
            clearRunsFilter();
        });
    }

    if (elements.runsPreviousButton) {
        elements.runsPreviousButton.addEventListener("click", function () {
            if (!latestDashboardSnapshot || currentRunsPage <= 1) {
                return;
            }

            currentRunsPage -= 1;
            renderDashboard(latestDashboardSnapshot);
        });
    }

    if (elements.runsNextButton) {
        elements.runsNextButton.addEventListener("click", function () {
            if (!latestDashboardSnapshot) {
                return;
            }

            const runs = getFilteredRuns(Array.isArray(latestDashboardSnapshot.runs) ? latestDashboardSnapshot.runs : []);
            const pageCount = getRunsPageCount(runs.length);
            if (currentRunsPage >= pageCount) {
                return;
            }

            currentRunsPage += 1;
            renderDashboard(latestDashboardSnapshot);
        });
    }

    window.addEventListener("syncfactors:themechange", function () {
        refreshCharts();
    });

    document.addEventListener("click", function (event) {
        if (!healthMenu || !healthMenu.open || healthMenu.contains(event.target)) {
            return;
        }

        healthMenu.open = false;
    });

    window.addEventListener("resize", function () {
        if (runsChartInstance) {
            runsChartInstance.resize();
        }

        if (bucketChartInstance) {
            bucketChartInstance.resize();
        }
    });

    function motionAllowed() {
        return !reduceMotionQuery || !reduceMotionQuery.matches;
    }

    function runMajorTransition(updateDom) {
        if (!supportsViewTransitions || !motionAllowed()) {
            updateDom();
            return;
        }

        document.startViewTransition(function () {
            updateDom();
        });
    }

    function statusClass(status) {
        switch ((status || "").toLowerCase()) {
            case "healthy":
                return "good";
            case "degraded":
                return "warn";
            case "unhealthy":
                return "bad";
            case "info":
                return "info";
            case "dim":
                return "dim";
            default:
                return "neutral";
        }
    }

    function runStatusClass(status) {
        switch ((status || "").toLowerCase()) {
            case "succeeded":
                return "good";
            case "failed":
                return "bad";
            case "inprogress":
                return "info";
            case "planned":
            case "pending":
            case "cancelrequested":
                return "warn";
            case "canceled":
            case "cancelled":
                return "dim";
            default:
                return "neutral";
        }
    }

    function formatTimestamp(value, fallback) {
        if (!value) {
            return fallback || "Unknown";
        }

        const parsed = new Date(value);
        if (Number.isNaN(parsed.getTime())) {
            return fallback || "Unknown";
        }

        const parts = usDateTimeFormatter.formatToParts(parsed).reduce(function (lookup, part) {
            if (part.type !== "literal") {
                lookup[part.type] = part.value;
            }

            return lookup;
        }, {});

        return parts.month + "/" + parts.day + "/" + parts.year + " " + parts.hour + ":" + parts.minute + " " + parts.dayPeriod;
    }

    function displayLabel(value) {
        if (!value) {
            return "Unknown";
        }

        return String(value)
            .trim()
            .replace(/[-_]+/g, " ")
            .replace(/(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])/g, " ");
    }

    function runKindLabel(run) {
        const mode = run && run.mode;
        const artifactType = run && run.artifactType;
        if (String(artifactType || "").toLowerCase() === "workerpreview" ||
            String(mode || "").toLowerCase() === "preview") {
            return "Preview Snapshot";
        }

        if (String(mode || "").toLowerCase() === "bulksync") {
            return run && run.dryRun ? "Dry Run" : "Live Sync";
        }

        const modeLabel = displayLabel(mode);
        return run && run.dryRun ? modeLabel + " Dry Run" : modeLabel;
    }

    function runDisplayName(run) {
        if (!run) {
            return "Unknown";
        }

        const parts = [runKindLabel(run)];
        if (run.syncScope && String(run.syncScope).toLowerCase() !== "unknown") {
            parts.push(displayLabel(run.syncScope));
        }
        parts.push(formatTimestamp(run.startedAt));
        return parts.join(" · ");
    }

    function runtimeDisplayName(status) {
        if (!status || !status.runId) {
            return "None";
        }

        const parts = [runKindLabel(status)];
        parts.push(status.startedAt ? formatTimestamp(status.startedAt) : status.runId);
        return parts.join(" · ");
    }

    function formatChartTimestamp(value) {
        if (!value) {
            return "Unknown";
        }

        const parsed = new Date(value);
        if (Number.isNaN(parsed.getTime())) {
            return "Unknown";
        }

        const parts = usChartDateTimeFormatter.formatToParts(parsed).reduce(function (lookup, part) {
            if (part.type !== "literal") {
                lookup[part.type] = part.value;
            }

            return lookup;
        }, {});

        return parts.month + "/" + parts.day + " " + parts.hour + ":" + parts.minute + " " + parts.dayPeriod;
    }

    function textOrFallback(value, fallback) {
        return value ? value : fallback;
    }

    function displayStage(stage) {
        if (!stage || stage.toLowerCase() === "notstarted") {
            return "standby";
        }

        return stage.replace(/inprogress/ig, "in progress").toLowerCase();
    }

    function workerProgressPercent(status) {
        const totalWorkers = status && status.totalWorkers ? status.totalWorkers : 0;
        const processedWorkers = status && status.processedWorkers ? status.processedWorkers : 0;

        if (totalWorkers <= 0) {
            return 0;
        }

        return Math.round((processedWorkers / totalWorkers) * 100);
    }

    function progressSnapshot(status) {
        return {
            processed: status && status.processedWorkers ? status.processedWorkers : 0,
            total: status && status.totalWorkers ? status.totalWorkers : 0
        };
    }

    function progressVisualState(status) {
        const currentStatus = (status && status.status ? status.status : "").toLowerCase();
        const currentStage = (status && status.stage ? status.stage : "").toLowerCase();

        if (currentStatus === "failed") {
            return "failed";
        }

        if (currentStatus === "cancelrequested") {
            return "canceling";
        }

        if (currentStatus === "canceled" ||
            currentStatus === "cancelled" ||
            currentStage === "canceled" ||
            currentStage === "cancelled") {
            return "canceled";
        }

        if (currentStatus === "planned" || currentStatus === "pending") {
            return "queued";
        }

        if (currentStatus === "inprogress") {
            if (currentStage === "starting") {
                return "starting";
            }

            if (currentStage === "planning") {
                return "planning";
            }

            if (currentStage === "applypreview") {
                return "applypreview";
            }

            return "syncing";
        }

        if ((currentStatus === "idle" || currentStatus === "succeeded") &&
            currentStage === "completed" &&
            (status.totalWorkers || 0) > 0) {
            return "complete";
        }

        return "idle";
    }

    function progressFillWidth(status) {
        const actualPercent = workerProgressPercent(status);
        const state = progressVisualState(status);

        switch (state) {
            case "starting":
                return 18;
            case "queued":
                return 16;
            case "canceling":
                return 32;
            case "planning":
                return Math.max(actualPercent, 18);
            case "applypreview":
                return Math.max(actualPercent, 38);
            case "syncing":
                return status && status.totalWorkers ? Math.max(actualPercent, 12) : 26;
            case "complete":
                return 100;
            case "failed":
                return status && status.totalWorkers ? Math.max(actualPercent, 18) : 100;
            case "canceled":
                return 18;
            default:
                return 8;
        }
    }

    function progressLabel(status) {
        const actualPercent = workerProgressPercent(status);

        switch (progressVisualState(status)) {
            case "starting":
                return "Starting";
            case "queued":
                return "Queued";
            case "canceling":
                return "Canceling";
            case "planning":
                return "Planning";
            case "applypreview":
                return actualPercent > 0 ? "Applying " + actualPercent + "%" : "Applying Preview";
            case "syncing":
                return actualPercent > 0 ? "Syncing " + actualPercent + "%" : "Syncing";
            case "complete":
                return "Done";
            case "failed":
                return "Failed";
            case "canceled":
                return "Canceled";
            default:
                return "Idle";
        }
    }

    function canCancelProgress(status) {
        const currentStatus = (status && status.status ? status.status : "").toLowerCase();
        return currentStatus === "planned" ||
            currentStatus === "pending" ||
            currentStatus === "inprogress" ||
            currentStatus === "cancelrequested";
    }

    function syncProgressCancel(status) {
        if (!elements.progressCancel) {
            return;
        }

        const currentStatus = (status && status.status ? status.status : "").toLowerCase();
        const cancelRequested = currentStatus === "cancelrequested";
        const canCancel = canCancelProgress(status);

        toggleHidden(elements.progressCancel, !canCancel);

        if (elements.progressCancelButton) {
            elements.progressCancelButton.disabled = !canCancel || cancelRequested;
        }

        updateText(elements.progressCancelLabel, cancelRequested ? "Canceling" : "Cancel run");
    }

    function buildProgressCopy(status) {
        const state = progressVisualState(status);
        const stage = displayStage(status && status.stage ? status.stage : null);
        const processedWorkers = status && status.processedWorkers ? status.processedWorkers : 0;
        const totalWorkers = status && status.totalWorkers ? status.totalWorkers : 0;

        switch (state) {
            case "starting":
                return status && status.runId
                    ? runtimeDisplayName(status) + " has started and is loading source workers."
                    : "The worker has claimed the run and is loading source workers.";
            case "queued":
                return status && status.runId
                    ? runtimeDisplayName(status) + " is queued and waiting for the worker service."
                    : "The next sync is queued and waiting for the worker service.";
            case "canceling":
                return "Cancellation has been requested. The worker will stop after the current operation.";
            case "planning":
                return "Building the sync plan before directory changes are applied.";
            case "applypreview":
                return status && status.currentWorkerId
                    ? "Applying the reviewed preview for worker " + status.currentWorkerId + "."
                    : "Applying the reviewed preview to Active Directory.";
            case "syncing":
                return totalWorkers > 0
                    ? processedWorkers + " of " + totalWorkers + " workers are accounted for in the current run."
                    : "Sync activity is running during " + stage + ".";
            case "complete":
                return processedWorkers + " of " + totalWorkers + " workers were processed in the last completed run.";
            case "failed":
                return textOrFallback(status && status.errorMessage ? status.errorMessage : null, "The sync failed during " + stage + ".");
            case "canceled":
                return "The last sync was canceled before worker progress completed.";
            default:
                return status && status.lastUpdatedAt
                    ? "Last worker activity was " + stage + "."
                    : "No active worker progress has been recorded yet.";
        }
    }

    function visualState(status) {
        const currentStatus = (status && status.status ? status.status : "").toLowerCase();
        const currentStage = (status && status.stage ? status.stage : "").toLowerCase();

        if (currentStatus === "failed") {
            return "failed";
        }

        if (currentStatus === "canceled" || currentStatus === "cancelled") {
            return "idle";
        }

        if (currentStatus === "planned" || currentStatus === "pending" || currentStatus === "cancelrequested") {
            return "queued";
        }

        if (currentStatus === "inprogress") {
            if (currentStage === "starting") {
                return "starting";
            }

            return "syncing";
        }

        if ((currentStatus === "idle" || currentStatus === "succeeded") &&
            currentStage === "completed" &&
            (status.totalWorkers || 0) > 0) {
            return "complete";
        }

        return "idle";
    }

    function buildStatusLine(status) {
        const state = visualState(status);
        const processedWorkers = status.processedWorkers || 0;
        const totalWorkers = status.totalWorkers || 0;
        const activeWorkerOffset = status.currentWorkerId ? 1 : 0;

        switch (state) {
            case "starting":
                return "Starting a new sync run";
            case "syncing":
                return totalWorkers > 0
                    ? "Syncing worker " + Math.min(processedWorkers + activeWorkerOffset, totalWorkers) + " of " + totalWorkers
                    : "Syncing directory changes";
            case "queued":
                return "Sync queued and waiting to start";
            case "failed":
                return "Sync stopped during " + displayStage(status.stage);
            case "complete":
                return "Last sync completed successfully";
            default:
                return "Idle and ready for the next sync";
        }
    }

    function buildStatusCaption(status) {
        const state = visualState(status);
        const stage = displayStage(status.stage);
        const processedWorkers = status.processedWorkers || 0;
        const totalWorkers = status.totalWorkers || 0;

        switch (state) {
            case "starting":
                return status.runId
                    ? runtimeDisplayName(status) + " has been claimed and is preparing worker data."
                    : "The worker has claimed the next run and is preparing to process workers.";
            case "syncing":
                if (status.currentWorkerId) {
                    return stage + " on worker " + status.currentWorkerId + ". " + processedWorkers + " of " + totalWorkers + " processed.";
                }

                return stage + ". " + processedWorkers + " of " + totalWorkers + " workers processed.";
            case "queued":
                return status.runId
                    ? runtimeDisplayName(status) + " is staged and waiting for execution."
                    : "The runtime is waiting for the worker service to begin the next run.";
            case "failed":
                return textOrFallback(status.errorMessage, "Review the last error and recent runs for the failure cause.");
            case "complete":
                return processedWorkers + " of " + totalWorkers + " workers were processed in the last completed run.";
            default:
                return status.lastUpdatedAt
                    ? "Last activity was " + stage + "."
                    : "No active sync is running.";
        }
    }

    function setBadge(element, text, status) {
        if (!element) {
            return;
        }

        element.className = "badge " + statusClass(status);
        element.textContent = text;
    }

    function setHealthBadge(status) {
        if (!overallBadge) {
            return;
        }

        const normalizedStatus = String(status || "").toLowerCase();
        overallBadge.className = "badge " + (normalizedStatus === "dim" ? "dim" : statusClass(status));
        overallBadge.textContent = "Connection Health";
    }

    function setLiveState(state, caption) {
        if (elements.liveBadge) {
            let className = "neutral";
            let label = "Connecting";

            switch (state) {
                case "live":
                    className = "good";
                    label = "SignalR Live";
                    break;
                case "reconnecting":
                    className = "info";
                    label = "Reconnecting";
                    break;
                case "fallback":
                    className = "warn";
                    label = "Polling Fallback";
                    break;
                default:
                    break;
            }

            elements.liveBadge.className = "badge " + className;
            elements.liveBadge.textContent = label;
        }

        if (elements.liveCaption) {
            elements.liveCaption.textContent = caption;
        }
    }

    function toggleHidden(element, hidden) {
        if (!element) {
            return;
        }

        element.classList.toggle("is-hidden", !!hidden);
    }

    function setPanelLoading(panel, loading) {
        if (!panel) {
            return;
        }

        panel.classList.toggle("is-loading", !!loading);
    }

    function syncHoverState() {
        if (!latestDashboardSnapshot) {
            return;
        }

        syncRunsInteractionState();
        syncTimelineInteractionState();
        renderRunsChart(latestDashboardSnapshot.runs || []);
        renderBucketChart(latestDashboardSnapshot);
    }

    function setHoveredRun(runId) {
        const nextValue = runId || null;
        if (hoveredRunId === nextValue) {
            return;
        }

        hoveredRunId = nextValue;
        syncHoverState();
    }

    function setHoveredBucket(bucketKey) {
        const nextValue = bucketKey || null;
        if (hoveredBucketKey === nextValue) {
            return;
        }

        hoveredBucketKey = nextValue;
        syncHoverState();
    }

    function flashUpdate(element) {
        if (!element || !motionAllowed() || !element.animate) {
            return;
        }

        element.animate(
            [
                { opacity: 0.72, filter: "saturate(0.94)" },
                { opacity: 1, filter: "saturate(1)" }
            ],
            {
                duration: 220,
                easing: "cubic-bezier(0.22, 1, 0.36, 1)"
            });
    }

    function updateText(element, nextValue) {
        if (!element) {
            return;
        }

        if (element.textContent === nextValue) {
            return;
        }

        element.textContent = nextValue;
        flashUpdate(element);
    }

    function runSummary(run) {
        const parts = [];

        if (run.creates) {
            parts.push(run.creates + " creates");
        }

        if (run.updates) {
            parts.push(run.updates + " updates");
        }

        if (run.enables) {
            parts.push(run.enables + " enables");
        }

        if (run.disables) {
            parts.push(run.disables + " disables");
        }

        if (run.graveyardMoves) {
            parts.push(run.graveyardMoves + " graveyard moves");
        }

        if (run.deletions) {
            parts.push(run.deletions + " deletions");
        }

        if (run.quarantined) {
            parts.push(run.quarantined + " quarantined");
        }

        if (run.conflicts) {
            parts.push(run.conflicts + " conflicts");
        }

        if (run.manualReview) {
            parts.push(run.manualReview + " manual review");
        }

        if (run.guardrailFailures) {
            parts.push(run.guardrailFailures + " guardrails");
        }

        return parts.length ? parts.join(" • ") : "No materialized bucket counts yet";
    }

    function bucketToneColor(tone, palette) {
        switch (tone) {
            case "good":
                return palette.good;
            case "accent":
                return palette.accent;
            case "info":
                return palette.info;
            case "warn":
                return palette.warn;
            case "bad":
                return palette.bad;
            case "dim":
                return palette.dim;
            default:
                return palette.muted;
        }
    }

    function formatBucketCount(value) {
        return countFormatter.format(Math.max(0, value || 0));
    }

    function formatRunPercent(value, total) {
        return total > 0 ? percentFormatter.format((value / total) * 100) + "%" : "0%";
    }

    function getRunChangedTotal(run) {
        return bucketDefinitions
            .filter(function (definition) { return definition.runMix; })
            .reduce(function (total, definition) {
                return total + (run[definition.key] || 0);
            }, 0);
    }

    function renderBucketSummary(focusRun, changedTotal, runTotal) {
        if (!elements.bucketChartSummary) {
            return;
        }

        elements.bucketChartSummary.innerHTML = "";

        if (!focusRun) {
            return;
        }

        const unchanged = focusRun.unchanged || 0;
        const items = [
            {
                label: "Changed",
                value: formatBucketCount(changedTotal),
                meta: formatRunPercent(changedTotal, runTotal) + " of run",
                tone: "accent",
                share: runTotal > 0 ? changedTotal / runTotal : 0
            },
            {
                label: "Unchanged",
                value: formatBucketCount(unchanged),
                meta: formatRunPercent(unchanged, runTotal) + " of run",
                bucketKey: "unchanged",
                tone: "dim",
                share: runTotal > 0 ? unchanged / runTotal : 0
            },
            {
                label: "Total",
                value: formatBucketCount(runTotal),
                meta: runDisplayName(focusRun),
                tone: "info",
                share: 1
            }
        ];

        items.forEach(function (item) {
            const node = item.bucketKey ? document.createElement("button") : document.createElement("div");
            node.className = "bucket-summary-item" +
                (item.bucketKey ? " bucket-summary-button" : "") +
                (item.tone ? " " + item.tone : "") +
                (selectedBucketKey === item.bucketKey ? " active" : "");
            node.style.setProperty("--bucket-share", (Math.max(0.02, Math.min(1, item.share || 0)) * 100) + "%");
            node.title = item.label + ": " + item.value + " (" + item.meta + ")";

            if (item.bucketKey) {
                node.type = "button";
                node.addEventListener("click", function () {
                    setSelectedBucket(item.bucketKey);
                });
            }

            const label = document.createElement("span");
            label.className = "bucket-summary-label";
            label.textContent = item.label;

            const value = document.createElement("strong");
            value.className = "bucket-summary-value";
            value.textContent = item.value;

            const meta = document.createElement("span");
            meta.className = "bucket-summary-meta";
            meta.textContent = item.meta;

            node.append(label, value, meta);
            elements.bucketChartSummary.appendChild(node);
        });
    }

    function animateProgress(status) {
        if (!elements.progressRoot || !elements.progressFill || !elements.progressCaption || !elements.progressCopy) {
            return;
        }

        const nextState = progressVisualState(status);
        const nextPercent = progressFillWidth(status);
        const summary = buildProgressCopy(status);
        const label = progressLabel(status);
        const previousState = elements.progressRoot.getAttribute("data-progress-state");

        elements.progressRoot.setAttribute("data-progress-state", nextState);
        updateText(elements.progressCopy, summary);
        updateText(elements.progressCaption, label);
        syncProgressCancel(status);

        if (previousState !== nextState) {
            flashUpdate(elements.progressRoot);
        }

        if (!motionAllowed()) {
            elements.progressFill.style.width = nextPercent + "%";
            latestProgressPercent = nextPercent;
            return;
        }

        animate(elements.progressFill, { width: nextPercent + "%" }, {
            duration: progressAnimationDurationMs / 1000,
            ease: "ease-out"
        });

        latestProgressPercent = nextPercent;
    }

    function renderSignal(status) {
        if (!elements.signalRoot) {
            return;
        }

        const previousState = elements.signalRoot.getAttribute("data-signal-state");
        const nextState = visualState(status);

        elements.signalRoot.setAttribute("data-signal-state", nextState);
        updateText(elements.statusLine, buildStatusLine(status));
        updateText(elements.statusCaption, buildStatusCaption(status));

        if (previousState !== nextState) {
            flashUpdate(elements.signalRoot);
        }
    }

    function formatScheduleCountdown(schedule) {
        if (!schedule || !schedule.enabled || !schedule.nextRunAt) {
            return { label: "Idle", progress: "0turn" };
        }

        const nextRunAt = new Date(schedule.nextRunAt);
        if (Number.isNaN(nextRunAt.getTime())) {
            return { label: "Unknown", progress: "0turn" };
        }

        const remainingMs = nextRunAt.getTime() - Date.now();
        if (remainingMs <= 0) {
            return { label: "Due", progress: "1turn" };
        }

        const totalMinutes = Math.max(1, Math.round(remainingMs / 60000));
        const hours = Math.floor(totalMinutes / 60);
        const minutes = totalMinutes % 60;
        const label = hours > 0 ? hours + "h " + minutes + "m" : minutes + "m";
        const progress = Math.max(0.08, Math.min(0.98, remainingMs / (24 * 60 * 60 * 1000)));

        return { label, progress: progress.toFixed(3) + "turn" };
    }

    function renderSchedule(schedule) {
        if (!elements.scheduleTitle || !elements.scheduleBadge || !elements.scheduleCountdown || !elements.scheduleCountdownValue || !elements.scheduleNextRun || !elements.scheduleInterval || !elements.scheduleLastRun) {
            return;
        }

        latestScheduleSnapshot = schedule;

        const isEnabled = !!(schedule && schedule.enabled);
        const countdown = formatScheduleCountdown(schedule);

        elements.scheduleTitle.textContent = isEnabled ? "Recurring schedule is active" : "Recurring schedule is paused";
        elements.scheduleBadge.className = "badge " + (isEnabled ? "good" : "dim");
        elements.scheduleBadge.textContent = isEnabled ? "Enabled" : "Paused";
        elements.scheduleCountdownValue.textContent = countdown.label;
        elements.scheduleCountdown.style.setProperty("--ring-progress", countdown.progress);
        elements.scheduleNextRun.setAttribute("data-dashboard-schedule-next-run-at", schedule && schedule.nextRunAt ? schedule.nextRunAt : "");
        elements.scheduleNextRun.textContent = schedule && schedule.nextRunAt ? formatTimestamp(schedule.nextRunAt) : "Not scheduled";
        elements.scheduleInterval.textContent = isEnabled ? (schedule.intervalMinutes || 0) + " minutes" : "Not scheduled";
        elements.scheduleLastRun.textContent = schedule && schedule.lastScheduledRunAt ? formatTimestamp(schedule.lastScheduledRunAt) : "Not scheduled";
    }

    function startScheduleTimer() {
        if (scheduleTimerId) {
            return;
        }

        scheduleTimerId = window.setInterval(function () {
            if (latestScheduleSnapshot) {
                renderSchedule(latestScheduleSnapshot);
            }
        }, 30000);
    }

    function runDetailHref(runId) {
        return "/Runs/Detail/" + encodeURIComponent(runId);
    }

    function getThemePalette() {
        const styles = getComputedStyle(document.documentElement);

        return {
            text: styles.getPropertyValue("--ink").trim(),
            muted: styles.getPropertyValue("--muted").trim(),
            line: styles.getPropertyValue("--line").trim(),
            accent: styles.getPropertyValue("--accent").trim(),
            info: styles.getPropertyValue("--info").trim(),
            good: styles.getPropertyValue("--good").trim(),
            warn: styles.getPropertyValue("--warn").trim(),
            bad: styles.getPropertyValue("--bad").trim(),
            dim: styles.getPropertyValue("--dim").trim()
        };
    }

    function ensureCharts() {
        if (elements.runsChart && !runsChartInstance) {
            runsChartInstance = echarts.init(elements.runsChart, null, { renderer: "canvas" });
        }

        if (elements.bucketChart && !bucketChartInstance) {
            bucketChartInstance = echarts.init(elements.bucketChart, null, { renderer: "canvas" });
        }
    }

    function findRunById(runs, runId) {
        return (runs || []).find(function (run) {
            return run.runId === runId;
        }) || null;
    }

    function getBucketDefinition(key) {
        return bucketDefinitions.find(function (definition) {
            return definition.key === key;
        }) || null;
    }

    function getFilteredRuns(runs) {
        if (selectedRunId) {
            return runs.filter(function (run) {
                return run.runId === selectedRunId;
            });
        }

        if (selectedBucketKey) {
            return runs.filter(function (run) {
                return (run[selectedBucketKey] || 0) > 0;
            });
        }

        return runs;
    }

    function getRunsPageCount(totalRuns) {
        return Math.max(1, Math.ceil(totalRuns / runsPageSize));
    }

    function clampRunsPage(totalRuns) {
        const pageCount = getRunsPageCount(totalRuns);
        currentRunsPage = Math.min(Math.max(currentRunsPage, 1), pageCount);
        return pageCount;
    }

    function getPagedRuns(runs) {
        const pageCount = clampRunsPage(runs.length);
        const startIndex = (currentRunsPage - 1) * runsPageSize;
        const endIndex = Math.min(startIndex + runsPageSize, runs.length);

        return {
            pageCount,
            startIndex,
            endIndex,
            runs: runs.slice(startIndex, endIndex)
        };
    }

    function syncFilterState(snapshot) {
        const runs = Array.isArray(snapshot && snapshot.runs) ? snapshot.runs : [];

        if (selectedRunId && !findRunById(runs, selectedRunId)) {
            selectedRunId = null;
        }

        if (selectedBucketKey && !runs.some(function (run) { return (run[selectedBucketKey] || 0) > 0; })) {
            selectedBucketKey = null;
        }
    }

    function setSelectedRun(runId) {
        selectedBucketKey = null;
        hoveredBucketKey = null;
        selectedRunId = selectedRunId === runId ? null : runId;
        currentRunsPage = 1;

        if (latestDashboardSnapshot) {
            renderDashboard(latestDashboardSnapshot);
        }
    }

    function setSelectedBucket(bucketKey) {
        selectedRunId = null;
        hoveredRunId = null;
        selectedBucketKey = selectedBucketKey === bucketKey ? null : bucketKey;
        currentRunsPage = 1;

        if (latestDashboardSnapshot) {
            renderDashboard(latestDashboardSnapshot);
        }
    }

    function clearRunsFilter() {
        selectedRunId = null;
        selectedBucketKey = null;
        hoveredRunId = null;
        hoveredBucketKey = null;
        currentRunsPage = 1;

        if (latestDashboardSnapshot) {
            renderDashboard(latestDashboardSnapshot);
        }
    }

    function getFilterCaption(runs, filteredRuns) {
        if (selectedRunId) {
            const selectedRun = findRunById(runs, selectedRunId);
            return selectedRun
                ? "Focused on " + runDisplayName(selectedRun) + ". Table filtered to a single run."
                : "The selected run is no longer present in the recent runs set.";
        }

        if (selectedBucketKey) {
            const definition = getBucketDefinition(selectedBucketKey);
            const label = definition ? definition.label : "the selected bucket";
            return filteredRuns.length
                ? "Filtered to runs with " + label.toLowerCase() + " activity."
                : "No recent runs match the " + label.toLowerCase() + " drill-down filter.";
        }

        return "Showing all recent runs.";
    }

    function renderFilterState(runs, filteredRuns) {
        if (elements.filterCaption) {
            elements.filterCaption.textContent = getFilterCaption(runs, filteredRuns);
        }

        toggleHidden(elements.clearFilterButton, !selectedRunId && !selectedBucketKey);

        if (!elements.runsEmptyMessage) {
            return;
        }

        elements.runsEmptyMessage.textContent = filteredRuns.length || !runs.length
            ? defaultRunsEmptyMessage
            : "No recent runs match the current chart drill-down filter.";
    }

    function renderRunsPagination(pagedRuns, totalRuns) {
        if (!elements.runsPagination || !elements.runsPaginationSummary || !elements.runsPreviousButton || !elements.runsNextButton) {
            return;
        }

        const hasRuns = totalRuns > 0;
        const hasMultiplePages = totalRuns > runsPageSize;

        toggleHidden(elements.runsPagination, !hasRuns || !hasMultiplePages);

        if (!hasRuns) {
            elements.runsPaginationSummary.textContent = "";
            elements.runsPreviousButton.disabled = true;
            elements.runsNextButton.disabled = true;
            return;
        }

        elements.runsPaginationSummary.textContent = "Showing " +
            String(pagedRuns.startIndex + 1) +
            "-" +
            String(pagedRuns.endIndex) +
            " of " +
            String(totalRuns) +
            " recent runs.";
        elements.runsPreviousButton.disabled = currentRunsPage <= 1;
        elements.runsNextButton.disabled = currentRunsPage >= pagedRuns.pageCount;
    }

    function createCell(className) {
        const cell = document.createElement("td");
        if (className) {
            cell.className = className;
        }
        return cell;
    }

    function buildRunRow() {
        const row = document.createElement("tr");
        const startedCell = createCell("recent-runs-table__started");
        const triggerCell = createCell("recent-runs-table__trigger");
        const modeCell = createCell("recent-runs-table__mode");
        const statusCell = createCell("recent-runs-table__status");
        const statusBadge = document.createElement("span");
        statusCell.appendChild(statusBadge);
        const dryRunCell = createCell("recent-runs-table__dry-run");
        const workersCell = createCell("recent-runs-table__workers");
        const summaryCell = createCell("recent-runs-table__summary");
        const summaryValue = document.createElement("span");
        summaryValue.className = "summary-value";
        summaryCell.appendChild(summaryValue);
        const actionsCell = createCell("recent-runs-table__actions");
        const link = document.createElement("a");
        link.textContent = "Open";
        actionsCell.appendChild(link);

        row.appendChild(startedCell);
        row.appendChild(triggerCell);
        row.appendChild(modeCell);
        row.appendChild(statusCell);
        row.appendChild(dryRunCell);
        row.appendChild(workersCell);
        row.appendChild(summaryCell);
        row.appendChild(actionsCell);

        row.__cells = {
            startedCell,
            triggerCell,
            modeCell,
            statusBadge,
            dryRunCell,
            workersCell,
            summaryCell,
            summaryValue,
            link
        };

        row.setAttribute("tabindex", "0");
        row.addEventListener("click", function (event) {
            if (event.target && event.target.closest("a")) {
                return;
            }

            setSelectedRun(row.dataset.runId);
        });
        row.addEventListener("keydown", function (event) {
            if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                setSelectedRun(row.dataset.runId);
            }
        });
        row.addEventListener("mouseenter", function () {
            setHoveredRun(row.dataset.runId);
        });
        row.addEventListener("mouseleave", function () {
            setHoveredRun(null);
        });

        return row;
    }

    function syncRunRowState(row) {
        if (!row) {
            return;
        }

        const isSelected = !!selectedRunId && selectedRunId === row.dataset.runId;
        const isHovered = !!hoveredRunId && hoveredRunId === row.dataset.runId;
        const isDimmed = (!!selectedRunId && !isSelected) || (!!hoveredRunId && !isHovered && !isSelected);

        row.classList.toggle("is-selected", isSelected);
        row.classList.toggle("is-hovered", isHovered);
        row.classList.toggle("is-dimmed", isDimmed);
    }

    function populateRunRow(row, run) {
        const cells = row.__cells;
        row.dataset.runId = run.runId;
        cells.startedCell.textContent = formatTimestamp(run.startedAt);
        cells.triggerCell.textContent = displayLabel(textOrFallback(run.runTrigger, "AdHoc"));
        cells.modeCell.textContent = runDisplayName(run);
        cells.modeCell.title = run.runId;
        cells.statusBadge.className = "badge " + runStatusClass(textOrFallback(run.status, "Unknown"));
        cells.statusBadge.textContent = displayLabel(textOrFallback(run.status, "Unknown"));
        cells.dryRunCell.textContent = run.dryRun ? "Yes" : "No";
        cells.workersCell.textContent = String(run.totalWorkers || 0);

        const summary = runSummary(run);
        cells.summaryCell.title = summary;
        cells.summaryValue.textContent = summary;
        cells.link.setAttribute("href", runDetailHref(run.runId));
        syncRunRowState(row);
    }

    function animateRunRowLayout(rows, beforeRects) {
        if (!motionAllowed()) {
            return;
        }

        rows.forEach(function (row, index) {
            const previousRect = beforeRects.get(row.dataset.runId);
            const nextRect = row.getBoundingClientRect();
            const deltaY = previousRect ? previousRect.top - nextRect.top : 0;

            row.animate(
                previousRect
                    ? [
                        { transform: "translateY(" + deltaY + "px)" },
                        { transform: "translateY(0)" }
                    ]
                    : [
                        { opacity: 0, transform: "translateY(14px)" },
                        { opacity: 1, transform: "translateY(0)" }
                    ],
                {
                    duration: previousRect ? 300 : 260,
                    delay: previousRect ? 0 : index * 24,
                    easing: "cubic-bezier(0.22, 1, 0.36, 1)"
                });
        });
    }

    function animateCollection(nodes) {
        if (!motionAllowed() || !nodes.length) {
            return;
        }

        animate(nodes, { opacity: [0, 1], y: [12, 0] }, {
            duration: 0.32,
            delay: stagger(0.035),
            ease: "ease-out"
        });
    }

    function renderRuns(runs) {
        if (!elements.runsBody || !elements.runsTable || !elements.runsEmpty) {
            return;
        }

        const beforeRects = new Map();
        const existingRows = Array.prototype.slice.call(elements.runsBody.querySelectorAll("tr"));
        const existingById = new Map();

        existingRows.forEach(function (row) {
            beforeRects.set(row.dataset.runId, row.getBoundingClientRect());
            existingById.set(row.dataset.runId, row);
        });

        toggleHidden(elements.runsTable, !runs.length);
        toggleHidden(elements.runsEmpty, !!runs.length);

        const fragment = document.createDocumentFragment();
        const activeRunIds = new Set();

        runs.forEach(function (run) {
            const row = existingById.get(run.runId) || buildRunRow();
            populateRunRow(row, run);
            activeRunIds.add(run.runId);
            fragment.appendChild(row);
        });

        elements.runsBody.appendChild(fragment);

        existingRows.forEach(function (row) {
            if (!activeRunIds.has(row.dataset.runId)) {
                row.remove();
            }
        });

        const renderedRows = Array.prototype.slice.call(elements.runsBody.querySelectorAll("tr"));
        animateRunRowLayout(renderedRows, beforeRects);
        if (!beforeRects.size) {
            animateCollection(renderedRows);
        }
    }

    function syncRunsInteractionState() {
        if (!elements.runsBody) {
            return;
        }

        Array.prototype.slice.call(elements.runsBody.querySelectorAll("tr")).forEach(syncRunRowState);
    }

    function renderStatus(snapshot) {
        if (!snapshot || !snapshot.status) {
            return;
        }

        const status = snapshot.status;
        const progress = progressSnapshot(status);
        renderSignal(status);
        updateText(valueNodes.status, displayLabel(textOrFallback(status.status, "Unknown")));
        updateText(valueNodes.stage, displayLabel(textOrFallback(status.stage, "Unknown")));
        updateText(valueNodes.runId, runtimeDisplayName(status));
        updateText(valueNodes.worker, textOrFallback(status.currentWorkerId, "None"));
        updateText(valueNodes.progress, progress.processed + " / " + progress.total);
        updateText(valueNodes.lastAction, textOrFallback(status.lastAction, "None"));
        updateText(valueNodes.started, formatTimestamp(status.startedAt, "Not started"));
        updateText(valueNodes.completed, formatTimestamp(status.completedAt, "Not completed"));
        updateText(valueNodes.lastUpdated, formatTimestamp(status.lastUpdatedAt));

        animateProgress(status);

        if (elements.statusError) {
            elements.statusError.textContent = textOrFallback(status.errorMessage, "");
            toggleHidden(elements.statusError, !status.errorMessage);
        }

        if (elements.attention) {
            elements.attention.textContent = textOrFallback(snapshot.attentionMessage, "");
            toggleHidden(elements.attention, !snapshot.requiresAttention);
        }
    }

    function timelineCompletionLabel(status, dryRun) {
        const prefix = dryRun ? "Dry run" : "Live sync";

        switch ((status || "").toLowerCase()) {
            case "succeeded":
            case "idle":
                return prefix + " completed";
            case "failed":
                return prefix + " failed";
            case "canceled":
            case "cancelled":
                return prefix + " canceled";
            default:
                return textOrFallback(status, prefix + " completed");
        }
    }

    function timelineProcessedDetail(processedWorkers, totalWorkers) {
        if (!(totalWorkers > 0)) {
            return null;
        }

        return processedWorkers + " of " + totalWorkers + " workers were processed.";
    }

    function pushProcessedWorkersStep(steps, stepTime, processedWorkers, totalWorkers) {
        const detail = timelineProcessedDetail(processedWorkers || 0, totalWorkers || 0);
        if (!detail) {
            return;
        }

        steps.push({
            label: "Processed workers",
            time: stepTime,
            detail: detail,
            tone: "info"
        });
    }

    function buildTimelineSteps(snapshot, focusRun) {
        const steps = [];
        const status = snapshot.status;
        const isCurrentRun = focusRun && status && focusRun.runId && status.runId === focusRun.runId;
        const runtimeState = status ? visualState(status) : "idle";

        if (!focusRun && status && status.runId) {
            if (status.startedAt) {
                steps.push({
                    label: "Started",
                    time: status.startedAt,
                    detail: (status.mode || "Runtime") + (status.dryRun ? " dry run started." : " run started."),
                    tone: "good"
                });
            }

            pushProcessedWorkersStep(
                steps,
                status.lastUpdatedAt || status.completedAt || status.startedAt || snapshot.checkedAt,
                status.processedWorkers,
                status.totalWorkers);

            if (runtimeState === "syncing" || (runtimeState === "queued" && focusRun.startedAt)) {
                steps.push({
                    label: runtimeState === "queued" ? "Queued" : "Current stage",
                    time: status.lastUpdatedAt || status.startedAt || snapshot.checkedAt,
                    detail: buildStatusCaption(status),
                    tone: runStatusClass(status.status)
                });
            }

            if (status.completedAt || runtimeState === "complete" || runtimeState === "failed") {
                steps.push({
                    label: timelineCompletionLabel(status.status, status.dryRun),
                    time: status.completedAt || status.lastUpdatedAt || snapshot.checkedAt,
                    detail: textOrFallback(status.errorMessage, buildStatusLine(status)),
                    tone: runtimeState === "complete" ? "good" : runStatusClass(status.status)
                });
            }

            return steps;
        }

        if (!focusRun) {
            return steps;
        }

        if (focusRun.startedAt) {
            steps.push({
                label: "Started",
                time: focusRun.startedAt,
                detail: runKindLabel(focusRun) + " started.",
                tone: "good"
            });
        }

        if (isCurrentRun) {
            pushProcessedWorkersStep(
                steps,
                status.lastUpdatedAt || status.completedAt || status.startedAt || snapshot.checkedAt,
                status.processedWorkers,
                status.totalWorkers);

            if (!focusRun.startedAt && status.runId && (status.status || "").match(/planned|pending|cancelrequested/i)) {
                steps.push({
                    label: "Queued",
                    time: status.lastUpdatedAt || snapshot.checkedAt,
                    detail: runtimeDisplayName(status) + " is waiting for the worker service to begin execution.",
                    tone: "warn"
                });
            }

            if (runtimeState === "syncing" || runtimeState === "queued") {
                steps.push({
                    label: runtimeState === "queued" ? "Queued" : "Current stage",
                    time: status.lastUpdatedAt || status.startedAt || snapshot.checkedAt,
                    detail: buildStatusCaption(status),
                    tone: runStatusClass(status.status)
                });
            }

            if (status.completedAt || runtimeState === "complete" || runtimeState === "failed") {
                steps.push({
                    label: timelineCompletionLabel(status.status, focusRun.dryRun),
                    time: status.completedAt || status.lastUpdatedAt || snapshot.checkedAt,
                    detail: textOrFallback(status.errorMessage, runSummary(focusRun)),
                    tone: runtimeState === "complete" ? "good" : runStatusClass(status.status)
                });
            }

            return steps;
        }

        pushProcessedWorkersStep(
            steps,
            focusRun.completedAt || focusRun.startedAt,
            focusRun.processedWorkers,
            focusRun.totalWorkers);

        if (focusRun.completedAt) {
            steps.push({
                label: timelineCompletionLabel(focusRun.status, focusRun.dryRun),
                time: focusRun.completedAt,
                detail: runSummary(focusRun),
                tone: (focusRun.status || "").toLowerCase() === "succeeded" ? "good" : runStatusClass(focusRun.status)
            });
        } else {
            steps.push({
                label: (focusRun.status || "").match(/planned|pending|cancelrequested/i) ? "Queued" : "Current stage",
                time: focusRun.startedAt,
                detail: focusRun.status
                    ? focusRun.status + " · " + runSummary(focusRun)
                    : runSummary(focusRun),
                tone: runStatusClass(focusRun.status)
            });
        }

        return steps;
    }

    function resolveTimelineRun(snapshot, filteredRuns) {
        const runs = snapshot.runs || [];

        if (selectedRunId) {
            return findRunById(runs, selectedRunId);
        }

        if (selectedBucketKey) {
            if (snapshot.activeRun && (snapshot.activeRun[selectedBucketKey] || 0) > 0) {
                return snapshot.activeRun;
            }

            if (snapshot.lastCompletedRun && (snapshot.lastCompletedRun[selectedBucketKey] || 0) > 0) {
                return snapshot.lastCompletedRun;
            }

            return filteredRuns[0] || null;
        }

        return snapshot.activeRun || snapshot.lastCompletedRun || runs[0] || null;
    }

    function renderTimeline(snapshot, filteredRuns) {
        if (!elements.timelineList || !elements.timelineTitle || !elements.timelineSummary) {
            return;
        }

        const focusRun = resolveTimelineRun(snapshot, filteredRuns);
        const steps = buildTimelineSteps(snapshot, focusRun);
        const hasRuntimeFocus = !focusRun && snapshot.status && snapshot.status.runId;
        const runtimeState = snapshot.status ? visualState(snapshot.status) : "idle";

        elements.timelineList.innerHTML = "";
        toggleHidden(elements.timelineEmpty, !!steps.length);

        if (!focusRun && !hasRuntimeFocus) {
            elements.timelineTitle.textContent = "Runtime focus";
            elements.timelineSummary.textContent = "Select a run from a chart or table row to focus its timeline.";
            return;
        }

        elements.timelineTitle.textContent = focusRun ? runDisplayName(focusRun) : "Runtime focus";

        if (selectedRunId) {
            elements.timelineSummary.textContent = "Focused from the recent runs table or chart. Select the same run again to clear focus.";
        } else if (selectedBucketKey) {
            const definition = getBucketDefinition(selectedBucketKey);
            elements.timelineSummary.textContent = "Focused on the first run matching the " + (definition ? definition.label.toLowerCase() : "selected") + " drill-down filter. Select the same chart slice again to clear focus.";
        } else if (hasRuntimeFocus) {
            elements.timelineSummary.textContent = "Following the live runtime state when a full run record is not yet available.";
        } else if (snapshot.activeRun && snapshot.activeRun.runId === focusRun.runId) {
            elements.timelineSummary.textContent = runtimeState === "syncing" || runtimeState === "queued"
                ? "Following the active run in the sticky live rail."
                : "Showing the latest run surfaced in the sticky live rail.";
        } else {
            elements.timelineSummary.textContent = "Showing the most recent completed run when no drill-down focus is selected.";
        }

        if (!steps.length) {
            return;
        }

        steps.forEach(function (step) {
            const item = document.createElement("li");
            item.className = "run-timeline-item " + (step.tone || "neutral");
            item.dataset.runId = focusRun ? focusRun.runId : "";

            const marker = document.createElement("span");
            marker.className = "run-timeline-marker";
            marker.setAttribute("aria-hidden", "true");
            item.appendChild(marker);

            const copy = document.createElement("div");
            copy.className = "run-timeline-copy";

            const head = document.createElement("div");
            head.className = "run-timeline-head";

            const label = document.createElement("p");
            label.className = "run-timeline-label";
            label.textContent = step.label;
            head.appendChild(label);

            const time = document.createElement("p");
            time.className = "run-timeline-time";
            time.textContent = formatTimestamp(step.time);
            head.appendChild(time);

            copy.appendChild(head);

            const detail = document.createElement("p");
            detail.className = "run-timeline-detail";
            detail.textContent = step.detail;
            copy.appendChild(detail);

            item.appendChild(copy);
            elements.timelineList.appendChild(item);
        });

        syncTimelineInteractionState();
    }

    function syncTimelineInteractionState() {
        if (!elements.timelineList) {
            return;
        }

        const items = Array.prototype.slice.call(elements.timelineList.querySelectorAll(".run-timeline-item"));
        const activeHoverRun = hoveredRunId || null;

        items.forEach(function (item) {
            const matchesHover = !!activeHoverRun && item.dataset.runId === activeHoverRun;
            const matchesSelection = !!selectedRunId && item.dataset.runId === selectedRunId;
            const isDimmed = (!!selectedRunId && !matchesSelection) || (!!activeHoverRun && !matchesHover && !matchesSelection);

            item.classList.toggle("is-hovered", matchesHover || matchesSelection);
            item.classList.toggle("is-dimmed", isDimmed);
        });
    }

    function buildSeriesData(runs, definition, palette) {
        const activeRunFocus = selectedRunId || hoveredRunId;
        const activeBucketFocus = selectedBucketKey || hoveredBucketKey;

        return runs.map(function (run) {
            const isFocusedRun = !!activeRunFocus && activeRunFocus === run.runId;
            const isHoveredRun = !!hoveredRunId && hoveredRunId === run.runId;
            const isRunDimmed = !!activeRunFocus && activeRunFocus !== run.runId;
            const isBucketDimmed = !!activeBucketFocus && activeBucketFocus !== definition.key;

            return {
                value: run[definition.key] || 0,
                itemStyle: {
                    opacity: isRunDimmed ? 0.22 : (isBucketDimmed ? 0.34 : 1),
                    borderColor: isFocusedRun || isHoveredRun ? palette.text : "transparent",
                    borderWidth: isFocusedRun ? 2 : (isHoveredRun ? 1.5 : 0)
                }
            };
        });
    }

    function buildRunMixAxis(runs) {
        const stackedDefinitions = bucketDefinitions.filter(function (definition) { return definition.runMix; });
        const rawMax = runs.reduce(function (currentMax, run) {
            const total = stackedDefinitions.reduce(function (sum, definition) {
                return sum + (run[definition.key] || 0);
            }, 0);
            return Math.max(currentMax, total);
        }, 0);

        if (rawMax <= 5) {
            return { max: 5, splitNumber: 5 };
        }

        if (rawMax <= 10) {
            return { max: 10, splitNumber: 5 };
        }

        if (rawMax <= 20) {
            return { max: 20, splitNumber: 4 };
        }

        if (rawMax <= 50) {
            return { max: 50, splitNumber: 5 };
        }

        const paddedMax = rawMax * 1.12;
        const max = paddedMax <= 100
            ? 100
            : Math.ceil(paddedMax / 100) * 100;

        return { max, splitNumber: 5 };
    }

    function bindRunsChartEvents(displayRuns) {
        if (!runsChartInstance) {
            return;
        }

        runsChartInstance.off("click");
        runsChartInstance.off("mouseover");
        runsChartInstance.off("globalout");
        runsChartInstance.on("click", function (params) {
            const run = displayRuns[params.dataIndex];
            if (run) {
                setSelectedRun(run.runId);
            }
        });
        runsChartInstance.on("mouseover", function (params) {
            const run = displayRuns[params.dataIndex];
            if (run) {
                setHoveredRun(run.runId);
            }
        });
        runsChartInstance.on("globalout", function () {
            setHoveredRun(null);
        });
    }

    function renderRunsChart(runs) {
        if (!elements.runsChart) {
            return;
        }

        ensureCharts();

        if (!runs || !runs.length) {
            toggleHidden(elements.runsChart, true);
            toggleHidden(elements.runsChartEmpty, false);
            if (runsChartInstance) {
                runsChartInstance.clear();
            }
            return;
        }

        toggleHidden(elements.runsChart, false);
        toggleHidden(elements.runsChartEmpty, true);

        const palette = getThemePalette();
        const displayRuns = runs.slice(0, 10).reverse();
        const labels = displayRuns.map(function (run) { return formatChartTimestamp(run.startedAt); });
        const runMixAxis = buildRunMixAxis(displayRuns);

        runsChartInstance.setOption({
            animationDuration: motionAllowed() ? 420 : 0,
            animationDurationUpdate: motionAllowed() ? 360 : 0,
            backgroundColor: "transparent",
            grid: { left: 12, right: 12, top: 112, bottom: 8, containLabel: true },
            tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
            legend: {
                top: 0,
                itemGap: 14,
                textStyle: { color: palette.muted }
            },
            xAxis: {
                type: "category",
                data: labels,
                axisLabel: { color: palette.muted, rotate: 18 },
                axisLine: { lineStyle: { color: palette.line } }
            },
            yAxis: {
                type: "value",
                max: runMixAxis.max,
                minInterval: 1,
                splitNumber: runMixAxis.splitNumber,
                axisLabel: { color: palette.muted },
                splitLine: { lineStyle: { color: palette.line } }
            },
            series: bucketDefinitions
                .filter(function (definition) { return definition.runMix; })
                .map(function (definition) {
                    return {
                        name: definition.label,
                        type: "bar",
                        stack: "runs",
                        itemStyle: { color: bucketToneColor(definition.tone, palette) },
                        data: buildSeriesData(displayRuns, definition, palette)
                    };
                })
        }, true);

        bindRunsChartEvents(displayRuns);
    }

    function bindBucketChartEvents() {
        if (!bucketChartInstance) {
            return;
        }

        bucketChartInstance.off("click");
        bucketChartInstance.off("mouseover");
        bucketChartInstance.off("globalout");
        bucketChartInstance.on("click", function (params) {
            const definition = bucketLabelToDefinition[params.name];
            if (definition) {
                setSelectedBucket(definition.key);
            }
        });
        bucketChartInstance.on("mouseover", function (params) {
            const definition = bucketLabelToDefinition[params.name];
            if (definition) {
                setHoveredBucket(definition.key);
            }
        });
        bucketChartInstance.on("globalout", function () {
            setHoveredBucket(null);
        });
    }

    function renderBucketChart(snapshot) {
        if (!elements.bucketChart) {
            return;
        }

        ensureCharts();

        const allRuns = Array.isArray(snapshot.runs) ? snapshot.runs : [];
        const focusRun = (selectedRunId && findRunById(allRuns, selectedRunId)) ||
            (hoveredRunId && findRunById(allRuns, hoveredRunId)) ||
            snapshot.activeRun ||
            snapshot.lastCompletedRun ||
            (allRuns.length ? allRuns[0] : null);
        if (!focusRun) {
            toggleHidden(elements.bucketChart, true);
            toggleHidden(elements.bucketChartEmpty, false);
            renderBucketSummary(null, 0, 0);
            if (elements.bucketChartMeta) {
                elements.bucketChartMeta.textContent = "Change composition appears once a run summary is available.";
            }
            if (elements.bucketChartEmpty) {
                elements.bucketChartEmpty.textContent = "Change composition appears once a run summary is available.";
            }
            if (bucketChartInstance) {
                bucketChartInstance.clear();
            }
            return;
        }

        const palette = getThemePalette();
        const changedTotal = getRunChangedTotal(focusRun);
        const runTotal = changedTotal + (focusRun.unchanged || 0);
        const entries = bucketDefinitions
            .filter(function (definition) { return definition.runMix; })
            .map(function (definition) {
                const isFocused = selectedBucketKey === definition.key || hoveredBucketKey === definition.key;
                const isDimmed = !!((selectedBucketKey && selectedBucketKey !== "unchanged") || hoveredBucketKey) && !isFocused;
                return {
                    name: definition.label,
                    value: focusRun[definition.key] || 0,
                    runTotal,
                    changedTotal,
                    selected: selectedBucketKey === definition.key,
                    itemStyle: {
                        opacity: isDimmed ? 0.34 : 1,
                        borderColor: isFocused ? palette.text : "transparent",
                        borderWidth: isFocused ? 1.5 : 0
                    }
                };
            })
            .filter(function (entry) { return entry.value > 0; });

        renderBucketSummary(focusRun, changedTotal, runTotal);

        if (!entries.length) {
            toggleHidden(elements.bucketChart, true);
            toggleHidden(elements.bucketChartEmpty, false);
            if (elements.bucketChartMeta) {
                elements.bucketChartMeta.textContent = runDisplayName(focusRun) + " has no changed buckets to chart.";
            }
            if (elements.bucketChartEmpty) {
                elements.bucketChartEmpty.textContent = changedTotal > 0
                    ? "Changed buckets will appear here once bucket counts are available."
                    : "No changed buckets in this run.";
            }
            if (bucketChartInstance) {
                bucketChartInstance.clear();
            }
            return;
        }

        toggleHidden(elements.bucketChart, false);
        toggleHidden(elements.bucketChartEmpty, true);

        if (elements.bucketChartMeta) {
            elements.bucketChartMeta.textContent = snapshot.activeRun
                ? "Click a slice to filter changed buckets. Showing live change composition for " + runDisplayName(focusRun) + "."
                : "Click a slice to filter changed buckets. Showing change composition for " + runDisplayName(focusRun) + ".";
        }

        bucketChartInstance.setOption({
            animationDuration: motionAllowed() ? 420 : 0,
            animationDurationUpdate: motionAllowed() ? 360 : 0,
            backgroundColor: "transparent",
            tooltip: {
                trigger: "item",
                formatter: function (params) {
                    const data = params.data || {};
                    return params.name + "<br>" +
                        formatBucketCount(params.value) + " users<br>" +
                        formatRunPercent(params.value, data.changedTotal || 0) + " of changed<br>" +
                        formatRunPercent(params.value, data.runTotal || 0) + " of run";
                }
            },
            legend: {
                orient: "vertical",
                right: 0,
                top: "middle",
                itemGap: 12,
                itemWidth: 14,
                itemHeight: 10,
                textStyle: { color: palette.muted }
            },
            graphic: [
                {
                    type: "group",
                    left: "36%",
                    top: "54%",
                    bounding: "raw",
                    children: [
                        {
                            type: "text",
                            left: "center",
                            top: -18,
                            style: {
                                text: formatBucketCount(changedTotal),
                                fill: palette.text,
                                fontSize: 24,
                                fontWeight: 700,
                                lineHeight: 26,
                                textAlign: "center"
                            },
                            silent: true
                        },
                        {
                            type: "text",
                            left: "center",
                            top: 12,
                            style: {
                                text: "changed",
                                fill: palette.muted,
                                fontSize: 12,
                                fontWeight: 600,
                                textAlign: "center"
                            },
                            silent: true
                        }
                    ],
                    silent: true
                }
            ],
            series: [
                {
                    type: "pie",
                    radius: ["46%", "70%"],
                    center: ["36%", "54%"],
                    avoidLabelOverlap: true,
                    selectedMode: "single",
                    percentPrecision: 1,
                    label: { color: palette.text },
                    data: entries.map(function (entry) {
                        const definition = bucketLabelToDefinition[entry.name];
                        return {
                            ...entry,
                            itemStyle: {
                                ...entry.itemStyle,
                                color: definition ? bucketToneColor(definition.tone, palette) : palette.muted
                            }
                        };
                    })
                }
            ]
        }, true);

        bindBucketChartEvents();
    }

    function refreshCharts() {
        if (latestDashboardSnapshot) {
            renderRunsChart(latestDashboardSnapshot.runs || []);
            renderBucketChart(latestDashboardSnapshot);
        }

        if (runsChartInstance) {
            runsChartInstance.resize();
        }

        if (bucketChartInstance) {
            bucketChartInstance.resize();
        }
    }

    function renderDashboard(snapshot) {
        latestDashboardSnapshot = snapshot;
        syncFilterState(snapshot);

        const allRuns = Array.isArray(snapshot.runs) ? snapshot.runs : [];
        const filteredRuns = getFilteredRuns(allRuns);
        const pagedRuns = getPagedRuns(filteredRuns);

        renderStatus(snapshot);
        renderFilterState(allRuns, filteredRuns);
        renderRuns(pagedRuns.runs);
        renderRunsPagination(pagedRuns, filteredRuns.length);
        renderTimeline(snapshot, filteredRuns);
        renderRunsChart(allRuns);
        renderBucketChart(snapshot);
        syncRunsInteractionState();
        syncTimelineInteractionState();
        setPanelLoading(elements.railPanel, false);
        setPanelLoading(elements.runsPanel, false);

        if (elements.checkedMessage) {
            elements.checkedMessage.textContent = "Live data refreshed " + formatTimestamp(snapshot.checkedAt);
        }
    }

    function buildProbeDetailParts(probe) {
        const detailParts = [];

        if (probe.summary) {
            detailParts.push(probe.summary);
        }

        if (probe.details) {
            detailParts.push(probe.details);
        }

        if (probe.checkedAt) {
            detailParts.push("Checked " + formatTimestamp(probe.checkedAt));
        }

        if (typeof probe.durationMilliseconds === "number") {
            detailParts.push("Latency " + probe.durationMilliseconds + " ms");
        }

        if (probe.observedAt) {
            detailParts.push("Observed " + formatTimestamp(probe.observedAt));
        }

        return detailParts;
    }

    function renderProbe(probe) {
        const item = document.createElement("li");
        item.className = "connection-health-item";
        item.title = probe.dependency + ": " + buildProbeDetailParts(probe).join(" • ");

        const head = document.createElement("div");
        head.className = "connection-health-item-head";

        const title = document.createElement("span");
        title.className = "connection-health-item-name";
        title.textContent = probe.dependency;
        head.appendChild(title);

        const badge = document.createElement("span");
        setBadge(badge, probe.status, probe.status);
        badge.title = item.title;
        head.appendChild(badge);
        item.appendChild(head);

        const summary = document.createElement("p");
        summary.className = "connection-health-item-summary";
        summary.textContent = probe.summary || "No data";
        item.appendChild(summary);

        return item;
    }

    function renderHealthSnapshot(snapshot) {
        if (snapshot && String(snapshot.status || "").toLowerCase() === "disabled") {
            renderHealthDisabled();
            return;
        }

        latestHealthSnapshot = snapshot;
        const probes = Array.isArray(snapshot && snapshot.probes) ? snapshot.probes.slice() : [];
        probes.sort(function (left, right) {
            return probeOrder.indexOf(left.dependency) - probeOrder.indexOf(right.dependency);
        });

        if (healthList) {
            healthList.innerHTML = "";
            probes.forEach(function (probe) {
                healthList.appendChild(renderProbe(probe));
            });
            animateCollection(Array.prototype.slice.call(healthList.querySelectorAll(".connection-health-item")));
        }

        setHealthBadge(snapshot.status);
        setPanelLoading(elements.root, false);
    }

    function renderHealthFailure(message) {
        latestHealthSnapshot = null;

        setHealthBadge("unhealthy");
        if (healthList) {
            healthList.innerHTML = "";

            probeOrder.forEach(function (dependency) {
                healthList.appendChild(renderProbe({
                    dependency,
                    status: "Unhealthy",
                    summary: "Probe unavailable.",
                    details: message
                }));
            });
        }

        setPanelLoading(elements.root, false);
    }

    function renderHealthDisabled() {
        latestHealthSnapshot = null;

        if (healthList) {
            healthList.innerHTML = "";

            probeOrder.forEach(function (dependency) {
                healthList.appendChild(renderProbe({
                    dependency,
                    status: "Disabled",
                    summary: "Dashboard probing is turned off.",
                    details: "Enable dashboard health probes to run this dependency check."
                }));
            });
        }

        setHealthBadge("dim");
        setPanelLoading(elements.root, false);
    }

    async function loadHealth() {
        if (isLoadingHealth) {
            return;
        }

        isLoadingHealth = true;
        setPanelLoading(elements.root, true);
        try {
            const response = await fetch("/api/dashboard/health", { headers: { Accept: "application/json" } });
            if (!response.ok) {
                throw new Error("Health probe request failed with HTTP " + response.status + ".");
            }

            renderHealthSnapshot(await response.json());
        } catch (error) {
            renderHealthFailure(error && error.message ? error.message : "Health probe request failed.");
        } finally {
            isLoadingHealth = false;
        }
    }

    async function loadSchedule() {
        try {
            const response = await fetch("/api/sync/schedule", { headers: { Accept: "application/json" } });
            if (!response.ok) {
                throw new Error("Schedule request failed with HTTP " + response.status + ".");
            }

            const payload = await response.json();
            renderSchedule(payload && payload.schedule ? payload.schedule : null);
        } catch (error) {
            if (!latestScheduleSnapshot) {
                renderSchedule(null);
            }
        }
    }

    async function loadDashboard() {
        if (isLoadingDashboard) {
            return;
        }

        isLoadingDashboard = true;
        setPanelLoading(elements.railPanel, true);
        setPanelLoading(elements.runsPanel, true);
        try {
            const response = await fetch("/api/dashboard", { headers: { Accept: "application/json" } });
            if (!response.ok) {
                throw new Error("Dashboard request failed with HTTP " + response.status + ".");
            }

            applyDashboardSnapshot(await response.json(), "fetch");
        } catch (error) {
            if (elements.checkedMessage) {
                elements.checkedMessage.textContent = error && error.message ? error.message : "Dashboard request failed.";
            }
        } finally {
            isLoadingDashboard = false;
            setPanelLoading(elements.railPanel, false);
            setPanelLoading(elements.runsPanel, false);
        }
    }

    function startFallbackPolling(options = {}) {
        const immediate = options.immediate !== false;

        if (immediate) {
            void loadDashboard();
            void loadHealth();
            void loadSchedule();
        }

        if (!dashboardTimerId) {
            dashboardTimerId = window.setInterval(loadDashboard, dashboardPollIntervalMs);
        }

        if (!healthTimerId) {
            healthTimerId = window.setInterval(loadHealth, healthPollIntervalMs);
        }
    }

    function stopFallbackPolling() {
        if (dashboardTimerId) {
            window.clearInterval(dashboardTimerId);
            dashboardTimerId = null;
        }

        if (healthTimerId) {
            window.clearInterval(healthTimerId);
            healthTimerId = null;
        }
    }

    function scheduleReconnect() {
        if (reconnectTimerId) {
            return;
        }

        reconnectTimerId = window.setTimeout(function () {
            reconnectTimerId = null;
            void startRealtimeConnection();
        }, 5000);
    }

    function handleRealtimeEvent(message) {
        if (!message || !message.type) {
            return;
        }

        if (message.type === "dashboardSnapshotUpdated" && message.dashboardSnapshot) {
            applyDashboardSnapshot(message.dashboardSnapshot, "signalr");
            return;
        }

        if (message.type === "healthSnapshotUpdated" && message.healthSnapshot) {
            renderHealthSnapshot(message.healthSnapshot);
        }
    }

    function applyDashboardSnapshot(snapshot, source) {
        const previousSnapshot = latestDashboardSnapshot;
        const shouldTransition = source === "signalr" &&
            previousSnapshot &&
            (previousSnapshot.status.runId !== snapshot.status.runId ||
                previousSnapshot.status.status !== snapshot.status.status);

        if (shouldTransition) {
            runMajorTransition(function () {
                renderDashboard(snapshot);
            });
            return;
        }

        renderDashboard(snapshot);
    }

    async function startRealtimeConnection() {
        if (connectionStartPromise) {
            return connectionStartPromise;
        }

        if (!connection) {
            connection = new signalR.HubConnectionBuilder()
                .withUrl("/hubs/dashboard")
                .withAutomaticReconnect([0, 2000, 5000, 10000, 20000])
                .build();

            connection.on("dashboardEvent", handleRealtimeEvent);

            connection.onreconnecting(function () {
                setLiveState("reconnecting", "Live connection interrupted. Polling fallback is active.");
                startFallbackPolling({ immediate: false });
            });

            connection.onreconnected(function () {
                setLiveState("live", "Push updates are active again.");
                stopFallbackPolling();
                void loadDashboard();
                void loadHealth();
            });

            connection.onclose(function () {
                setLiveState("fallback", "Live connection is unavailable. Polling fallback remains active.");
                startFallbackPolling({ immediate: false });
                connection = null;
                connectionStartPromise = null;
                scheduleReconnect();
            });
        }

        setLiveState("connecting", "Connecting to the live dashboard channel.");

        connectionStartPromise = connection.start()
            .then(function () {
                setLiveState("live", "Push updates are active.");
                stopFallbackPolling();
            })
            .catch(function () {
                setLiveState("fallback", "Live connection failed to start. Polling fallback remains active.");
                startFallbackPolling({ immediate: false });
                connection = null;
                scheduleReconnect();
            })
            .finally(function () {
                connectionStartPromise = null;
            });

        return connectionStartPromise;
    }

    function startDashboard() {
        startScheduleTimer();
        if (!initialHealthProbesEnabled) {
            renderHealthDisabled();
        }
        startFallbackPolling();
        void startRealtimeConnection();
    }

    if ("requestIdleCallback" in window) {
        window.requestIdleCallback(startDashboard, { timeout: 2000 });
    } else {
        window.setTimeout(startDashboard, 0);
    }

    window.addEventListener("beforeunload", function () {
        stopFallbackPolling();

        if (reconnectTimerId) {
            window.clearTimeout(reconnectTimerId);
        }

        if (scheduleTimerId) {
            window.clearInterval(scheduleTimerId);
        }

        clearProgressDoneTimer();

        if (connection) {
            void connection.stop();
        }

        if (runsChartInstance) {
            runsChartInstance.dispose();
        }

        if (bucketChartInstance) {
            bucketChartInstance.dispose();
        }
    });
})();
