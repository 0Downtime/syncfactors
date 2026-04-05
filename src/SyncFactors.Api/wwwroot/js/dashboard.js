(function () {
    var dashboardPollIntervalMs = 15000;
    var dashboardTimerId = null;
    var statusRoot = document.querySelector("[data-dashboard-status]");
    var runsBody = document.querySelector("[data-runs-body]");
    var runsTable = document.querySelector("[data-runs-table]");
    var runsEmpty = document.querySelector("[data-runs-empty]");
    var checkedMessage = document.querySelector("[data-dashboard-checked]");
    var statusError = document.querySelector("[data-status-error]");
    var attention = document.querySelector("[data-dashboard-attention]");
    var activeRunEmpty = document.querySelector("[data-active-run-empty]");
    var activeRunCard = document.querySelector("[data-active-run-card]");
    var lastRunEmpty = document.querySelector("[data-last-run-empty]");
    var lastRunCard = document.querySelector("[data-last-run-card]");
    var refreshButton = document.querySelector("[data-dashboard-refresh]");

    var root = document.querySelector("[data-connection-health]");
    if (!root) {
        return;
    }

    var list = root.querySelector("[data-health-list]");
    var overallBadge = root.querySelector("[data-health-overall-badge]");
    var lastChecked = root.querySelector("[data-health-last-checked]");
    var segments = Array.prototype.slice.call(root.querySelectorAll("[data-probe-segment]"));
    var probeOrder = ["SuccessFactors", "Active Directory", "Worker Service", "SQLite"];
    var pollIntervalMs = 60000;
    var timerId = null;
    var isLoading = false;
    var isDashboardLoading = false;

    if (refreshButton) {
        refreshButton.addEventListener("click", function () {
            window.location.reload();
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

        var parsed = new Date(value);
        return isNaN(parsed.getTime()) ? "Unknown" : parsed.toLocaleString();
    }

    function setBadge(element, text, status) {
        if (!element) {
            return;
        }

        element.className = "badge " + statusClass(status);
        element.textContent = text;
    }

    function textOrFallback(value, fallback) {
        return value ? value : fallback;
    }

    function runDetailHref(runId) {
        return "/Runs/Detail/" + encodeURIComponent(runId);
    }

    function toggleHidden(element, hidden) {
        if (!element) {
            return;
        }

        element.classList.toggle("is-hidden", !!hidden);
    }

    function runSummary(run) {
        var parts = [];

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

    function renderRuns(runs) {
        if (!runsBody || !runsTable || !runsEmpty) {
            return;
        }

        runsBody.innerHTML = "";
        toggleHidden(runsTable, !runs.length);
        toggleHidden(runsEmpty, !!runs.length);

        runs.forEach(function (run) {
            var row = document.createElement("tr");
            appendCell(row, formatTimestamp(run.startedAt));
            appendCell(row, textOrFallback(run.runTrigger, "AdHoc"));
            appendCell(row, textOrFallback(run.mode, "Unknown"));
            appendStatusCell(row, textOrFallback(run.status, "Unknown"));
            appendCell(row, run.dryRun ? "Yes" : "No");
            appendCell(row, String(run.totalWorkers || 0));
            appendCell(row, runSummary(run));
            appendLinkCell(row, runDetailHref(run.runId), "Open");
            runsBody.appendChild(row);
        });
    }

    function appendCell(row, text) {
        var cell = document.createElement("td");
        cell.textContent = text;
        row.appendChild(cell);
    }

    function appendStatusCell(row, status) {
        var cell = document.createElement("td");
        var badge = document.createElement("span");
        badge.className = "badge " + runStatusClass(status);
        badge.textContent = status;
        cell.appendChild(badge);
        row.appendChild(cell);
    }

    function appendLinkCell(row, href, text) {
        var cell = document.createElement("td");
        var link = document.createElement("a");
        link.setAttribute("href", href);
        link.textContent = text;
        cell.appendChild(link);
        row.appendChild(cell);
    }

    function renderStatus(snapshot) {
        if (!statusRoot || !snapshot || !snapshot.status) {
            return;
        }

        var status = snapshot.status;
        statusRoot.querySelector("[data-status-value]").textContent = textOrFallback(status.status, "Unknown");
        statusRoot.querySelector("[data-stage-value]").textContent = textOrFallback(status.stage, "Unknown");
        statusRoot.querySelector("[data-run-id-value]").textContent = textOrFallback(status.runId, "None");
        statusRoot.querySelector("[data-worker-value]").textContent = textOrFallback(status.currentWorkerId, "None");
        statusRoot.querySelector("[data-progress-value]").textContent = (status.processedWorkers || 0) + " / " + (status.totalWorkers || 0);
        statusRoot.querySelector("[data-last-action-value]").textContent = textOrFallback(status.lastAction, "None");
        statusRoot.querySelector("[data-last-updated-value]").textContent = formatTimestamp(status.lastUpdatedAt);

        if (statusError) {
            statusError.textContent = textOrFallback(status.errorMessage, "");
            toggleHidden(statusError, !status.errorMessage);
        }

        if (attention) {
            attention.textContent = textOrFallback(snapshot.attentionMessage, "");
            toggleHidden(attention, !snapshot.requiresAttention);
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
        card.querySelector(idSelector).textContent = run.runId;
        card.querySelector(summarySelector).textContent = run.status + " · " + run.mode + " · " + (run.processedWorkers || 0) + " / " + (run.totalWorkers || 0) + " workers";
        card.querySelector(linkSelector).setAttribute("href", runDetailHref(run.runId));
    }

    function renderDashboard(snapshot) {
        renderStatus(snapshot);
        renderRuns(Array.isArray(snapshot.runs) ? snapshot.runs : []);
        renderRunCard(activeRunCard, activeRunEmpty, snapshot.activeRun, "[data-active-run-summary]", "[data-active-run-id]", "[data-active-run-link]", "No run is active.");
        renderRunCard(lastRunCard, lastRunEmpty, snapshot.lastCompletedRun, "[data-last-run-summary]", "[data-last-run-id]", "[data-last-run-link]", "No completed runs yet.");

        if (checkedMessage) {
            checkedMessage.textContent = "Live data refreshed " + formatTimestamp(snapshot.checkedAt);
        }
    }

    function renderProbe(probe) {
        var card = document.createElement("article");
        card.className = "connection-card";

        var head = document.createElement("header");
        head.className = "connection-head";

        var title = document.createElement("h4");
        title.textContent = probe.dependency;
        head.appendChild(title);

        var badge = document.createElement("span");
        setBadge(badge, probe.status, probe.status);
        head.appendChild(badge);
        card.appendChild(head);

        var summary = document.createElement("p");
        summary.className = "connection-summary";
        summary.textContent = probe.summary;
        card.appendChild(summary);

        var detail = document.createElement("p");
        detail.className = "connection-detail muted";
        var detailParts = [];

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

    function renderSnapshot(snapshot) {
        var probes = Array.isArray(snapshot && snapshot.probes) ? snapshot.probes.slice() : [];
        probes.sort(function (left, right) {
            return probeOrder.indexOf(left.dependency) - probeOrder.indexOf(right.dependency);
        });

        if (list) {
            list.innerHTML = "";
            probes.forEach(function (probe) {
                list.appendChild(renderProbe(probe));
            });
        }

        setBadge(overallBadge, snapshot.status || "Unknown", snapshot.status);
        if (lastChecked) {
            lastChecked.textContent = "Last checked " + formatTimestamp(snapshot.checkedAt);
        }

        segments.forEach(function (segment) {
            var dependency = segment.getAttribute("data-probe-segment");
            var probe = probes.find(function (item) { return item.dependency === dependency; });
            segment.className = "connection-segment " + statusClass(probe ? probe.status : "unknown");
            segment.title = dependency + ": " + (probe ? probe.summary : "No data");
        });
    }

    function renderFailure(message) {
        setBadge(overallBadge, "Unhealthy", "unhealthy");
        if (lastChecked) {
            lastChecked.textContent = message;
        }
    }

    async function loadHealth() {
        if (isLoading) {
            return;
        }

        isLoading = true;
        try {
            var response = await fetch("/api/health", { headers: { "Accept": "application/json" } });
            if (!response.ok) {
                throw new Error("Health probe request failed with HTTP " + response.status + ".");
            }

            renderSnapshot(await response.json());
        } catch (error) {
            renderFailure(error && error.message ? error.message : "Health probe request failed.");
        } finally {
            isLoading = false;
        }
    }

    async function loadDashboard() {
        if (isDashboardLoading) {
            return;
        }

        isDashboardLoading = true;
        try {
            var response = await fetch("/api/dashboard", { headers: { "Accept": "application/json" } });
            if (!response.ok) {
                throw new Error("Dashboard request failed with HTTP " + response.status + ".");
            }

            renderDashboard(await response.json());
        } catch (error) {
            if (checkedMessage) {
                checkedMessage.textContent = error && error.message ? error.message : "Dashboard request failed.";
            }
        } finally {
            isDashboardLoading = false;
        }
    }

    function startPolling() {
        loadDashboard();
        loadHealth();
        timerId = window.setInterval(loadHealth, pollIntervalMs);
        dashboardTimerId = window.setInterval(loadDashboard, dashboardPollIntervalMs);
    }

    if ("requestIdleCallback" in window) {
        window.requestIdleCallback(startPolling, { timeout: 2000 });
    } else {
        window.setTimeout(startPolling, 0);
    }

    window.addEventListener("beforeunload", function () {
        if (timerId) {
            window.clearInterval(timerId);
        }

        if (dashboardTimerId) {
            window.clearInterval(dashboardTimerId);
        }
    });
})();
