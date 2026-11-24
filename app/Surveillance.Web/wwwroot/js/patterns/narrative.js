// Pattern: streaming narration. token events are appended as they arrive; text from a new model
// turn (after a tool call or step) starts on its own line. Partial: Views/Shared/Patterns/_Narrative.cshtml
(function () {
    "use strict";

    AgentUi.register("narrative", ctx => {
        const box = ctx.part("narrative");
        return {
            onEvent(event, ctx) {
                if (event.type !== "token") return;
                if (ctx.state.lastType !== "token" && box.textContent && !/\s$/.test(box.textContent)) {
                    box.append(document.createTextNode("\n"));
                }
                box.append(document.createTextNode(event.payload.text));
                box.scrollTop = box.scrollHeight;
            },
            reset() {
                box.textContent = "";
            },
        };
    });
})();
