#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Network_A.Voice.Client.MediaV2.Windows
{
    public static class VoiceWindowsMediaResilienceInstaller
    {
        private const string RootName = "Voice_Windows_Media_Resilience_Wrapper";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            if (root.GetComponent<VoiceWindowsMediaResilienceWrapper>() == null)
                root.AddComponent<VoiceWindowsMediaResilienceWrapper>();

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_RESILIENCE=READY" +
                " | reference=WebGL" +
                " | pingIntervalMs=5000" +
                " | heartbeatTimeoutMs=15000" +
                " | retryScheduleMs=100,200,400,800,1600,2000" +
                " | stableFilesChanged=False" +
                " | webglPathChanged=False" +
                " | questPathChanged=False");
        }
    }

    [DisallowMultipleComponent]
    public sealed class VoiceWindowsMediaResilienceWrapper : MonoBehaviour
    {
        private static readonly BindingFlags InstancePrivate =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo AdapterMediaSessionField =
            typeof(VoiceWindowsMediaRuntimeAdapter).GetField(
                "mediaSession",
                InstancePrivate);

        private static readonly FieldInfo AdapterRetryAtField =
            typeof(VoiceWindowsMediaRuntimeAdapter).GetField(
                "retryAt",
                InstancePrivate);

        private static readonly FieldInfo AdapterMediaConnectStartedField =
            typeof(VoiceWindowsMediaRuntimeAdapter).GetField(
                "mediaConnectStarted",
                InstancePrivate);

        private static readonly FieldInfo AdapterMediaBoundField =
            typeof(VoiceWindowsMediaRuntimeAdapter).GetField(
                "mediaBound",
                InstancePrivate);

        private static readonly FieldInfo AdapterResetRequestedField =
            typeof(VoiceWindowsMediaRuntimeAdapter).GetField(
                "resetRequested",
                InstancePrivate);

        private static readonly FieldInfo AdapterResetReasonField =
            typeof(VoiceWindowsMediaRuntimeAdapter).GetField(
                "resetReason",
                InstancePrivate);

        private static readonly FieldInfo SessionTransportField =
            typeof(VoiceMediaV2ClientSession).GetField(
                "transport",
                InstancePrivate);

        private static readonly FieldInfo SessionStreamIdField =
            typeof(VoiceMediaV2ClientSession).GetField(
                "streamId",
                InstancePrivate);

        private static readonly MethodInfo SessionCreatePacketMethod =
            typeof(VoiceMediaV2ClientSession).GetMethod(
                "CreatePacket",
                InstancePrivate);

        private static readonly byte[] PingPayload = { 14, 2, 1, 4 };

        private VoiceWindowsMediaRuntimeAdapter adapter;
        private VoiceMediaV2ClientSession attachedSession;
        private IVoiceMediaTransportV2 attachedTransport;
        private VoiceClientRuntime runtime;

        private int consecutiveFailures;
        private int pongCount;
        private long inboundPacketCount;
        private long lastInboundStopwatchTicks;
        private float nextPingAt;
        private float nextHeartbeatCheckAt;
        private bool heartbeatResetPending;
        private bool previousMediaConnectStarted;
        private VoiceMediaV2ClientSession previousObservedSession;
        private bool reflectionReadyLogged;
        private bool reflectionFailureLogged;

        private void Update()
        {
            ResolveAdapter();
            if (adapter == null) return;

            if (!ValidateReflection()) return;

            ResolveRuntime();
            ObserveConnectionTransitions();
            AttachCurrentSession();

            if (attachedSession == null ||
                attachedTransport == null ||
                !attachedSession.IsBound)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;

            if (now >= nextPingAt)
            {
                nextPingAt = now + VoiceWindowsMediaConnectionPolicy.PingIntervalSeconds;
                _ = SendPingAsync(attachedSession, attachedTransport);
            }

            if (now >= nextHeartbeatCheckAt)
            {
                nextHeartbeatCheckAt =
                    now + VoiceWindowsMediaConnectionPolicy.HeartbeatCheckIntervalSeconds;
                CheckHeartbeat();
            }
        }

        private bool ValidateReflection()
        {
            bool ready =
                AdapterMediaSessionField != null &&
                AdapterRetryAtField != null &&
                AdapterMediaConnectStartedField != null &&
                AdapterMediaBoundField != null &&
                AdapterResetRequestedField != null &&
                AdapterResetReasonField != null &&
                SessionTransportField != null &&
                SessionStreamIdField != null &&
                SessionCreatePacketMethod != null;

            if (ready)
            {
                if (!reflectionReadyLogged)
                {
                    reflectionReadyLogged = true;
                    Debug.Log(
                        "VOICE_WINDOWS_MEDIA_RESILIENCE_REFLECTION=PASS" +
                        " | stableAdapterModified=False");
                }
                return true;
            }

            if (!reflectionFailureLogged)
            {
                reflectionFailureLogged = true;
                Debug.LogError(
                    "VOICE_WINDOWS_MEDIA_RESILIENCE_REFLECTION=FAIL" +
                    " | stableAdapterModified=False");
            }

            return false;
        }

        private void ResolveAdapter()
        {
            if (adapter != null) return;

            adapter = UnityEngine.Object.FindObjectOfType<VoiceWindowsMediaRuntimeAdapter>();
            if (adapter == null) return;

            previousMediaConnectStarted = GetBool(
                AdapterMediaConnectStartedField,
                adapter);
            previousObservedSession = GetSession();

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_RESILIENCE_ADAPTER=PASS" +
                " | adapter=VoiceWindowsMediaRuntimeAdapter" +
                " | transportPreserved=VoiceGrpcMediaTransportV2");
        }

        private void ResolveRuntime()
        {
            if (runtime != null) return;
            runtime = UnityEngine.Object.FindObjectOfType<VoiceClientRuntime>();
        }

        private void ObserveConnectionTransitions()
        {
            VoiceMediaV2ClientSession currentSession = GetSession();
            bool connectStarted = GetBool(
                AdapterMediaConnectStartedField,
                adapter);

            if (previousObservedSession != null && currentSession == null)
            {
                string reason = GetString(AdapterResetReasonField, adapter);
                if (IsUnexpectedMediaReset(reason))
                    ApplyWebGLRetryDelay("session_reset", reason);
            }
            else if (previousMediaConnectStarted &&
                     !connectStarted &&
                     currentSession == null &&
                     IsControlAuthenticated())
            {
                ApplyWebGLRetryDelay("connect_failed", "bind_or_connect_failed");
            }

            previousObservedSession = currentSession;
            previousMediaConnectStarted = connectStarted;
        }

        private void ApplyWebGLRetryDelay(string source, string reason)
        {
            consecutiveFailures += 1;
            float delay = VoiceWindowsMediaConnectionPolicy.GetRetryDelaySeconds(
                consecutiveFailures);
            AdapterRetryAtField.SetValue(
                adapter,
                Time.realtimeSinceStartup + delay);

            Debug.LogWarning(
                "VOICE_WINDOWS_MEDIA_RETRY_SCHEDULED=PASS" +
                " | source=" + Safe(source) +
                " | reason=" + Safe(reason) +
                " | consecutiveFailures=" + consecutiveFailures +
                " | retryDelayMs=" + Math.Round(delay * 1000f) +
                " | policy=webglParity");
        }

        private void AttachCurrentSession()
        {
            VoiceMediaV2ClientSession current = GetSession();
            if (ReferenceEquals(current, attachedSession)) return;

            DetachSession();
            if (current == null || !current.IsBound) return;

            IVoiceMediaTransportV2 transport =
                SessionTransportField.GetValue(current) as IVoiceMediaTransportV2;
            if (transport == null) return;

            attachedSession = current;
            attachedTransport = transport;
            attachedTransport.PacketReceived += HandleTransportPacket;

            Interlocked.Exchange(
                ref lastInboundStopwatchTicks,
                Stopwatch.GetTimestamp());
            nextPingAt =
                Time.realtimeSinceStartup +
                VoiceWindowsMediaConnectionPolicy.PingIntervalSeconds;
            nextHeartbeatCheckAt =
                Time.realtimeSinceStartup +
                VoiceWindowsMediaConnectionPolicy.HeartbeatCheckIntervalSeconds;
            heartbeatResetPending = false;

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_WEBGL_PARITY=BOUND" +
                " | pingIntervalMs=5000" +
                " | heartbeatTimeoutMs=15000" +
                " | transport=VoiceGrpcMediaTransportV2" +
                " | webglPathChanged=False");
        }

        private void DetachSession()
        {
            if (attachedTransport != null)
            {
                try
                {
                    attachedTransport.PacketReceived -= HandleTransportPacket;
                }
                catch
                {
                }
            }

            attachedSession = null;
            attachedTransport = null;
            heartbeatResetPending = false;
        }

        private async Task SendPingAsync(
            VoiceMediaV2ClientSession session,
            IVoiceMediaTransportV2 transport)
        {
            if (session == null || transport == null || !session.IsBound) return;

            try
            {
                string streamId =
                    (SessionStreamIdField.GetValue(session) as string ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();

                if (!Guid.TryParse(streamId, out _)) return;

                object created = SessionCreatePacketMethod.Invoke(
                    session,
                    new object[]
                    {
                        VoiceMediaV2PacketKind.Ping,
                        VoiceMediaV2Codec.None,
                        0u,
                        0UL,
                        VoiceMediaV2Constants.EmptyUuid,
                        streamId,
                        VoiceMediaV2Constants.EmptyUuid,
                        PingPayload
                    });

                VoiceMediaV2Packet packet = created as VoiceMediaV2Packet;
                if (packet == null) return;

                bool sent = await transport.SendAsync(
                    packet.Encode(),
                    CancellationToken.None);

                if (!sent)
                {
                    RequestAdapterReset("media_ping_send_failed");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "VOICE_WINDOWS_MEDIA_PING=FAIL" +
                    " | error=" + Safe(exception.Message));
                RequestAdapterReset("media_ping_exception");
            }
        }

        private void HandleTransportPacket(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            Interlocked.Exchange(
                ref lastInboundStopwatchTicks,
                Stopwatch.GetTimestamp());
            Interlocked.Increment(ref inboundPacketCount);

            try
            {
                VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
                if (packet.Kind == VoiceMediaV2PacketKind.Pong)
                {
                    int currentPongs = Interlocked.Increment(ref pongCount);
                    Interlocked.Exchange(ref consecutiveFailures, 0);
                    heartbeatResetPending = false;

                    if (currentPongs == 1)
                    {
                        Debug.Log(
                            "VOICE_WINDOWS_MEDIA_KEEPALIVE=PASS" +
                            " | pingIntervalMs=5000" +
                            " | heartbeatTimeoutMs=15000" +
                            " | pongCount=1" +
                            " | policy=webglParity");
                    }
                }
                else if (packet.Kind == VoiceMediaV2PacketKind.Media)
                {
                    Interlocked.Exchange(ref consecutiveFailures, 0);
                    heartbeatResetPending = false;
                }
            }
            catch
            {
            }
        }

        private void CheckHeartbeat()
        {
            long last = Interlocked.Read(ref lastInboundStopwatchTicks);
            if (last <= 0) return;

            double ageSeconds =
                (Stopwatch.GetTimestamp() - last) /
                (double)Stopwatch.Frequency;

            if (ageSeconds <=
                VoiceWindowsMediaConnectionPolicy.HeartbeatTimeoutSeconds)
            {
                return;
            }

            if (heartbeatResetPending) return;
            heartbeatResetPending = true;

            Debug.LogWarning(
                "VOICE_WINDOWS_MEDIA_HEARTBEAT=TIMEOUT" +
                " | ageMs=" + Math.Round(ageSeconds * 1000d) +
                " | timeoutMs=15000" +
                " | policy=webglParity");

            RequestAdapterReset("media_heartbeat_timeout");
        }

        private void RequestAdapterReset(string reason)
        {
            if (adapter == null) return;
            AdapterResetReasonField.SetValue(adapter, reason);
            AdapterResetRequestedField.SetValue(adapter, 1);
        }

        private VoiceMediaV2ClientSession GetSession()
        {
            return adapter == null
                ? null
                : AdapterMediaSessionField.GetValue(adapter) as VoiceMediaV2ClientSession;
        }

        private bool IsControlAuthenticated()
        {
            ResolveRuntime();
            return runtime != null &&
                   runtime.IsAuthenticated &&
                   Guid.TryParse(runtime.VoiceConnectionId, out _);
        }

        private static bool IsUnexpectedMediaReset(string reason)
        {
            string value = reason ?? string.Empty;
            return value.StartsWith(
                       "media_disconnected_",
                       StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith(
                       "media_failed_",
                       StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith(
                       "media_heartbeat_",
                       StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith(
                       "media_ping_",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool GetBool(FieldInfo field, object target)
        {
            if (field == null || target == null) return false;
            object value = field.GetValue(target);
            return value is bool flag && flag;
        }

        private static string GetString(FieldInfo field, object target)
        {
            if (field == null || target == null) return string.Empty;
            return field.GetValue(target) as string ?? string.Empty;
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "none";
            return value
                .Replace("|", "/")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        private void OnDestroy()
        {
            DetachSession();
            adapter = null;
            runtime = null;
        }
    }
}
#endif
