using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Controllers;
using Network_A.DedicatedGameServer.Client;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    /// <summary>
    /// WindowsPlayer-only manual recovery callbacks for scene-owned Unity UI Buttons.
    /// Do not attach WebGL button handlers to these buttons.
    /// This class does not own a transport, microphone, or recovery loop.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WindowsVoiceRecoveryButtons : MonoBehaviour
    {
        private const string VoiceScene = "Grpc_Enviroment_Voice";
        private const string VoiceRoot = "Voice_Client_Runtime_Root";
        private const string HealthUrl = "https://dev-world-3d.metarang.com/health";
        // Never silently lose a user request while the previous recovery is running.
        // A different recovery action is deferred and serialized (never overlaps).
        private static bool operationInProgress;
        private static RecoveryAction? activeAction;
        private static RecoveryAction? queuedAction;
        // Only a genuine non-200 -> 200 HEALTH transition arms this flag.
        // On a network flap, Voice is repaired before attempting one optional
        // acknowledged same-room refresh. Never reset a healthy Realtime stream.
        private static bool pendingNetworkVoiceRepair;
        private static float nextAutoRepairAt;
        private static int failedAutoRepairs;
        private static bool nativeMediaRecoveryTried;
        // Separate network-only action. Never route automatic recovery through
        // the reusable CompleteVoice command on the same Dedicated identity.
        // That produced duplicate Voice auth (code 1) after a real outage.
        private static int networkFreshRoomAttempts;
        // Only ONE optional fallback after the canonical manager completes its recovery.
        // A second room leave/reload is never started merely because an old Voice ID survived.
        private const int MaxNetworkFreshRoomAttempts = 1;
        private const float ExistingVoiceSettleSeconds = 12f;
        // Snapshot ONLY an outage which began AFTER the player had active Voice.
        // A failed initial startup probe must never trigger a Voice reset.
        private static string outageVoiceRoomId = string.Empty;
        private static string outageVoiceConnectionId = string.Empty;
        private static bool voiceWasLiveBeforeOutage;
        private static float eligibleSince = -1f;
        private static float manualOverrideUntil;
        // Dedicated may drop AGAIN after the main Manager already declared HEALTH recovery
        // complete. Voice is bound to Dedicated.ConnectionId on the server. Track that
        // identity independently of the Internet/Realtime recovery epoch, otherwise
        // an old authenticated Voice + Producer can mask a stale server binding.
        private static string observedDedicatedConnectionId = string.Empty;
        private static string voiceBoundDedicatedConnectionId = string.Empty;
        private static string pendingDedicatedConnectionId = string.Empty;
        private static bool dedicatedEpochVoiceRepairPending;
        private static float epochRepairEligibleAt;
        private static int dedicatedEpochRepairsStarted;

        // The HEALTH owner calls this on a verified 200 -> non-200 edge,
        // before the shared managers can clear their room/voice state.
        public static void NotifyHealthLostForVoice()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            pendingNetworkVoiceRepair = false;
            voiceWasLiveBeforeOutage = false;
            nativeMediaRecoveryTried = false;
            networkFreshRoomAttempts = 0;
            outageVoiceRoomId = string.Empty;
            outageVoiceConnectionId = string.Empty;
            eligibleSince = -1f;
            // A newer Internet outage supersedes any unstarted Dedicated-epoch repair.
            // Keep the last *authenticated* Dedicated ID to notice its later replacement.
            dedicatedEpochVoiceRepairPending = false;
            pendingDedicatedConnectionId = string.Empty;
            var room = RealtimeRoomGameServerManager.Instance;
            var runtime = FindVoiceRuntime();
            if (SceneManager.GetActiveScene().name != VoiceScene ||
                room == null || !room.IsJoinedRoom || room.IsInsidePublicLobbyRoom ||
                room.IsRoomExitInProgress || room.IsManualRoomRestartRunning ||
                runtime == null || !runtime.IsVoiceEligible) return;
            var bridge = runtime.GetComponent<WindowsWebRtcVoiceBridge>();
            if (bridge == null)
                bridge = UnityEngine.Object.FindObjectOfType<WindowsWebRtcVoiceBridge>();
            voiceWasLiveBeforeOutage = runtime.IsAuthenticated ||
                !string.IsNullOrWhiteSpace(runtime.VoiceConnectionId) ||
                (bridge != null && bridge.NativeProducerActive);
            if (!voiceWasLiveBeforeOutage) return;
            outageVoiceRoomId = room.CurrentRoomId;
            outageVoiceConnectionId = runtime.VoiceConnectionId;
            Debug.Log("VME2_WIN_VOICE_HEALTH_REPAIR=OUTAGE_SNAPSHOT | activeVoiceBeforeLoss=true" +
                      " | room=" + outageVoiceRoomId);
#endif
        }

        // Invoked exclusively by the Windows HEALTH owner after verified HTTP 200.
        // Never infer InternetUnavailable from Realtime, Voice, Dedicated or gRPC.
        public static void NotifyHealthRecoveredForVoice()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!voiceWasLiveBeforeOutage || string.IsNullOrWhiteSpace(outageVoiceRoomId))
            {
                Debug.Log("VME2_WIN_VOICE_HEALTH_REPAIR=NOT_ARMED | reason=no_active_voice_before_outage_or_initial_startup");
                return;
            }
            pendingNetworkVoiceRepair = true;
            failedAutoRepairs = 0;
            nativeMediaRecoveryTried = false;
            networkFreshRoomAttempts = 0;
            eligibleSince = -1f;
            nextAutoRepairAt = 0f;
            Debug.Log("VME2_WIN_VOICE_HEALTH_REPAIR=ARMED | source=real_in_room_voice_outage" +
                      " | originalRoom=" + outageVoiceRoomId);
#endif
        }

        // Pumped by the existing HEALTH owner; does not make a second HTTP probe
        // and does not open an independent network session.
        public static void PumpHealthVoiceRecovery(bool freshHealth200)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // Always watch the Dedicated identity, EVEN AFTER HEALTH recovery was
            // marked successful and pendingNetworkVoiceRepair was cleared.
            // The old code missed the second, delayed Dedicated disconnect entirely.
            ObserveDedicatedConnectionEpoch(freshHealth200);
            if (dedicatedEpochVoiceRepairPending)
            {
                PumpDedicatedEpochVoiceRecovery(freshHealth200);
                return; // Never run a competing automatic room reload for this epoch.
            }
            if (!pendingNetworkVoiceRepair || !freshHealth200 || operationInProgress ||
                Time.realtimeSinceStartup < nextAutoRepairAt ||
                Time.realtimeSinceStartup < manualOverrideUntil ||
                SceneManager.GetActiveScene().name != VoiceScene) return;
            var room = RealtimeRoomGameServerManager.Instance;
            var dedicated = DedicatedGameServerWsClient.Instance;
            if (room == null || room.IsRoomExitInProgress || room.IsManualRoomRestartRunning ||
                room.IsPermanentRecoveryFailureWaitingForLobby) return;
            // Never recover a different room or an intentional room change.
            if (room.IsJoinedRoom && !string.IsNullOrWhiteSpace(room.CurrentRoomId) &&
                !string.Equals(room.CurrentRoomId, outageVoiceRoomId, StringComparison.Ordinal))
            {
                pendingNetworkVoiceRepair = false;
                voiceWasLiveBeforeOutage = false;
                Debug.Log("VME2_WIN_VOICE_HEALTH_REPAIR=CANCELLED | reason=room_changed");
                return;
            }
            if (!room.IsRealtimeReady || !room.IsJoinedRoom || room.IsRecoveryRunning ||
                dedicated == null || !dedicated.IsReady || !MetaverseNetworkClient.isReady ||
                !string.Equals(room.CurrentRoomId, dedicated.RoomId, StringComparison.Ordinal))
            {
                eligibleSince = -1f;
                return;
            }
            // Both the Voice Runtime and the Bridge must exist before we consider
            // any destructive operation. The initial route builds these at different frames.
            var runtime = FindVoiceRuntime();
            var bridge = runtime != null ? runtime.GetComponent<WindowsWebRtcVoiceBridge>() : null;
            if (bridge == null)
                bridge = UnityEngine.Object.FindObjectOfType<WindowsWebRtcVoiceBridge>();
            if (runtime == null || bridge == null) return;
            if (eligibleSince < 0f) eligibleSince = Time.realtimeSinceStartup;

            // Let the original Realtime/Dedicated manager own the network recovery.
            // A valid control + current-room Voice session + native producer is
            // working regardless of whether its VoiceConnectionId rotated.
            // The previous "must be a NEW Voice ID" check destroyed successfully
            // recovered sessions and triggered a second room reload and UI flow.
            // A Producer created for a PREVIOUS Dedicated connection is not a
            // valid Voice owner even if the old local IsAuthenticated remains true.
            // The server derives VoiceConnectionId from Dedicated.ConnectionId.
            bool voiceAlreadyReady = runtime.IsAuthenticated && !runtime.IsDisconnecting &&
                SameDedicatedIdentity(runtime.VoiceConnectionId, dedicated.ConnectionId) &&
                runtime.ActiveSessionCount > 0 && bridge.NativeProducerActive;
            if (voiceAlreadyReady)
            {
                pendingNetworkVoiceRepair = false;
                voiceWasLiveBeforeOutage = false;
                failedAutoRepairs = 0;
                eligibleSince = -1f;
                Debug.Log("VME2_WIN_NET_VOICE_RECONNECT=EXISTING_VOICE_READY" +
                    " | joinedSessions=" + runtime.ActiveSessionCount +
                    " | producerActive=true | managerRoomRecovered=true" +
                    " | automaticRoomReloadSkipped=true");
                return;
            }

            // The canonical manager may have just finished reconnecting while
            // the Voice control/ICE/consumer are still legitimately negotiating.
            // Never tear down that new owner in its first seconds.
            float roomReadyAge = Time.realtimeSinceStartup - eligibleSince;
            if (roomReadyAge < ExistingVoiceSettleSeconds) return;
            if (!string.Equals(room.CurrentRoomId, outageVoiceRoomId,
                    StringComparison.Ordinal) || room.IsInsidePublicLobbyRoom)
                return;
            if (networkFreshRoomAttempts >= MaxNetworkFreshRoomAttempts)
            {
                pendingNetworkVoiceRepair = false;
                voiceWasLiveBeforeOutage = false;
                Debug.LogWarning("VME2_WIN_NET_VOICE_RECONNECT=EXHAUSTED" +
                    " | attempts=" + networkFreshRoomAttempts +
                    " | manualReloadRoomRemainsAvailable=true" +
                    " | healthVerdictUnchanged=true");
                return;
            }
            // Do not initiate a SECOND room reload from an independent Voice
            // repair loop. The canonical Manager owns room/Dedicated recovery;
            // the existing Voice AutoConnector and Windows bridge own retries.
            // Dedicated epoch changes are handled by the voice-only epoch path.
            nextAutoRepairAt = Time.realtimeSinceStartup + 8f;
            Debug.LogWarning("VME2_WIN_NET_VOICE_RECONNECT=INCOMPLETE" +
                " | controlAuthenticated=" + runtime.IsAuthenticated +
                " | producerActive=" + bridge.NativeProducerActive +
                " | dedicatedIdentityMatches=" +
                SameDedicatedIdentity(runtime.VoiceConnectionId, dedicated.ConnectionId) +
                " | action=existing_connector_retry_only" +
                " | extraRoomReload=false" +
                " | consumerAndPcmNotVerified=true");
