// Pattern: workflow progress. step_started (CHANGE-001) moves the list of the five ASD stages:
// earlier stages become done, stages jumped over become "dilewati", and the run's end marks the
// current stage. Partial: Views/Shared/Patterns/_StepProgress.cshtml
(function () {
    "use strict";

    const order = ["measure", "explain", "deep_dive", "assess", "memo"];

    AgentUi.register("step-progress", ctx => {
        const items = Object.fromEntries([...ctx.part("steps").querySelectorAll("[data-step]")].map(li => [li.dataset.step, li]));
        let current = null;

        function mark(step, state, text) {
            const li = items[step];
            li.className = "step-" + state;
            li.querySelector(".step-state").textContent = text;
            if (state === "active") li.setAttribute("aria-current", "step");
            else li.removeAttribute("aria-current");
        }

        return {
            onEvent(event) {
                if (event.type === "step_started") {
                    const next = order.indexOf(event.payload.step);
                    const from = current === null ? 0 : order.indexOf(current) + 1;
                    if (current !== null) mark(current, "done", "selesai");
                    for (let i = from; i < next; i++) mark(order[i], "skipped", "dilewati");
                    current = event.payload.step;
                    mark(current, "active", "berjalan");
                } else if (event.type === "approval_required" && current === "memo") {
                    mark("memo", "active", "menunggu keputusan");
                } else if (event.type === "done" && current) {
                    const outcome = event.payload.outcome;
                    mark(current, outcome === "cancelled" ? "stopped" : "done",
                        outcome === "approved" ? "disetujui" : outcome === "rejected" ? "ditolak" : "dihentikan");
                } else if (event.type === "error" && current) {
                    mark(current, "stopped", "berhenti karena galat");
                }
            },
            reset() {
                current = null;
                for (const step of order) mark(step, "pending", "");
            },
        };
    });
})();
