// Polling: ask events.json?after=<last sequence> every few seconds. Works through any proxy and
// needs no open connection, at the cost of one request per interval and up to one interval of delay.
(function () {
    "use strict";

    window.AgentTransports.register("polling", function (options, stats, deliver) {
        let timer = null;
        let stopped = false;
        const interval = options.pollingIntervalMs || 2000;

        async function poll() {
            if (stopped) return;
            let finished = false;
            try {
                stats.requests++;
                const response = await fetch(options.eventsJsonUrl + "?after=" + options.lastSequence(), {
                    headers: { Accept: "application/json" },
                });
                if (!response.ok) throw new Error("HTTP " + response.status);
                const events = await response.json();
                options.onStatus("Terhubung (polling tiap " + interval / 1000 + " detik)");
                for (const event of events) {
                    if (deliver(event)) finished = true;
                }
            } catch {
                options.onStatus("Koneksi terputus, mencoba lagi...");
            }
            if (finished) {
                stopped = true;
            } else if (!stopped) {
                timer = setTimeout(poll, interval);
            }
        }

        return {
            name: "polling",
            start() {
                stopped = false;
                poll();
            },
            stop() {
                stopped = true;
                clearTimeout(timer);
            },
        };
    });
})();
