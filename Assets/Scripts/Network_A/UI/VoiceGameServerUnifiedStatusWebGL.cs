#if UNITY_WEBGL
using System;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Dedicated;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.UI
{
    // UI-only: does not invoke connect/disconnect, alter voice sessions, or change server state.
    internal static class VoiceGameServerUnifiedStatusWebGLInstaller
    {
        private const string TargetScene = "WebGL_Enviroment_Voice";
        private const string RootName = "Voice_GameServer_Unified_Status_WebGL";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterSceneCallback()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void HandleInitialScene()
        {
            InstallForScene(SceneManager.GetActiveScene());
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            InstallForScene(scene);
        }

        private static void InstallForScene(Scene scene)
        {
            if (!scene.IsValid() ||
                !string.Equals(scene.name, TargetScene, StringComparison.Ordinal))
                return;

            GameObject root = GameObject.Find(RootName);
            if (root != null) return;

            root = new GameObject(RootName);
            root.AddComponent<VoiceGameServerUnifiedStatusWebGL>();
        }
    }

    [DisallowMultipleComponent]
    [DefaultExecutionOrder(32000)]
    public sealed class VoiceGameServerUnifiedStatusWebGL : MonoBehaviour
    {
        private const string TargetScene = "WebGL_Enviroment_Voice";
        private const float StatusIntervalSeconds = 0.25f;
        private const float ReferenceIntervalSeconds = 1f;
        private const float ProximityIntervalSeconds = 0.75f;

        private TextMeshProUGUI unifiedText;
        private TextMeshProUGUI oldStatusText;
        private bool oldStatusOriginalEnabled;
        private bool oldStatusOriginalCaptured;

        private DedicatedGameServerRealtimeRoomBinderWebGL binder;
        private DedicatedGameServerWsClient dedicated;
        private DedicatedRemotePlayerStateReceiver receiver;
        private G7ThreeDModeController threeD;
        private VoiceClientRuntime voice;

        private float nextReferenceCheck;
        private float nextStatusCheck;
        private float nextProximityCheck;
        private string cachedPeerStatus = "صوت: در انتظار اطلاعات بازیکنان";
        private string desiredText = "سرور بازی: در حال بررسی\nصوت: در حال بررسی";

        private void OnEnable()
        {
            nextReferenceCheck = 0f;
            nextStatusCheck = 0f;
            nextProximityCheck = 0f;
        }

        private void LateUpdate()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, TargetScene,
                    StringComparison.Ordinal))
                return;

            float now = Time.unscaledTime;
            if (now >= nextReferenceCheck || unifiedText == null)
            {
                nextReferenceCheck = now + ReferenceIntervalSeconds;
                ResolveReferences();
            }

            if (unifiedText == null) return;

            // Other existing scripts may still update this legacy field.
            // Keep its content and listeners intact; only stop drawing it.
            if (oldStatusText != null && oldStatusText != unifiedText &&
                oldStatusText.enabled)
                oldStatusText.enabled = false;

            if (now >= nextStatusCheck)
            {
                nextStatusCheck = now + StatusIntervalSeconds;
                string networkNotice = binder != null
                    ? binder.CurrentNetworkDiagnosticMessage
                    : string.Empty;
                desiredText = ResolveGameServerStatus() + "\n" + ResolveVoiceStatus(now);
                if (!string.IsNullOrWhiteSpace(networkNotice))
                    desiredText += "\n" + networkNotice;
            }

            // Override legacy direct writes, without editing their event handlers.
            if (!string.Equals(unifiedText.text, desiredText, StringComparison.Ordinal))
                unifiedText.text = desiredText;
        }

        private void ResolveReferences()
        {
            if (unifiedText == null)
            {
                GameObject canvas = null;
                GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (string.Equals(roots[i].name, "MainCanvas", StringComparison.Ordinal))
                    {
                        canvas = roots[i];
                        break;
                    }
                }
                if (canvas != null)
                {
                    Transform target = canvas.transform.Find("GameServerStateText");
                    if (target != null)
                    {
                        unifiedText = target.GetComponent<TextMeshProUGUI>();
                        if (unifiedText != null)
                        {
                            // Keep the existing font and rectangle; allow the
                            // two short Persian lines to fit the available area.
                            unifiedText.enableAutoSizing = true;
                            unifiedText.fontSizeMin = 18f;
                            unifiedText.fontSizeMax = Mathf.Max(24f, unifiedText.fontSize);
                        }
                    }

                    Transform previous = canvas.transform.Find("StatusText");
                    if (previous != null)
                    {
                        TextMeshProUGUI candidate =
                            previous.GetComponent<TextMeshProUGUI>();
                        if (candidate != null && candidate != unifiedText)
                        {
                            oldStatusText = candidate;
                            if (!oldStatusOriginalCaptured)
                            {
                                oldStatusOriginalEnabled = candidate.enabled;
                                oldStatusOriginalCaptured = true;
                            }
                        }
                    }
                }
            }

            if (binder == null)
                binder = DedicatedGameServerRealtimeRoomBinderWebGL.Instance;
            if (dedicated == null)
                dedicated = FindObjectOfType<DedicatedGameServerWsClient>();
            if (receiver == null)
                receiver = FindObjectOfType<DedicatedRemotePlayerStateReceiver>();
            if (threeD == null)
                threeD = FindObjectOfType<G7ThreeDModeController>();
            if (voice == null)
            {
                GameObject root = GameObject.Find("Voice_Client_Runtime_Root");
                if (root != null)
                    voice = root.GetComponent<VoiceClientRuntime>();
            }
        }

        private string ResolveGameServerStatus()
        {
            if (binder != null && binder.IsDisconnectFlowRunning)
                return "سرور بازی: در حال خروج";
            if (dedicated != null && dedicated.IsReady)
                return "سرور بازی: متصل";
            if (binder != null && binder.IsConnectFlowRunning)
                return "سرور بازی: در حال اتصال";
            if (binder != null && binder.HasUnclosedDedicatedSession)
                return "سرور بازی: در حال بازیابی اتصال";
            if (binder == null || dedicated == null)
                return "سرور بازی: در حال آماده سازی";
            return "سرور بازی: در انتظار اتصال";
        }

        private string ResolveVoiceStatus(float now)
        {
            if (dedicated == null || !dedicated.IsReady)
                return "صوت: در انتظار اتصال سرور";
            if (voice == null)
                return "صوت: در حال آماده سازی";
            if (voice.IsDisconnecting)
                return "صوت: در حال اتصال مجدد";
            if (!voice.IsVoiceEligible)
                return "صوت: میکروفون و بلندگو خاموش هستند";
            if (!voice.IsAuthenticated)
                return "صوت: در حال اتصال";

            if (voice.ActiveSessionCount > 0)
            {
                if (voice.IsMicrophoneMuted && !voice.IsSpeakerOff)
                    return "صوت: نشست فعال، میکروفون خاموش";
                if (voice.IsSpeakerOff && !voice.IsMicrophoneMuted)
                    return "صوت: نشست فعال، بلندگو خاموش";
                return "صوت: نشست فعال";
            }

            if (receiver == null)
                return "صوت: در انتظار اطلاعات بازیکنان";
            if (receiver.RemotePlayerCount == 0)
                return "صوت: در انتظار بازیکن دیگر";

            if (now >= nextProximityCheck)
            {
                nextProximityCheck = now + ProximityIntervalSeconds;
                cachedPeerStatus = ResolvePeerStatus();
            }
            return cachedPeerStatus;
        }

        private string ResolvePeerStatus()
        {
            Transform local = threeD != null ? threeD.GetLocalPlayerTransform() : null;
            if (local == null)
                return "صوت: در انتظار موقعیت بازیکن";

            RealtimeRoomGameServerManager room =
                RealtimeRoomGameServerManager.Instance;
            string roomId = room != null ? room.CurrentRoomId : string.Empty;
            if (string.IsNullOrEmpty(roomId))
                return "صوت: در انتظار اطلاعات روم";

            var peers = receiver.CreateSnapshot();
            bool foundSameRoom = false;
            for (int i = 0; i < peers.Count; i++)
            {
                DedicatedRemotePlayerState peer = peers[i];
                if (peer == null ||
                    !string.Equals(peer.roomId, roomId, StringComparison.Ordinal))
                    continue;

                foundSameRoom = true;
                if (Vector3.Distance(local.position, peer.Position) <=
                    VoiceDedicatedProximityEvaluator.EnterDistanceMeters)
                    return "صوت: در انتظار تشکیل نشست";
            }

            return foundSameRoom
                ? "صوت: بازیکن نزدیک وجود ندارد"
                : "صوت: در انتظار بازیکن دیگر";
        }

        private void OnDestroy()
        {
            // Only restore the visibility property we changed, and only if
            // the original text still exists (for editor play-mode teardown).
            if (oldStatusText != null && oldStatusOriginalCaptured)
                oldStatusText.enabled = oldStatusOriginalEnabled;
        }
    }
}

#endif
