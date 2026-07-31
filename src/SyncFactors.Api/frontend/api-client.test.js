import { describe, expect, it, vi } from "vitest";
import { createSyncFactorsApiFetch } from "./api-client.js";

describe("createSyncFactorsApiFetch", () => {
    it("adds the antiforgery header to unsafe requests", async () => {
        const fetchImplementation = vi.fn().mockResolvedValue({ ok: true });
        const apiFetch = createSyncFactorsApiFetch(fetchImplementation, () => "request-token");

        await apiFetch("/api/runs", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: "{}"
        });

        const [, init] = fetchImplementation.mock.calls[0];
        expect(init.headers.get("X-SyncFactors-Antiforgery")).toBe("request-token");
        expect(init.headers.get("Content-Type")).toBe("application/json");
    });

    it("does not require a token for safe requests", async () => {
        const fetchImplementation = vi.fn().mockResolvedValue({ ok: true });
        const apiFetch = createSyncFactorsApiFetch(fetchImplementation, () => null);

        await apiFetch("/api/dashboard", { headers: { Accept: "application/json" } });

        expect(fetchImplementation).toHaveBeenCalledOnce();
    });

    it("fails locally rather than sending an unsafe request without a token", async () => {
        const fetchImplementation = vi.fn();
        const apiFetch = createSyncFactorsApiFetch(fetchImplementation, () => null);

        await expect(apiFetch("/api/runs", { method: "POST" }))
            .rejects.toThrow("antiforgery token is unavailable");
        expect(fetchImplementation).not.toHaveBeenCalled();
    });
});
