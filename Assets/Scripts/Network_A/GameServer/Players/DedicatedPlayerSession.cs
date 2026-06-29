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
