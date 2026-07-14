export function createRealtimeLifecycle({
    createConnection,
    handleEvent,
    setLiveState,
    startFallbackPolling,
    stopFallbackPolling,
    loadDashboard,
    loadHealth,
    scheduleReconnect
}) {
    let connection = null;
    let startPromise = null;
    let disposed = false;

    function attachConnectionHandlers(nextConnection) {
        nextConnection.on("dashboardEvent", function (event) {
            if (!disposed) {
                handleEvent(event);
            }
        });
        nextConnection.onreconnecting(function () {
            if (disposed) {
                return;
            }

            setLiveState("reconnecting", "Live connection interrupted. Polling fallback is active.");
            startFallbackPolling({ immediate: false });
        });
        nextConnection.onreconnected(function () {
            if (disposed) {
                return;
            }

            setLiveState("live", "Push updates are active again.");
            stopFallbackPolling();
            void loadDashboard();
            void loadHealth();
        });
        nextConnection.onclose(function () {
            if (disposed) {
                return;
            }

            setLiveState("fallback", "Live connection is unavailable. Polling fallback remains active.");
            startFallbackPolling({ immediate: false });
            connection = null;
            startPromise = null;
            scheduleReconnect();
        });
    }

    function start() {
        if (startPromise) {
            return startPromise;
        }

        if (!connection) {
            connection = createConnection();
            attachConnectionHandlers(connection);
        }

        setLiveState("connecting", "Connecting to the live dashboard channel.");
        startPromise = connection.start()
            .then(function () {
                if (disposed) {
                    return;
                }

                setLiveState("live", "Push updates are active.");
                stopFallbackPolling();
            })
            .catch(function () {
                if (disposed) {
                    return;
                }

                setLiveState("fallback", "Live connection failed to start. Polling fallback remains active.");
                startFallbackPolling({ immediate: false });
                connection = null;
                scheduleReconnect();
            })
            .finally(function () {
                startPromise = null;
            });

        return startPromise;
    }

    async function dispose() {
        disposed = true;

        if (!connection) {
            return;
        }

        const activeConnection = connection;
        connection = null;
        startPromise = null;
        await activeConnection.stop();
    }

    return {
        start,
        dispose,
        get connection() {
            return connection;
        }
    };
}
