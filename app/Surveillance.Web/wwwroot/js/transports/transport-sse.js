// Server-Sent Events: one long GET per connection. The server replays stored events after the
// last id, then follows live events. EventSource reconnects by itself and sends Last-Event-ID,
// waiting the retry: interval the server sets. "ping" events keep proxies from closing the line.
(function () {
    "use strict";

    const eventTypes = ["run_started", "step_started", "token", "tool_call_started", "tool_call_finished",
        "approval_required", "worker_started", "worker_finished", "done", "error"];

    window.AgentTransports.register("sse", function (options, stats, deliver) {
        let source = null;

        function stop() {
            if (source) source.close();
            source = null;
        }

        return {
            name: "sse",
            start() {
                const after = options.lastSequence();
                source = new EventSource(options.eventsUrl + (after > 0 ? "?after=" + after : ""));
                source.onopen = () => {
                    stats.requests++;
                    options.onStatus("Terhubung (SSE)");
                };
                source.onerror = () => {
                    if (source) options.onStatus("Koneksi terputus, menyambung ulang...");
                };
                for (const type of eventTypes) {
                    source.addEventListener(type, e => {
                        if (deliver(JSON.parse(e.data))) stop();
                    });
                }
            },
            stop,
        };
    });
})();
