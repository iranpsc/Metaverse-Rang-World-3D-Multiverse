using System;
using System.Collections.Generic;


/// <summary>
/// پاسخ استاندارد سرور برای لاگین/ثبت‌نام
/// </summary>
[Serializable]
public class AuthResponse
{
    public string token_type;
    public int expires_in;
    public string access_token;
    public string refresh_token;
    public bool success;
    public string message;         // پیام برای نمایش به کاربر
    public User user;

    public TokenData token;
}
[Serializable]
public class UserProfileResponse
{
    public bool success;
    public string message;
    public User user;
}

/// <summary>
/// مدل کاربر
/// </summary>
[Serializable]
public class User
{
    public string userId = "0";
    public string username;
    public string email;
    public string password;
    public string avatarId;
    public string displayName;
    public string bio;
    public string avatarUrl;
    public string refresh_token;
    public bool isActive;
    public string lastLogin;
    public string createdAt;
    public string updatedAt;
}

/// <summary>
/// مدل درخواست لاگین
/// </summary>
[Serializable]
public class LoginRequest
{
    public string username = "abbas.ajorlou1371@gmail.com";
    public string password = "46769732@cH";

    // OAuth Password Grant (طبق چیزی که قبلاً گذاشتی)
    public string grant_type = "password";
    public string client_id = "26";
    public string client_secret = "bdpJBrC44N78xRdjOs9lmEUxQvnFZKu3eqcQSfX6";
    public string scope = "*";
}


/// <summary>
/// مدل درخواست ثبت‌نام
/// </summary>
[Serializable]
public class RegisterRequest
{
    public string username;
    public string email;
    public string password;
    public string avatarId;
    public string displayName;
}

/// <summary>
/// مدل درخواست رفرش توکن
/// </summary>
[Serializable]
public class RefreshTokenRequest
{
    public string refreshToken;
    public string deviceInfo;
}

/// <summary>
/// اطلاعات ذخیره‌شده توکن در حافظه محلی
/// </summary>
[Serializable]
public class StoredTokenData
{
    public string encryptedToken;
    public string encryptedRefreshToken;
    public long expiryTimestamp;   // Unix timestamp
    public string userId;
    public string deviceFingerprint;
}

[Serializable]
public class TokenData
{
    public string token_type;
    public int expires_in;
    public string access_token;
    public string refresh_token;
}


[Serializable]
public class LoginFormRequest
{
    public string username;
    public string password;

    // OAuth Password Grant (پیش‌فرض‌ها)
    public string grant_type = "password";
    public string client_id = "4";
    public string client_secret = "8hJzyQYvAQDHuKVZVdrAZrIm5pj4n39IQPMxqDdC";
    public string scope = "*";

    public Dictionary<string, string> ToFormDictionary()
    {
        return new Dictionary<string, string>
        {
            ["username"] = username ?? "",
            ["password"] = password ?? "",
            ["grant_type"] = grant_type ?? "",
            ["client_id"] = client_id ?? "",
            ["client_secret"] = client_secret ?? "",
            ["scope"] = scope ?? ""
        };
    }
}







