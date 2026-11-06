using System.Collections.Generic;

namespace Network_A.Lobby.Buildings
{
    public sealed class CompletedBuildingsLoadResult
    {
        public bool IsSuccess;
        public List<CompletedBuildingDto> Buildings = new List<CompletedBuildingDto>();
        public string ErrorMessage = string.Empty;
        public string TechnicalDetails = string.Empty;
        public int StatusCode;
        public bool IsNetworkError;
        public int LoadedPages;
        public int TotalExpectedItems;

        //* این تابع نتیجه موفق دریافت همه صفحه‌های ساختمان را می‌سازد.
        public static CompletedBuildingsLoadResult Success(List<CompletedBuildingDto> buildings, int loadedPages, int totalExpectedItems)
        {
            return new CompletedBuildingsLoadResult
            {
                IsSuccess = true,
                Buildings = buildings ?? new List<CompletedBuildingDto>(),
                LoadedPages = loadedPages,
                TotalExpectedItems = totalExpectedItems
            };
        }

        //* این تابع نتیجه ناموفق دریافت ساختمان را همراه با اطلاعات لازم برای گزارش می‌سازد.
        public static CompletedBuildingsLoadResult Failure(string errorMessage, string technicalDetails, int statusCode, bool isNetworkError, int loadedPages)
        {
            return new CompletedBuildingsLoadResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage ?? string.Empty,
                TechnicalDetails = technicalDetails ?? string.Empty,
                StatusCode = statusCode,
                IsNetworkError = isNetworkError,
                LoadedPages = loadedPages
            };
        }
    }
}
