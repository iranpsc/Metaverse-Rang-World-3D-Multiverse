using UnityEngine;

[DefaultExecutionOrder(-10000)]
public class MetaverseDedicatedServerBootstrap : MonoBehaviour
{
    [Header("Config")]
    [SerializeField] private MetaverseDedicatedServerRuntimeConfig runtimeConfig;
    [SerializeField] private bool installOnAwake = true;

    [Header("Phase 33A")]
    [SerializeField] private bool installMirrorLikeGameplayApi = true;

    private static bool installed;
    private static MetaverseDedicatedServerRuntimeConfig activeRuntimeConfig;

    public MetaverseDedicatedServerRuntimeConfig RuntimeConfig => runtimeConfig;
    public static bool IsInstalled => installed && MetaverseSpawnManager.Instance != null;
    public static MetaverseDedicatedServerRuntimeConfig ActiveRuntimeConfig => activeRuntimeConfig;

    private void Awake()
    {
        if (!installOnAwake) return;
        MetaverseDedicatedServerRuntimeConfig config = runtimeConfig != null ? runtimeConfig : MetaverseDedicatedServerRuntimeConfig.LoadDefault();
        if (installMirrorLikeGameplayApi) InstallMirrorLikeGameplayApi(config);
        else InstallSpawnSystem(config);
    }

    public static MetaverseSpawnManager InstallSpawnSystem(MetaverseDedicatedServerRuntimeConfig config)
    {
        if (installed && MetaverseSpawnManager.Instance != null) return MetaverseSpawnManager.Instance;
        if (config == null)
        {
            Debug.LogWarning("[MetaverseDedicatedServerBootstrap] Runtime config not found in Resources.");
            return null;
        }

        activeRuntimeConfig = config;

        if (!config.AutoInstallSpawnSystem)
        {
            if (config.LogInstall) Debug.Log("[MetaverseDedicatedServerBootstrap] Spawn system auto install is disabled.");
            return MetaverseSpawnManager.Instance;
        }

        MetaverseSpawnManager manager = MetaverseSpawnSystemInstaller.Install(config);
        installed = manager != null;
        return manager;
    }

    public static MetaverseSpawnManager InstallMirrorLikeGameplayApi(MetaverseDedicatedServerRuntimeConfig config)
    {
        if (installed && MetaverseSpawnManager.Instance != null) return MetaverseSpawnManager.Instance;
        if (config == null)
        {
            Debug.LogWarning("[MetaverseDedicatedServerBootstrap] Runtime config not found for Phase 33A.");
            return null;
        }

        activeRuntimeConfig = config;

        if (!config.AutoInstallSpawnSystem)
        {
            if (config.LogInstall) Debug.Log("[MetaverseDedicatedServerBootstrap] Phase 33A install skipped because auto install is disabled.");
            return MetaverseSpawnManager.Instance;
        }

        MetaverseSpawnManager manager = MetaverseSpawnSystemInstaller.InstallMirrorLikeGameplayApi(config);
        installed = manager != null;
        if (config.LogInstall) Debug.Log("[MetaverseDedicatedServerBootstrap] Phase33A install result | installed=" + installed + " | " + MetaverseSpawnSystemInstaller.GetMirrorLikeGameplayApiInstallSummary());
        return manager;
    }

    public static MetaverseSpawnManager InstallMirrorLikeGameplayApiFromResources()
    {
        return InstallMirrorLikeGameplayApi(MetaverseDedicatedServerRuntimeConfig.LoadDefault());
    }

    public static string GetInstallDebugSummary()
    {
        return "installed=" + IsInstalled +
               " | activeConfig=" + (activeRuntimeConfig != null ? activeRuntimeConfig.name : "null") +
               " | spawnManager=" + (MetaverseSpawnManager.Instance != null ? "ON" : "OFF") +
               " | phase33A=" + MetaverseSpawnSystemInstaller.GetMirrorLikeGameplayApiInstallSummary();
    }

    public static void ResetInstallStateForTests()
    {
        installed = false;
        activeRuntimeConfig = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstallAfterSceneLoad()
    {
        MetaverseDedicatedServerRuntimeConfig config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
        if (config == null || !config.AutoInstallSpawnSystem) return;
        InstallMirrorLikeGameplayApi(config);
    }
}
