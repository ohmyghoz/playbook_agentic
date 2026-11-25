// Host for the agent UI patterns. A root element ([data-agent-ui]) holds one or more pattern
// partials; each pattern module registers here and receives every catalog event of the case.
//
//   AgentUi.register(name, ctx => ({ onEvent(event, ctx) {...}, onFinish(ctx) {...} }))
//   AgentUi.mount(root)   // creates the patterns found in root ([data-pattern]) and returns the host
//
// ctx gives a module what it may need, and nothing about which agent solution runs:
//   ctx.root, ctx.part(name) (element with data-part=name inside root), ctx.mode ("live" | "replay"),
//   ctx.interactive (live and not finished on load), ctx.antiforgery, ctx.data (root's data-* values),
//   ctx.state ({ finished, awaiting, lastType, lastEventAt }), ctx.helpers (shared text and formatting).
// The host drops events at or below the last sequence, keeps the run status, and connects the
// transport (js/transports/) or plays stored events back.
(function () {
    "use strict";

    const factories = {};

    const statusText = {
        running: "Berjalan",
        awaiting: "Menunggu keputusan analis",
        approved: "Memo disetujui",
        rejected: "Memo ditolak",
        cancelled: "Dihentikan",
        failed: "Gagal",
    };

    function mount(root) {
        const tokenInput = root.querySelector("input[name='__RequestVerificationToken']")
            || document.querySelector("input[name='__RequestVerificationToken']");
        const ctx = {
            root,
            mode: root.dataset.mode || "live",
            data: root.dataset,
            antiforgery: tokenInput ? tokenInput.value : "",
            state: { finished: false, awaiting: false, lastType: null, lastEventAt: Date.now() },
            part: name => root.querySelector("[data-part='" + name + "']"),
            helpers: window.AgentUi.helpers,
        };
        ctx.interactive = ctx.mode === "live" && root.dataset.terminal !== "true";

        const names = [...root.querySelectorAll("[data-pattern]")].map(e => e.dataset.pattern);
        const patterns = names.filter(n => factories[n]).map(n => factories[n](ctx));
        let lastSequence = 0;
        let transport = null;
        const statusEl = ctx.part("status");
        const connectionEl = ctx.part("connection");

        // A use case 2 job (data-kind="job") submits a report, not a memo.
        const jobText = { approved: "Laporan disetujui", rejected: "Laporan ditolak" };

        function setStatus(key) {
            if (!statusEl) return;
            statusEl.textContent = (root.dataset.kind === "job" && jobText[key]) || statusText[key];
            statusEl.className = "status status-" + key;
        }

        function finish() {
            ctx.state.finished = true;
            if (transport) transport.stop();
            if (connectionEl) connectionEl.textContent = "";
            for (const p of patterns) if (p.onFinish) p.onFinish(ctx);
        }

        function handle(event) {
            if (event.sequence <= lastSequence) return; // already shown (replay or reconnect overlap)
            lastSequence = event.sequence;
            ctx.state.lastEventAt = Date.now();
            const p = event.payload;

            switch (event.type) {
                case "run_started":
                case "tool_call_finished":
                    ctx.state.awaiting = false;
                    if (!ctx.state.finished) setStatus("running");
                    break;
                case "approval_required":
                    ctx.state.awaiting = true;
                    setStatus("awaiting");
                    break;
                case "done":
                    ctx.state.awaiting = false;
                    setStatus(p.outcome);
                    break;
                case "error":
                    ctx.state.awaiting = false;
                    setStatus("failed");
                    break;
            }

            for (const pattern of patterns) if (pattern.onEvent) pattern.onEvent(event, ctx);
            ctx.state.lastType = event.type;
            if (event.type === "done" || event.type === "error") finish();
        }

        function reset() {
            lastSequence = 0;
            ctx.state.finished = false;
            ctx.state.awaiting = false;
            ctx.state.lastType = null;
            for (const p of patterns) if (p.reset) p.reset(ctx);
        }

        async function loadAll() {
            const response = await fetch(root.dataset.eventsJsonUrl);
            return response.json();
        }

        async function replay(speed) {
            const events = await loadAll();
            reset();
            for (let i = 0; i < events.length; i++) {
                if (speed > 0 && i > 0) {
                    const gap = (Date.parse(events[i].timestamp) - Date.parse(events[i - 1].timestamp)) / speed;
                    await new Promise(r => setTimeout(r, Math.min(Math.max(gap, 0), 2000)));
                }
                handle(events[i]);
            }
        }

        function connect() {
            transport = AgentTransports.create(root.dataset.transport || "sse", {
                caseId: root.dataset.caseId,
                eventsUrl: root.dataset.eventsUrl,
                eventsJsonUrl: root.dataset.eventsJsonUrl,
                hubUrl: root.dataset.hubUrl,
                pollingIntervalMs: Number(root.dataset.pollingInterval) || 2000,
                lastSequence: () => lastSequence,
                onEvent: handle,
                onStatus: text => {
                    if (!ctx.state.finished && connectionEl) connectionEl.textContent = text;
                },
            });
            transport.start();
        }

        function start() {
            if (ctx.mode === "replay") {
                const speed = ctx.part("replay-speed");
                const button = ctx.part("replay-start");
                if (button) button.addEventListener("click", () => replay(Number(speed.value)));
                replay(speed ? Number(speed.value) : 0);
            } else if (root.dataset.terminal === "true") {
                loadAll().then(events => events.forEach(handle));
            } else {
                connect();
            }
        }

        return { ctx, handle, start, patterns: names };
    }

    window.AgentUi = {
        register(name, factory) {
            factories[name] = factory;
        },
        mount,
        helpers: {
            // Builds an element with optional class and text.
            el(tag, className, text) {
                const node = document.createElement(tag);
                if (className) node.className = className;
                if (text !== undefined && text !== null) node.textContent = text;
                return node;
            },
            // Data timestamps are UTC; analysts read them in WIB.
            formatTime(iso) {
                if (!iso) return "-";
                return new Date(iso).toLocaleString("id-ID", {
                    timeZone: "Asia/Jakarta", day: "numeric", month: "long", year: "numeric",
                    hour: "2-digit", minute: "2-digit", second: "2-digit",
                }) + " WIB";
            },
        },
    };
})();
