using System;

namespace Network_A.GameServer.Players
{
    [Serializable]
    public class DedicatedPlayerSession
    {
        public string connectionId;
        public string remoteEndPoint;

        public string userId;
        public string playerId;
        public string userName;

        public string roomId;
        public string serverId;
        public string sessionId;

        public long joinedAtUnixMs;
        public long lastSeenAtUnixMs;

        public bool isReady;
        public bool isAuthenticated;

        public bool HasConnectionId => !string.IsNullOrWhiteSpace(connectionId);
        public bool HasUserId => !string.IsNullOrWhiteSpace(userId);
        public bool HasPlayerId => !string.IsNullOrWhiteSpace(playerId);
        public bool HasRoomId => !string.IsNullOrWhiteSpace(roomId);
        public bool HasServerId => !string.IsNullOrWhiteSpace(serverId);
        public bool HasSessionId => !string.IsNullOrWhiteSpace(sessionId);
        public bool IsValidForGameplay => HasConnectionId && HasUserId && HasPlayerId && HasRoomId && HasServerId && HasSessionId;
        public bool IsMirrorLikeReady => isAuthenticated && isReady && IsValidForGameplay;

        public string OwnerDebugLabel
        {
            get
            {
                return "connectionId=" + SafeValue(connectionId) +
                       " | userId=" + SafeValue(userId) +
                       " | playerId=" + SafeValue(playerId) +
                       " | roomId=" + SafeValue(roomId);
            }
        }

        //* این تابع بررسی می کند که این سشن برای همین کانکشن است یا نه.
        public bool IsConnection(string targetConnectionId)
        {
            return Same(connectionId, targetConnectionId);
        }

        //* این تابع بررسی می کند که این سشن برای همین یوزر است یا نه.
        public bool IsUser(string targetUserId)
        {
            return Same(userId, targetUserId);
        }

        //* این تابع بررسی می کند که این سشن برای همین پلیر است یا نه.
        public bool IsPlayer(string targetPlayerId)
        {
            return Same(playerId, targetPlayerId);
        }

        //* این تابع بررسی می کند که این سشن داخل همین روم است یا نه.
        public bool IsRoom(string targetRoomId)
        {
            return Same(roomId, targetRoomId);
        }

        //* این تابع بررسی می کند که این سشن برای همین سرور است یا نه.
        public bool IsServer(string targetServerId)
        {
            return Same(serverId, targetServerId);
        }

        //* این تابع برای مسیرهای شبیه میرور بررسی می کند که یک آبجکت می تواند متعلق به این سشن باشد یا نه.
        public bool MatchesAnyOwner(string targetConnectionId, string targetUserId, string targetPlayerId)
        {
            if (!string.IsNullOrWhiteSpace(targetConnectionId) && IsConnection(targetConnectionId)) return true;
            if (!string.IsNullOrWhiteSpace(targetUserId) && IsUser(targetUserId)) return true;
            if (!string.IsNullOrWhiteSpace(targetPlayerId) && IsPlayer(targetPlayerId)) return true;
            return false;
        }

        //* این تابع وضعیت آماده بودن سشن را تغییر می دهد.
        public void MarkReady(bool ready, long nowUnixMs)
        {
            isReady = ready;
            Touch(nowUnixMs);
        }

        //* این تابع وضعیت آث شدن سشن را تغییر می دهد.
        public void MarkAuthenticated(bool authenticated, long nowUnixMs)
        {
            isAuthenticated = authenticated;
            Touch(nowUnixMs);
        }

        //* این تابع عمر سشن را به میلی ثانیه برمی گرداند.
        public long GetAgeMs(long nowUnixMs)
        {
            return joinedAtUnixMs > 0 ? Math.Max(0, nowUnixMs - joinedAtUnixMs) : 0;
        }

        //* این تابع مدت زمان بی فعالیتی سشن را به میلی ثانیه برمی گرداند.
        public long GetIdleMs(long nowUnixMs)
        {
            return lastSeenAtUnixMs > 0 ? Math.Max(0, nowUnixMs - lastSeenAtUnixMs) : 0;
        }

        //* این تابع خلاصه وضعیت سشن را برای لاگ مسیرهای شبیه میرور برمی گرداند.
        public string GetMirrorLikeDebugSummary(long nowUnixMs)
        {
            return "ready=" + isReady +
                   " | authenticated=" + isAuthenticated +
                   " | valid=" + IsValidForGameplay +
                   " | ageMs=" + GetAgeMs(nowUnixMs) +
                   " | idleMs=" + GetIdleMs(nowUnixMs) +
                   " | " + OwnerDebugLabel;
        }

        private bool Same(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);
        }

        private string SafeValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
        }

        //* این تابع زمان آخرین دیده شدن پلیر را به روز می کند.
        public void Touch(long nowUnixMs)
        {
            lastSeenAtUnixMs = nowUnixMs;
        }

        /*
        توضیح مکتوب فایل:
        این فایل مدل داخلی پلیر تأیید شده داخل یونیتی ددیکیتد سرور است.
        بعد از وریفای موفق تیکت، برای هر کانکشن یک DedicatedPlayerSession ساخته می شود.
        این مدل فقط داخل ددیکیتد سرور استفاده می شود و به کلاینت اعتماد مستقیم ندارد.
        در فازهای بعدی موقع اسپاون، سینک حرکت و خروج پلیر از همین اطلاعات استفاده می شود.
        */
    }
}
