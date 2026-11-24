// Starts every agent UI root on the page: the investigation page has one, /lab/patterns has one
// per pattern. The patterns live in js/patterns/, the transports in js/transports/.
// The page never knows which agent solution produced the events.
(function () {
    "use strict";

    for (const root of document.querySelectorAll("[data-agent-ui]")) {
        AgentUi.mount(root).start();
    }
})();