#endif
        }


        // GUID format differs between Dedicated (N, 32 hex digits) and Voice (D,
        // hyphenated). Compare the 128-bit identity, never string formatting.
        private static bool SameDedicatedIdentity(string voiceId, string dedicatedId)
        {
            Guid voice, dedicated;
            return Guid.TryParse(voiceId, out voice) &&
                   Guid.TryParse(dedicatedId, out dedicated) && voice == dedicated;
        }

        private static void ObserveDedicatedConnectionEpoch(bool health200)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (SceneManager.GetActiveScene().name != VoiceScene) return;
            var room = RealtimeRoomGameServerManager.Instance;
            var ws = DedicatedGameServerWsClient.Instance;
            if (room == null || ws == null || !ws.IsReady || !room.IsJoinedRoom ||
                room.IsInsidePublicLobbyRoom ||
                !string.Equals(room.CurrentRoomId, ws.RoomId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(ws.ConnectionId)) return;

            string latest = ws.ConnectionId;
            var runtime = FindVoiceRuntime();
            bool currentVoiceMatches = runtime != null && runtime.IsAuthenticated &&
                SameDedicatedIdentity(runtime.VoiceConnectionId, latest);

            // A second Dedicated epoch can arrive while the previous epoch's
            // Voice cleanup is in flight. Preserve that *new* target; do not
            // consume it as though it were a manual room reload.
            if (operationInProgress && activeAction == RecoveryAction.DedicatedEpochVoice)
            {
                if (!string.IsNullOrWhiteSpace(observedDedicatedConnectionId) &&
                    !SameDedicatedIdentity(observedDedicatedConnectionId, latest))
                {
                    observedDedicatedConnectionId = latest;
                    if (!currentVoiceMatches)
                    {
                        pendingDedicatedConnectionId = latest;
                        dedicatedEpochVoiceRepairPending = true;
                        epochRepairEligibleAt = Time.realtimeSinceStartup + 0.25f;
                        Debug.LogWarning("VME2_WIN_VOICE_DEDICATED_EPOCH=SUPERSEDED" +
                            " | nextEpochQueued=true | roomReload=false");
                    }
                }
                return;
            }
            // A second Dedicated epoch during a manual Voice-only reset must not
            // be swallowed. Queue the new epoch for the existing serialized pump.
            // This never applies to the manual RoomFromBeginning action.
            if (operationInProgress && activeAction == RecoveryAction.CompleteVoice)
            {
                if (!string.IsNullOrWhiteSpace(observedDedicatedConnectionId) &&
                    !SameDedicatedIdentity(observedDedicatedConnectionId, latest) &&
                    !currentVoiceMatches)
                {
                    pendingDedicatedConnectionId = latest;
                    dedicatedEpochVoiceRepairPending = true;
                    epochRepairEligibleAt = Time.realtimeSinceStartup + 0.5f;
                    Debug.LogWarning("VME2_WIN_VOICE_DEDICATED_EPOCH=QUEUED_DURING_MANUAL_VOICE" +
                        " | roomReload=false");
                }
                observedDedicatedConnectionId = latest;
                return;
            }
            // Room reload intentionally replaces Dedicated and its Voice root.
            // Do not turn that manual action into an automatic epoch repair.
            if (operationInProgress || Time.realtimeSinceStartup < manualOverrideUntil ||
                room.IsManualRoomRestartRunning || room.IsRoomExitInProgress)
            {
                observedDedicatedConnectionId = latest;
                if (currentVoiceMatches) voiceBoundDedicatedConnectionId = latest;
                dedicatedEpochVoiceRepairPending = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(observedDedicatedConnectionId))
                observedDedicatedConnectionId = latest;

            bool changed = !SameDedicatedIdentity(observedDedicatedConnectionId, latest);
            if (changed)
            {
                // Do not reset initial scene entry, when Voice never existed.
                bool voicePreviouslyBound = !string.IsNullOrWhiteSpace(voiceBoundDedicatedConnectionId) &&
                    !SameDedicatedIdentity(voiceBoundDedicatedConnectionId, latest);
                observedDedicatedConnectionId = latest;
                if (voicePreviouslyBound && !currentVoiceMatches)
                {
                    pendingDedicatedConnectionId = latest;
                    dedicatedEpochVoiceRepairPending = true;
                    epochRepairEligibleAt = Time.realtimeSinceStartup;
                    // The network HEALTH repair concluded before this *new* epoch.
                    // It must not invoke automatic ROOM reload simultaneously.
                    pendingNetworkVoiceRepair = false;
                    voiceWasLiveBeforeOutage = false;
                    eligibleSince = -1f;
                    Debug.LogWarning("VME2_WIN_VOICE_DEDICATED_EPOCH=CHANGED" +
                        " | voiceBoundToPreviousDedicated=true" +
                        " | action=voice_only_fresh_identity" +
                        " | automaticRoomReload=false | manualReloadUnchanged=true");
                }
            }

            if (currentVoiceMatches)
            {
                voiceBoundDedicatedConnectionId = latest;
                if (dedicatedEpochVoiceRepairPending &&
                    SameDedicatedIdentity(pendingDedicatedConnectionId, latest) &&
                    runtime.ActiveSessionCount > 0)
                {
                    // New Control auth ALREADY succeeded on this Dedicated ID.
                    // Rotating its clientInstanceId now would collide with that
                    // ACTIVE server registry record. Leave its Native producer
                    // and Consumer negotiation to the existing WebRTC bridge.
                    dedicatedEpochVoiceRepairPending = false;
                    var bridge = runtime.GetComponent<WindowsWebRtcVoiceBridge>();
                    Debug.Log("VME2_WIN_VOICE_DEDICATED_EPOCH=CONTROL_REBOUND" +
                        " | producerActive=" + (bridge != null && bridge.NativeProducerActive) +
                        " | voiceIdentityRotationSkipped=true | roomReload=false");
                }
            }
#endif
        }

        private static void PumpDedicatedEpochVoiceRecovery(bool health200)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!health200 || operationInProgress || queuedAction.HasValue ||
                Time.realtimeSinceStartup < manualOverrideUntil ||
                Time.realtimeSinceStartup < epochRepairEligibleAt) return;
            var room = RealtimeRoomGameServerManager.Instance;
            var ws = DedicatedGameServerWsClient.Instance;
            if (room == null || ws == null || !room.IsRealtimeReady || !room.IsJoinedRoom ||
                room.IsRecoveryRunning || room.IsRoomExitInProgress ||
                room.IsManualRoomRestartRunning || !ws.IsReady || !MetaverseNetworkClient.isReady ||
                !string.Equals(room.CurrentRoomId, ws.RoomId, StringComparison.Ordinal) ||
                !SameDedicatedIdentity(ws.ConnectionId, pendingDedicatedConnectionId)) return;
            var runtime = FindVoiceRuntime();
            if (runtime == null || runtime.IsDisconnecting) return;
            var connector = runtime.GetComponent("VoiceClientAutoConnector") as Behaviour;
            var bridge = runtime.GetComponent<WindowsWebRtcVoiceBridge>();
            if (connector == null || bridge == null) return;

            if (runtime.IsAuthenticated &&
                SameDedicatedIdentity(runtime.VoiceConnectionId, ws.ConnectionId) &&
                runtime.ActiveSessionCount > 0)
            {
                dedicatedEpochVoiceRepairPending = false;
                voiceBoundDedicatedConnectionId = ws.ConnectionId;
                Debug.Log("VME2_WIN_VOICE_DEDICATED_EPOCH=CONTROL_REBOUND" +
                    " | producerActive=" + bridge.NativeProducerActive +
                    " | noIdentityRotation=true | roomReload=false");
                return;
            }
            var handler = UnityEngine.Object.FindObjectOfType<WindowsVoiceRecoveryButtons>();
            if (handler == null) return;
            dedicatedEpochVoiceRepairPending = false;
            dedicatedEpochRepairsStarted++;
            Debug.Log("VME2_WIN_VOICE_DEDICATED_EPOCH=VOICE_RESET_REQUESTED" +
                " | count=" + dedicatedEpochRepairsStarted +
                " | controlBoundToCurrentDedicated=false" +
                " | freshVoiceInstance=true | roomReload=false");
            handler.Execute(RecoveryAction.DedicatedEpochVoice, true);
