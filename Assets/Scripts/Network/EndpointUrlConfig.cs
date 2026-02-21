using UnityEngine;

public static class EndpointUrlConfig
{
    // Auth
    public static string RegisterEndpoint => "api/auth/register";
    public static string LoginEndpoint => "oauth/token";
    public static string RefreshEndpoint => "api/auth/refresh";
    public static string MeEndpoint => "api/auth/me";
}
