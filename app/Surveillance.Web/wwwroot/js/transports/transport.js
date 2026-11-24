// One interface for the three ways the browser receives investigation events.
//
//   const t = AgentTransports.create(name, options);   // name: "sse" | "polling" | "signalr"
//   t.start();  t.stop();  t.stats
//
// options:
//   caseId, eventsUrl, eventsJsonUrl, hubUrl   endpoints of the case
//   pollingIntervalMs                          polling only
//   lastSequence()                             the highest sequence the page has shown; used to resume
//   onEvent(event)                             a catalog event (contracts/events.schema.json), parsed
//   onStatus(text)                             connection state for the user, in Indonesian
//
// A transport may deliver an event twice (replay and live overlap, reconnects); the page drops
// anything at or below lastSequence(). A transport stops by itself after done or error.
// stats: { requests, events, duplicates, liveEvents, delaySumMs } for the comparison page.
(function () {
    "use strict";

    const registry = {};

    function createStats() {
        return { requests: 0, events: 0, duplicates: 0, liveEvents: 0, delaySumMs: 0, highest: 0, startedAt: Date.now() };
    }

    // Shared bookkeeping: counts, delay from the event's timestamp, and the terminal check.
    function deliverer(stats, options) {
        return function deliver(event) {
            stats.events++;
            if (event.sequence <= stats.highest) {
                stats.duplicates++;
            } else {
                stats.highest = event.sequence;
                const created = Date.parse(event.timestamp);
                if (created >= stats.startedAt) {
                    stats.liveEvents++;
                    stats.delaySumMs += Math.max(0, Date.now() - created);
                }
            }
            options.onEvent(event);
            return event.type === "done" || event.type === "error";
        };
    }

    window.AgentTransports = {
        register(name, factory) {
            registry[name] = factory;
        },
        create(name, options) {
            const factory = registry[name] || registry.sse;
            const stats = createStats();
            const transport = factory(options, stats, deliverer(stats, options));
            transport.stats = stats;
            return transport;
        },
        names() {
            return Object.keys(registry);
        },
    };
})();
