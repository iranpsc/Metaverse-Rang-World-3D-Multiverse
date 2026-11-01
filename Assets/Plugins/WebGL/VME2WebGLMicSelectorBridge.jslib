// WebGL-only adapter for the EXISTING VoiceBrowserAudioStart capture pipeline.
// No extra producer, no SFU change, no microphone selection outside user action.
mergeInto(LibraryManager.library, {
    VME2WebGLMicSelectorList: function (receiverObjectPtr) {
        var receiver = UTF8ToString(receiverObjectPtr);
        var tell = function (message) {
            try { SendMessage(receiver, "OnVME2WebGLMicDevices", message); }
            catch (e) { console.warn("VME2_WEBGL_MIC_LIST_CALLBACK_FAIL", e); }
        };
        try {
            if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) {
                tell("ERROR|media_devices_unavailable");
                return 0;
            }
            navigator.mediaDevices.enumerateDevices().then(function (devices) {
                var seen = {};
                var inputs = devices.filter(function (d) {
                    if (!d || d.kind !== "audioinput" || !d.deviceId ||
                        d.deviceId === "default" || d.deviceId === "communications" ||
                        seen[d.deviceId]) return false;
                    seen[d.deviceId] = true;
                    return true;
                });
                var rows = inputs.map(function (d, i) {
                    return encodeURIComponent(d.deviceId) + ":" +
                        encodeURIComponent(d.label || ("Microphone " + (i + 1)));
                });
                tell("OK|" + rows.join("|"));
            }).catch(function (error) {
                tell("ERROR|" + encodeURIComponent(error && error.name || "enumerate_failed"));
            });
            return 1;
        } catch (error) {
            tell("ERROR|" + encodeURIComponent(error && error.name || "list_exception"));
            return 0;
        }
    },

    VME2WebGLMicSelectorSelect: function (deviceIdPtr, receiverObjectPtr) {
        var requestedId = UTF8ToString(deviceIdPtr);
        var receiver = UTF8ToString(receiverObjectPtr);
        var state = Module.vme2WebGLMicSelectorState ||
            (Module.vme2WebGLMicSelectorState = { desiredId: "", pending: false, lastPipeline: null });
        var tell = function (status, detail) {
            try { SendMessage(receiver, "OnVME2WebGLMicSelected", status + "|" + detail); }
            catch (_) {}
        };
        if (!requestedId || state.pending) {
            tell("FAIL", "empty_id_or_busy");
            return 0;
        }
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia ||
            !navigator.mediaDevices.enumerateDevices) {
            tell("FAIL", "browser_capture_unavailable");
            return 0;
        }
        var locatePipeline = function () {
            var all = Module.voiceBrowserAudioPipelines || {};
            var keys = Object.keys(all);
            for (var i = 0; i < keys.length; i++) {
                var p = all[keys[i]];
                if (p && !p.closed && p.context && p.worklet && p.source && p.stream &&
                    p.stream.getAudioTracks && p.stream.getAudioTracks().length) return p;
            }
            return null;
        };
        var stopStream = function (s) {
            if (!s) return;
            try { s.getTracks().forEach(function (t) { try { t.stop(); } catch (_) {} }); }
            catch (_) {}
        };
        // Retain the plain-string switching closure for WebGL reconnection.
        state.apply = function (plainId, plainReceiver) {
            if (state.pending) return false;
            requestedId = plainId;
            receiver = plainReceiver;
            state.pending = true;
            (async function () {
            var newStream = null;
            var newSource = null;
            try {
                var devices = await navigator.mediaDevices.enumerateDevices();
                if (!devices.some(function (d) {
                    return d && d.kind === "audioinput" && d.deviceId === requestedId;
                })) throw new Error("requested_input_missing");
                var pipeline = locatePipeline();
                if (!pipeline) throw new Error("voice_capture_not_ready");
                var oldStream = pipeline.stream;
                var oldTrack = oldStream.getAudioTracks()[0];
                var oldId = String(oldTrack.getSettings && oldTrack.getSettings().deviceId ||
                    pipeline.selectedDeviceId || "");
                if (oldId === requestedId && oldTrack.readyState === "live") {
                    state.desiredId = requestedId;
                    state.lastPipeline = pipeline;
                    if (pipeline.deviceChangeHandler && navigator.mediaDevices.removeEventListener) {
                        navigator.mediaDevices.removeEventListener("devicechange", pipeline.deviceChangeHandler);
                        pipeline.deviceChangeHandler = null;
                    }
                    tell("PASS", "already_active");
                    return;
                }
                // Preserve the capture settings used by the current browser pipeline.
                // Do not change gain/AEC/NS policy, speaker, recording, or transport.
                var constraints = oldTrack.getConstraints ? oldTrack.getConstraints() : {};
                constraints = Object.assign({}, constraints, { deviceId: { exact: requestedId } });
                newStream = await navigator.mediaDevices.getUserMedia({
                    audio: constraints, video: false
                });
                if (locatePipeline() !== pipeline || pipeline.closed ||
                    pipeline.stream !== oldStream) throw new Error("voice_pipeline_changed_during_switch");
                var newTrack = newStream.getAudioTracks()[0];
                if (!newTrack || newTrack.readyState !== "live")
                    throw new Error("requested_track_not_live");
                newSource = pipeline.context.createMediaStreamSource(newStream);
                newSource.connect(pipeline.worklet);
                var previousSource = pipeline.source;
                pipeline.source = newSource;
                pipeline.stream = newStream;
                pipeline.selectedDeviceId = String(
                    newTrack.getSettings && newTrack.getSettings().deviceId || requestedId);
                state.desiredId = requestedId;
                state.lastPipeline = pipeline;
                // Disable only automatic headset reselection for this manually
                // selected pipeline. Existing reconnect and audio handling remain intact.
                if (pipeline.deviceChangeHandler && navigator.mediaDevices.removeEventListener) {
                    navigator.mediaDevices.removeEventListener("devicechange", pipeline.deviceChangeHandler);
                    pipeline.deviceChangeHandler = null;
                }
                if (pipeline.deviceChangeTimer) {
                    clearTimeout(pipeline.deviceChangeTimer);
                    pipeline.deviceChangeTimer = null;
                }
                try { previousSource.disconnect(); } catch (_) {}
                // WebRTC W4's existing one-second collectStats loop observes
                // pipeline.stream and calls producer.replaceTrack. Keep old track
                // briefly live while that existing replacement occurs.
                setTimeout(function () { stopStream(oldStream); }, 4000);
                newStream = null;
                newSource = null;
                tell("PASS", "device_selected");
            } catch (error) {
                if (newSource) { try { newSource.disconnect(); } catch (_) {} }
                stopStream(newStream);
                tell("FAIL", String(error && (error.name || error.message) || "switch_failed")
                    .replace(/[|\r\n]/g, "_").slice(0, 100));
            } finally {
                state.pending = false;
            }
            })();
            return true;
        };
        state.apply(requestedId, receiver);
        return 1;
    },

    VME2WebGLMicSelectorMaintain: function (receiverObjectPtr) {
        var state = Module.vme2WebGLMicSelectorState;
        if (!state || !state.desiredId || state.pending) return 0;
        var all = Module.voiceBrowserAudioPipelines || {};
        var keys = Object.keys(all);
        for (var i = 0; i < keys.length; i++) {
            var p = all[keys[i]];
            if (!p || p.closed || !p.stream || !p.source || !p.worklet) continue;
            var track = p.stream.getAudioTracks && p.stream.getAudioTracks()[0];
            if (!track || track.readyState !== "live") continue;
            var activeId = String(track.getSettings && track.getSettings().deviceId ||
                p.selectedDeviceId || "");
            if (activeId === state.desiredId) {
                state.lastPipeline = p;
                return 1;
            }
            // Pipeline may have been rebuilt by the EXISTING voice reconnect.
            // Reapply the user's selection without starting/restarting a session.
            if (typeof state.apply === "function")
                state.apply(state.desiredId, UTF8ToString(receiverObjectPtr));
            return 2;
        }
        return 0;
    }
});
