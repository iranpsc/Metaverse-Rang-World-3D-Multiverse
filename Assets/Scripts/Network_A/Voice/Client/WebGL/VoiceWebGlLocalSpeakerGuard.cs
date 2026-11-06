#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Network_A.Voice.Client.Participants;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.WebGL
{
    // Adds a per-remote-user local playback guard, without altering
    // shared participant controls, server routing or Windows/Quest.
    public sealed class VoiceWebGlLocalSpeakerGuard : MonoBehaviour
    {
        [DllImport("__Internal")]
        private static extern int VME2W4SetLocalSpeakerGuard(string connectionId, int muted);
        private const string SceneName = "WebGL_Enviroment_Voice";
        private static VoiceWebGlLocalSpeakerGuard instance;
        private static readonly FieldInfo ControlField = typeof(VoiceParticipantListItemView)
            .GetField("directionalControlClient", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ButtonField = typeof(VoiceParticipantListItemView)
            .GetField("speakerButton", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo RuntimeMethod = typeof(VoiceDirectionalControlClient)
            .GetMethod("TryResolveRuntime", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo PeerMethod = typeof(VoiceDirectionalControlClient)
            .GetMethod("TryResolvePeerConnectionId", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerImageField = typeof(VoiceParticipantListItemView)
            .GetField("speakerImage", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerOnSpriteField = typeof(VoiceParticipantListItemView)
            .GetField("speakerOnSprite", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeakerOffSpriteField = typeof(VoiceParticipantListItemView)
            .GetField("speakerOffSprite", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class RowState
        {
            public VoiceParticipantListItemView Row;
            public Button Button;
            public UnityEngine.Events.UnityAction Listener;
            public string Peer = "";
            public bool Enabled = true;
            public bool NeedsReapply = true;
        }
        private readonly Dictionary<int, RowState> rows = new Dictionary<int, RowState>();
        private readonly HashSet<int> seen = new HashSet<int>();
        private readonly List<int> stale = new List<int>();
        private float nextPoll;
        private bool unreadyLogged;
        private bool sceneActiveLogged;
        private bool emptyRowsLogged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (instance != null) return;
            var go = new GameObject("VME2_WEBGL_LOCAL_SPEAKER_GUARD");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<VoiceWebGlLocalSpeakerGuard>();
            Debug.Log("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=READY | version=20261028_v3_scene_corrected | nativeWindowsUnchanged=true");
        }

        private void Update()
        {
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 0.15f;
            if (SceneManager.GetActiveScene().name != SceneName)
            {
                Clear();
                sceneActiveLogged = false;
                emptyRowsLogged = false;
                return;
            }
            if (!sceneActiveLogged)
            {
                sceneActiveLogged = true;
                Debug.Log("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=SCENE_ACTIVE | scene=" + SceneName);
            }
            seen.Clear();
            int availableRows = 0;
            foreach (var row in FindObjectsOfType<VoiceParticipantListItemView>())
            {
                if (row == null || !row.isActiveAndEnabled || string.IsNullOrEmpty(row.UserId)) continue;
                availableRows++;
                int key = row.GetInstanceID();
                seen.Add(key);
                if (!rows.TryGetValue(key, out RowState state))
                {
                    Button b = ButtonField == null ? null : ButtonField.GetValue(row) as Button;
                    if (b == null) { Debug.LogWarning("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=ROW_BUTTON_MISSING"); continue; }
                    state = new RowState { Row = row, Button = b, Enabled = row.SpeakerEnabled };
                    int captured = key;
                    state.Listener = () => OnSpeakerClicked(captured);
                    b.onClick.AddListener(state.Listener);
                    rows[key] = state;
                    Debug.Log("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=ROW_BOUND | userId=" + row.UserId);
                }
                string peer = ResolvePeer(row);
                if (string.IsNullOrEmpty(peer)) continue;
                if (!string.Equals(peer, state.Peer, StringComparison.OrdinalIgnoreCase))
                {
                    if (state.Peer.Length != 0 && !state.Enabled) Apply(state.Peer, false, "peer_changed_release");
                    state.Peer = peer;
                    state.NeedsReapply = true;
                }
                // Keep visual state synchronized even if the old control send returns false.
                // This guard's state is local to WebGL; it is not a server ACK.
                SetVisualState(state);
                if (state.NeedsReapply || !state.Enabled)
                {
                    int result = Apply(state.Peer, !state.Enabled, state.NeedsReapply ? "peer_ready" : "persist");
                    if (result >= 0) state.NeedsReapply = false;
                }
            }
            if (availableRows == 0 && !emptyRowsLogged)
            {
                emptyRowsLogged = true;
                Debug.Log("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=NO_PARTICIPANT_ROWS_YET | scene=" + SceneName);
            }
            else if (availableRows > 0) emptyRowsLogged = false;
            stale.Clear();
            foreach (var p in rows) if (!seen.Contains(p.Key)) stale.Add(p.Key);
            foreach (int id in stale) Release(id);
        }

        private void OnSpeakerClicked(int key)
        {
            if (!rows.TryGetValue(key, out RowState state)) return;
            state.Enabled = !state.Enabled;
            // Original UI callback may still be waiting for a network ACK; apply
            // receiver-side action synchronously, independent of that ACK.
            SetVisualState(state);
            if (state.Peer.Length == 0) state.Peer = ResolvePeer(state.Row);
            int result = Apply(state.Peer, !state.Enabled, "click");
            state.NeedsReapply = result < 0;
            Debug.Log("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=CLICK | muted=" + (!state.Enabled)
                + " | appliedConsumers=" + result + " | peerResolved=" + (state.Peer.Length > 0));
        }

        private static string ResolvePeer(VoiceParticipantListItemView row)
        {
            if (ControlField == null || RuntimeMethod == null || PeerMethod == null) return "";
            try
            {
                var ctrl = ControlField.GetValue(row) as VoiceDirectionalControlClient;
                if (ctrl == null || !(bool)RuntimeMethod.Invoke(ctrl, null)) return "";
                object[] args = { row.UserId, "" };
                if (!(bool)PeerMethod.Invoke(ctrl, args)) return "";
                string id = (args[1] as string ?? "").Trim().ToLowerInvariant();
                return Guid.TryParse(id, out _) ? id : "";
            }
            catch (Exception e) { Debug.LogWarning("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=RESOLVE_FAILED | " + e.Message); return ""; }
        }
        private static void SetVisualState(RowState state)
        {
            if (state.Row == null || SpeakerImageField == null) return;
            try
            {
                var image = SpeakerImageField.GetValue(state.Row) as Image;
                var spriteField = state.Enabled ? SpeakerOnSpriteField : SpeakerOffSpriteField;
                if (image != null && spriteField != null)
                {
                    Sprite sprite = spriteField.GetValue(state.Row) as Sprite;
                    if (sprite != null && image.sprite != sprite) image.sprite = sprite;
                }
            }
            catch (Exception e) { Debug.LogWarning("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=VISUAL_FAILED | " + e.Message); }
        }
        private int Apply(string peer, bool muted, string origin)
        {
            if (string.IsNullOrEmpty(peer)) return -2;
            try
            {
                int result = VME2W4SetLocalSpeakerGuard(peer, muted ? 1 : 0);
                if (result == -1 && !unreadyLogged)
                {
                    unreadyLogged = true;
                    Debug.LogError("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=JS_UNAVAILABLE | check_new_WebGL_build_and_browser_cache");
                }
                if (origin == "click" || origin == "peer_ready")
                    Debug.Log("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=APPLY | origin=" + origin + " | muted=" + muted + " | consumers=" + result);
                return result;
            }
            catch (Exception e)
            {
                if (!unreadyLogged) { Debug.LogError("VOICE_WEBGL_LOCAL_SPEAKER_GUARD=BRIDGE_FAILED | " + e.Message); unreadyLogged = true; }
                return -1;
            }
        }
        private void Release(int key)
        {
            if (!rows.TryGetValue(key, out RowState state)) return;
            if (state.Button != null && state.Listener != null) state.Button.onClick.RemoveListener(state.Listener);
            if (!state.Enabled && state.Peer.Length > 0) Apply(state.Peer, false, "release");
            rows.Remove(key);
        }
        private void Clear()
        {
            stale.Clear(); foreach (var key in rows.Keys) stale.Add(key);
            foreach (int key in stale) Release(key);
        }
        private void OnDestroy() { Clear(); if (instance == this) instance = null; }
    }
}
#endif
