const runMixBuckets = [
    "creates",
    "updates",
    "enables",
    "disables",
    "graveyardMoves",
    "deletions",
    "quarantined",
    "manualReview",
    "conflicts",
    "guardrailFailures"
];

export function buildRunMixAxis(runs) {
    const safeRuns = Array.isArray(runs) ? runs : [];
    const rawMax = safeRuns.reduce(function (currentMax, run) {
        const total = runMixBuckets.reduce(function (sum, bucket) {
            return sum + (run && run[bucket] ? run[bucket] : 0);
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
