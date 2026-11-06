using System;

namespace Network_A.Lobby.Buildings
{
    [Serializable]
    public sealed class CompletedBuildingDto
    {
        public int id;
        public int feature_id;
        public string feature_properties_id;
        public string karbari;
        public string density;
        public string length;
        public string width;

        //* این تابع مقدارهای متنی خالی را به رشته امن تبدیل می‌کند تا مصرف‌کننده‌های رابط کاربری با مقدار تهی روبه‌رو نشوند.
        public void Normalize()
        {
            feature_properties_id = feature_properties_id ?? string.Empty;
            karbari = karbari ?? string.Empty;
            density = density ?? string.Empty;
            length = length ?? string.Empty;
            width = width ?? string.Empty;
        }

        //* این تابع بررسی می‌کند ساختمان دریافتی شناسه اصلی معتبر داشته باشد.
        public bool HasValidFeatureId()
        {
            return feature_id > 0;
        }
    }
}