#endif
        }

        /// <summary>Button 1: restart Native SFU media only; never leave the room.</summary>
        public void ReloadSound()
        {
            Execute(RecoveryAction.MediaOnly);
        }

        /// <summary>Button 2: restart Voice control and Native media; never leave the room.</summary>
        public void RecoverCompleteVoice()
        {
            Execute(RecoveryAction.CompleteVoice);
        }

        /// <summary>Button 3: stop Voice, explicitly leave and rejoin the same server room, then restart Voice.</summary>
        public void ReloadRoom()
        {
            Execute(RecoveryAction.RoomFromBeginning);
        }

        private enum RecoveryAction { MediaOnly, CompleteVoice, RoomFromBeginning, NetworkReconnectVoice, DedicatedEpochVoice }

        private void Execute(RecoveryAction action, bool automatic = false)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (automatic && (operationInProgress || queuedAction.HasValue)) return;
            if (!automatic) manualOverrideUntil = Time.realtimeSinceStartup + 25f;
            if (operationInProgress)
            {
                if (activeAction == action)
                {
                    Debug.Log("VME2_WIN_MANUAL_RECOVERY=ALREADY_RUNNING | action=" + action);
                    return;
                }
                // Full room recovery supersedes less comprehensive operations.
                if (!queuedAction.HasValue || action == RecoveryAction.RoomFromBeginning ||
                    queuedAction.Value != RecoveryAction.RoomFromBeginning)
                    queuedAction = action;
                Debug.Log("VME2_WIN_MANUAL_RECOVERY=QUEUED | active=" + activeAction +
                    " | pending=" + queuedAction);
                return;
            }
            if (!string.Equals(SceneManager.GetActiveScene().name, VoiceScene, StringComparison.Ordinal))
            {
                Debug.LogWarning("VME2_WIN_MANUAL_RECOVERY=SKIPPED | reason=not_voice_gameplay_scene | action=" + action);
                return;
            }
            operationInProgress = true;
            activeAction = action;
            Debug.Log("VME2_WIN_MANUAL_RECOVERY=ACCEPTED | action=" + action);
            _ = ExecuteAsync(action, automatic);
