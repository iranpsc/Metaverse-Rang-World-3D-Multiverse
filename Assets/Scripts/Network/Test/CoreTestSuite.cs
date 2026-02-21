using UnityEngine;
using System.Collections;
using Assets.Scripts.Network.Core.Models;
using Assets.Scripts.Network.Core.Utils;
/// <summary>
/// تست واحد سریع برای اعتبارسنجی هسته ارتباطی
/// این اسکریپت در صحنه تست اجرا می‌شود
/// </summary>
/// 
namespace Assets.Scripts.Network.Test
{
    public class CoreTestSuite : MonoBehaviour
    {
        private void Awake()
        {
            //  Debug.unityLogger.logEnabled = false;  // همه Debug.Log/Warning/Error خاموش می‌شود
        }
        void Start()
        {
            RunAllTests();

        }
        public void RunAllTests()
        {
            Debug.Log("========================================");
            Debug.Log("شروع تست واحد هسته ارتباطی (مرحله ۱)...");
            Debug.Log("========================================");

            TestRequestModel();
            TestResponseModel();
            TestJSONSerializer();
            TestURLBuilder();
            TestNetworkError();

            Debug.Log("========================================");
            Debug.Log("تست واحد هسته ارتباطی کامل شد ✅");
            Debug.Log("========================================");
        }

        private void TestRequestModel()
        {
            Debug.Log("\n[تست ۱] RequestModel");

            var request = new RequestModel
            {
                Method = HttpMethod.POST,
                Url = "user/profile"
            }
            .AddHeader("X-Custom", "value")
            .AddQueryParam("userId", "123")
            .AddTag("auth")
            .SetJsonBody(new { name = "Test User" });

            Debug.Assert(!string.IsNullOrEmpty(request.RequestId), "RequestId باید تولید شود");
            Debug.Assert(request.Headers.ContainsKey("X-Custom"), "هدر باید اضافه شود");
            Debug.Assert(request.QueryParams.ContainsKey("userId"), "پارامتر Query باید اضافه شود");
            Debug.Assert(request.Tags.Contains("auth"), "تگ باید اضافه شود");

            Debug.Log("✅ RequestModel: موفق");
        }

        private void TestResponseModel()
        {
            Debug.Log("\n[تست ۲] ResponseModel");

            var successResponse = ResponseModel.Success("{\"userId\":\"123\",\"name\":\"Test\"}", 200);
            var user = successResponse.GetData<UserTestModel>();

            Debug.Assert(successResponse.IsSuccess, "پاسخ موفق باید IsSuccess=true داشته باشد");
            Debug.Assert(user.userId == "123", "دی‌سریالایز باید موفق باشد");

            var errorResponse = ResponseModel.Failure(
                new NetworkError(NetworkErrorCode.Unauthorized, "توکن منقضی شده"),
                "",
                401
            );

            Debug.Assert(!errorResponse.IsSuccess, "پاسخ خطا باید IsSuccess=false داشته باشد");
            Debug.Assert(errorResponse.StatusCode == 401, "کد وضعیت باید 401 باشد");

            Debug.Log("✅ ResponseModel: موفق");
        }

        private void TestJSONSerializer()
        {
            Debug.Log("\n[تست ۳] JSONSerializer");

            var obj = new SimpleTestModel
            {
                userId = "456",
                name = "Serializer Test",
                level = 10
            };

            string json = JSONSerializer.Serialize(obj);

            Debug.Assert(!string.IsNullOrEmpty(json), "سریالایز نباید خالی باشد");
            Debug.Assert(json.Contains("userId"), "JSON باید فیلدها را داشته باشد");

            var deserialized = JSONSerializer.Deserialize<SimpleTestModel>(json);
            Debug.Assert(deserialized.userId == "456", "دی‌سریالایز باید مقدار را حفظ کند");

            Debug.Log("✅ JSONSerializer: موفق");
        }
        private void TestURLBuilder()
        {
            Debug.Log("\n[تست ۴] URLBuilder");

            var paramsDict = new System.Collections.Generic.Dictionary<string, string>
        {
            { "userId", "789" },
            { "token", "abc123" }
        };

            string url = URLBuilder.Build("https://api.metaverse.gov.ir", "v1/user/profile", paramsDict);

            Debug.Assert(url.StartsWith("https://"), "آدرس باید با https شروع شود");
            Debug.Assert(url.Contains("userId=789"), "پارامتر userId باید وجود داشته باشد");
            Debug.Assert(URLBuilder.IsValidUrl(url), "آدرس باید معتبر باشد");

            Debug.Log("✅ URLBuilder: موفق");
        }

        private void TestNetworkError()
        {
            Debug.Log("\n[تست ۵] NetworkError");

            var error = new NetworkError(
                NetworkErrorCode.TokenExpired,
                "توکن منقضی شده",
                "لطفاً دوباره وارد شوید",
                null
            );

            Debug.Assert(error.Code == NetworkErrorCode.TokenExpired, "کد خطا باید صحیح باشد");
            Debug.Assert(!string.IsNullOrEmpty(error.Message), "پیام خطا نباید خالی باشد");

            Debug.Log("✅ NetworkError: موفق");
        }

        // مدل‌های تست
        [System.Serializable]
        private class UserTestModel
        {
            public string userId;
            public string name;
        }

        [System.Serializable]
        private class SimpleTestModel
        {
            public string userId;
            public string name;
            public int level;
        }
    }
}