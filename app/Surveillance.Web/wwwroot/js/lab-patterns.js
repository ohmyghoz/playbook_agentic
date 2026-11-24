// /lab/patterns: each section starts its own fake run and mounts a UI root holding only the
// pattern(s) it demonstrates. Playing again replaces the section's root with a fresh copy.
(function () {
    "use strict";

    const page = document.getElementById("lab-patterns");
    if (!page) return;
    const antiforgery = document.querySelector("input[name='__RequestVerificationToken']").value;

    for (const section of page.querySelectorAll(":scope > section")) { // pattern partials have their own sections
        const pristine = section.querySelector("[data-agent-ui-deferred]").cloneNode(true);
        const link = section.querySelector("[data-part-link='details']");

        for (const button of section.querySelectorAll("[data-play-scenario]")) {
            button.addEventListener("click", async () => {
                button.disabled = true;
                const data = new FormData();
                data.set("scenario", button.dataset.playScenario);
                data.set("__RequestVerificationToken", antiforgery);
                try {
                    const response = await fetch(page.dataset.startUrl, { method: "POST", body: data });
                    if (!response.ok) throw new Error("HTTP " + response.status);
                    const run = await response.json();

                    const root = pristine.cloneNode(true);
                    section.querySelector("[data-agent-ui-deferred]").replaceWith(root);
                    Object.assign(root.dataset, {
                        caseId: run.caseId, eventsUrl: run.eventsUrl, eventsJsonUrl: run.eventsJsonUrl,
                        approvalUrl: run.approvalUrl, stopUrl: run.stopUrl, retryUrl: run.retryUrl,
                        previewUrlTemplate: run.previewUrlTemplate, hubUrl: page.dataset.hubUrl,
                    });
                    link.href = run.detailsUrl;
                    link.classList.remove("d-none");
                    AgentUi.mount(root).start();
                } catch {
                    button.insertAdjacentText("afterend", " Gagal memulai skenario. Coba lagi.");
                } finally {
                    button.disabled = false;
                }
            });
        }
    }
})();