#else
            Debug.Log("VME2_WIN_MANUAL_RECOVERY=SKIPPED | reason=windows_player_only");
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private sealed class VoiceRecoveryHold
        {
            public VoiceClientRuntime Runtime;
            public WindowsWebRtcVoiceBridge Bridge;
            public Behaviour Connector;
            public bool ConnectorWasEnabled;
            public bool BridgeWasEnabled;
            public bool MicrophoneMuted;
            public bool SpeakerOff;
            public bool RecordingConsentDesired;
            public string OldVoiceConnectionId;
        }

        private static async Task ExecuteAsync(RecoveryAction action, bool automatic)
        {
            try
            {
                if (!await VerifyHealth200Async())
                {
                    Debug.LogWarning("VME2_WIN_MANUAL_RECOVERY=BLOCKED | reason=health_not_200 | action=" + action);
                    return;
                }
                var room = RealtimeRoomGameServerManager.Instance;
                if (room == null || room.IsRoomExitInProgress || room.IsManualRoomRestartRunning)
                {
                    Debug.LogWarning("VME2_WIN_MANUAL_RECOVERY=BLOCKED | reason=room_manager_busy_or_missing | action=" + action);
                    return;
                }

                if (action == RecoveryAction.RoomFromBeginning)
                {
                    // Existing manual button path: byte-for-byte unchanged.
                    await RestartWindowsRoomAsync(room);
                    return;
                }
                if (action == RecoveryAction.NetworkReconnectVoice)
                {
                    // Separate automatic entry point; never reuse same-Dedicated
                    // CompleteVoice and never race the already recovered Manager.
                    Debug.Log("VME2_WIN_NET_VOICE_RECONNECT=EXECUTING" +
                        " | action=network_only | manualButtonUnchanged=true");
                    await RestartWindowsRoomAsync(room);
                    return;
                }
                if (action == RecoveryAction.DedicatedEpochVoice)
                {
                    // A late Dedicated reauthentication is NOT an Internet edge.
                    // VoiceConnectionId is derived from the new Dedicated ID on
                    // the server, so do NOT reuse clientInstanceId from the old
                    // Dedicated. Unlike manual room Reload, no scene or room exit.
                    var ws = DedicatedGameServerWsClient.Instance;
                    if (ws == null || !ws.IsReady || room.IsRecoveryRunning ||
                        !room.IsRealtimeReady || !room.IsJoinedRoom ||
                        !MetaverseNetworkClient.isReady ||
                        !SameDedicatedIdentity(ws.ConnectionId, pendingDedicatedConnectionId))
                    {
                        dedicatedEpochVoiceRepairPending = true;
                        epochRepairEligibleAt = Time.realtimeSinceStartup + 0.5f;
                        Debug.Log("VME2_WIN_VOICE_DEDICATED_EPOCH=DEFERRED | reason=existing_manager_not_ready");
                        return;
                    }
                    VoiceRecoveryHold hold = null;
                    try
                    {
                        hold = await CloseWindowsVoiceAsync("dedicated_connection_epoch_changed", true);
                        if (hold == null)
                        {
                            dedicatedEpochVoiceRepairPending = true;
                            epochRepairEligibleAt = Time.realtimeSinceStartup + 1f;
                            Debug.LogWarning("VME2_WIN_VOICE_DEDICATED_EPOCH=DEFERRED | reason=voice_runtime_busy");
                            return;
                        }
                    }
                    finally
                    {
                        if (hold != null) RestoreWindowsVoice(hold, "after_dedicated_epoch_changed");
                    }
                    // A new Dedicated identity can supersede this async repair.
                    // Never count a previous epoch as a successful current binding.
                    if (ws == null || !ws.IsReady ||
                        !SameDedicatedIdentity(ws.ConnectionId, pendingDedicatedConnectionId))
                    {
                        dedicatedEpochVoiceRepairPending = true;
                        epochRepairEligibleAt = Time.realtimeSinceStartup + 0.5f;
                        Debug.LogWarning("VME2_WIN_VOICE_DEDICATED_EPOCH=SUPERSEDED_AFTER_CLOSE | action=queue_latest_epoch");
                        return;
                    }
                    bool established = await WaitForVoiceReauthenticationAsync(hold, "dedicated_epoch_changed");
                    if (ws == null || !ws.IsReady ||
                        !SameDedicatedIdentity(ws.ConnectionId, pendingDedicatedConnectionId))
                    {
                        dedicatedEpochVoiceRepairPending = true;
                        epochRepairEligibleAt = Time.realtimeSinceStartup + 0.5f;
                        Debug.LogWarning("VME2_WIN_VOICE_DEDICATED_EPOCH=SUPERSEDED_AFTER_WAIT | action=queue_latest_epoch");
                        return;
                    }
                    if (established && SameDedicatedIdentity(hold.Runtime.VoiceConnectionId, ws.ConnectionId))
                    {
                        voiceBoundDedicatedConnectionId = ws.ConnectionId;
                        Debug.Log("VME2_WIN_VOICE_DEDICATED_EPOCH=PASS | newDedicatedVoiceBound=true" +
                            " | roomReload=false | consumerMustBeCheckedSeparately=true");
                    }
                    else
                        Debug.LogWarning("VME2_WIN_VOICE_DEDICATED_EPOCH=PENDING | newVoiceAuthOrMediaIncomplete" +
                            " | existingConnectorAutoRetry=true | noExtraRoomReload=true");
                    return;
                }
                if (!room.IsRealtimeReady || !room.IsJoinedRoom || !MetaverseNetworkClient.isReady)
                {
                    // A manual click during the manager's recovery must not be
                    // thrown away. Wait for the EXISTING owner; never force a
                    // second competing transport reset.
                    Debug.Log("VME2_WIN_MANUAL_RECOVERY=WAIT_FOR_EXISTING_OWNER | action=" + action);
                    float deadline = Time.realtimeSinceStartup + 18f;
                    while (Time.realtimeSinceStartup < deadline &&
                           (!room.IsRealtimeReady || !room.IsJoinedRoom || !MetaverseNetworkClient.isReady))
                    {
                        if (room.IsRoomExitInProgress || room.IsManualRoomRestartRunning ||
                            RealtimeRoomGameServerManager.Instance != room) break;
                        await Task.Yield();
                    }
                    if (!room.IsRealtimeReady || !room.IsJoinedRoom || !MetaverseNetworkClient.isReady)
                    {
                        Debug.LogWarning("VME2_WIN_MANUAL_RECOVERY=PENDING | reason=existing_owner_not_ready | action=" + action +
                                         " | internet_verdict_not_changed=true");
                        RecordNetworkVoiceRecoveryResult(false, "manual_wait_owner");
                        return;
                    }
                }

                if (action == RecoveryAction.MediaOnly)
                {
                    var currentRuntime = FindVoiceRuntime();
                    if (currentRuntime == null || !currentRuntime.IsAuthenticated || currentRuntime.IsDisconnecting)
                    {
                        Debug.LogWarning("VME2_WIN_MEDIA_RELOAD=BLOCKED | reason=control_not_ready");
                        return;
                    }
                    ResetNativeMediaWithoutRoomExit();
                    return;
                }

                VoiceRecoveryHold recovery = null;
                try
                {
                    // If the Dedicated websocket has rotated, server Voice ownership
                    // belongs to that new Dedicated ID. A manual Voice reset must not
                    // reuse the client instance of the old Dedicated connection.
                    // Never rotate on the SAME Dedicated ID: that can collide with
                    // an unacknowledged old Voice registry record.
                    var currentDedicated = DedicatedGameServerWsClient.Instance;
                    var currentVoice = FindVoiceRuntime();
                    bool alreadyBoundToCurrentDedicated = currentVoice != null &&
                        currentVoice.IsAuthenticated && currentDedicated != null &&
                        SameDedicatedIdentity(currentVoice.VoiceConnectionId,
                            currentDedicated.ConnectionId);
                    bool staleDedicatedVoiceIdentity = !automatic &&
                        currentDedicated != null && currentDedicated.IsReady &&
                        !alreadyBoundToCurrentDedicated &&
                        (recoveryWasBoundToOtherDedicated(currentDedicated.ConnectionId) ||
                         (currentVoice != null &&
                          !string.IsNullOrWhiteSpace(currentVoice.VoiceConnectionId) &&
                          !SameDedicatedIdentity(currentVoice.VoiceConnectionId,
                              currentDedicated.ConnectionId)));
                    if (!automatic && (currentDedicated == null || !currentDedicated.IsReady ||
                        !string.Equals(room.CurrentRoomId, currentDedicated.RoomId,
                            StringComparison.Ordinal)))
                    {
                        Debug.LogWarning("VME2_WIN_MANUAL_VOICE_VERDICT=WAIT_DEDICATED" +
                            " | reason=dedicated_not_ready_or_room_mismatch" +
                            " | roomReload=false");
                        return;
                    }
                    recovery = await CloseWindowsVoiceAsync(
                        automatic ? "after_real_network_outage" : "manual_complete_voice",
                        rotateClientInstance: staleDedicatedVoiceIdentity);
                    if (staleDedicatedVoiceIdentity)
                        Debug.Log("VME2_WIN_MANUAL_VOICE_VERDICT=STALE_DEDICATED_IDENTITY" +
                            " | action=new_voice_instance_for_new_dedicated_only");
                    if (recovery == null)
                    {
                        RecordNetworkVoiceRecoveryResult(false, "voice_close_failed");
                        return;
                    }
                    Debug.Log("VME2_WIN_FULL_VOICE_RECOVERY=OLD_VOICE_RELEASED | roomExit=false" +
                              " | existing_realtime_preserved=true");
                    // The manager already performed its own authenticated same-room
                    // recovery on HEALTH 200. The previous patch disconnected it AGAIN,
                    // producing gRPC timeouts and breaking the ReloadRoom button.
                    // No private Realtime transport API may be called from Voice reset.
                }
                finally
                {
                    if (recovery != null)
                    {
                        RestoreWindowsVoice(recovery, "manual_complete_voice");
                    }
                }
                if (recovery != null)
                {
                    bool voiceOkay = await WaitForVoiceReauthenticationAsync(recovery, "manual_complete_voice");
                    RecordNetworkVoiceRecoveryResult(voiceOkay, "complete_voice");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("VME2_WIN_MANUAL_RECOVERY=ERROR | action=" + action +
                    " | detail=" + Sanitize(ex.GetType().Name + ":" + ex.Message));
            }
            finally
            {
                operationInProgress = false;
                activeAction = null;
                if (action == RecoveryAction.CompleteVoice && dedicatedEpochVoiceRepairPending)
                    manualOverrideUntil = Mathf.Min(manualOverrideUntil,
                        Time.realtimeSinceStartup + 1f);
                if (queuedAction.HasValue)
                {
                    RecoveryAction deferred = queuedAction.Value;
                    queuedAction = null;
                    if (string.Equals(SceneManager.GetActiveScene().name, VoiceScene, StringComparison.Ordinal))
                    {
                        operationInProgress = true;
                        activeAction = deferred;
                        Debug.Log("VME2_WIN_MANUAL_RECOVERY=DEQUEUED | action=" + deferred);
                        _ = ExecuteAsync(deferred, false);
                    }
                    else
                        Debug.LogWarning("VME2_WIN_MANUAL_RECOVERY=QUEUED_CANCELLED | reason=left_voice_scene");
                }
            }
        }

        private static bool ReadNativeProgressFlag(WindowsWebRtcVoiceBridge bridge, string name)
        {
            if (bridge == null) return false;
            var field = typeof(WindowsWebRtcVoiceBridge).GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic);
            // Fail closed on an unknown bridge version: the SHA-guarded installer
            // also pins the current source revision.
            return field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(bridge);
        }

        private static VoiceClientRuntime FindVoiceRuntime()
        {
            GameObject root = GameObject.Find(VoiceRoot);
            return root != null ? root.GetComponent<VoiceClientRuntime>() : null;
        }

        // Destructive room reload only: don't reuse the old Voice Runtime.
        // This is deliberately separate from RecoverCompleteVoice and automatic
        // outage recovery; those are allowed to reuse an active Dedicated socket.
        private static async Task<VoiceRecoveryHold> CloseVoiceForFinalRoomExitAsync()
        {
            var runtime = FindVoiceRuntime();
            if (runtime == null || runtime.IsDisconnecting)
            {
                Debug.LogWarning("VME2_WIN_ROOM_VOICE_EXIT=BLOCKED | reason=runtime_missing_or_disconnecting");
                return null;
            }
            var connector = runtime.GetComponent("VoiceClientAutoConnector") as Behaviour;
            var bridge = runtime.GetComponent<WindowsWebRtcVoiceBridge>();
            if (bridge == null)
                bridge = UnityEngine.Object.FindObjectOfType<WindowsWebRtcVoiceBridge>();
            if (connector == null || bridge == null)
            {
                Debug.LogWarning("VME2_WIN_ROOM_VOICE_EXIT=BLOCKED | reason=connector_or_native_bridge_missing");
                return null;
            }
            var hold = new VoiceRecoveryHold
            {
                Runtime = runtime,
                Connector = connector,
                ConnectorWasEnabled = connector.enabled,
                Bridge = bridge,
                BridgeWasEnabled = bridge.enabled,
                MicrophoneMuted = runtime.IsMicrophoneMuted,
                SpeakerOff = runtime.IsSpeakerOff,
                RecordingConsentDesired = ReadRecordingConsentDesired(runtime),
                OldVoiceConnectionId = runtime.VoiceConnectionId
            };
            connector.enabled = false;
            bridge.enabled = false;
            Debug.Log("VME2_WIN_ROOM_VOICE_EXIT=OWNERS_STOPPED | hadAuthenticatedControl=" +
                runtime.IsAuthenticated + " | hadNativeProducer=" + bridge.NativeProducerActive);
            // Unlike DisconnectForPlayerUnavailableAsync, this waits for the
            // server-side transport closure. It permanently ends the old runtime,
            // which is why we MUST destroy/recreate the entire root afterward.
            bool ack = await runtime.DisconnectGracefullyAsync(
                "windows_manual_room_reload_final_exit", 6000, CancellationToken.None);
            Debug.Log("VME2_WIN_ROOM_VOICE_EXIT=" + (ack ? "SERVER_CLOSE_ACK" : "SERVER_CLOSE_UNCONFIRMED") +
                " | oldVoiceIdPresent=" + !string.IsNullOrWhiteSpace(hold.OldVoiceConnectionId) +
                " | next=server_room_leave_then_runtime_recreate");
            return hold;
        }

        private static async Task<bool> RetireOldVoiceRootAsync(VoiceRecoveryHold hold)
        {
            if (hold == null || hold.Runtime == null) return false;
            GameObject root = hold.Runtime.gameObject;
            if (root == null || root.name != VoiceRoot) return false;
            UnityEngine.Object.Destroy(root);
            float deadline = Time.realtimeSinceStartup + 3f;
            while (GameObject.Find(VoiceRoot) != null && Time.realtimeSinceStartup < deadline)
                await Task.Yield();
            bool retired = GameObject.Find(VoiceRoot) == null;
            Debug.Log("VME2_WIN_ROOM_VOICE_EXIT=RUNTIME_DESTROYED | result=" + retired +
                " | oldRuntimeReused=false");
            return retired;
        }

        private static bool HoldFreshVoiceRuntimeAfterSceneReload(VoiceRecoveryHold hold)
        {
            if (hold == null) return false;
            VoiceClientRuntime fresh = FindVoiceRuntime();
            if (fresh == null || ReferenceEquals(fresh, hold.Runtime)) return false;
            var connector = fresh.GetComponent("VoiceClientAutoConnector") as Behaviour;
            if (connector == null) return false;
            connector.enabled = false;
            hold.Runtime = fresh;
            hold.Connector = connector;
            hold.Bridge = fresh.GetComponent<WindowsWebRtcVoiceBridge>();
            Debug.Log("VME2_WIN_ROOM_VOICE_FRESH=RUNTIME_CREATED | connectorPaused=true" +
                " | oldRuntimeReused=false | originalMicMuted=" + hold.MicrophoneMuted +
                " | originalSpeakerOff=" + hold.SpeakerOff);
            return true;
        }

        private static async Task<bool> CaptureFreshVoiceBridgeAsync(VoiceRecoveryHold hold)
        {
            if (hold == null || hold.Runtime == null) return false;
            float deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var bridge = hold.Runtime.GetComponent<WindowsWebRtcVoiceBridge>();
                if (bridge != null)
                {
                    bridge.enabled = false;
                    hold.Bridge = bridge;
                    Debug.Log("VME2_WIN_ROOM_VOICE_FRESH=NATIVE_BRIDGE_READY | oldBridgeReused=false");
                    return true;
                }
                await Task.Yield();
            }
            return false;
        }

        // This is a STOP barrier, not merely resetting the Native producer.
        // The existing AutoConnector MUST be paused *before* sending Disconnect,
        // otherwise it can reconnect to the old dedicated room between the Voice
        // DISCONNECT and the server-confirmed realtime LEAVE.
        // The bound identity is only a hint when the Runtime was already cleared;
        // it does not override the current authoritative Dedicated connection.
        private static bool recoveryWasBoundToOtherDedicated(string dedicatedConnectionId)
        {
            return !string.IsNullOrWhiteSpace(voiceBoundDedicatedConnectionId) &&
                !SameDedicatedIdentity(voiceBoundDedicatedConnectionId, dedicatedConnectionId);
        }

        private static async Task<VoiceRecoveryHold> CloseWindowsVoiceAsync(
            string reason, bool rotateClientInstance = true)
        {
            VoiceClientRuntime runtime = FindVoiceRuntime();
            if (runtime == null || runtime.IsDisconnecting)
            {
                Debug.LogWarning("VME2_WIN_VOICE_CLOSE=BLOCKED | reason=runtime_missing_or_busy");
                return null;
            }
            var connector = runtime.GetComponent("VoiceClientAutoConnector") as Behaviour;
            var bridge = runtime.GetComponent<WindowsWebRtcVoiceBridge>();
            if (bridge == null)
                bridge = UnityEngine.Object.FindObjectOfType<WindowsWebRtcVoiceBridge>();
            if (connector == null || bridge == null)
            {
                Debug.LogWarning("VME2_WIN_VOICE_CLOSE=BLOCKED | reason=connector_or_native_bridge_missing | connector=" +
                    (connector != null) + " | bridge=" + (bridge != null));
                return null;
            }
            var hold = new VoiceRecoveryHold
            {
                Runtime = runtime,
                Connector = connector,
                ConnectorWasEnabled = connector.enabled,
                Bridge = bridge,
                BridgeWasEnabled = bridge.enabled,
                MicrophoneMuted = runtime.IsMicrophoneMuted,
                SpeakerOff = runtime.IsSpeakerOff,
                RecordingConsentDesired = ReadRecordingConsentDesired(runtime),
                OldVoiceConnectionId = runtime.VoiceConnectionId
            };
            try
            {
                connector.enabled = false; // cancels any in-flight ConnectAsync
                bridge.enabled = false;    // OnDisable disposes SFU Producer/Consumers
                Debug.Log("VME2_WIN_VOICE_CLOSE=STOPPED_OWNERS | reason=" + reason +
                    " | nativeBridgeOff=true | autoConnectorOff=true | hadVoiceId=" +
                    !string.IsNullOrWhiteSpace(hold.OldVoiceConnectionId));

                // PlayerUnavailable is reusable; DisconnectGracefullyAsync is NOT:
                // its shuttingDown flag permanently disables this Runtime.
                bool sent = await runtime.DisconnectForPlayerUnavailableAsync(
                    "windows_explicit_voice_reset_" + reason, 4000, CancellationToken.None);
                bool locallyClosed = !runtime.IsAuthenticated &&
                    string.IsNullOrWhiteSpace(runtime.VoiceConnectionId) &&
                    !runtime.IsDisconnecting;
                if (!locallyClosed)
                {
                    Debug.LogWarning("VME2_WIN_VOICE_CLOSE=FAILED | stage=control_transport_still_active");
                    RestoreWindowsVoice(hold, "cleanup_failed_restore");
                    return null;
                }
                Debug.Log("VME2_WIN_VOICE_CLOSE=LOCAL_CLOSED | disconnectSent=" + sent +
                    " | oldConnectionIdPresent=" + !string.IsNullOrWhiteSpace(hold.OldVoiceConnectionId) +
                    " | serverCloseAck=not_exposed_by_reusable_runtime_api");
                // Scene reload reuses the persistent Voice Runtime.
                // Voice registry derives its connection id from the current
                // Dedicated connection id. On SAME Dedicated, changing only
                // clientInstanceId causes an INVALID_PAYLOAD collision if the
                // previous registry record remains. CompleteVoice (automatic
                // and manual) keeps that identity to permit server-side resume.
                // Full room reload can rotate it because its Dedicated connection
                // will be closed and reauthenticated after server-confirmed LEAVE.
                // This reusable API has NO server-close acknowledgement.
                // IMPORTANT: the server binds a Voice connection to the Dedicated
                // connection ID. Changing only clientInstanceId while continuing to
                // use the SAME Dedicated connection can produce:
                // "Dedicated connection id belongs to another Voice connection".
                // Preserve the client identity for automatic same-Dedicated recovery.
                // Room reload rotates after the confirmed new Dedicated JOIN;
                // CompleteVoice (manual OR auto) preserves it on the same Dedicated.
                if (rotateClientInstance)
                {
                    if (!StartFreshVoiceInstanceAfterClose(runtime, reason))
                    {
                        Debug.LogError("VME2_WIN_VOICE_CLOSE=FAILED | stage=rotate_fresh_instance | prior_owners_restored=true");
                        RestoreWindowsVoice(hold, "rotate_failed_restore");
                        return null;
                    }
                }
                else
                    Debug.Log("VME2_WIN_VOICE_CLIENT_ID=PRESERVED | source=automatic_network_recovery" +
                              " | dedicatedConnectionNotReplaced=true");
                await Task.Yield();
                return hold;
            }
            catch
            {
                RestoreWindowsVoice(hold, "cleanup_exception_restore");
                throw;
            }
        }

        private static bool StartFreshVoiceInstanceAfterClose(VoiceClientRuntime runtime, string reason)
        {
            if (runtime == null || runtime.IsAuthenticated || runtime.IsDisconnecting ||
                !string.IsNullOrWhiteSpace(runtime.VoiceConnectionId))
                return false;
            FieldInfo idField = typeof(VoiceClientRuntime).GetField(
                "clientInstanceId", BindingFlags.NonPublic | BindingFlags.Instance);
            if (idField == null || idField.FieldType != typeof(string))
            {
                Debug.LogError("VME2_WIN_VOICE_NEW_INSTANCE=FAIL | reason=field_not_found");
                return false;
            }
            string previous = (string)idField.GetValue(runtime);
            if (!Guid.TryParse(previous, out _))
                return false;
            string current = Guid.NewGuid().ToString("D");
            idField.SetValue(runtime, current);
            Debug.Log("VME2_WIN_VOICE_NEW_INSTANCE=PASS | reason=" + reason +
                " | oldAndNewDiffer=" + !string.Equals(previous, current, StringComparison.Ordinal) +
                " | previousServerCloseAck=not_exposed | onlyManualFullRecovery=true");
            return true;
        }

        private static void RestoreWindowsVoice(VoiceRecoveryHold hold, string reason)
        {
            if (hold == null) return;
            try
            {
                if (hold.Runtime != null)
                {
                    // PlayerUnavailable clears the Mic and recording consent.
                    // Restore the user's saved Mic/Speaker choices; NEVER unmute
                    // someone who was muted when they clicked the button.
                    if (hold.Runtime.IsMicrophoneMuted != hold.MicrophoneMuted)
                        hold.Runtime.SetMicrophoneMuted(hold.MicrophoneMuted);
                    if (hold.Runtime.IsSpeakerOff != hold.SpeakerOff)
                        hold.Runtime.SetSpeakerOff(hold.SpeakerOff);
                    // Consent is independent of the Mic toggle; restore the
                    // exact previously chosen consent, never infer it from Mic.
                    hold.Runtime.SetRecordingConsentForAll(hold.RecordingConsentDesired);
                }
                if (hold.Bridge != null)
                {
                    hold.Bridge.enabled = hold.BridgeWasEnabled;
                    if (hold.BridgeWasEnabled)
                    {
                        FieldInfo retry = typeof(WindowsWebRtcVoiceBridge).GetField(
                            "retryAt", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (retry != null && retry.FieldType == typeof(float))
                            retry.SetValue(hold.Bridge, 0f);
                    }
                }
                if (hold.Connector != null)
                {
                    // Its OnDisable sets attempted=true. Clear that stale latch
                    // and the 3s retry timer *before* resuming it.
                    Type connectorType = hold.Connector.GetType();
                    SetPrivateField(connectorType, hold.Connector, "attempted", false);
                    SetPrivateField(connectorType, hold.Connector, "nextAttemptAt", 0f);
                    hold.Connector.enabled = hold.ConnectorWasEnabled;
                    SetPrivateField(connectorType, hold.Connector, "attempted", false);
                    SetPrivateField(connectorType, hold.Connector, "nextAttemptAt", 0f);
                }
                Debug.Log("VME2_WIN_VOICE_RESTART=OWNER_RESUMED | reason=" + reason +
                    " | micMuted=" + hold.MicrophoneMuted + " | speakerOff=" + hold.SpeakerOff +
                    " | recordingConsent=" + hold.RecordingConsentDesired +
                    " | reconnect=existing_auto_connector");
            }
            catch (Exception ex)
            {
                Debug.LogError("VME2_WIN_VOICE_RESTART=ERROR | detail=" + Sanitize(ex.Message));
            }
        }

        private static bool ReadRecordingConsentDesired(VoiceClientRuntime runtime)
        {
            FieldInfo field = typeof(VoiceClientRuntime).GetField(
                "recordingConsentDesired", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(bool))
                throw new MissingFieldException("VoiceClientRuntime", "recordingConsentDesired");
            return (bool)field.GetValue(runtime);
        }

        private static void SetPrivateField(Type type, object target, string name, object value)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(type.Name, name);
            field.SetValue(target, value);
        }

        private static async Task<bool> WaitForVoiceReauthenticationAsync(VoiceRecoveryHold hold, string source)
        {
            if (hold == null || hold.Runtime == null) return false;
            if (hold.MicrophoneMuted && hold.SpeakerOff)
            {
                Debug.Log("VME2_WIN_VOICE_RESTART=WAITING_FOR_USER_ELIGIBILITY | source=" + source +
                    " | micMuted=true | speakerOff=true");
                return false;
            }
            float deadline = Time.realtimeSinceStartup + 16f;
            bool controlLogged = false;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (queuedAction.HasValue)
                {
                    Debug.Log("VME2_WIN_VOICE_RESTART=DEFER_TO_QUEUED_ACTION | source=" + source +
                        " | action=" + queuedAction.Value);
                    return false;
                }
                if (hold.Runtime == null) return false;
                if (hold.Runtime.IsAuthenticated && MetaverseNetworkClient.isReady)
                {
                    if (!controlLogged)
                    {
                        Debug.Log("VME2_WIN_VOICE_RESTART=CONTROL_AUTH_PASS | source=" + source);
                        controlLogged = true;
                    }
                    if (hold.Bridge != null && hold.Bridge.NativeProducerActive)
                    {
                        // Only the manual CompleteVoice button waits for remote
                        // playout: the other two paths must preserve their original
                        // timing and allow a legitimate empty room to stay connected.
                        if (source == "manual_complete_voice")
                        {
                            var ws = DedicatedGameServerWsClient.Instance;
                            bool sameDedicated = ws != null && ws.IsReady &&
                                SameDedicatedIdentity(hold.Runtime.VoiceConnectionId, ws.ConnectionId);
                            bool sessionPresent = hold.Runtime.ActiveSessionCount > 0;
                            int consumersReady = hold.Bridge.NativeReadyConsumerCount;
                            long receivedPcm = hold.Bridge.NativeReceivedPcmSampleCount;
                            if (sameDedicated && sessionPresent && consumersReady > 0 && receivedPcm > 0)
                            {
                                Debug.Log("VME2_WIN_MANUAL_VOICE_VERDICT=MEDIA_RX_READY" +
                                    " | currentDedicated=true | sessionCount=" +
                                    hold.Runtime.ActiveSessionCount +
                                    " | consumerCount=" + consumersReady +
                                    " | receivedPcmSamples=" + receivedPcm +
                                    " | nonSilentSamples=" +
                                    hold.Bridge.NativeReceivedNonSilentSampleCount +
                                    " | audibility=unverified");
                                return true;
                            }
                            // No premature PASS just because the Producer exists.
                            // Fall through to Task.Yield: never spin on the main thread.
                        }
                        Debug.Log("VME2_WIN_VOICE_RESTART=PASS | source=" + source +
                            " | controlAuthenticated=true | nativeProducerActive=true" +
                            " | note=consumer_and_audibility_require_separate_test");
                        return true;
                    }
                }
                await Task.Yield();
            }
            if (source == "manual_complete_voice")
            {
                var ws = DedicatedGameServerWsClient.Instance;
                Debug.LogWarning("VME2_WIN_MANUAL_VOICE_VERDICT=INCOMPLETE" +
                    " | controlAuthenticated=" + (hold.Runtime != null && hold.Runtime.IsAuthenticated) +
                    " | sessionCount=" + (hold.Runtime != null ? hold.Runtime.ActiveSessionCount : 0) +
                    " | nativeProducerActive=" + (hold.Bridge != null && hold.Bridge.NativeProducerActive) +
                    " | consumerCount=" + (hold.Bridge != null ? hold.Bridge.NativeReadyConsumerCount : 0) +
                    " | receivedPcmSamples=" + (hold.Bridge != null ? hold.Bridge.NativeReceivedPcmSampleCount : 0) +
                    " | nonSilentSamples=" + (hold.Bridge != null ? hold.Bridge.NativeReceivedNonSilentSampleCount : 0) +
                    " | currentDedicated=" + (ws != null && ws.IsReady && hold.Runtime != null &&
                        SameDedicatedIdentity(hold.Runtime.VoiceConnectionId, ws.ConnectionId)) +
                    " | reason=consumer_or_session_or_pcm_not_ready_or_no_remote_talking" +
                    " | roomReload=false | internetVerdictUnchanged=true");
            }
            Debug.LogWarning("VME2_WIN_VOICE_RESTART=PENDING | source=" + source +
                " | controlAuthenticated=" + (hold.Runtime != null && hold.Runtime.IsAuthenticated) +
                " | nativeProducerActive=" + (hold.Bridge != null && hold.Bridge.NativeProducerActive) +
                " | healthVerdictIndependent=true");
            return false;
        }

        private static void RecordNetworkVoiceRecoveryResult(bool success, string source)
        {
            if (!pendingNetworkVoiceRepair) return;
            if (success)
            {
                pendingNetworkVoiceRepair = false;
                voiceWasLiveBeforeOutage = false;
                eligibleSince = -1f;
                failedAutoRepairs = 0;
                Debug.Log("VME2_WIN_VOICE_HEALTH_REPAIR=PASS | source=" + source +
                          " | voice_control_and_native_producer=true | no_second_realtime_reset=true");
            }
            else
            {
                failedAutoRepairs = Math.Min(failedAutoRepairs + 1, 6);
                nextAutoRepairAt = Time.realtimeSinceStartup +
                    Mathf.Min(20f, 3f * (1 << Math.Min(failedAutoRepairs - 1, 3)));
                Debug.LogWarning("VME2_WIN_VOICE_HEALTH_REPAIR=RETRY_SCHEDULED | source=" + source +
                    " | nextAttemptAt=" + nextAutoRepairAt.ToString("F1") +
                    " | verdict=voice_service_only_not_internet_offline");
            }
        }

        // IMPORTANT: no second Realtime transport owner here. The canonical
        // manager recovers Realtime after HEALTH 200. Disconnecting that fresh
        // authenticated transport from this Voice wrapper caused the regression
        // confirmed in both 19:00 WindowsPlayer logs.

        // Windows-specific room restart. The shared Manager's
        // ManuallyRestartRoomFromBeginningAsync uses the *WebGL* binder and its
        // non-WebGL reachability check uses gRPC-first. Neither is the correct
        // endpoint for an authenticated WindowsPlayer room reset.
        // Reuse ONLY public APIs already owned by the realtime and dedicated
        // managers; never create a second socket, publisher or session owner.
        private static async Task RestartWindowsRoomAsync(RealtimeRoomGameServerManager room)
        {
            if (!room.IsJoinedRoom || !room.IsRealtimeReady)
            {
                Debug.Log("VME2_WIN_ROOM_RELOAD=WAIT_FOR_EXISTING_REALTIME | no_second_transport_owner=true");
                float waitUntil = Time.realtimeSinceStartup + 18f;
                while (Time.realtimeSinceStartup < waitUntil &&
                       (!room.IsJoinedRoom || !room.IsRealtimeReady))
                {
                    if (room.IsRoomExitInProgress || room.IsManualRoomRestartRunning ||
                        RealtimeRoomGameServerManager.Instance != room) break;
                    await Task.Yield();
                }
            }
            string previousRoomId = room.CurrentRoomId;
            string previousRoomName = room.CurrentRoomName;
            if (!room.IsJoinedRoom || room.IsInsidePublicLobbyRoom ||
                string.IsNullOrWhiteSpace(previousRoomId) ||
                string.IsNullOrWhiteSpace(previousRoomName))
            {
                Debug.LogWarning("VME2_WIN_ROOM_RELOAD=BLOCKED | stage=validate | reason=not_joined_building_room");
                return;
            }
            var binder = DedicatedGameServerRealtimeRoomBinder.Instance;
            if (binder == null || binder.IsDisconnectFlowRunning || binder.IsConnectFlowRunning)
            {
                Debug.LogWarning("VME2_WIN_ROOM_RELOAD=BLOCKED | stage=validate | reason=windows_binder_busy_or_missing");
                return;
            }
            var buildings = room.CompletedBuildings;
            Network_A.Lobby.Buildings.CompletedBuildingDto target = null;
            if (buildings != null)
                for (int i = 0; i < buildings.Count; i++)
                {
                    var item = buildings[i];
                    if (item != null &&
                        string.Equals((item.feature_properties_id ?? string.Empty).Trim(),
                            previousRoomName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        target = item;
                        break;
                    }
                }
            if (target == null)
            {
                Debug.LogWarning("VME2_WIN_ROOM_RELOAD=BLOCKED | stage=validate | reason=building_not_in_catalog | roomName=" + previousRoomName);
                return;
            }

            // Manual room restart is a full exit, not a reusable player-unavailable
            // reconnect. Wait for server transport closure and retire the old Runtime.
            // Auto/CompleteVoice paths continue using their reusable Runtime.
            VoiceRecoveryHold voice = await CloseVoiceForFinalRoomExitAsync();
            if (voice == null)
            {
                Debug.LogWarning("VME2_WIN_ROOM_RELOAD=BLOCKED | stage=voice_stop_not_confirmed | no_room_left=true");
                return;
            }
            bool roomAuthPassed = false;
            try
            {
                Debug.Log("VME2_WIN_ROOM_RELOAD=START | sameRoom=" + previousRoomId +
                    " | oldVoiceReleasedLocally=true | binder=windows");
                // Match the exact WebGL manual same-room LEAVE contract.
                // Both Windows and WebGL Binders suppress automatic Lobby navigation
                // only when the reason contains "before_realtime_disconnect".
                bool left = await binder.DisconnectGameServerAndLeaveRoomAsync(
                    "manual_room_restart_before_realtime_disconnect", false);
                if (!left || room.IsJoinedRoom || binder.HasUnclosedDedicatedSession)
                {
                    Debug.LogWarning("VME2_WIN_ROOM_RELOAD=FAILED | stage=dedicated_close_or_server_leave" +
                        " | leaveResult=" + left + " | joined=" + room.IsJoinedRoom +
                        " | unclosedDedicated=" + binder.HasUnclosedDedicatedSession);
                    return;
                }
                Debug.Log("VME2_WIN_ROOM_RELOAD=SERVER_LEAVE_ACK | roomId=" + previousRoomId +
                    " | lobbyNavigationSuppressed=true");

                // A scene load alone DOES NOT destroy the DontDestroyOnLoad Voice
                // root (observed as CONNECTOR_REUSED in 19:53:15 client03 log).
                // Retire it only after server LEAVE ACK. The shared installer will
                // build ONE entirely new root/Runtime/Instance on sceneLoaded.
                if (!await RetireOldVoiceRootAsync(voice))
                {
                    Debug.LogError("VME2_WIN_ROOM_RELOAD=FAILED | stage=old_voice_root_not_retired | join_not_started=true");
                    return;
                }
                if (!string.Equals(SceneManager.GetActiveScene().name, VoiceScene, StringComparison.Ordinal))
                {
                    Debug.LogError("VME2_WIN_ROOM_RELOAD=FAILED | stage=unexpected_scene_after_leave" +
                        " | actualScene=" + SceneManager.GetActiveScene().name);
                    return;
                }
                await ReloadGameplaySceneAsync();
                // Disable the NEW connector before any further room/Dedicated
                // callbacks. It must not Auth while the next room is still joining.
                if (!HoldFreshVoiceRuntimeAfterSceneReload(voice))
                {
                    Debug.LogError("VME2_WIN_ROOM_RELOAD=FAILED | stage=fresh_voice_runtime_not_created");
                    return;
                }
                if (RealtimeRoomGameServerManager.Instance != room)
                {
                    Debug.LogError("VME2_WIN_ROOM_RELOAD=FAILED | stage=realtime_manager_replaced_by_scene_reload");
                    return;
                }
                Debug.Log("VME2_WIN_ROOM_RELOAD=WEBGL_ORDER_CONFIRMED | leaveAck=true" +
                    " | gameplaySceneReloaded=true | next=same_room_join | roomId=" + previousRoomId);

                if (!room.IsRealtimeReady)
                {
                    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                    {
                        bool ready = await room.EnsureRealtimeReadyAsync(true, deadline.Token);
                        if (!ready || !room.IsRealtimeReady)
                        {
                            Debug.LogWarning("VME2_WIN_ROOM_RELOAD=FAILED | stage=realtime_ready | health200_not_equivalent_realtime_ready=true");
                            return;
                        }
                    }
                }
                bool joined = await room.EnterBuildingRoomAsync(target);
                if (!joined || !room.IsJoinedRoom ||
                    !string.Equals(room.CurrentRoomId, previousRoomId, StringComparison.Ordinal))
                {
                    Debug.LogWarning("VME2_WIN_ROOM_RELOAD=FAILED | stage=same_room_join" +
                        " | joined=" + joined + " | actualRoom=" + room.CurrentRoomId);
                    return;
                }
                Debug.Log("VME2_WIN_ROOM_RELOAD=SERVER_JOIN_ACK | roomId=" + previousRoomId);
                for (int i = 0; i < 4; i++) await Task.Yield();
                binder = DedicatedGameServerRealtimeRoomBinder.Instance;
                if (binder == null)
                {
                    Debug.LogWarning("VME2_WIN_ROOM_RELOAD=PENDING | stage=windows_binder_missing_after_join");
                    return;
                }
                if (!binder.HasActiveDedicatedConnection && !binder.IsConnectFlowRunning)
                    await binder.ConnectGameServerAsync();

                float deadlineAt = Time.realtimeSinceStartup + 25f;
                while (Time.realtimeSinceStartup < deadlineAt)
                {
                    var ws = DedicatedGameServerWsClient.Instance;
                    if (binder.HasActiveDedicatedConnection && ws != null && ws.IsReady &&
                        string.Equals(ws.RoomId, previousRoomId, StringComparison.Ordinal))
                    {
                        roomAuthPassed = true;
                        Debug.Log("VME2_WIN_ROOM_RELOAD=ROOM_AUTH_PASS | roomId=" + previousRoomId +
                            " | realtimeAck=true | dedicatedAuth=true | voiceRestart=next");
                        break;
                    }
                    await Task.Yield();
                }
                if (roomAuthPassed)
                {
                    // WebGL's code-6 recovery has a public same-socket AUTH+JOIN
                    // path. On Windows a fresh Dedicated socket alone does not
                    // prove that the Voice service sees a Realtime membership.
                    // Do not disconnect a working Realtime transport a second time.
                    bool membershipVerified;
                    using (var membershipDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                        membershipVerified = await room.RevalidateRealtimeMembershipForVoiceAsync(membershipDeadline.Token);
                    Debug.Log("VME2_WIN_ROOM_RELOAD=REALTIME_MEMBERSHIP_REASSERTED | result=" +
                        membershipVerified + " | freshTransport=false | room=" + previousRoomId);
                    if (!membershipVerified)
                        Debug.LogWarning("VME2_WIN_ROOM_RELOAD=REALTIME_MEMBERSHIP_UNCONFIRMED | voice_auto_retry_continues=true");
                    // Bootstrap attaches Native Bridge by scene scan (up to 750 ms).
                    // Hold the connector disabled until the Bridge has been found.
                    if (!await CaptureFreshVoiceBridgeAsync(voice))
                        Debug.LogWarning("VME2_WIN_ROOM_RELOAD=NATIVE_BRIDGE_PENDING | connector_is_restored_for_later_retry=true");
                }
                if (roomAuthPassed && pendingNetworkVoiceRepair)
                    Debug.Log("VME2_WIN_ROOM_RELOAD=VOICE_FIRST_AFTER_SAME_ROOM_ACK | no_extra_realtime_reset=true");
                if (!roomAuthPassed)
                    Debug.LogWarning("VME2_WIN_ROOM_RELOAD=PENDING | stage=dedicated_auth | voiceWillWaitForPlayerReady=true");
            }
            finally
            {
                // Graceful final exit permanently shuts down the old Runtime.
                // If the room LEAVE or scene-load step fails, the old instance
                // must NEVER be re-enabled: rebuild a fresh root in-place so the
                // existing game/room can still recover without another owner.
                if (voice.Runtime != null && voice.Runtime.IsDisconnecting &&
                    string.Equals(SceneManager.GetActiveScene().name, VoiceScene, StringComparison.Ordinal))
                {
                    Debug.LogWarning("VME2_WIN_ROOM_VOICE_FRESH=RECOVER_FROM_INTERRUPTED_RELOAD | reason=old_runtime_permanently_closed");
                    try
                    {
                        if (await RetireOldVoiceRootAsync(voice))
                        {
                            await ReloadGameplaySceneAsync();
                            if (HoldFreshVoiceRuntimeAfterSceneReload(voice))
                                await CaptureFreshVoiceBridgeAsync(voice);
                        }
                    }
                    catch (Exception error)
                    {
                        Debug.LogError("VME2_WIN_ROOM_VOICE_FRESH=RECOVERY_FAILED | error=" + Sanitize(error.Message));
                    }
                }
                // Restore exact saved Mic/Speaker/consent on the NEW Runtime,
                // then enable its sole existing AutoConnector/Native bridge.
                RestoreWindowsVoice(voice, "after_manual_room_reload");
            }
            if (roomAuthPassed)
            {
                bool voiceOkay = await WaitForVoiceReauthenticationAsync(voice, "after_room_reload");
                RecordNetworkVoiceRecoveryResult(voiceOkay, "room_reload");
                Debug.Log("VME2_WIN_ROOM_RELOAD=ROOM_PASS_VOICE_SEPARATE | roomId=" + previousRoomId);
            }
            else
            {
                RecordNetworkVoiceRecoveryResult(false, "room_reload_not_authenticated");
            }
        }

        private static void ResetNativeMediaWithoutRoomExit()
        {
            WindowsWebRtcVoiceBridge bridge = UnityEngine.Object.FindObjectOfType<WindowsWebRtcVoiceBridge>();
            if (bridge == null || !bridge.isActiveAndEnabled)
            {
                Debug.LogWarning("VME2_WIN_MEDIA_RELOAD=SKIPPED | reason=native_bridge_not_ready");
                return;
            }
            // OnDisable invokes Bridge.StopSession(), which cancels and disposes its
            // Producer/Consumers and restores the existing microphone event owner.
            // On the next Update it recreates the Native SFU session automatically.
            // Never call Shutdown() (permanent) or acquire another capture device.
            bridge.enabled = false;
            bridge.enabled = true;
            // The existing 5s retryAt is solely for a failed attempt; bypass this
            // artificial delay for an explicit user action. Fail safe if it changes.
            FieldInfo retry = typeof(WindowsWebRtcVoiceBridge).GetField(
                "retryAt", BindingFlags.NonPublic | BindingFlags.Instance);
            if (retry != null && retry.FieldType == typeof(float))
                retry.SetValue(bridge, 0f);
            Debug.Log("VME2_WIN_MEDIA_RELOAD=REQUESTED | nativeBridgeReset=true" +
                " | owner=existing_bridge | roomExit=false | no_new_microphone=true");
        }

        private static async Task<bool> VerifyHealth200Async()
        {
            using (UnityWebRequest req = UnityWebRequest.Get(HealthUrl))
            {
                req.timeout = 4;
                var operation = req.SendWebRequest();
                while (!operation.isDone) await Task.Yield();
                bool success = req.result == UnityWebRequest.Result.Success && req.responseCode == 200;
                Debug.Log("VME2_WIN_MANUAL_HEALTH=" + (success ? "200" : "WAIT") +
                          " | status=" + req.responseCode + " | source=get_health_only");
                return success;
            }
        }

        private static async Task ReloadGameplaySceneAsync()
        {
            string current = SceneManager.GetActiveScene().name;
            if (!string.Equals(current, VoiceScene, StringComparison.Ordinal))
                throw new InvalidOperationException("Manual same-room reload refused outside Voice scene");
            var op = SceneManager.LoadSceneAsync(VoiceScene, LoadSceneMode.Single);
            if (op == null) throw new InvalidOperationException("Failed to start Voice scene reload");
            while (!op.isDone) await Task.Yield();
            await Task.Yield();
            if (!string.Equals(SceneManager.GetActiveScene().name, VoiceScene, StringComparison.Ordinal))
                throw new InvalidOperationException("Voice gameplay scene reload did not complete");
            Debug.Log("VME2_WIN_ROOM_RELOAD=SCENE_RELOADED | scene=" + VoiceScene);
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Replace('|', '/');
        }
#endif
    }
}
