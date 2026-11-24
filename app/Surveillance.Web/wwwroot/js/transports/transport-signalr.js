// SignalR: one connection (WebSocket when available) to /hubs/investigations. JoinCase adds the
// connection to the case's group and returns stored events after the last sequence; live events
// arrive as "event" messages. After a reconnect the page joins again from its last sequence.
// Needs lib/microsoft-signalr/signalr.min.js on the page.
(function () {
    "use strict";

    window.AgentTransports.register("signalr", function (options, stats, deliver) {
        let connection = null;
        let stopped = false;
        let joining = false;
        let held = [];

        // Live events that arrive while JoinCase is in flight wait until the stored ones are shown.
        function receive(json) {
            const event = JSON.parse(json);
            if (joining) {
                held.push(event);
            } else if (deliver(event)) {
                stop();
            }
        }

        async function join() {
            joining = true;
            held = [];
            try {
                stats.requests++;
                const stored = await connection.invoke("JoinCase", options.caseId, options.lastSequence());
                const events = stored.map(json => JSON.parse(json)).concat(held).sort((a, b) => a.sequence - b.sequence);
                joining = false;
                for (const event of events) {
                    if (deliver(event)) {
                        stop();
                        return;
                    }
                }
            } finally {
                joining = false;
                held = [];
            }
        }

        async function connect() {
            try {
                stats.requests++;
                await connection.start();
                options.onStatus("Terhubung (SignalR)");
                await join();
            } catch {
                if (!stopped) {
                    options.onStatus("Koneksi terputus, mencoba lagi...");
                    setTimeout(connect, 5000);
                }
            }
        }

        function stop() {
            stopped = true;
            if (connection) connection.stop();
        }

        return {
            name: "signalr",
            start() {
                connection = new signalR.HubConnectionBuilder()
                    .withUrl(options.hubUrl)
                    .withAutomaticReconnect()
                    .build();
                connection.on("event", receive);
                connection.onreconnecting(() => options.onStatus("Koneksi terputus, menyambung ulang..."));
                connection.onreconnected(async () => {
                    options.onStatus("Terhubung (SignalR)");
                    await join();
                });
                connection.onclose(() => {
                    if (!stopped) {
                        options.onStatus("Koneksi terputus, mencoba lagi...");
                        setTimeout(connect, 5000);
                    }
                });
                connect();
            },
            stop,
        };
    });
})();
