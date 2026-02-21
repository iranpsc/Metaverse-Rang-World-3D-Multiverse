using System;
using Assets.Scripts.Network.Core.Utils;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    /// <summary>
    /// استاندارد پیام‌های WebSocket برای متاورس
    /// تمام پیام‌های ارسالی و دریافتی باید از این ساختار پیروی کنند
    /// </summary>
    [Serializable]
    public class WebSocketMessage
    {
        /// <summary>
        /// نوع پیام (برای مسیریابی و پردازش)
        /// </summary>
        public string type;

        /// <summary>
        /// شناسه فرستنده پیام
        /// </summary>
        public string senderId;

        /// <summary>
        /// داده اصلی پیام (می‌تواند هر شیء JSON سریالایز شده باشد)
        /// </summary>
        public object data;

        /// <summary>
        /// زمان ارسال پیام (Unix timestamp)
        /// </summary>
        public long timestamp;

        /// <summary>
        /// شناسه یکتای پیام برای تأیید دریافت (ACK)
        /// </summary>
        public string messageId;

        /// <summary>
        /// نیاز به تأیید دریافت (Acknowledgment)
        /// </summary>
        public bool requiresAck;

        /// <summary>
        /// تگ‌های دلخواه برای دسته‌بندی پیام‌ها
        /// </summary>
        public string[] tags;

        /// <summary>
        /// سازنده پیش‌فرض
        /// </summary>
        public WebSocketMessage()
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            messageId = Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// سازنده با پارامترها
        /// </summary>
        public WebSocketMessage(string type, object data, string senderId = null, bool requiresAck = false)
        {
            this.type = type;
            this.data = data;
            this.senderId = senderId;
            this.requiresAck = requiresAck;
            this.timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            this.messageId = Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// تبدیل پیام به رشته JSON
        /// </summary>
        public string ToJson()
        {
            return JSONSerializer.Serialize(this);
        }

        /// <summary>
        /// تبدیل رشته JSON به پیام
        /// </summary>
        public static WebSocketMessage FromJson(string json)
        {
            try
            {
                return JSONSerializer.Deserialize<WebSocketMessage>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در دی‌سریالایز پیام WebSocket: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ایجاد پیام تأیید دریافت (ACK)
        /// </summary>
        public WebSocketMessage CreateAckMessage()
        {
            return new WebSocketMessage
            {
                type = "ack",
                senderId = senderId,
                data = new { originalMessageId = messageId },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                requiresAck = false
            };
        }

        /// <summary>
        /// بررسی آیا پیام قدیمی است (بیش از ۳۰ ثانیه)
        /// </summary>
        public bool IsExpired(int maxAgeSeconds = 30)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return (now - timestamp) > (maxAgeSeconds * 1000);
        }
    }

    /// <summary>
    /// انواع پیام‌های رایج در متاورس
    /// این کلاس به عنوان مرجع برای توسعه‌دهندگان استفاده می‌شود
    /// </summary>
    public static class WebSocketMessageTypes
    {
        // پیام‌های سیستمی
        public const string CONNECT = "connect";
        public const string DISCONNECT = "disconnect";
        public const string PING = "ping";
        public const string PONG = "pong";
        public const string ACK = "ack";
        public const string ERROR = "error";

        // پیام‌های آواتار
        public const string AVATAR_POSITION = "avatar_position";
        public const string AVATAR_ROTATION = "avatar_rotation";
        public const string AVATAR_ANIMATION = "avatar_animation";
        public const string AVATAR_JOIN = "avatar_join";
        public const string AVATAR_LEAVE = "avatar_leave";

        // پیام‌های چت
        public const string CHAT_MESSAGE = "chat_message";
        public const string CHAT_TYPING = "chat_typing";
        public const string CHAT_READ = "chat_read";

        // پیام‌های صوتی
        public const string VOICE_START = "voice_start";
        public const string VOICE_DATA = "voice_data";
        public const string VOICE_END = "voice_end";
        public const string VOICE_MUTE = "voice_mute";

        // پیام‌های دنیا/محیط
        public const string WORLD_STATE = "world_state";
        public const string OBJECT_SPAWN = "object_spawn";
        public const string OBJECT_UPDATE = "object_update";
        public const string OBJECT_DESTROY = "object_destroy";

        // پیام‌های رویداد
        public const string EVENT_TRIGGER = "event_trigger";
        public const string EVENT_COMPLETE = "event_complete";

        // پیام‌های NPC
        public const string NPC_DIALOGUE = "npc_dialogue";
        public const string NPC_ACTION = "npc_action";
    }

    /// <summary>
    /// مدل‌های داده‌ای متداول برای پیام‌های آواتار
    /// </summary>
    [Serializable]
    public class AvatarPositionData
    {
        public string avatarId;
        public float x, y, z;
        public float velocityX, velocityY, velocityZ;
    }

    [Serializable]
    public class AvatarRotationData
    {
        public string avatarId;
        public float pitch, yaw, roll;
    }

    [Serializable]
    public class ChatMessageData
    {
        public string senderId;
        public string senderName;
        public string message;
        public string worldId;
        public string channelId;
    }

    [Serializable]
    public class VoiceStartData
    {
        public string speakerId;
        public string sessionId;
        public int sampleRate;
        public int channels;
    }

    [Serializable]
    public class VoiceDataPacket
    {
        public string sessionId;
        public byte[] audioData; // Opus encoded
        public int sequenceNumber;
        public long timestamp;
    }
}