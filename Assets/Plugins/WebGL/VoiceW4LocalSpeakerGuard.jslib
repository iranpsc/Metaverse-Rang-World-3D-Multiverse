// Browser-only native Unity JS bridge. 0 = state accepted, +N = bound consumers,
// -1 = WebRTC bundle not loaded, -2 = invalid peer id.
mergeInto(LibraryManager.library, {
  VME2W4SetLocalSpeakerGuard: function (peerPtr, muted) {
    try {
      if (typeof window === 'undefined' || !window.VME2WebRtcVoice ||
          typeof window.VME2WebRtcVoice.setLocallyMutedPeer !== 'function') return -1;
      return window.VME2WebRtcVoice.setLocallyMutedPeer(UTF8ToString(peerPtr), !!muted);
    } catch (e) {
      console.error('VOICE_WEBGL_LOCAL_SPEAKER_GUARD_JS_FAIL', e);
      return -1;
    }
  }
});
