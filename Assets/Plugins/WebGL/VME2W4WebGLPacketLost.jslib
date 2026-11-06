// Read only the already collected inbound-rtp packetsLost counter from W4.
// No additional getStats request, loss calculation, or alteration of media code.
mergeInto(LibraryManager.library, {
    VME2W4GetWebRtcPacketsLost: function () {
        if (typeof window === "undefined") return 0;
        var sessions = Module.voiceWebRtcSessions;
        if (!sessions) return 0;

        var hasSession = false;
        for (var handle in sessions) {
            if (Object.prototype.hasOwnProperty.call(sessions, handle) &&
                sessions[handle] && !sessions[handle].closed) {
                hasSession = true;
                break;
            }
        }
        if (!hasSession) return 0;

        var stats = window.VME2WebRtcLastStats;
        if (!stats || typeof stats.lost !== "number" || !isFinite(stats.lost))
            return 0;
        return stats.lost;
    }
});
