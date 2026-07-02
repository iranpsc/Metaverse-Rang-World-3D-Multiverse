
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
using System.Collections.Generic;
using Grpc.Core;
using Network_A.Auth;
using UnityEngine;

namespace Network_A.Core
{
    public static class GrpcMetadataAdapter
    {
        //* Builds shared gRPC metadata for native unary calls.
        public static Metadata BuildMetadata(bool auth, Dictionary<string, string> customHeaders = null)
        {
            var metadata = new Metadata();

            Add(metadata, "x-metaverse-client", Application.platform.ToString());
            Add(metadata, "x-metaverse-version", Application.version);
            Add(metadata, "x-client-name", ServerConfig.ClientName);
            Add(metadata, "x-client-version", ServerConfig.ClientVersion);

            if (customHeaders != null)
            {
                foreach (var pair in customHeaders)
                {
                    Add(metadata, pair.Key, pair.Value);
                }
            }

            if (auth)
            {
                string token = SecureTokenStorage.GetAccessToken();
                if (!string.IsNullOrEmpty(token)) Add(metadata, "authorization", "Bearer " + token);
            }

            return metadata;
        }

        //* Adds a metadata entry safely.
        private static void Add(Metadata metadata, string key, string value)
        {
            if (metadata == null) return;
            if (string.IsNullOrWhiteSpace(key)) return;
            if (value == null) value = string.Empty;

            metadata.Add(key.ToLowerInvariant(), value);
        }
    }
}

#endif