// Read-only audio levels from the EXISTING WebRTC producer/consumers.
// The backend, stream lifecycle, and audio routing are unchanged.
mergeInto(LibraryManager.library, {
    VME2W4VoiceMeterReadPacked: function () {
        try {
            var bridge = window.VME2WebRtcVoice;
            if (!bridge || typeof bridge.getLevelsPacked !== 'function') return 0;
            return bridge.getLevelsPacked() | 0;
        } catch (_) {
            return 0;
        }
    }
});
