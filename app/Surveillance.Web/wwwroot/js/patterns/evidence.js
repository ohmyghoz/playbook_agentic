// Pattern: evidence. A link in a memo finding ([data-evidence-call]) opens the tool call behind it:
// friendly name, source and data time from tool_call_finished.source, the summary, and the raw
// output, which the browser fetches only now from /investigations/{id}/tools/{callId}/preview.
// Partial: Views/Shared/Patterns/_Evidence.cshtml
(function () {
    "use strict";

    const providerText = { yahoo: "Yahoo Finance", stockbit: "Stockbit", ipot: "IPOT (Indo Premier)", idx: "Bursa Efek Indonesia" };

    AgentUi.register("evidence", ctx => {
        const panel = ctx.part("evidence");
        const calls = {};

        function show(callId) {
            const call = calls[callId];
            const label = window.AgentUi.toolLabel ? window.AgentUi.toolLabel(call ? call.toolName : callId, false) : callId;
            ctx.part("evidence-title").textContent = "Bukti: " + label;
            ctx.part("evidence-tool").textContent = call ? call.toolName + " (" + callId + ")" : callId;
            const source = call && call.source;
            ctx.part("evidence-source").textContent = source
                ? (providerText[source.provider] || source.provider || "tidak disebut") + " lewat server MCP " + source.server
                  + (source.stale ? " (salinan cache yang sudah kedaluwarsa)" : "")
                : "Tidak tersedia untuk panggilan ini.";
            ctx.part("evidence-time").textContent = source && source.fetchedAt ? ctx.helpers.formatTime(source.fetchedAt) : "-";
            ctx.part("evidence-summary").textContent = call ? call.summary : "-";
            const preview = ctx.part("evidence-preview");
            preview.textContent = "Memuat data mentah…";
            panel.classList.remove("d-none");
            panel.focus();

            const url = ctx.data.previewUrlTemplate.replace("__call__", encodeURIComponent(callId));
            fetch(url, { headers: { Accept: "application/json" } })
                .then(r => r.json().then(body => ({ ok: r.ok, body })))
                .then(({ ok, body }) => {
                    preview.textContent = ok ? prettify(body.preview) : body.message || "Data mentah tidak tersedia.";
                })
                .catch(() => (preview.textContent = "Tidak bisa menghubungi server. Coba lagi."));
        }

        function prettify(text) {
            try {
                return JSON.stringify(JSON.parse(text), null, 2);
            } catch {
                return text; // cut at 8 KB or not JSON: show as is
            }
        }

        ctx.root.addEventListener("click", e => {
            const link = e.target.closest("[data-evidence-call]");
            if (!link || !ctx.root.contains(link)) return;
            e.preventDefault();
            show(link.dataset.evidenceCall);
        });
        ctx.part("evidence-close").addEventListener("click", () => panel.classList.add("d-none"));

        return {
            onEvent(event) {
                if (event.type === "tool_call_finished") calls[event.payload.toolCallId] = event.payload;
            },
        };
    });
})();
