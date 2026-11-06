mergeInto(LibraryManager.library, {
    VME2W4RecoveryOverlayShow: function(rootPtr, titlePtr, mediaPtr, fullPtr, available) {
        if (typeof document === "undefined" || !document.body) return 0;
        var state = window.__vme2W4RecoveryOverlay;
        if (!state || !state.root || !state.root.isConnected) {
            var root = document.createElement("div");
            root.id = "vme2-w4-voice-recovery";
            root.setAttribute("role", "dialog");
            root.setAttribute("aria-live", "polite");
            root.dir = "rtl";
            root.style.cssText = "position:fixed;top:36px;left:50%;transform:translateX(-50%);width:min(740px,calc(100vw - 24px));box-sizing:border-box;z-index:2147483647;padding:16px 20px;background:rgba(15,23,35,.97);color:white;border:1px solid rgba(255,255,255,.28);border-radius:12px;box-shadow:0 8px 30px rgba(0,0,0,.5);font:600 18px/1.8 Tahoma,Arial,sans-serif;text-align:center;pointer-events:auto";
            var title = document.createElement("div");
            title.style.marginBottom = "12px";
            var actions = document.createElement("div");
            actions.style.cssText = "display:flex;flex-wrap:wrap;gap:12px;justify-content:center";
            var makeButton = function() {
                var button = document.createElement("button");
                button.type = "button";
                button.style.cssText = "flex:1 1 260px;min-height:50px;padding:9px 12px;border-radius:8px;border:1px solid rgba(255,255,255,.32);background:#2463a3;color:white;font:600 16px Tahoma,Arial,sans-serif;cursor:pointer;direction:rtl";
                actions.appendChild(button);
                return button;
            };
            // The five-second media button is intentionally not created.
            // Recovery only starts from the user's full-Voice button click.
            var fullButton = makeButton();
            root.appendChild(title);
            root.appendChild(actions);
            document.body.appendChild(root);
            state = { root: root, title: title, full: fullButton, unityRoot: "" };
            fullButton.addEventListener("click", function() {
                if (fullButton.disabled) return;
                try { SendMessage(state.unityRoot, "HandleBrowserFullVoiceRecoveryClicked", ""); }
                catch (error) { console.warn("[VME2_W4] voice button", error); }
            });
            window.__vme2W4RecoveryOverlay = state;
        }
        state.unityRoot = UTF8ToString(rootPtr);
        state.title.textContent = UTF8ToString(titlePtr);
        state.full.textContent = UTF8ToString(fullPtr);
        var enabled = available !== 0;
        state.full.disabled = !enabled;
        state.full.style.opacity = enabled ? "1" : "0.55";
        return 1;
    },
    VME2W4RecoveryOverlayHide: function() {
        if (typeof window === "undefined") return;
        var state = window.__vme2W4RecoveryOverlay;
        if (state && state.root) state.root.remove();
        window.__vme2W4RecoveryOverlay = null;
    },
    // The permanent Reload Sound UI is owned by the scene's Btn_ReloadSound.
    // Preserve these two WebGL exports because the already-installed C# owner
    // imports them. Do not create any additional browser reload button.
    VME2W4VoiceReloadControlShow: function(rootPtr, enabled) {
        // Clean up any leftover button from an earlier JavaScript version.
        if (typeof window !== "undefined") {
            var state = window.__vme2W4PermanentVoiceReload;
            if (state && state.button && state.button.remove) state.button.remove();
            window.__vme2W4PermanentVoiceReload = null;
        }
        return 1;
    },
    VME2W4VoiceReloadControlHide: function() {
        if (typeof window === "undefined") return;
        var state = window.__vme2W4PermanentVoiceReload;
        if (state && state.button && state.button.remove) state.button.remove();
        window.__vme2W4PermanentVoiceReload = null;
    }
});
