// Presentation-only extension point. Never modify input, contenteditable, clipboard,
// message payloads or network requests. Unicode remains the source of truth.
const emojiModule = Object.freeze({
    initialize() {
        // Future: load a local display asset map here, without fetching message data.
    },
    applyTweaks(_root) {
        // Intentionally empty in v1: Instagram's picker and sent Unicode stay intact.
    },
    dispose() {},
    displayText(unicodeEmoji) { return unicodeEmoji; }
});
