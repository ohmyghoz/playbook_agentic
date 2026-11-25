// Pattern: error banner and end-of-run banner. Every error says what happened (message from the
// agent) and what to do next: a retry button for retryable errors, a concrete next step for the
// others. Partial: Views/Shared/Patterns/_ErrorBanner.cshtml
(function () {
    "use strict";

    // Next steps for errors that a retry with the same data would not fix.
    const nextSteps = {
        step_limit_exceeded: "Langkah berikutnya: buka panggilan data di kanan untuk melihat apa yang sudah terkumpul, "
            + "lalu mulai investigasi baru dengan catatan yang lebih spesifik, atau lanjutkan penelaahan secara manual.",
        revision_limit_exceeded: "Langkah berikutnya: draf terakhir tetap bisa dibaca di atas. Bila masih perlu perubahan, "
            + "mulai investigasi baru dan tulis arahan revisi di catatan.",
        invalid_input: "Langkah berikutnya: periksa kode saham dan tanggal anomali, lalu mulai investigasi baru.",
        internal: "Langkah berikutnya: laporkan nomor kasus di judul halaman ke tim aplikasi. Data yang sudah masuk tetap tersimpan.",
        model_error: "Langkah berikutnya: tunggu beberapa menit, lalu mulai investigasi baru.",
        mcp_unavailable: "Langkah berikutnya: pastikan server data pasar berjalan, lalu mulai investigasi baru.",
    };
    const doneText = {
        approved: ["alert-success", "Memo disetujui analis. Investigasi selesai."],
        rejected: ["alert-secondary", "Memo ditolak analis. Investigasi selesai."],
        cancelled: ["alert-warning", "Investigasi dihentikan. Event yang sudah masuk tetap tersimpan."],
    };

    const jobDoneText = {
        approved: ["alert-success", "Laporan disetujui analis. Job selesai."],
        rejected: ["alert-secondary", "Laporan ditolak analis. Job selesai."],
        cancelled: ["alert-warning", "Job dihentikan. Temuan yang sudah masuk tetap tersimpan."],
    };

    AgentUi.register("error-banner", ctx => {
        // The agent ends both a stop and an expiry with done(cancelled); the application knows which.
        async function explainCancel(p) {
            try {
                const response = await fetch(ctx.data.claimUrl, { headers: { Accept: "application/json" } });
                const state = await response.json();
                if (state.status !== "expired") return;
                doneBanner.className = "alert alert-warning";
                doneBanner.textContent = "Draf kedaluwarsa: " + (state.decision && state.decision.comment || "batas waktu keputusan terlewati.")
                    + " Panggilan data: " + p.toolCallsUsed + ".";
                const status = ctx.part("status");
                if (status) {
                    status.textContent = state.statusLabel;
                    status.className = "status status-expired";
                }
                ctx.root.dispatchEvent(new CustomEvent("agentui:case-state", { detail: state }));
            } catch {
                // keep the generic text
            }
        }

        const banner = ctx.part("error-banner");
        const doneBanner = ctx.part("done-banner");
        const retry = ctx.part("retry-form");
        // Pages that mount a root after the case exists (/lab/patterns) pass the retry URL as data.
        if (ctx.data.retryUrl) retry.action = ctx.data.retryUrl;
        return {
            onEvent(event) {
                const p = event.payload;
                if (event.type === "error") {
                    ctx.part("error-message").textContent = p.message;
                    const canRetry = p.retryable && ctx.mode === "live";
                    ctx.part("error-next").textContent = canRetry ? "" : nextSteps[p.code] || nextSteps.internal;
                    retry.classList.toggle("d-none", !canRetry);
                    banner.classList.remove("d-none");
                } else if (event.type === "done") {
                    const [cls, text] = (ctx.data.kind === "job" && jobDoneText[p.outcome]) || doneText[p.outcome];
                    doneBanner.className = "alert " + cls;
                    doneBanner.textContent = text + " Panggilan data: " + p.toolCallsUsed + ".";
                    if (p.outcome === "cancelled" && ctx.data.claimUrl && ctx.mode === "live") explainCancel(p);
                }
            },
            reset() {
                banner.classList.add("d-none");
                doneBanner.className = "alert d-none";
            },
        };
    });
})();
