const unsafeMethods = new Set(["POST", "PUT", "PATCH", "DELETE"]);

export function createSyncFactorsApiFetch(fetchImplementation = globalThis.fetch, tokenProvider = readAntiforgeryToken) {
    return async function syncFactorsApiFetch(input, init = {}) {
        const method = String(init.method || "GET").toUpperCase();
        if (!unsafeMethods.has(method)) {
            return fetchImplementation(input, init);
        }

        const token = tokenProvider();
        if (!token) {
            throw new Error("The SyncFactors antiforgery token is unavailable. Refresh the page before retrying this action.");
        }

        const headers = new Headers(init.headers || {});
        headers.set("X-SyncFactors-Antiforgery", token);
        return fetchImplementation(input, { ...init, headers });
    };
}

export function readAntiforgeryToken() {
    return globalThis.document
        ?.querySelector('meta[name="syncfactors-antiforgery-token"]')
        ?.getAttribute("content") || null;
}

export const syncFactorsApiFetch = createSyncFactorsApiFetch();
