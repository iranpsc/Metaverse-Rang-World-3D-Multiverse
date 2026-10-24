using System;

namespace Network_A.Lobby.Buildings
{
    [Serializable]
    public sealed class CompletedBuildingsResponseDto
    {
        public CompletedBuildingDto[] data;
        public CompletedBuildingsLinksDto links;
        public CompletedBuildingsMetaDto meta;

        //* این تابع آرایه و مدل‌های داخلی پاسخ را برای استفاده امن آماده می‌کند.
        public void Normalize()
        {
            if (data == null) data = new CompletedBuildingDto[0];
            if (links == null) links = new CompletedBuildingsLinksDto();
            if (meta == null) meta = new CompletedBuildingsMetaDto();

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != null) data[i].Normalize();
            }
        }
    }

    [Serializable]
    public sealed class CompletedBuildingsLinksDto
    {
        public string first;
        public string last;
        public string next;
        public string prev;
    }

    [Serializable]
    public sealed class CompletedBuildingsMetaDto
    {
        public int current_page;
        public int from;
        public int last_page;
        public string path;
        public int per_page;
        public int to;
        public int total;
    }
}
