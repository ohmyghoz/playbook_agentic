// Pattern: stop button. Stopping ends the run with done(cancelled); it deletes nothing, which the
// help text says before the user clicks. The button disappears once the run has ended.
// Partial: Views/Shared/Patterns/_StopButton.cshtml
(function () {
    "use strict";

    AgentUi.register("stop-button", ctx => {
        const button = ctx.part("stop");
        const help = ctx.part("stop-help");
        if (!ctx.interactive) {
            button.closest("[data-pattern]").remove();
            return {};
        }

        button.addEventListener("click", async () => {
            button.disabled = true;
            button.textContent = "Menghentikan...";
            help.textContent = "Menunggu agent berhenti. Narasi, panggilan data, dan draf yang sudah masuk tetap tersimpan.";
            const data = new FormData();
            data.set("__RequestVerificationToken", ctx.antiforgery);
            try {
                const response = await fetch(ctx.data.stopUrl, { method: "POST", body: data });
                if (!response.ok) throw new Error("HTTP " + response.status);
            } catch {
                button.disabled = false;
                button.textContent = "Hentikan investigasi";
                help.textContent = "Tidak bisa menghubungi server. Coba lagi.";
            }
        });

        return {
            onFinish() {
                const container = button.closest("[data-pattern]");
                if (container) container.remove();
            },
        };
    });
})();
