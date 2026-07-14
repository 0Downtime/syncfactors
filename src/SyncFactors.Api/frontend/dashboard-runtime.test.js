import { describe, expect, it, vi } from "vitest";
import { createRealtimeLifecycle } from "./dashboard-runtime.js";

function createConnection() {
    const handlers = {};
    return {
        handlers,
        start: vi.fn().mockResolvedValue(undefined),
        stop: vi.fn(async function () { handlers.close?.(); }),
        on: vi.fn(function (name, handler) { handlers[name] = handler; }),
        onreconnecting: vi.fn(function (handler) { handlers.reconnecting = handler; }),
        onreconnected: vi.fn(function (handler) { handlers.reconnected = handler; }),
        onclose: vi.fn(function (handler) { handlers.close = handler; })
    };
}

describe("createRealtimeLifecycle", function () {
    it("falls back to polling while reconnecting and restores live state after reconnection", async function () {
        const connection = createConnection();
        const setLiveState = vi.fn();
        const startFallbackPolling = vi.fn();
        const stopFallbackPolling = vi.fn();
        const loadDashboard = vi.fn();
        const loadHealth = vi.fn();
        const lifecycle = createRealtimeLifecycle({
            createConnection: () => connection,
            handleEvent: vi.fn(),
            setLiveState,
            startFallbackPolling,
            stopFallbackPolling,
            loadDashboard,
            loadHealth,
            scheduleReconnect: vi.fn()
        });

        await lifecycle.start();
        connection.handlers.reconnecting();
        connection.handlers.reconnected();

        expect(connection.on).toHaveBeenCalledWith("dashboardEvent", expect.any(Function));
        expect(setLiveState).toHaveBeenNthCalledWith(2, "live", "Push updates are active.");
        expect(startFallbackPolling).toHaveBeenCalledWith({ immediate: false });
        expect(stopFallbackPolling).toHaveBeenCalledTimes(2);
        expect(loadDashboard).toHaveBeenCalledOnce();
        expect(loadHealth).toHaveBeenCalledOnce();
    });

    it("resets the connection and schedules a retry when the live channel closes", async function () {
        const connection = createConnection();
        const scheduleReconnect = vi.fn();
        const lifecycle = createRealtimeLifecycle({
            createConnection: () => connection,
            handleEvent: vi.fn(),
            setLiveState: vi.fn(),
            startFallbackPolling: vi.fn(),
            stopFallbackPolling: vi.fn(),
            loadDashboard: vi.fn(),
            loadHealth: vi.fn(),
            scheduleReconnect
        });

        await lifecycle.start();
        connection.handlers.close();

        expect(lifecycle.connection).toBeNull();
        expect(scheduleReconnect).toHaveBeenCalledOnce();
    });

    it("disposes the active connection during browser shutdown without scheduling a reconnect", async function () {
        const connection = createConnection();
        connection.stop.mockImplementation(async function () {
            connection.handlers.close();
        });
        const scheduleReconnect = vi.fn();
        const lifecycle = createRealtimeLifecycle({
            createConnection: () => connection,
            handleEvent: vi.fn(),
            setLiveState: vi.fn(),
            startFallbackPolling: vi.fn(),
            stopFallbackPolling: vi.fn(),
            loadDashboard: vi.fn(),
            loadHealth: vi.fn(),
            scheduleReconnect
        });

        await lifecycle.start();
        await lifecycle.dispose();

        expect(connection.stop).toHaveBeenCalledOnce();
        expect(lifecycle.connection).toBeNull();
        expect(scheduleReconnect).not.toHaveBeenCalled();
    });

    it("ignores late realtime callbacks after browser shutdown", async function () {
        const connection = createConnection();
        const setLiveState = vi.fn();
        const startFallbackPolling = vi.fn();
        const stopFallbackPolling = vi.fn();
        const loadDashboard = vi.fn();
        const loadHealth = vi.fn();
        const lifecycle = createRealtimeLifecycle({
            createConnection: () => connection,
            handleEvent: vi.fn(),
            setLiveState,
            startFallbackPolling,
            stopFallbackPolling,
            loadDashboard,
            loadHealth,
            scheduleReconnect: vi.fn()
        });

        await lifecycle.start();
        await lifecycle.dispose();
        setLiveState.mockClear();
        startFallbackPolling.mockClear();
        stopFallbackPolling.mockClear();

        connection.handlers.reconnecting();
        connection.handlers.reconnected();

        expect(setLiveState).not.toHaveBeenCalled();
        expect(startFallbackPolling).not.toHaveBeenCalled();
        expect(stopFallbackPolling).not.toHaveBeenCalled();
        expect(loadDashboard).not.toHaveBeenCalled();
        expect(loadHealth).not.toHaveBeenCalled();
    });

    it("does not restore live state when a start completes after browser shutdown", async function () {
        const connection = createConnection();
        let resolveStart;
        connection.start.mockImplementation(function () {
            return new Promise(function (resolve) {
                resolveStart = resolve;
            });
        });
        const setLiveState = vi.fn();
        const stopFallbackPolling = vi.fn();
        const lifecycle = createRealtimeLifecycle({
            createConnection: () => connection,
            handleEvent: vi.fn(),
            setLiveState,
            startFallbackPolling: vi.fn(),
            stopFallbackPolling,
            loadDashboard: vi.fn(),
            loadHealth: vi.fn(),
            scheduleReconnect: vi.fn()
        });

        const starting = lifecycle.start();
        await lifecycle.dispose();
        setLiveState.mockClear();
        stopFallbackPolling.mockClear();
        resolveStart();
        await starting;

        expect(setLiveState).not.toHaveBeenCalled();
        expect(stopFallbackPolling).not.toHaveBeenCalled();
    });

    it("ignores dashboard events received after browser shutdown", async function () {
        const connection = createConnection();
        const handleEvent = vi.fn();
        const lifecycle = createRealtimeLifecycle({
            createConnection: () => connection,
            handleEvent,
            setLiveState: vi.fn(),
            startFallbackPolling: vi.fn(),
            stopFallbackPolling: vi.fn(),
            loadDashboard: vi.fn(),
            loadHealth: vi.fn(),
            scheduleReconnect: vi.fn()
        });

        await lifecycle.start();
        await lifecycle.dispose();
        connection.handlers.dashboardEvent({ type: "dashboardSnapshotUpdated" });

        expect(handleEvent).not.toHaveBeenCalled();
    });

    it("does not start fallback work when shutdown races a failed connection attempt", async function () {
        const connection = createConnection();
        let rejectStart;
        connection.start.mockImplementation(function () {
            return new Promise(function (_, reject) {
                rejectStart = reject;
            });
        });
        const startFallbackPolling = vi.fn();
        const scheduleReconnect = vi.fn();
        const lifecycle = createRealtimeLifecycle({
            createConnection: () => connection,
            handleEvent: vi.fn(),
            setLiveState: vi.fn(),
            startFallbackPolling,
            stopFallbackPolling: vi.fn(),
            loadDashboard: vi.fn(),
            loadHealth: vi.fn(),
            scheduleReconnect
        });

        const starting = lifecycle.start();
        await lifecycle.dispose();
        rejectStart(new Error("connection failed"));
        await starting;

        expect(startFallbackPolling).not.toHaveBeenCalled();
        expect(scheduleReconnect).not.toHaveBeenCalled();
    });
});
