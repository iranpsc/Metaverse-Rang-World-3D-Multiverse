#if UNITY_EDITOR
using System;
using Network_A.GameServer;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedAuthorityPhaseDirectTest
    {
        //* این تابع تمام تست‌های مستقل بخش اقتدار صوت سرور اختصاصی را یکجا اجرا می‌کند.
        [MenuItem("Tools/Network A/Voice/Run Complete Dedicated Authority Phase Tests")]
        public static void RunCompletePhaseTests()
        {
            try
            {
                VoiceDedicatedProximityEvaluatorDirectTest.RunFromEditorMenu();
                TestDeltaContractUsesUserAndConnectionId();
                TestMemberLeftValidation();
                TestRemovalReasonMapping();
                TestManualBinderContract();
                TestEndpointContract();
                TestPairVoiceConnectionReadinessConflictIsRetryable();

                Debug.Log("VOICE_V3_DEDICATED_DELTA_CONTRACT=PASS");
                Debug.Log("VOICE_V3_DEDICATED_DELTA_JSON_USER_CONNECTION_ID=PASS");
                Debug.Log("VOICE_V3_DEDICATED_REMOVAL_REASON_MAPPING=PASS");
                Debug.Log("VOICE_V3_DEDICATED_MANUAL_BINDER_CONTRACT=PASS");
                Debug.Log("VOICE_G4_PAIR_CONNECTION_NOT_READY_RETRY_CONTRACT=PASS");
                Debug.Log("VOICE_V3_UNITY_DEDICATED_AUTHORITY_PHASE=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_V3_UNITY_DEDICATED_AUTHORITY_PHASE=FAIL | " +
                    exception);

                throw;
            }
        }

        //* این تابع آماده‌بودن همه وابستگی‌ها و اتصال‌های دستی بخش صوت سرور اختصاصی را بررسی می‌کند.
        [MenuItem("Tools/Network A/Voice/Validate Dedicated Authority Runtime Dependencies")]
        public static void ValidateRuntimeDependencies()
        {
            DedicatedServerRuntime runtime =
                DedicatedServerRuntime.Instance ??
                FindSceneObject<DedicatedServerRuntime>();

            DedicatedPlayerRegistry playerRegistry =
                FindSceneObject<DedicatedPlayerRegistry>();

            DedicatedPlayerStateStore playerStateStore =
                FindSceneObject<DedicatedPlayerStateStore>();

            GameServerControlDedicatedClient controlClient =
                FindSceneObject<GameServerControlDedicatedClient>();

            VoiceDedicatedAuthoritativePositionProvider positionProvider =
                FindSceneObject<VoiceDedicatedAuthoritativePositionProvider>();

            VoiceDedicatedSessionDeltaSender deltaSender =
                FindSceneObject<VoiceDedicatedSessionDeltaSender>();

            VoiceDedicatedAuthorityMonitor authorityMonitor =
                FindSceneObject<VoiceDedicatedAuthorityMonitor>();

            VoiceDedicatedAuthorityManualBinder manualBinder =
                FindSceneObject<VoiceDedicatedAuthorityManualBinder>();

            if (runtime == null ||
                playerRegistry == null ||
                playerStateStore == null ||
                controlClient == null ||
                positionProvider == null ||
                deltaSender == null ||
                authorityMonitor == null ||
                manualBinder == null)
            {
                throw new InvalidOperationException(
                    "VOICE_V3_DEDICATED_MANUAL_DEPENDENCIES=FAIL" +
                    " | runtime=" + (runtime != null) +
                    " | playerRegistry=" + (playerRegistry != null) +
                    " | playerStateStore=" + (playerStateStore != null) +
                    " | controlClient=" + (controlClient != null) +
                    " | positionProvider=" + (positionProvider != null) +
                    " | deltaSender=" + (deltaSender != null) +
                    " | authorityMonitor=" + (authorityMonitor != null) +
                    " | manualBinder=" + (manualBinder != null));
            }

            Debug.Log(
                "VOICE_V3_DEDICATED_MANUAL_DEPENDENCIES=PASS" +
                " | runtime=" + runtime.name +
                " | playerRegistry=" + playerRegistry.name +
                " | playerStateStore=" + playerStateStore.name +
                " | controlClient=" + controlClient.name +
                " | positionProvider=" + positionProvider.name +
                " | deltaSender=" + deltaSender.name +
                " | authorityMonitor=" + authorityMonitor.name +
                " | manualBinder=" + manualBinder.name);
        }

        //* این تابع قرارداد رویداد را می‌سازد و سریال‌سازی شناسه کاربر و اتصال هر دو عضو را بررسی می‌کند.
        private static void TestDeltaContractUsesUserAndConnectionId()
        {
            VoiceDedicatedParticipantPair pair =
                new VoiceDedicatedParticipantPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa",
                    "user-b",
                    "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb");

            VoiceDedicatedProximityEvaluator evaluator =
                new VoiceDedicatedProximityEvaluator(
                    1000,
                    delegate
                    {
                        return "66666666-6666-4666-8666-666666666666";
                    });

            evaluator.Evaluate(pair, 2.5f, 0, 100000);
            VoiceDedicatedProximityDecision created =
                evaluator.Evaluate(pair, 2.5f, 1000, 101000);

            VoiceDedicatedSessionDelta delta =
                VoiceDedicatedSessionDelta.FromProximityDecision(
                    created,
                    "77777777-7777-4777-8777-777777777777",
                    1);

            string json = JsonUtility.ToJson(delta);

            Require(
                json.Contains("\"firstUserId\":\"user-a\"") &&
                json.Contains("\"firstConnectionId\":\"aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa\"") &&
                json.Contains("\"secondUserId\":\"user-b\"") &&
                json.Contains("\"secondConnectionId\":\"bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb\""),
                "The dedicated Voice delta did not serialize both userId and connectionId pairs.");

            Require(
                !json.Contains("avatarId"),
                "The dedicated Voice delta serialized an avatarId field.");

            VoiceDedicatedSessionDeltaBatchRequest batch =
                new VoiceDedicatedSessionDeltaBatchRequest
                {
                    serviceToken = "test-service-token",
                    serverId = "server-1",
                    authorityEpochId = delta.authorityEpochId,
                    events = new[] { delta }
                };

            string batchJson = JsonUtility.ToJson(batch);

            Require(
                batchJson.Contains("voice") ||
                batchJson.Contains("session_created"),
                "The dedicated Voice delta batch was not serialized.");
        }

        //* این تابع عضویت memberUserId در همان زوج و علت معتبر خروج را بررسی می‌کند.
        private static void TestMemberLeftValidation()
        {
            VoiceDedicatedParticipantPair pair =
                new VoiceDedicatedParticipantPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa",
                    "user-b",
                    "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb");

            VoiceDedicatedSessionDelta valid =
                VoiceDedicatedSessionDelta.CreateMemberLeft(
                    pair,
                    "88888888-8888-4888-8888-888888888888",
                    "user-a",
                    2.0f,
                    VoiceDedicatedSessionReason.RoomLeft,
                    200000,
                    "99999999-9999-4999-8999-999999999999",
                    2);

            valid.ValidateOrThrow();

            bool rejectedForeignMember = false;

            try
            {
                VoiceDedicatedSessionDelta.CreateMemberLeft(
                    pair,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    "user-c",
                    2.0f,
                    VoiceDedicatedSessionReason.RoomLeft,
                    200001,
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                    3);
            }
            catch (InvalidOperationException)
            {
                rejectedForeignMember = true;
            }

            Require(
                rejectedForeignMember,
                "A member_left delta accepted a userId outside the pair.");
        }

        //* این تابع نگاشت دلایل واقعی رجیستری به علت‌های قرارداد صوت را بررسی می‌کند.
        private static void TestRemovalReasonMapping()
        {
            Require(
                VoiceDedicatedSessionDelta.MapPlayerRemovalReason(
                    "reconnect_grace_expired:transport_lost") ==
                VoiceDedicatedSessionReason.ReconnectExpired,
                "Reconnect expiry reason mapping failed.");

            Require(
                VoiceDedicatedSessionDelta.MapPlayerRemovalReason(
                    "leave_room") ==
                VoiceDedicatedSessionReason.RoomLeft,
                "Room leave reason mapping failed.");

            Require(
                VoiceDedicatedSessionDelta.MapPlayerRemovalReason(
                    "auth_failed") ==
                VoiceDedicatedSessionReason.AccessRevoked,
                "Access revoke reason mapping failed.");

            Require(
                VoiceDedicatedSessionDelta.MapPlayerRemovalReason(
                    "server_stopped") ==
                VoiceDedicatedSessionReason.DedicatedDisconnected,
                "Dedicated disconnect reason mapping failed.");
        }

        //* این تابع وجود اتصال‌دهنده دستی و قابل‌افزودن‌بودن اجزای اصلی صوت را بررسی می‌کند.
        private static void TestManualBinderContract()
        {
            Require(
                typeof(MonoBehaviour).IsAssignableFrom(
                    typeof(VoiceDedicatedAuthorityManualBinder)),
                "The manual dedicated Voice binder is not a Unity component.");

            Require(
                typeof(MonoBehaviour).IsAssignableFrom(
                    typeof(VoiceDedicatedAuthoritativePositionProvider)) &&
                typeof(MonoBehaviour).IsAssignableFrom(
                    typeof(VoiceDedicatedSessionDeltaSender)) &&
                typeof(MonoBehaviour).IsAssignableFrom(
                    typeof(VoiceDedicatedAuthorityMonitor)),
                "One or more dedicated Voice components cannot be attached manually.");
        }

        //* این تابع ثابت‌بودن مسیر انتقال رویدادهای صوت روی کنترل گیم سرور موجود را بررسی می‌کند.
        private static void TestEndpointContract()
        {
            Require(
                string.Equals(
                    VoiceDedicatedSessionDeltaSender.RelativeEndpointPath,
                    "/game-server-control/dedicated/voice-session-delta",
                    StringComparison.Ordinal),
                "Dedicated Voice delta endpoint path changed unexpectedly.");
        }

        //* این تابع تضمین می‌کند فقط 409 آماده‌نبودن Voice Connection موقت است و سایر Conflictها همان رفتار قبلی را حفظ می‌کنند.
        private static void TestPairVoiceConnectionReadinessConflictIsRetryable()
        {
            Require(
                VoiceDedicatedSessionDeltaSender.IsVoiceConnectionReadinessConflict(
                    409,
                    "{\"success\":false,\"reason\":\"voice_delta_pair_voice_connection_not_ready\"}"),
                "Voice connection readiness conflict must keep the same queued delta retryable.");

            Require(
                !VoiceDedicatedSessionDeltaSender.IsVoiceConnectionReadinessConflict(
                    409,
                    "{\"success\":false,\"reason\":\"voice_delta_sequence_gap\"}"),
                "Unrelated Voice delta conflicts must not be classified as connection readiness retries.");

            Require(
                !VoiceDedicatedSessionDeltaSender.IsVoiceConnectionReadinessConflict(
                    500,
                    "{\"success\":false,\"reason\":\"voice_delta_pair_voice_connection_not_ready\"}"),
                "Only the expected 409 readiness conflict may use the dedicated retry path.");
        }

        //* این تابع یک آبجکت صحنه را همراه با آبجکت‌های غیرفعال پیدا می‌کند.
        private static T FindSceneObject<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<T>(
                FindObjectsInactive.Include);
#else
            T[] loadedObjects = Resources.FindObjectsOfTypeAll<T>();

            for (int index = 0; index < loadedObjects.Length; index += 1)
            {
                Component component = loadedObjects[index] as Component;
                if (component == null || !component.gameObject.scene.IsValid())
                {
                    continue;
                }

                return loadedObjects[index];
            }

            return null;
#endif
        }

        //* این تابع شرط تست را بررسی و در صورت شکست خطای دقیق تولید می‌کند.
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

/*
توضیح فایل:
این فایل تست کامل بخش یونیتی اقتدار صوت را اجرا می‌کند و قرارداد رویداد، هویت مبتنی بر شناسه کاربر، نگاشت دلایل حذف، تنظیمات رانتایم و مسیر انتقال را بررسی می‌کند.
*/
#endif
