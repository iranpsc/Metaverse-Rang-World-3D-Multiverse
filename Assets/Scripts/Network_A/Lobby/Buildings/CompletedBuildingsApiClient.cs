using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Lobby.Buildings
{
    public sealed class CompletedBuildingsApiClient
    {
        private readonly string endpointUrl;
        private readonly int requestTimeoutMs;
        private readonly int maximumPages;

        //* این تابع کلاینت دریافت ساختمان‌ها را با نشانی، مهلت درخواست و سقف صفحه‌های مجاز آماده می‌کند.
        public CompletedBuildingsApiClient(string endpointUrl, int requestTimeoutMs, int maximumPages)
        {
            this.endpointUrl = string.IsNullOrWhiteSpace(endpointUrl) ? string.Empty : endpointUrl.Trim();
            this.requestTimeoutMs = Mathf.Clamp(requestTimeoutMs, 1000, 60000);
            this.maximumPages = Mathf.Clamp(maximumPages, 1, 500);
        }

        //* این تابع همه صفحه‌های ساختمان‌های تکمیل‌شده را به ترتیب دریافت و بر اساس شناسه اصلی از تکرار جلوگیری می‌کند.
        public async Task<CompletedBuildingsLoadResult> GetAllAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(endpointUrl)) return CompletedBuildingsLoadResult.Failure("نشانی دریافت ساختمان‌ها تنظیم نشده است.", "Completed buildings endpoint is empty.", 0, false, 0);

            var buildings = new List<CompletedBuildingDto>();
            var featureIds = new HashSet<int>();
            int page = 1;
            int lastPage = 1;
            int loadedPages = 0;
            int totalExpectedItems = 0;

            while (page <= lastPage && page <= maximumPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompletedBuildingsPageResult pageResult = await GetPageAsync(page, cancellationToken);

                if (!pageResult.IsSuccess)
                {
                    return CompletedBuildingsLoadResult.Failure(pageResult.ErrorMessage, pageResult.TechnicalDetails, pageResult.StatusCode, pageResult.IsNetworkError, loadedPages);
                }

                CompletedBuildingsResponseDto response = pageResult.Response;
                response.Normalize();
                loadedPages++;

                if (response.meta.last_page > 0) lastPage = response.meta.last_page;
                if (response.meta.total > 0) totalExpectedItems = response.meta.total;
                if (lastPage > maximumPages) return CompletedBuildingsLoadResult.Failure("تعداد صفحه‌های پاسخ بیشتر از حد مجاز است.", "lastPage=" + lastPage + " | maximumPages=" + maximumPages, pageResult.StatusCode, false, loadedPages);

                for (int i = 0; i < response.data.Length; i++)
                {
                    CompletedBuildingDto building = response.data[i];
                    if (building == null || !building.HasValidFeatureId()) continue;
                    if (!featureIds.Add(building.feature_id)) continue;
                    buildings.Add(building);
                }

                NetworkFileLogger.Info("COMPLETED_BUILDINGS_PAGE", "page=" + page + " | lastPage=" + lastPage + " | pageItems=" + response.data.Length + " | uniqueItems=" + buildings.Count);
                page++;
            }

            if (page <= lastPage) return CompletedBuildingsLoadResult.Failure("دریافت همه صفحه‌های ساختمان کامل نشد.", "nextPage=" + page + " | lastPage=" + lastPage + " | maximumPages=" + maximumPages, 0, false, loadedPages);

            NetworkFileLogger.Info("COMPLETED_BUILDINGS_SUCCESS", "loadedPages=" + loadedPages + " | uniqueItems=" + buildings.Count + " | expectedItems=" + totalExpectedItems);
            return CompletedBuildingsLoadResult.Success(buildings, loadedPages, totalExpectedItems);
        }

        //* این تابع یک صفحه مشخص از فهرست ساختمان‌ها را با درخواست امن دریافت و پاسخ آن را تبدیل می‌کند.
        private async Task<CompletedBuildingsPageResult> GetPageAsync(int page, CancellationToken cancellationToken)
        {
            string pageUrl = BuildPageUrl(page);

            using (CancellationTokenSource timeoutCts = new CancellationTokenSource(requestTimeoutMs))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            using (UnityWebRequest request = UnityWebRequest.Get(pageUrl))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(1, Mathf.CeilToInt(requestTimeoutMs / 1000f));
                request.SetRequestHeader("Accept", "application/json");

                NetworkFileLogger.Info("COMPLETED_BUILDINGS_REQUEST", "page=" + page + " | url=" + pageUrl + " | timeoutMs=" + requestTimeoutMs);

                try
                {
                    await UnityWebRequestAsync.SendAsync(request, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    request.Abort();
                    bool externalCancellation = cancellationToken.IsCancellationRequested;
                    string message = externalCancellation ? "دریافت ساختمان‌ها لغو شد." : "زمان دریافت ساختمان‌ها به پایان رسید.";
                    string details = externalCancellation ? "Request canceled by caller." : "Request timeout after " + requestTimeoutMs + "ms.";
                    return CompletedBuildingsPageResult.Failure(message, details, 0, !externalCancellation);
                }
                catch (Exception ex)
                {
                    request.Abort();
                    return CompletedBuildingsPageResult.Failure("دریافت ساختمان‌ها با خطا روبه‌رو شد.", ex.ToString(), 0, true);
                }

                string body = request.downloadHandler != null ? request.downloadHandler.text ?? string.Empty : string.Empty;
                int statusCode = request.responseCode > int.MaxValue ? int.MaxValue : (int)request.responseCode;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    bool networkError = request.responseCode <= 0 || request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError;
                    string details = "page=" + page + " | status=" + request.responseCode + " | result=" + request.result + " | error=" + (request.error ?? string.Empty) + " | body=" + body;
                    return CompletedBuildingsPageResult.Failure("فهرست ساختمان‌ها دریافت نشد.", details, statusCode, networkError);
                }

                if (string.IsNullOrWhiteSpace(body)) return CompletedBuildingsPageResult.Failure("پاسخ ساختمان‌ها خالی است.", "page=" + page + " | status=" + request.responseCode, statusCode, false);

                try
                {
                    CompletedBuildingsResponseDto response = JsonUtility.FromJson<CompletedBuildingsResponseDto>(body);
                    if (response == null) return CompletedBuildingsPageResult.Failure("پاسخ ساختمان‌ها قابل خواندن نیست.", "page=" + page + " | status=" + request.responseCode + " | body=" + body, statusCode, false);
                    response.Normalize();
                    return CompletedBuildingsPageResult.Success(response, statusCode);
                }
                catch (Exception ex)
                {
                    return CompletedBuildingsPageResult.Failure("پاسخ ساختمان‌ها قابل خواندن نیست.", "page=" + page + " | status=" + request.responseCode + " | exception=" + ex + " | body=" + body, statusCode, false);
                }
            }
        }

        //* این تابع نشانی صفحه موردنظر را بدون تغییر نشانی اصلی آماده می‌کند.
        private string BuildPageUrl(int page)
        {
            string separator = endpointUrl.IndexOf("?", StringComparison.Ordinal) >= 0 ? "&" : "?";
            return endpointUrl + separator + "page=" + Mathf.Max(1, page);
        }

        private sealed class CompletedBuildingsPageResult
        {
            public bool IsSuccess;
            public CompletedBuildingsResponseDto Response;
            public string ErrorMessage = string.Empty;
            public string TechnicalDetails = string.Empty;
            public int StatusCode;
            public bool IsNetworkError;

            //* این تابع نتیجه موفق یک صفحه را می‌سازد.
            public static CompletedBuildingsPageResult Success(CompletedBuildingsResponseDto response, int statusCode)
            {
                return new CompletedBuildingsPageResult { IsSuccess = true, Response = response, StatusCode = statusCode };
            }

            //* این تابع نتیجه ناموفق یک صفحه را همراه با اطلاعات خطا می‌سازد.
            public static CompletedBuildingsPageResult Failure(string errorMessage, string technicalDetails, int statusCode, bool isNetworkError)
            {
                return new CompletedBuildingsPageResult
                {
                    IsSuccess = false,
                    ErrorMessage = errorMessage ?? string.Empty,
                    TechnicalDetails = technicalDetails ?? string.Empty,
                    StatusCode = statusCode,
                    IsNetworkError = isNetworkError
                };
            }
        }
    }
}
