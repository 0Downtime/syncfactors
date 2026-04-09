import * as signalR from "@microsoft/signalr";
import * as echarts from "echarts/core";
import { BarChart, PieChart } from "echarts/charts";
import { GridComponent, LegendComponent, TooltipComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import { animate, stagger } from "motion";

echarts.use([BarChart, PieChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer]);

(function () {
    const dashboardPollIntervalMs = 15000;
    const healthPollIntervalMs = 60000;
    const reduceMotionQuery = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
    const supportsViewTransitions = typeof document.startViewTransition === "function";

    const elements = {
        root: document.querySelector("[data-connection-health]"),
        statusRoot: document.querySelector("[data-dashboard-status]"),
        runsBody: document.querySelector("[data-runs-body]"),
        runsTable: document.querySelector("[data-runs-table]"),
        runsEmpty: document.querySelector("[data-runs-empty]"),
        checkedMessage: document.querySelector("[data-dashboard-checked]"),
        statusError: document.querySelector("[data-status-error]"),
        attention: document.querySelector("[data-dashboard-attention]"),
        activeRunEmpty: document.querySelector("[data-active-run-empty]"),
        activeRunCard: document.querySelector("[data-active-run-card]"),
        lastRunEmpty: document.querySelector("[data-last-run-empty]"),
        lastRunCard: document.querySelector("[data-last-run-card]"),
        refreshButton: document.querySelector("[data-dashboard-refresh]"),
        signalRoot: document.querySelector("[data-dashboard-signal]"),
        statusLine: document.querySelector("[data-status-line]"),
        statusCaption: document.querySelector("[data-status-caption]"),
        liveBadge: document.querySelector("[data-live-status-badge]"),
        liveCaption: document.querySelector("[data-live-status-caption]"),
        progressFill: document.querySelector("[data-progress-fill]"),
        progressCaption: document.querySelector("[data-progress-caption]"),
        progressCopy: document.querySelector("[data-progress-copy]"),
        runsChart: document.querySelector("[data-runs-chart]"),
        runsChartEmpty: document.querySelector("[data-runs-chart-empty]"),
        bucketChart: document.querySelector("[data-buckets-chart]"),
        bucketChartEmpty: document.querySelector("[data-buckets-chart-empty]"),
        bucketChartMeta: document.querySelector("[data-buckets-chart-meta]")
    };

    if (!elements.root || !elements.statusRoot) {
        return;
    }

    const healthList = elements.root.querySelector("[data-health-list]");
    const overallBadge = elements.root.querySelector("[data-health-overall-badge]");
    const lastChecked = elements.root.querySelector("[data-health-last-checked]");
    const segments = Array.prototype.slice.call(elements.root.querySelectorAll("[data-probe-segment]"));
    const probeOrder = ["SuccessFactors", "Active Directory", "Worker Service", "SQLite"];
    const valueNodes = {
        status: elements.statusRoot.querySelector("[data-status-value]"),
        stage: elements.statusRoot.querySelector("[data-stage-value]"),
        runId: elements.statusRoot.querySelector("[data-run-id-value]"),
        worker: elements.statusRoot.querySelector("[data-worker-value]"),
        progress: elements.statusRoot.querySelector("[data-progress-value]"),
        lastAction: elements.statusRoot.querySelector("[data-last-action-value]"),
        lastUpdated: elements.statusRoot.querySelector("[data-last-updated-value]")
    };

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
    let latestProgressPercent = 0;

    if (elements.refreshButton) {
        elements.refreshButton.addEventListener("click", function () {
            runMajorTransition(function () {
                void loadDashboard();
                void loadHealth();
            });
        });
    }

    window.addEventListener("syncfactors:themechange", function () {
        refreshCharts();
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

    function formatTimestamp(value) {
        if (!value) {
            return "Unknown";
        }

        const parsed = new Date(value);
        return Number.isNaN(parsed.getTime())
            ? "Unknown"
            : parsed.toLocaleString([], {
                year: "numeric",
                month: "numeric",
                day: "numeric",
                hour: "numeric",
                minute: "2-digit"
            });
    }

    function formatChartTimestamp(value) {
        if (!value) {
            return "Unknown";
        }

        const parsed = new Date(value);
        return Number.isNaN(parsed.getTime())
            ? "Unknown"
            : parsed.toLocaleTimeString([], {
                month: "short",
                day: "numeric",
                hour: "numeric",
                minute: "2-digit"
            });
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
            case "syncing":
                if (status.currentWorkerId) {
                    return stage + " on worker " + status.currentWorkerId + ". " + processedWorkers + " of " + totalWorkers + " processed.";
                }

                return stage + ". " + processedWorkers + " of " + totalWorkers + " workers processed.";
            case "queued":
                return status.runId
                    ? "Run " + status.runId + " is staged and waiting for execution."
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

    function flashUpdate(element) {
        if (!element || !motionAllowed() || !element.animate) {
            return;
        }

        element.animate(
            [
                { transform: "translateY(6px)", opacity: 0.55, filter: "saturate(0.92)" },
                { transform: "translateY(0)", opacity: 1, filter: "saturate(1)" }
            ],
            {
                duration: 280,
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

    function animateProgress(status) {
        if (!elements.progressFill || !elements.progressCaption || !elements.progressCopy) {
            return;
        }

        const processed = status.processedWorkers || 0;
        const total = status.totalWorkers || 0;
        const nextPercent = total > 0 ? Math.round((processed / total) * 100) : 0;
        const summary = total > 0
            ? processed + " of " + total + " workers are accounted for in the current run."
            : "No active worker progress has been recorded yet.";

        updateText(elements.progressCopy, summary);

        if (!motionAllowed()) {
            elements.progressFill.style.width = nextPercent + "%";
            elements.progressCaption.textContent = nextPercent + "%";
            latestProgressPercent = nextPercent;
            return;
        }

        animate(latestProgressPercent, nextPercent, {
            duration: 0.6,
            ease: "ease-out",
            onUpdate: function (latest) {
                elements.progressCaption.textContent = Math.round(latest) + "%";
            }
        });

        animate(elements.progressFill, { width: nextPercent + "%" }, {
            duration: 0.7,
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

    function runDetailHref(runId) {
        return "/Runs/Detail/" + encodeURIComponent(runId);
    }

    function appendCell(row, text, className) {
        const cell = document.createElement("td");
        cell.textContent = text;
        if (className) {
            cell.className = className;
        }
        row.appendChild(cell);
    }

    function appendStatusCell(row, status) {
        const cell = document.createElement("td");
        cell.className = "recent-runs-table__status";
        const badge = document.createElement("span");
        badge.className = "badge " + runStatusClass(status);
        badge.textContent = status;
        cell.appendChild(badge);
        row.appendChild(cell);
    }

    function appendSummaryCell(row, text) {
        const cell = document.createElement("td");
        const content = document.createElement("span");
        cell.className = "recent-runs-table__summary";
        cell.title = text;
        content.className = "summary-value";
        content.textContent = text;
        cell.appendChild(content);
        row.appendChild(cell);
    }

    function appendLinkCell(row, href, text) {
        const cell = document.createElement("td");
        cell.className = "recent-runs-table__actions";
        const link = document.createElement("a");
        link.setAttribute("href", href);
        link.textContent = text;
        cell.appendChild(link);
        row.appendChild(cell);
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

        elements.runsBody.innerHTML = "";
        toggleHidden(elements.runsTable, !runs.length);
        toggleHidden(elements.runsEmpty, !!runs.length);

        runs.forEach(function (run) {
            const row = document.createElement("tr");
            appendCell(row, formatTimestamp(run.startedAt), "recent-runs-table__started");
            appendCell(row, textOrFallback(run.runTrigger, "AdHoc"), "recent-runs-table__trigger");
            appendCell(row, textOrFallback(run.mode, "Unknown"), "recent-runs-table__mode");
            appendStatusCell(row, textOrFallback(run.status, "Unknown"));
            appendCell(row, run.dryRun ? "Yes" : "No", "recent-runs-table__dry-run");
            appendCell(row, String(run.totalWorkers || 0), "recent-runs-table__workers");
            appendSummaryCell(row, runSummary(run));
            appendLinkCell(row, runDetailHref(run.runId), "Open");
            elements.runsBody.appendChild(row);
        });

        animateCollection(Array.prototype.slice.call(elements.runsBody.querySelectorAll("tr")));
    }

    function renderStatus(snapshot) {
        if (!snapshot || !snapshot.status) {
            return;
        }

        const status = snapshot.status;
        renderSignal(status);
        updateText(valueNodes.status, textOrFallback(status.status, "Unknown"));
        updateText(valueNodes.stage, textOrFallback(status.stage, "Unknown"));
        updateText(valueNodes.runId, textOrFallback(status.runId, "None"));
        updateText(valueNodes.worker, textOrFallback(status.currentWorkerId, "None"));
        updateText(valueNodes.progress, (status.processedWorkers || 0) + " / " + (status.totalWorkers || 0));
        updateText(valueNodes.lastAction, textOrFallback(status.lastAction, "None"));
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

    function renderRunCard(card, empty, run, summarySelector, idSelector, linkSelector, emptyText) {
        if (!card || !empty) {
            return;
        }

        if (!run) {
            toggleHidden(empty, false);
            empty.textContent = emptyText;
            toggleHidden(card, true);
            return;
        }

        toggleHidden(empty, true);
        toggleHidden(card, false);
        updateText(card.querySelector(idSelector), run.runId);
        updateText(
            card.querySelector(summarySelector),
            run.status + " · " + run.mode + " · " + (run.processedWorkers || 0) + " / " + (run.totalWorkers || 0) + " workers");
        card.querySelector(linkSelector).setAttribute("href", runDetailHref(run.runId));
        flashUpdate(card);
    }

    function getThemePalette() {
        const styles = getComputedStyle(document.documentElement);

        return {
            text: styles.getPropertyValue("--ink").trim(),
            muted: styles.getPropertyValue("--muted").trim(),
            line: styles.getPropertyValue("--line").trim(),
            panel: styles.getPropertyValue("--panel").trim(),
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
        const seriesRuns = runs.slice(0, 10).reverse();
        const labels = seriesRuns.map(function (run) { return formatChartTimestamp(run.startedAt); });

        runsChartInstance.setOption({
            animationDuration: motionAllowed() ? 420 : 0,
            animationDurationUpdate: motionAllowed() ? 360 : 0,
            backgroundColor: "transparent",
            grid: { left: 12, right: 12, top: 28, bottom: 8, containLabel: true },
            tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
            legend: {
                top: 0,
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
                axisLabel: { color: palette.muted },
                splitLine: { lineStyle: { color: palette.line } }
            },
            series: [
                { name: "Creates", type: "bar", stack: "runs", itemStyle: { color: palette.good }, data: seriesRuns.map(function (run) { return run.creates || 0; }) },
                { name: "Updates", type: "bar", stack: "runs", itemStyle: { color: palette.accent }, data: seriesRuns.map(function (run) { return run.updates || 0; }) },
                { name: "Manual Review", type: "bar", stack: "runs", itemStyle: { color: palette.warn }, data: seriesRuns.map(function (run) { return run.manualReview || 0; }) },
                { name: "Conflicts", type: "bar", stack: "runs", itemStyle: { color: palette.bad }, data: seriesRuns.map(function (run) { return run.conflicts || 0; }) },
                { name: "Guardrails", type: "bar", stack: "runs", itemStyle: { color: palette.dim }, data: seriesRuns.map(function (run) { return run.guardrailFailures || 0; }) }
            ]
        }, true);
    }

    function renderBucketChart(snapshot) {
        if (!elements.bucketChart) {
            return;
        }

        ensureCharts();

        const focusRun = snapshot.activeRun || snapshot.lastCompletedRun || (snapshot.runs && snapshot.runs.length ? snapshot.runs[0] : null);
        if (!focusRun) {
            toggleHidden(elements.bucketChart, true);
            toggleHidden(elements.bucketChartEmpty, false);
            if (elements.bucketChartMeta) {
                elements.bucketChartMeta.textContent = "Run composition appears once a run summary is available.";
            }
            if (bucketChartInstance) {
                bucketChartInstance.clear();
            }
            return;
        }

        const entries = [
            { name: "Creates", value: focusRun.creates || 0 },
            { name: "Updates", value: focusRun.updates || 0 },
            { name: "Manual Review", value: focusRun.manualReview || 0 },
            { name: "Conflicts", value: focusRun.conflicts || 0 },
            { name: "Guardrails", value: focusRun.guardrailFailures || 0 },
            { name: "Unchanged", value: focusRun.unchanged || 0 }
        ].filter(function (entry) { return entry.value > 0; });

        if (!entries.length) {
            toggleHidden(elements.bucketChart, true);
            toggleHidden(elements.bucketChartEmpty, false);
            if (elements.bucketChartMeta) {
                elements.bucketChartMeta.textContent = "Run " + focusRun.runId + " has no materialized bucket counts yet.";
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
                ? "Showing live bucket composition for active run " + focusRun.runId + "."
                : "Showing bucket composition for recent run " + focusRun.runId + ".";
        }

        const palette = getThemePalette();
        bucketChartInstance.setOption({
            animationDuration: motionAllowed() ? 420 : 0,
            animationDurationUpdate: motionAllowed() ? 360 : 0,
            backgroundColor: "transparent",
            tooltip: { trigger: "item" },
            legend: {
                orient: "vertical",
                right: 0,
                top: "middle",
                textStyle: { color: palette.muted }
            },
            series: [
                {
                    type: "pie",
                    radius: ["46%", "70%"],
                    center: ["36%", "54%"],
                    avoidLabelOverlap: true,
                    label: { color: palette.text },
                    data: entries,
                    color: [palette.good, palette.accent, palette.warn, palette.bad, palette.dim, palette.info]
                }
            ]
        }, true);
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
        renderStatus(snapshot);
        renderRuns(Array.isArray(snapshot.runs) ? snapshot.runs : []);
        renderRunCard(elements.activeRunCard, elements.activeRunEmpty, snapshot.activeRun, "[data-active-run-summary]", "[data-active-run-id]", "[data-active-run-link]", "No run is active.");
        renderRunCard(elements.lastRunCard, elements.lastRunEmpty, snapshot.lastCompletedRun, "[data-last-run-summary]", "[data-last-run-id]", "[data-last-run-link]", "No completed runs yet.");
        renderRunsChart(snapshot.runs || []);
        renderBucketChart(snapshot);

        if (elements.checkedMessage) {
            elements.checkedMessage.textContent = "Live data refreshed " + formatTimestamp(snapshot.checkedAt);
        }
    }

    function renderProbe(probe) {
        const card = document.createElement("article");
        card.className = "connection-card";

        const head = document.createElement("header");
        head.className = "connection-head";

        const title = document.createElement("h4");
        title.textContent = probe.dependency;
        head.appendChild(title);

        const badge = document.createElement("span");
        setBadge(badge, probe.status, probe.status);
        head.appendChild(badge);
        card.appendChild(head);

        const summary = document.createElement("p");
        summary.className = "connection-summary";
        summary.textContent = probe.summary;
        card.appendChild(summary);

        const detail = document.createElement("p");
        detail.className = "connection-detail muted";
        const detailParts = [];

        if (probe.details) {
            detailParts.push(probe.details);
        }

        detailParts.push("Checked " + formatTimestamp(probe.checkedAt));
        detailParts.push("Latency " + probe.durationMilliseconds + " ms");

        if (probe.observedAt) {
            detailParts.push("Observed " + formatTimestamp(probe.observedAt));
        }

        detail.textContent = detailParts.join(" • ");
        card.appendChild(detail);
        return card;
    }

    function renderHealthSnapshot(snapshot) {
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
            animateCollection(Array.prototype.slice.call(healthList.querySelectorAll(".connection-card")));
        }

        setBadge(overallBadge, snapshot.status || "Unknown", snapshot.status);
        if (lastChecked) {
            lastChecked.textContent = "Last checked " + formatTimestamp(snapshot.checkedAt);
        }

        segments.forEach(function (segment) {
            const dependency = segment.getAttribute("data-probe-segment");
            const probe = probes.find(function (item) { return item.dependency === dependency; });
            segment.className = "connection-segment " + statusClass(probe ? probe.status : "unknown");
            segment.title = dependency + ": " + (probe ? probe.summary : "No data");
        });
    }

    function renderHealthFailure(message) {
        setBadge(overallBadge, "Unhealthy", "unhealthy");
        if (lastChecked) {
            lastChecked.textContent = message;
        }
    }

    async function loadHealth() {
        if (isLoadingHealth) {
            return;
        }

        isLoadingHealth = true;
        try {
            const response = await fetch("/api/health", { headers: { Accept: "application/json" } });
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

    async function loadDashboard() {
        if (isLoadingDashboard) {
            return;
        }

        isLoadingDashboard = true;
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
        }
    }

    function startFallbackPolling(options = {}) {
        const immediate = options.immediate !== false;

        if (immediate) {
            void loadDashboard();
            void loadHealth();
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
