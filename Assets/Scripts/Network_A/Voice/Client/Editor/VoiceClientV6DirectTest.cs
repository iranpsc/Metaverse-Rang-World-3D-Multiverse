#if UNITY_EDITOR
using System;
using Network_A.Voice.Client.Protocol;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Client.Editor
{
    public static class VoiceClientV6DirectTest
    {
        //* این تابع قراردادهای مستقل کلاینت V6 را بدون اتصال شبکه اجرا می‌کند.
        [MenuItem("Tools/Network A/Voice/Run Voice V6 Client Direct Tests")]
        public static void RunFromEditorMenu()
        {
            try
            {
                const string sessionId = "550e8400-e29b-41d4-a716-446655440000";
                const string senderId = "65adc593-c1b9-4677-8c79-a0b1f16393ab";

                VoiceClientEnvelope envelope = new VoiceClientEnvelope
                {
                    MessageType = VoiceClientMessageType.VoiceFrame,
                    Flags = VoiceClientMessageFlags.Dtx,
                    Sequence = 77,
                    TimestampMs = 123456789UL,
                    SessionId = sessionId,
                    SenderId = senderId,
                    Payload = new byte[] { 0xf8, 0xff, 0xfe }
                };

                byte[] packet = envelope.Encode();
                Require(packet.Length == 63, "Envelope length mismatch.");
                Require(packet[0] == 'M' && packet[3] == 'C', "Envelope magic mismatch.");
                Require(packet[28] == 0x55 && packet[29] == 0x0e, "UUID network byte order mismatch.");

                VoiceClientEnvelope decoded = VoiceClientEnvelope.Decode(packet);
                Require(decoded.Sequence == 77, "Envelope sequence mismatch.");
                Require(decoded.SessionId == sessionId, "Envelope sessionId mismatch.");
                Require(decoded.SenderId == senderId, "Envelope senderId mismatch.");
                Require(decoded.Payload.Length == 3, "Envelope payload mismatch.");

                byte[] publishStart = VoiceClientControlPayload.EncodePublishStart(32);
                Require(publishStart.Length == 12, "Publish start length mismatch.");
                Require(publishStart[0] == 1 && publishStart[1] == 1 && publishStart[2] == 1, "Publish start header mismatch.");
                Require(VoiceClientEnvelope.ReadUInt32(publishStart, 4) == 48000, "Publish sample rate mismatch.");
                Require(VoiceClientEnvelope.ReadUInt16(publishStart, 8) == 20, "Publish frame duration mismatch.");

                byte[] auth = VoiceClientControlPayload.EncodeAuthRequest(
                    VoiceClientPlatform.Windows,
                    "token",
                    "room-v6",
                    "11111111-1111-4111-8111-111111111111",
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    "v6-test");

                Require(auth[0] == (byte)VoiceClientPlatform.Windows, "Auth platform mismatch.");
                Require(VoiceClientEnvelope.ReadUInt16(auth, 6) == 36, "Auth userId alias length mismatch.");

                byte[] reconnect = VoiceClientControlPayload.EncodeReconnectRequest(
                    senderId,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    12,
                    9,
                    123000,
                    "token",
                    "room-v6",
                    "11111111-1111-4111-8111-111111111111");

                Require(reconnect.Length > 56, "Reconnect payload mismatch.");
                Require(VoiceClientEnvelope.ReadUInt32(reconnect, 32) == 12, "Reconnect receive sequence mismatch.");

                Debug.Log("VOICE_V6_CLIENT_BINARY_ENVELOPE=PASS");
                Debug.Log("VOICE_V6_CLIENT_AUTH_USER_ID_ALIAS=PASS");
                Debug.Log("VOICE_V6_CLIENT_OPUS_SETTINGS=PASS");
                Debug.Log("VOICE_V6_CLIENT_RECONNECT_CONTRACT=PASS");
                Debug.Log("VOICE_V6_CLIENT_DIRECT_TESTS=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError("VOICE_V6_CLIENT_DIRECT_TESTS=FAIL | " + exception);
                throw;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

/*
توضیح فایل:
این فایل قراردادهای مستقل Envelope، Auth با userId، تنظیمات Opus و Reconnect کلاینت V6 را داخل Unity Editor بررسی می‌کند.
*/
#endif
