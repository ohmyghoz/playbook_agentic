// /lab/transport: the same case through SSE and polling, with request counts and delays.
(function () {
    "use strict";

    const root = document.getElementById("lab-transport");
    if (!root) return;

    for (const panel of root.querySelectorAll("[data-transport]")) {
        const name = panel.dataset.transport;
        const log = panel.querySelector(".panel-log");
        const statsEl = panel.querySelector(".panel-stats");
        const statusEl = panel.querySelector(".panel-status");
        const interval = panel.querySelector("#polling-interval");
        let last = 0;
        let ended = false;
        let transport = null;
        let totals = { requests: 0, events: 0, duplicates: 0, liveEvents: 0, delaySumMs: 0 };

        function addTotals(stats) {
            for (const key of Object.keys(totals)) totals[key] += stats[key];
        }

        function render() {
            const s = transport ? transport.stats : null;
            const sum = key => totals[key] + (s ? s[key] : 0);
            const live = sum("liveEvents");
            const delay = live > 0 ? Math.round(sum("delaySumMs") / live) + " ms" : "-";
            statsEl.textContent = "Permintaan: " + sum("requests") + " · Event diterima: " + sum("events")
                + " · Duplikat dibuang: " + sum("duplicates") + " · Jeda rata-rata: " + delay
                + " · Sequence terakhir: " + last;
        }

        function start() {
            transport = AgentTransports.create(name, {
                caseId: root.dataset.caseId,
                eventsUrl: root.dataset.eventsUrl,
                eventsJsonUrl: root.dataset.eventsJsonUrl,
                pollingIntervalMs: interval ? Number(interval.value) : undefined,
                lastSequence: () => last,
                onEvent: event => {
                    if (event.sequence <= last) return; // duplicate; counted by the transport
                    if (event.sequence !== last + 1) {
                        const gap = document.createElement("li");
                        gap.className = "text-danger";
                        gap.textContent = "Lompatan: sequence " + (last + 1) + " sampai " + (event.sequence - 1) + " hilang";
                        log.append(gap);
                    }
                    last = event.sequence;
                    ended = event.type === "done" || event.type === "error";
                    const item = document.createElement("li");
                    item.value = event.sequence;
                    item.textContent = event.type;
                    log.append(item);
                    render();
                },
                onStatus: text => (statusEl.textContent = text),
            });
            transport.start();
        }

        function restart() {
            if (ended) {
                statusEl.textContent = "Run sudah selesai; tidak ada yang perlu disambung ulang.";
                return;
            }
            if (transport) {
                transport.stop();
                addTotals(transport.stats);
            }
            statusEl.textContent = "Terputus";
            setTimeout(start, 500);
        }

        panel.querySelector(".panel-reconnect").addEventListener("click", restart);
        if (interval) interval.addEventListener("change", restart);
        setInterval(render, 1000);
        start();
    }
})();
