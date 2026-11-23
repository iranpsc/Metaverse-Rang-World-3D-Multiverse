using Network_A.Core;
using UnityEngine;

namespace Network_A.Auth
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10000)]
    public sealed class ServerConfigBootstrap : MonoBehaviour
    {
        public enum ServerEnvironment
        {
            Local,
            Dedicated
        }

        public static bool HasAppliedConfiguration { get; private set; }
        public static ServerEnvironment AppliedEnvironment { get; private set; }

        [Header("Server")]
        [SerializeField] private ServerEnvironment serverEnvironment = ServerEnvironment.Dedicated;

#if UNITY_EDITOR
        [Header("Editor Test")]
        [Tooltip("خاموش: gRPC-Web | روشن: Native gRPC")]
        [SerializeField] private bool editorUseNativeGrpc;
#endif

        private void Awake()
        {
            bool useNativeGrpc;

#if UNITY_WEBGL && !UNITY_EDITOR
            useNativeGrpc = false;
#elif UNITY_EDITOR
            useNativeGrpc = editorUseNativeGrpc;
#elif UNITY_STANDALONE_WIN || UNITY_ANDROID
            useNativeGrpc = true;
#else
            useNativeGrpc = false;
#endif

            if (serverEnvironment == ServerEnvironment.Local)
            {
                if (useNativeGrpc)
                    ServerConfig.UseLocalGrpcNative();
                else
                    ServerConfig.UseLocalGrpcWeb();
            }
            else
            {
                if (useNativeGrpc)
                    ServerConfig.UseDedicatedGrpcNative();
                else
                    ServerConfig.UseDedicatedGrpcWeb();
            }

            AppliedEnvironment = serverEnvironment;
            HasAppliedConfiguration = true;

            NetworkFileLogger.Info(
                "SERVER_CONFIG_BOOTSTRAP",
                "environment=" + serverEnvironment +
                " | platform=" + Application.platform +
                " | transport=" + ServerConfig.CurrentTransportKind +
                " | endpoint=" + ServerConfig.CurrentEndpoint);
        }
    }
}
