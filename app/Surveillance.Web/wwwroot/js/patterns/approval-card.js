// Pattern: approval card. approval_required shows the memo with three decisions (approve, revise,
// reject; a comment is required for the last two). A newer draft replaces the pending card; the
// card closes when submit_memo finishes or the run ends, and keeps the decision that closed it,
// so the drafts above each other form the memo's version history. Findings link to their evidence
// ([data-evidence-call], see evidence.js). Partial: Views/Shared/Patterns/_ApprovalCard.cshtml
(function () {
    "use strict";

    const categoryText = {
        price_volume: "Harga dan volume", news: "Berita", corporate_action: "Aksi korporasi",
        broker: "Broker", bandar: "Bandar", foreign: "Asing", insider: "Insider",
    };
    const evidenceText = { strong: "Kuat", moderate: "Sedang", weak: "Lemah", insufficient: "Tidak cukup" };
    const recommendationText = { escalate: "Eskalasi", monitor: "Pantau", close: "Tutup" };
    const decisionText = { approve: "Disetujui", revise: "Revisi diminta", reject: "Ditolak", expire: "Kedaluwarsa" };

    AgentUi.register("approval-card", ctx => {
        const { el } = ctx.helpers;
        const list = ctx.part("approvals");
        const template = ctx.part("approval-template");
        let pending = null;
        let submitCallId = null;
        // Decisions already stored (rendered by the server), by approvalId, plus the ones made on this page.
        const decisions = {};
        for (const d of JSON.parse(ctx.data.decisions || "[]")) decisions[d.approvalId] = d;

        function resolve(text) {
            if (!pending) return;
            const form = pending.querySelector(".approval-form");
            if (form) {
                form.dataset.closed = "true";
                form.querySelectorAll("button, input, textarea").forEach(x => (x.disabled = true));
            }
            const d = decisions[pending.dataset.approvalId];
            pending.querySelector(".approval-state").textContent = d
                ? (decisionText[d.decision] || d.decision) + " oleh " + d.decidedBy + (d.comment ? ": \u201C" + d.comment + "\u201D" : "")
                : text || "Selesai";
            pending = null;
        }

        function renderMemo(container, memo) {
            container.append(el("p", "lead fs-6", memo.summary), el("p", "small text-muted", memo.anomaly.description),
                el("h3", "h6 mt-3", "Temuan"));
            const findings = el("ul", "findings");
            for (const f of memo.findings) {
                const li = el("li");
                li.append(el("span", "badge text-bg-light me-1", categoryText[f.category] || f.category), document.createTextNode(f.statement));
                const ev = el("div", "small text-muted");
                f.evidence.forEach((e, i) => {
                    if (i > 0) ev.append(document.createTextNode("; "));
                    const a = el("a", null, e.toolName);
                    a.href = "#tool-" + e.toolCallId;
                    a.dataset.evidenceCall = e.toolCallId;
                    ev.append("Bukti: ", a, document.createTextNode(" — " + e.detail));
                });
                li.append(ev);
                findings.append(li);
            }
            container.append(findings);

            if (memo.unavailableData.length) {
                container.append(el("h3", "h6 mt-3", "Data tidak tersedia"));
                const ul = el("ul", "small");
                for (const u of memo.unavailableData) ul.append(el("li", null, u.toolName + ": " + u.reason));
                container.append(ul);
            }

            const dl = el("dl", "row small mb-0 mt-3");
            const term = (label, value, note) => dl.append(el("dt", "col-sm-4", label), el("dd", "col-sm-8", value + (note ? ". " + note : "")));
            term("Tingkat bukti", evidenceText[memo.evidenceLevel] || memo.evidenceLevel, memo.evidenceRationale);
            term("Saran tindak lanjut", recommendationText[memo.recommendation] || memo.recommendation, memo.recommendationRationale);
            container.append(dl, el("p", "disclaimer small mt-2 mb-0", memo.disclaimer));
        }

        // Pattern-detection report (CHANGE-002): clusters, alternatives, leads not followed, limits.
        function renderReport(container, report) {
            const leadKind = { stock: "saham", broker: "broker" };
            container.append(el("p", "lead fs-6", report.summary),
                el("p", "small text-muted", report.counts.clusters + " klaster, " + report.counts.stocks + " saham, "
                    + report.counts.brokers + " broker · periode " + report.periodStart + " sampai " + report.periodEnd));

            const statements = (title, list, target) => {
                if (!list.length) return;
                target.append(el("h3", "h6 mt-3", title));
                const ul = el("ul", "findings");
                for (const s of list) {
                    const li = el("li", null, s.statement);
                    if (s.evidence && s.evidence.length) {
                        const ev = el("div", "small text-muted");
                        s.evidence.forEach((e, i) => {
                            if (i > 0) ev.append(document.createTextNode("; "));
                            const a = el("a", null, e.toolName);
                            a.href = "#tool-" + e.toolCallId;
                            a.dataset.evidenceCall = e.toolCallId;
                            ev.append("Bukti: ", a, document.createTextNode(" — " + e.detail));
                        });
                        li.append(ev);
                    }
                    ul.append(li);
                }
                target.append(ul);
            };
            const plain = (title, list) => {
                if (!list.length) return;
                container.append(el("h3", "h6 mt-3", title));
                const ul = el("ul", "small");
                for (const text of list) ul.append(el("li", null, text));
                container.append(ul);
            };

            for (const c of report.clusters) {
                const section = el("section", "cluster border rounded p-2 mt-3");
                section.append(el("h3", "h6 mb-1", c.name),
                    el("div", "small", "Saham: " + c.stocks.join(", ") + " · Broker dominan: " + (c.dominantBrokers.join(", ") || "-")),
                    el("p", "small mb-1", c.chronology));
                if (c.ownershipLinks) section.append(el("p", "small text-muted mb-1", "Kepemilikan: " + c.ownershipLinks));
                statements("Temuan", c.findings, section);
                container.append(section);
            }
            statements("Penjelasan alternatif", report.alternativeExplanations, container);
            plain("Jejak yang tidak diikuti", report.leadsNotFollowed.map(l => (leadKind[l.kind] || l.kind) + " " + l.target + ": " + l.reason));
            plain("Batas yang tercapai", report.limitsReached);
            plain("Keterbatasan data", report.dataLimitations);
            plain("Usulan tindak lanjut", report.followUps);
            container.append(el("p", "disclaimer small mt-2 mb-0", report.disclaimer));
        }

        async function submit(e, form, approvalId) {
            e.preventDefault();
            const message = form.querySelector(".approval-message");
            const decision = e.submitter ? e.submitter.value : "approve";
            const data = new FormData(form);
            data.set("Decision", decision);
            data.set("ApprovalId", approvalId);
            data.set("__RequestVerificationToken", ctx.antiforgery);

            if (decision !== "approve" && !String(data.get("Comment") || "").trim()) {
                message.className = "approval-message small mt-2 text-danger";
                message.textContent = "Tulis komentar dulu supaya agent tahu apa yang perlu diubah atau kenapa memo ditolak.";
                return;
            }

            form.querySelectorAll("button").forEach(b => (b.disabled = true));
            message.className = "approval-message small mt-2 text-muted";
            message.textContent = "Mengirim keputusan...";
            try {
                const response = await fetch(ctx.data.approvalUrl, { method: "POST", body: data });
                const body = await response.json().catch(() => ({}));
                message.className = "approval-message small mt-2 " + (response.ok ? "text-success" : "text-danger");
                message.textContent = body.message || (response.ok ? "Keputusan tersimpan." : "Keputusan belum tersimpan. Coba lagi.");
                if (response.ok) decisions[approvalId] = { decision, comment: String(data.get("Comment") || "").trim(), decidedBy: ctx.data.user || "Anda" };
                if (!response.ok) form.querySelectorAll("button").forEach(b => (b.disabled = false));
            } catch {
                message.className = "approval-message small mt-2 text-danger";
                message.textContent = "Tidak bisa menghubungi server. Periksa koneksi, lalu coba lagi.";
                form.querySelectorAll("button").forEach(b => (b.disabled = false));
            }
        }

        // error-banner.js reports what the application decided after a done(cancelled), e.g. an expiry.
        let lastCard = null;
        ctx.root.addEventListener("agentui:case-state", e => {
            const d = e.detail.decision;
            if (lastCard && d && d.approvalId === lastCard.dataset.approvalId) {
                lastCard.querySelector(".approval-state").textContent =
                    (decisionText[d.decision] || d.decision) + " oleh " + d.decidedBy;
            }
        });

        return {
            onEvent(event) {
                const p = event.payload;
                if (event.type === "tool_call_started" && (p.toolName === "submit_memo" || p.toolName === "submit_report")) {
                    submitCallId = p.toolCallId;
                } else if (event.type === "approval_required") {
                    resolve("Diganti oleh draf berikutnya.");
                    const card = template.content.firstElementChild.cloneNode(true);
                    card.dataset.approvalId = p.approvalId;
                    const kind = p.report ? "Draf laporan" : "Draf memo";
                    card.querySelector(".approval-title").textContent = p.revision === 0 ? kind : kind + ", revisi ke-" + p.revision;
                    card.querySelector(".approval-state").textContent = "Menunggu keputusan";
                    if (p.report) renderReport(card.querySelector(".memo"), p.report);
                    else renderMemo(card.querySelector(".memo"), p.memo);
                    const form = card.querySelector(".approval-form");
                    if (ctx.interactive) form.addEventListener("submit", e => submit(e, form, p.approvalId));
                    else form.remove();
                    pending = card;
                    lastCard = card;
                    list.append(card);
                    card.scrollIntoView({ behavior: "smooth", block: "nearest" });
                } else if (event.type === "tool_call_finished" && p.toolCallId === submitCallId) {
                    resolve(p.summary);
                } else if (event.type === "done" || event.type === "error") {
                    resolve(null);
                }
            },
            reset() {
                list.textContent = "";
                pending = null;
                submitCallId = null;
            },
        };
    });
})();
