using Network_A.Core;

namespace Network_A.Auth
{
    public static class ServerConfig
    {
        public static TransportKind CurrentTransportKind = TransportKind.GrpcWeb;

        public static Endpoint CurrentEndpoint = new Endpoint("localhost", 8443, true);
        public static Endpoint GrpcWebEndpoint = new Endpoint("localhost", 8443, true);
        public static Endpoint GrpcNativeEndpoint = new Endpoint("localhost", 50051, false);

        public static string ServiceName = "metaverse.v1.AuthService";
        public static string HealthServiceName = "metaverse.v1.HealthService";
        public static string ClientName = "Unity";
        public static string ClientVersion = "1.0.0";
        public static int TimeoutSeconds = 15;

        public static int RefreshRequestTokenFieldNumber = 1;
        public static int AuthReplyRefreshTokenFieldNumber = 4;
        public static int LogoutRequestRefreshTokenFieldNumber = 1;

        public static string RegisterUrl { get { return BuildGrpcWebUrl(ServiceName, "Register"); } }
        public static string LoginUrl { get { return BuildGrpcWebUrl(ServiceName, "Login"); } }
        public static string RefreshUrl { get { return BuildGrpcWebUrl(ServiceName, "Refresh"); } }
        public static string GetUserDataUrl { get { return BuildGrpcWebUrl(ServiceName, "GetUserData"); } }
        public static string LogoutUrl { get { return BuildGrpcWebUrl(ServiceName, "Logout"); } }
        public static string HealthUrl { get { return BuildGrpcWebUrl(HealthServiceName, "Check"); } }
        public static string LogoutAllDevicesUrl { get { return BuildGrpcWebUrl(ServiceName, "LogoutAllDevices"); } }

        public static string NativeRegisterMethod { get { return BuildGrpcNativeMethod(ServiceName, "Register"); } }
        public static string NativeLoginMethod { get { return BuildGrpcNativeMethod(ServiceName, "Login"); } }
        public static string NativeRefreshMethod { get { return BuildGrpcNativeMethod(ServiceName, "Refresh"); } }
        public static string NativeGetUserDataMethod { get { return BuildGrpcNativeMethod(ServiceName, "GetUserData"); } }
        public static string NativeLogoutMethod { get { return BuildGrpcNativeMethod(ServiceName, "Logout"); } }
        public static string NativeHealthMethod { get { return BuildGrpcNativeMethod(HealthServiceName, "Check"); } }
        public static string NativeLogoutAllDevicesMethod { get { return BuildGrpcNativeMethod(ServiceName, "LogoutAllDevices"); } }

        //* Sets the local Envoy endpoint used for gRPC-Web during development.
        public static void UseLocalGrpcWeb()
        {
            CurrentTransportKind = TransportKind.GrpcWeb;
            GrpcWebEndpoint = new Endpoint("localhost", 8443, true);
            CurrentEndpoint = GrpcWebEndpoint;
        }

        //* Sets the local native gRPC endpoint used by Windows and Android/Quest during development.
        public static void UseLocalGrpcNative()
        {
            CurrentTransportKind = TransportKind.GrpcNative;
            GrpcNativeEndpoint = new Endpoint("localhost", 50051, false);
            CurrentEndpoint = GrpcNativeEndpoint;
        }

        //* Sets the current transport kind without changing endpoint values.
        public static void UseTransport(TransportKind transportKind)
        {
            CurrentTransportKind = transportKind;
            CurrentEndpoint = transportKind == TransportKind.GrpcNative ? GrpcNativeEndpoint : GrpcWebEndpoint;
        }

        //* Sets a custom endpoint for the current transport.
        public static void UseEndpoint(Endpoint endpoint)
        {
            CurrentEndpoint = endpoint;

            if (CurrentTransportKind == TransportKind.GrpcNative) GrpcNativeEndpoint = endpoint;
            else GrpcWebEndpoint = endpoint;
        }

        //* Sets a custom gRPC-Web endpoint.
        public static void UseGrpcWebEndpoint(Endpoint endpoint)
        {
            GrpcWebEndpoint = endpoint;

            if (CurrentTransportKind == TransportKind.GrpcWeb) CurrentEndpoint = endpoint;
        }

        //* Sets a custom native gRPC endpoint.
        public static void UseGrpcNativeEndpoint(Endpoint endpoint)
        {
            GrpcNativeEndpoint = endpoint;

            if (CurrentTransportKind == TransportKind.GrpcNative) CurrentEndpoint = endpoint;
        }

        //* Builds a gRPC-Web unary URL for Envoy.
        public static string BuildGrpcWebUrl(string serviceName, string methodName)
        {
            return GrpcWebEndpoint.ToHttpBaseUrl() + "/" + serviceName + "/" + methodName;
        }

        //* Builds a native gRPC method path.
        public static string BuildGrpcNativeMethod(string serviceName, string methodName)
        {
            return "/" + serviceName + "/" + methodName;
        }

        //* Builds the native gRPC target address.
        public static string BuildGrpcNativeTarget()
        {
            return GrpcNativeEndpoint.Host + ":" + GrpcNativeEndpoint.Port;
        }

        //* Returns true when the current transport is gRPC-Web.
        public static bool IsGrpcWeb()
        {
            return CurrentTransportKind == TransportKind.GrpcWeb;
        }

        //* Returns true when the current transport is native gRPC.
        public static bool IsGrpcNative()
        {
            return CurrentTransportKind == TransportKind.GrpcNative;
        }
    }
}