// Pattern: case claim. While a draft waits, the page shows who holds the case. Decision buttons
// stay disabled until this analyst takes it; activity on the page renews the claim (at most once a
// minute), and a claim lapses on the server after a period without activity. The state is read
// from /investigations/{id}/claim every few seconds, so a supervisor's reassignment shows up.
// Partial: Views/Shared/Patterns/_CaseClaim.cshtml
(function () {
    "use strict";

    AgentUi.register("case-claim", ctx => {
        const box = ctx.part("claim");
        const text = ctx.part("claim-text");
        const take = ctx.part("claim-take");
        const release = ctx.part("claim-release");
        let mine = false;
        let lastRenew = 0;
        let timer = null;

        if (!ctx.interactive || !ctx.data.claimUrl) {
            box.remove();
            return {};
        }

        function applyButtons() {
            for (const button of ctx.root.querySelectorAll(".approval-form button")) {
                if (!button.closest(".approval-form").dataset.closed) button.disabled = !mine;
            }
        }

        function show(state) {
            mine = state.mine;
            box.classList.toggle("d-none", !ctx.state.awaiting);
            take.classList.toggle("d-none", mine || !!state.holder);
            release.classList.toggle("d-none", !mine);
            text.textContent = mine
                ? "Kasus ini Anda pegang. Analis lain tidak bisa memutuskannya selama Anda aktif."
                : state.holder
                    ? "Sedang ditelaah oleh " + state.holder + ". Keputusan hanya bisa dikirim oleh pemegang kasus."
                    : "Belum ada yang memegang kasus ini. Ambil kasus sebelum memutuskan.";
            if (state.message && state.message !== text.textContent && !mine) text.textContent = state.message;
            applyButtons();
        }

        async function post(url) {
            const data = new FormData();
            data.set("__RequestVerificationToken", ctx.antiforgery);
            const response = await fetch(url, { method: "POST", body: data });
            const body = await response.json().catch(() => ({}));
            if (response.ok) {
                show(body);
            } else {
                await refresh();
                if (body.message) text.textContent = body.message;
            }
        }

        async function refresh() {
            if (!ctx.state.awaiting) {
                box.classList.add("d-none");
                return;
            }
            try {
                const response = await fetch(ctx.data.claimUrl, { headers: { Accept: "application/json" } });
                if (response.ok) show(await response.json());
            } catch {
                // keep the last known state; the next refresh tries again
            }
        }

        take.addEventListener("click", () => post(ctx.data.claimUrl));
        release.addEventListener("click", () => post(ctx.data.releaseUrl));
        // Any interaction renews this analyst's claim, so an active reviewer does not lose the case.
        ctx.root.addEventListener("input", () => {
            if (mine && Date.now() - lastRenew > 60000) {
                lastRenew = Date.now();
                post(ctx.data.claimUrl);
            }
        });

        return {
            onEvent(event) {
                if (event.type === "approval_required") {
                    refresh();
                    if (!timer) timer = setInterval(refresh, 5000);
                } else if (event.type === "tool_call_finished" || event.type === "done" || event.type === "error") {
                    box.classList.add("d-none");
                }
            },
            onFinish() {
                clearInterval(timer);
                box.classList.add("d-none");
            },
        };
    });
})();
