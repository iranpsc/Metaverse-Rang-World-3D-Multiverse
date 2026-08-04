using Network_A.Auth;
using UnityEngine;

namespace Network_A.Temporary
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-9999)]
    public sealed class TemporaryCompletedBuildingsUrlBootstrap : MonoBehaviour
    {
        private const string TemporaryCompletedBuildingsUrl =
            "https://dev-world-3d.metarang.com/completed-buildings.json";

        private void Awake()
        {
            ServerConfig.UseCompletedBuildingsUrl(TemporaryCompletedBuildingsUrl);

            Debug.Log(
                "[TemporaryCompletedBuildingsUrlBootstrap] Completed buildings URL: " +
                ServerConfig.CompletedBuildingsUrl);
        }
    }
}