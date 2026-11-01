#if UNITY_EDITOR
using System;
using Network_A.Voice.Client.Codec;
using Network_A.Voice.Dedicated;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Client.Editor
{
    public static class VoiceCompleteRoadmapDirectTest
    {
        //* این تابع تمام آزمون‌های مستقیم Unity از V3 تا V9 و Plugin واقعی Opus را یکجا اجرا می‌کند.
        [MenuItem("Tools/Network A/Voice/Run Complete Voice V3-V9 Final Tests")]
        public static void RunFromEditorMenu()
        {
            try
            {
                VoiceV3CompletePhaseDirectTest.RunFromEditorMenu();
                VoiceClientV6DirectTest.RunFromEditorMenu();

                using (VoiceNativeOpusCodec codec = new VoiceNativeOpusCodec(32))
                {
                    byte[] packet = codec.Encode(new float[VoiceNativeOpusCodec.FrameSamples]);
                    if (packet == null || packet.Length == 0 || packet.Length > 4096)
                        throw new InvalidOperationException("Native Opus encoder output is invalid.");

                    float[] decoded = codec.Decode(packet);
                    if (decoded == null || decoded.Length == 0 || decoded.Length > VoiceNativeOpusCodec.FrameSamples)
                        throw new InvalidOperationException("Native Opus decoder output is invalid.");
                }

                Debug.Log("VOICE_V9_OPUS_NATIVE_PLUGIN=PASS");
                Debug.Log("VOICE_V9_WINDOWS_EDITOR_CODEC=PASS");
                Debug.Log("VOICE_V3_TO_V9_UNITY_DIRECT_TESTS=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError("VOICE_V3_TO_V9_UNITY_DIRECT_TESTS=FAIL | " + exception);
                throw;
            }
        }
    }
}

/*
توضیح فایل:
این فایل تنها نقطه اجرای نهایی تست‌های مستقیم Unity است و علاوه بر قراردادها، وجود و کارکرد واقعی libopus در Editor ویندوز را بررسی می‌کند.
*/
#endif
