// Pattern: tool calls. Each call shows a friendly Indonesian label ("Sedang memeriksa broker
// summary…"), its state and summary; the technical name and arguments stay visible in small print.
// The counter compares data calls with maxToolCalls from run_started.
// Partial: Views/Shared/Patterns/_ToolCalls.cshtml
(function () {
    "use strict";

    // tool name -> [while running, when finished]
    const labels = {
        idx_stockbit_status: ["Sedang memeriksa sesi Stockbit…", "Sesi Stockbit"],
        idx_quote: ["Sedang mengambil harga terakhir…", "Harga terakhir"],
        idx_history: ["Sedang mengambil riwayat harga dan volume…", "Riwayat harga dan volume"],
        idx_news: ["Sedang membaca berita emiten…", "Berita emiten"],
        idx_stockbit_actions: ["Sedang memeriksa aksi korporasi…", "Aksi korporasi"],
        idx_corporate_actions: ["Sedang memeriksa aksi korporasi…", "Aksi korporasi"],
        idx_broker: ["Sedang memeriksa broker summary…", "Broker summary"],
        idx_bandar: ["Sedang menilai akumulasi bandar…", "Analisis bandar"],
        idx_foreign: ["Sedang memeriksa arus dana asing…", "Arus dana asing"],
        idx_stockbit_movements: ["Sedang memeriksa transaksi insider…", "Transaksi insider"],
        submit_memo: ["Draf memo menunggu keputusan analis…", "Draf memo"],
        submit_report: ["Draf laporan menunggu keputusan analis…", "Draf laporan"],
        idx_screen: ["Sedang menyaring saham ramai…", "Penyaringan saham"],
        idx_market_brokers: ["Sedang memeriksa peringkat broker pasar…", "Peringkat broker pasar"],
        idx_members: ["Sedang memeriksa daftar anggota bursa…", "Anggota bursa"],
        idx_technical: ["Sedang menghitung indikator teknikal…", "Indikator teknikal"],
        idx_ownership_ticker: ["Sedang membaca pemegang saham di atas 1%…", "Pemegang saham di atas 1%"],
        idx_ownership_graph: ["Sedang menelusuri jaringan kepemilikan…", "Jaringan kepemilikan"],
        idx_ownership_entity: ["Sedang membaca kepemilikan satu entitas…", "Kepemilikan entitas"],
        idx_ownership_changes: ["Sedang memeriksa perubahan kepemilikan…", "Perubahan kepemilikan"],
    };

    function label(name, running) {
        const pair = labels[name];
        if (!pair) return running ? "Sedang mengambil data (" + name + ")…" : "Data " + name;
        return running ? pair[0] : pair[1];
    }

    function formatArgs(args) {
        const parts = Object.entries(args || {}).map(([k, v]) => k + "=" + (typeof v === "object" ? JSON.stringify(v) : v));
        return parts.length ? parts.join(", ") : "tanpa parameter";
    }

    AgentUi.register("tool-calls", ctx => {
        const { el } = ctx.helpers;
        const list = ctx.part("tool-calls");
        const count = ctx.part("tool-count");
        let max = null;
        let started = 0;

        function updateCount() {
            count.textContent = max ? started + " / " + max : String(started);
        }

        return {
            onEvent(event) {
                const p = event.payload;
                if (event.type === "run_started") {
                    max = p.maxToolCalls;
                    updateCount();
                } else if (event.type === "tool_call_started") {
                    const li = el("li", "tool-call running");
                    li.id = "tool-" + p.toolCallId;
                    li.dataset.toolCallId = p.toolCallId;
                    li.dataset.toolName = p.toolName;
                    const head = el("div", "tool-head");
                    const icon = el("span", "tool-icon");
                    icon.setAttribute("aria-hidden", "true");
                    head.append(icon, el("span", "tool-label", label(p.toolName, true)), el("span", "tool-state", "berjalan"));
                    const technical = el("div", "tool-args");
                    technical.append(el("code", null, p.toolName),
                        document.createTextNode(" · " + (p.toolName === "submit_memo" ? "draf memo" : formatArgs(p.arguments))));
                    li.append(head, technical, el("div", "tool-summary"));
                    list.append(li);
                    if (p.toolName !== "submit_memo") started++;
                    updateCount();
                } else if (event.type === "tool_call_finished") {
                    const li = list.querySelector("[data-tool-call-id='" + CSS.escape(p.toolCallId) + "']");
                    if (!li) return;
                    li.classList.remove("running");
                    li.classList.add(p.status === "ok" ? "ok" : "failed");
                    li.querySelector(".tool-label").textContent = label(p.toolName, false);
                    li.querySelector(".tool-state").textContent = p.status === "ok" ? "selesai" : "gagal";
                    li.querySelector(".tool-summary").textContent = p.summary;
                }
            },
            reset() {
                list.textContent = "";
                started = 0;
                max = null;
                updateCount();
            },
        };
    });

    window.AgentUi.toolLabel = label;
})();
