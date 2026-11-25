// Job notifications on every page (use case 2): a badge on "Deteksi pola" with the user's jobs
// waiting for a decision, and a short notice when a job needs them or has ended. The server sends
// "jobUpdate" only to the user who started the job (JobNotifier, SignalR Clients.User).
(function () {
    "use strict";

    const nav = document.getElementById("jobs-nav");
    if (!nav || !window.signalR) return;
    const badge = document.getElementById("jobs-badge");
    const notice = document.getElementById("job-notice");

    function setBadge(count) {
        badge.textContent = count > 0 ? String(count) : "";
        badge.classList.toggle("d-none", !(count > 0));
        badge.setAttribute("aria-label", count + " job menunggu keputusan");
    }

    fetch(nav.dataset.badgeUrl, { headers: { Accept: "application/json" } })
        .then(r => (r.ok ? r.json() : { awaiting: 0 }))
        .then(b => setBadge(b.awaiting))
        .catch(() => {});

    const connection = new signalR.HubConnectionBuilder().withUrl(nav.dataset.hubUrl).withAutomaticReconnect().build();
    connection.on("jobUpdate", update => {
        setBadge(update.awaitingCount);
        notice.querySelector("[data-part='notice-text']").textContent = update.text;
        notice.querySelector("a").href = update.url;
        notice.classList.remove("d-none");
    });
    notice.querySelector("button").addEventListener("click", () => notice.classList.add("d-none"));
    connection.start().catch(() => {}); // notifications are a convenience; the jobs page still shows status
})();
