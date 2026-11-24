// Renders the agent event catalog (contracts/events.schema.json) for one case.
// The page never knows which agent solution produced the events.
(function () {
    "use strict";

    const root = document.getElementById("investigation");
    if (!root) return;

    const mode = root.dataset.mode;
    const terminalOnLoad = root.dataset.terminal === "true";
    const antiforgery = root.querySelector("input[name='__RequestVerificationToken']").value;

    const el = {
        status: document.getElementById("run-status"),
        connection: document.getElementById("connection"),
        stop: document.getElementById("stop-button"),
        narrative: document.getElementById("narrative"),
        toolCalls: document.getElementById("tool-calls"),
        toolCount: document.getElementById("tool-count"),
        approvals: document.getElementById("approvals"),
        errorBanner: document.getElementById("error-banner"),
        errorMessage: document.getElementById("error-message"),
        retryForm: document.getElementById("retry-form"),
        doneBanner: document.getElementById("done-banner"),
        approvalTemplate: document.getElementById("approval-template"),
    };

    const statusText = {
        running: "Berjalan",
        awaiting: "Menunggu keputusan analis",
        approved: "Memo disetujui",
        rejected: "Memo ditolak",
        cancelled: "Dihentikan",
        failed: "Gagal",
    };
    const categoryText = {
        price_volume: "Harga dan volume",
        news: "Berita",
        corporate_action: "Aksi korporasi",
        broker: "Broker",
        bandar: "Bandar",
        foreign: "Asing",
        insider: "Insider",
    };
    const evidenceText = { strong: "Kuat", moderate: "Sedang", weak: "Lemah", insufficient: "Tidak cukup" };
    const recommendationText = { escalate: "Eskalasi", monitor: "Pantau", close: "Tutup" };
    const doneText = {
        approved: ["alert-success", "Memo disetujui analis. Investigasi selesai."],
        rejected: ["alert-secondary", "Memo ditolak analis. Investigasi selesai."],
        cancelled: ["alert-warning", "Investigasi dihentikan. Event yang sudah masuk tetap tersimpan."],
    };

    let lastSequence = 0;
    let lastType = null;
    let maxToolCalls = null;
    let toolCallsStarted = 0;
    let pendingApproval = null;
    let finished = false;
    let source = null;
    const interactive = mode === "live" && !terminalOnLoad;

    // ---- rendering ---------------------------------------------------------

    function setStatus(key) {
        el.status.textContent = statusText[key];
        el.status.className = "status status-" + key;
    }

    function handle(event) {
        if (event.sequence <= lastSequence) return; // already shown (replay or reconnect overlap)
        lastSequence = event.sequence;
        const p = event.payload;
        const previousType = lastType;
        lastType = event.type;

        switch (event.type) {
            case "run_started":
                maxToolCalls = p.maxToolCalls;
                updateToolCount();
                setStatus("running");
                break;
            case "token":
                // Text from a new model turn starts on its own line.
                if (previousType !== "token" && el.narrative.textContent && !/\s$/.test(el.narrative.textContent)) {
                    el.narrative.append(document.createTextNode("\n"));
                }
                el.narrative.append(document.createTextNode(p.text));
                el.narrative.scrollTop = el.narrative.scrollHeight;
                break;
            case "tool_call_started":
                addToolCall(p);
                if (p.toolName !== "submit_memo") toolCallsStarted++;
                updateToolCount();
                break;
            case "tool_call_finished":
                finishToolCall(p);
                if (pendingApproval && pendingApproval.toolCallId === p.toolCallId) {
                    resolveApproval(p.summary);
                }
                if (!finished) setStatus("running");
                break;
            case "approval_required":
                addApproval(p);
                setStatus("awaiting");
                break;
            case "done": {
                finish();
                resolveApproval(null);
                setStatus(p.outcome);
                const [cls, text] = doneText[p.outcome];
                el.doneBanner.className = "alert " + cls;
                el.doneBanner.textContent = text + " Panggilan data: " + p.toolCallsUsed + ".";
                break;
            }
            case "error":
                finish();
                resolveApproval(null);
                setStatus("failed");
                el.errorMessage.textContent = p.message;
                el.errorBanner.classList.remove("d-none");
                if (p.retryable && mode === "live") el.retryForm.classList.remove("d-none");
                break;
        }
    }

    function updateToolCount() {
        el.toolCount.textContent = maxToolCalls ? toolCallsStarted + " / " + maxToolCalls : String(toolCallsStarted);
    }

    function addToolCall(p) {
        const li = document.createElement("li");
        li.className = "tool-call running";
        li.id = "tool-" + p.toolCallId;

        const head = document.createElement("div");
        head.className = "tool-head";
        const icon = document.createElement("span");
        icon.className = "tool-icon";
        icon.setAttribute("aria-hidden", "true");
        const name = document.createElement("code");
        name.textContent = p.toolName;
        const state = document.createElement("span");
        state.className = "tool-state";
        state.textContent = p.toolName === "submit_memo" ? "menunggu keputusan" : "berjalan";
        head.append(icon, name, state);

        const args = document.createElement("div");
        args.className = "tool-args";
        args.textContent = p.toolName === "submit_memo" ? "draf memo" : formatArgs(p.arguments);

        const summary = document.createElement("div");
        summary.className = "tool-summary";

        li.append(head, args, summary);
        el.toolCalls.append(li);
    }

    function finishToolCall(p) {
        const li = document.getElementById("tool-" + p.toolCallId);
        if (!li) return;
        li.classList.remove("running");
        li.classList.add(p.status === "ok" ? "ok" : "failed");
        li.querySelector(".tool-state").textContent = p.status === "ok" ? "selesai" : "gagal";
        li.querySelector(".tool-summary").textContent = p.summary;
    }

    function formatArgs(args) {
        const parts = Object.entries(args || {}).map(([k, v]) => k + "=" + (typeof v === "object" ? JSON.stringify(v) : v));
        return parts.length ? parts.join(", ") : "tanpa parameter";
    }

    function addApproval(p) {
        resolveApproval("Diganti oleh draf berikutnya.");
        const card = el.approvalTemplate.content.firstElementChild.cloneNode(true);
        card.querySelector(".approval-title").textContent =
            p.revision === 0 ? "Draf memo" : "Draf memo, revisi ke-" + p.revision;
        card.querySelector(".approval-state").textContent = "Menunggu keputusan";
        renderMemo(card.querySelector(".memo"), p.memo);

        const form = card.querySelector(".approval-form");
        const submitCall = [...el.toolCalls.querySelectorAll(".tool-call.running")]
            .find(li => li.querySelector("code").textContent === "submit_memo");
        pendingApproval = { card, approvalId: p.approvalId, toolCallId: submitCall ? submitCall.id.slice(5) : null };

        if (interactive) {
            form.addEventListener("submit", e => submitDecision(e, form, p.approvalId));
        } else {
            form.remove();
        }

        el.approvals.append(card);
        card.scrollIntoView({ behavior: "smooth", block: "nearest" });
    }

    function resolveApproval(text) {
        if (!pendingApproval) return;
        const { card } = pendingApproval;
        const form = card.querySelector(".approval-form");
        if (form) form.querySelectorAll("button, input, textarea").forEach(x => (x.disabled = true));
        if (text) card.querySelector(".approval-state").textContent = text;
        else card.querySelector(".approval-state").textContent = "Selesai";
        pendingApproval = null;
    }

    function renderMemo(container, memo) {
        const add = (tag, text, cls) => {
            const node = document.createElement(tag);
            if (cls) node.className = cls;
            node.textContent = text;
            container.append(node);
            return node;
        };

        add("p", memo.summary, "lead fs-6");
        add("p", memo.anomaly.description, "small text-muted");

        add("h3", "Temuan", "h6 mt-3");
        const findings = document.createElement("ul");
        findings.className = "findings";
        for (const f of memo.findings) {
            const li = document.createElement("li");
            const cat = document.createElement("span");
            cat.className = "badge text-bg-light me-1";
            cat.textContent = categoryText[f.category] || f.category;
            li.append(cat, document.createTextNode(f.statement));
            const ev = document.createElement("div");
            ev.className = "small text-muted";
            f.evidence.forEach((e, i) => {
                if (i > 0) ev.append(document.createTextNode("; "));
                const a = document.createElement("a");
                a.href = "#tool-" + e.toolCallId;
                a.textContent = e.toolName;
                ev.append("Bukti: ", a, document.createTextNode(" — " + e.detail));
            });
            li.append(ev);
            findings.append(li);
        }
        container.append(findings);

        if (memo.unavailableData.length) {
            add("h3", "Data tidak tersedia", "h6 mt-3");
            const ul = document.createElement("ul");
            ul.className = "small";
            for (const u of memo.unavailableData) {
                const li = document.createElement("li");
                li.textContent = u.toolName + ": " + u.reason;
                ul.append(li);
            }
            container.append(ul);
        }

        const dl = document.createElement("dl");
        dl.className = "row small mb-0 mt-3";
        const term = (label, value, note) => {
            const dt = document.createElement("dt");
            dt.className = "col-sm-4";
            dt.textContent = label;
            const dd = document.createElement("dd");
            dd.className = "col-sm-8";
            dd.textContent = value + (note ? ". " + note : "");
            dl.append(dt, dd);
        };
        term("Tingkat bukti", evidenceText[memo.evidenceLevel] || memo.evidenceLevel, memo.evidenceRationale);
        term("Saran tindak lanjut", recommendationText[memo.recommendation] || memo.recommendation, memo.recommendationRationale);
        container.append(dl);

        add("p", memo.disclaimer, "disclaimer small mt-2 mb-0");
    }

    function finish() {
        finished = true;
        if (source) source.close();
        if (el.stop) el.stop.remove();
        el.connection.textContent = "";
    }

    // ---- actions -----------------------------------------------------------

    async function submitDecision(e, form, approvalId) {
        e.preventDefault();
        const message = form.querySelector(".approval-message");
        const decision = e.submitter ? e.submitter.value : "approve";
        const data = new FormData(form);
        data.set("Decision", decision);
        data.set("ApprovalId", approvalId);
        data.set("__RequestVerificationToken", antiforgery);

        if (decision !== "approve" && !String(data.get("Comment") || "").trim()) {
            message.className = "approval-message small mt-2 text-danger";
            message.textContent = "Tulis komentar dulu supaya agent tahu apa yang perlu diubah atau kenapa memo ditolak.";
            return;
        }

        form.querySelectorAll("button").forEach(b => (b.disabled = true));
        message.className = "approval-message small mt-2 text-muted";
        message.textContent = "Mengirim keputusan...";

        try {
            const response = await fetch(root.dataset.approvalUrl, { method: "POST", body: data });
            const body = await response.json().catch(() => ({}));
            message.className = "approval-message small mt-2 " + (response.ok ? "text-success" : "text-danger");
            message.textContent = body.message || (response.ok ? "Keputusan tersimpan." : "Keputusan belum tersimpan. Coba lagi.");
            if (!response.ok) form.querySelectorAll("button").forEach(b => (b.disabled = false));
        } catch {
            message.className = "approval-message small mt-2 text-danger";
            message.textContent = "Tidak bisa menghubungi server. Periksa koneksi, lalu coba lagi.";
            form.querySelectorAll("button").forEach(b => (b.disabled = false));
        }
    }

    if (el.stop) {
        el.stop.addEventListener("click", async () => {
            el.stop.disabled = true;
            el.stop.textContent = "Menghentikan...";
            const data = new FormData();
            data.set("__RequestVerificationToken", antiforgery);
            try {
                await fetch(root.dataset.stopUrl, { method: "POST", body: data });
            } catch {
                el.stop.disabled = false;
                el.stop.textContent = "Hentikan investigasi";
            }
        });
    }

    // ---- data sources ------------------------------------------------------

    const eventTypes = ["run_started", "step_started", "token", "tool_call_started", "tool_call_finished", "approval_required", "done", "error"];

    function connect() {
        // The server replays stored events first, so a reload loses nothing.
        // EventSource resends the last id as Last-Event-ID when it reconnects.
        source = new EventSource(root.dataset.eventsUrl);
        source.onopen = () => (el.connection.textContent = "Terhubung");
        source.onerror = () => {
            if (!finished) el.connection.textContent = "Koneksi terputus, menyambung ulang...";
        };
        for (const type of eventTypes) {
            source.addEventListener(type, e => handle(JSON.parse(e.data)));
        }
    }

    async function loadAll() {
        const response = await fetch(root.dataset.eventsJsonUrl);
        return response.json();
    }

    function resetView() {
        lastSequence = 0;
        lastType = null;
        toolCallsStarted = 0;
        maxToolCalls = null;
        pendingApproval = null;
        finished = false;
        el.narrative.textContent = "";
        el.toolCalls.textContent = "";
        el.approvals.textContent = "";
        el.errorBanner.classList.add("d-none");
        el.doneBanner.className = "alert d-none";
    }

    async function replay() {
        const events = await loadAll();
        const speed = Number(document.getElementById("replay-speed").value);
        resetView();
        for (let i = 0; i < events.length; i++) {
            if (speed > 0 && i > 0) {
                const gap = (Date.parse(events[i].timestamp) - Date.parse(events[i - 1].timestamp)) / speed;
                await new Promise(r => setTimeout(r, Math.min(Math.max(gap, 0), 2000)));
            }
            handle(events[i]);
        }
    }

    if (mode === "replay") {
        document.getElementById("replay-start").addEventListener("click", replay);
        replay();
    } else if (terminalOnLoad) {
        loadAll().then(events => events.forEach(handle));
    } else {
        connect();
    }
})();
