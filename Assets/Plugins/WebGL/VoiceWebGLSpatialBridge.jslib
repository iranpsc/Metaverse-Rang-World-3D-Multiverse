mergeInto(LibraryManager.library, {
    VoiceWebGLSpatialSetSenderState: function (
        senderConnectionIdPtr,
        pan,
        gain,
        rearAmount) {
        var senderConnectionId = UTF8ToString(senderConnectionIdPtr)
            .trim()
            .toLowerCase();

        if (!senderConnectionId) return 0;
        if (Module.voiceWebRtcSessions) {
            try {
                var webRtcVoice = window.VME2WebRtcVoice;
                return webRtcVoice && webRtcVoice.setSpatialState
                    ? webRtcVoice.setSpatialState(senderConnectionId, pan, gain, rearAmount)
                    : 0;
            } catch (_) {
                return 0;
            }
        }
        if (!Module.voiceBrowserAudioPipelines) return 0;

        var delivered = 0;
        var pipelines = Module.voiceBrowserAudioPipelines;

        Object.keys(pipelines).forEach(function (key) {
            var pipeline = pipelines[key];
            if (!pipeline || pipeline.closed || !pipeline.worklet) return;

            pipeline.worklet.port.postMessage({
                type: "spatial_state",
                streamId: senderConnectionId,
                pan: Math.max(-1, Math.min(1, Number(pan) || 0)),
                gain: Math.max(0, Math.min(1, Number(gain) || 0)),
                rearAmount: Math.max(
                    0,
                    Math.min(1, Number(rearAmount) || 0))
            });
            delivered += 1;
        });

        return delivered;
    },

    VoiceWebGLSpatialRemoveSender: function (senderConnectionIdPtr) {
        var senderConnectionId = UTF8ToString(senderConnectionIdPtr)
            .trim()
            .toLowerCase();

        if (!senderConnectionId) return 0;
        if (Module.voiceWebRtcSessions) {
            try {
                var webRtcVoice = window.VME2WebRtcVoice;
                return webRtcVoice && webRtcVoice.removeSpatialState
                    ? webRtcVoice.removeSpatialState(senderConnectionId)
                    : 0;
            } catch (_) {
                return 0;
            }
        }
        if (!Module.voiceBrowserAudioPipelines) return 0;

        var delivered = 0;
        var pipelines = Module.voiceBrowserAudioPipelines;

        Object.keys(pipelines).forEach(function (key) {
            var pipeline = pipelines[key];
            if (!pipeline || pipeline.closed || !pipeline.worklet) return;

            pipeline.worklet.port.postMessage({
                type: "spatial_remove",
                streamId: senderConnectionId
            });
            delivered += 1;
        });

        return delivered;
    }
});
