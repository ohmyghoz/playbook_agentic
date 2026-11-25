// Pattern: worker panels for a multi-agent run (CHANGE-002). worker_started opens a panel for its
// workerId; tool calls whose agentName is that worker fill it; worker_finished closes it with its
// status, summary and the new leads it proposes. Orchestrator events are left to the other
// patterns (narration, approval card). Partial: Views/Shared/Patterns/_WorkerPanels.cshtml
(function () {
    "use strict";

    const roleText = { stock: "Worker saham", broker: "Worker broker", ownership: "Worker kepemilikan" };
    const statusText = { ok: "selesai", error: "gagal", cancelled: "dihentikan", limit_reached: "batas langkah tercapai" };
    const leadText = { stock: "saham", broker: "broker" };

    AgentUi.register("worker-panels", ctx => {
        const { el } = ctx.helpers;
        const box = ctx.part("workers");
        const count = ctx.part("worker-count");
        const panels = {};
        let finished = 0;

        function updateCount() {
            const total = Object.keys(panels).length;
            count.textContent = total ? finished + " / " + total + " selesai" : "";
        }

        function label(name, running) {
            return window.AgentUi.toolLabel ? window.AgentUi.toolLabel(name, running) : name;
        }

        return {
            onEvent(event) {
                const p = event.payload;
                const panel = event.agentName ? panels[event.agentName] : null;
                if (event.type === "worker_started") {
                    const card = el("article", "worker card mb-2 running");
                    card.dataset.workerId = p.workerId;
                    const head = el("div", "card-header py-1 d-flex justify-content-between align-items-center small");
                    const title = el("strong", null, (roleText[p.role] || p.role) + ": " + p.target);
                    const state = el("span", "worker-state", "berjalan" + (p.depth > 0 ? " · kedalaman " + p.depth : ""));
                    head.append(title, state);
                    const body = el("div", "card-body py-2 small");
                    if (p.reason) body.append(el("div", "text-muted mb-1", p.reason));
                    const tools = el("ol", "tool-calls list-unstyled mb-1");
                    body.append(tools, el("div", "worker-summary"), el("ul", "worker-leads mb-0"));
                    card.append(head, body);
                    box.append(card);
                    panels[p.workerId] = { card, tools, state, depth: p.depth };
                    updateCount();
                } else if (event.type === "tool_call_started" && panel) {
                    const li = el("li", "tool-call running");
                    li.id = "tool-" + p.toolCallId;
                    li.dataset.toolCallId = p.toolCallId;
                    const head = el("div", "tool-head");
                    const icon = el("span", "tool-icon");
                    icon.setAttribute("aria-hidden", "true");
                    head.append(icon, el("span", "tool-label", label(p.toolName, true)), el("span", "tool-state", "berjalan"));
                    li.append(head, el("div", "tool-summary"));
                    panel.tools.append(li);
                } else if (event.type === "tool_call_finished" && panel) {
                    const li = panel.tools.querySelector("[data-tool-call-id='" + CSS.escape(p.toolCallId) + "']");
                    if (!li) return;
                    li.classList.remove("running");
                    li.classList.add(p.status === "ok" ? "ok" : "failed");
                    li.querySelector(".tool-label").textContent = label(p.toolName, false);
                    li.querySelector(".tool-state").textContent = p.status === "ok" ? "selesai" : "gagal";
                    li.querySelector(".tool-summary").textContent = p.summary;
                } else if (event.type === "worker_finished" && panel) {
                    panel.card.classList.remove("running");
                    panel.card.classList.add(p.status === "ok" ? "ok" : "failed");
                    panel.state.textContent = (statusText[p.status] || p.status) + (panel.depth > 0 ? " · kedalaman " + panel.depth : "");
                    panel.card.querySelector(".worker-summary").textContent = p.summary;
                    const leads = panel.card.querySelector(".worker-leads");
                    for (const lead of p.leads || []) {
                        leads.append(el("li", null, "Jejak baru (" + (leadText[lead.kind] || lead.kind) + " " + lead.target + "): " + lead.reason));
                    }
                    finished++;
                    updateCount();
                }
            },
            reset() {
                box.textContent = "";
                for (const k of Object.keys(panels)) delete panels[k];
                finished = 0;
                updateCount();
            },
        };
    });
})();
