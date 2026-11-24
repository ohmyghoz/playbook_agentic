// Pattern: thinking indicator. No event is needed from the agent: while the run is active (not
// waiting for the analyst, not finished) and nothing has arrived for a few seconds, the UI says
// so, and changes its text after a longer silence. Thresholds come from data-thinking-after-ms and
// data-thinking-long-ms on the root. Partial: Views/Shared/Patterns/_ThinkingIndicator.cshtml
(function () {
    "use strict";

    AgentUi.register("thinking-indicator", ctx => {
        const box = ctx.part("thinking");
        const text = ctx.part("thinking-text");
        const after = Number(ctx.data.thinkingAfterMs) || 4000;
        const long = Number(ctx.data.thinkingLongMs) || 15000;
        let started = false;

        function tick() {
            const quiet = Date.now() - ctx.state.lastEventAt;
            const active = started && ctx.mode === "live" && !ctx.state.finished && !ctx.state.awaiting;
            if (!active || quiet < after) {
                box.classList.add("d-none");
                return;
            }
            box.classList.remove("d-none");
            text.textContent = quiet < long
                ? "Agent sedang berpikir…"
                : "Agent masih bekerja (" + Math.round(quiet / 1000) + " detik tanpa kabar). Model kadang perlu waktu lebih lama. "
                  + "Anda boleh menutup halaman ini; investigasi tetap berjalan.";
        }

        const timer = setInterval(tick, 500);
        return {
            onEvent(event) {
                if (event.type === "run_started") started = true;
                tick();
            },
            onFinish() {
                box.classList.add("d-none");
                clearInterval(timer);
            },
        };
    });
})();
