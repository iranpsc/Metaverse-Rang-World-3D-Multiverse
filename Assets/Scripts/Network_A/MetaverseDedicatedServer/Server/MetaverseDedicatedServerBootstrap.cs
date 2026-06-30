using UnityEngine;

[DefaultExecutionOrder(-10000)]
public class MetaverseDedicatedServerBootstrap : MonoBehaviour
{
    [Header("Config")]
    [SerializeField] private MetaverseDedicatedServerRuntimeConfig runtimeConfig;
    [SerializeField] private bool installOnAwake = true;

    private static bool installed;

    public MetaverseDedicatedServerRuntimeConfig RuntimeConfig => runtimeConfig;

    private void Awake()
    {
        if (!installOnAwake) return;
        InstallSpawnSystem(runtimeConfig != null ? runtimeConfig : MetaverseDedicatedServerRuntimeConfig.LoadDefault());
    }

    public static MetaverseSpawnManager InstallSpawnSystem(MetaverseDedicatedServerRuntimeConfig config)
    {
        if (installed && MetaverseSpawnManager.Instance != null) return MetaverseSpawnManager.Instance;
        if (config == null)
        {
            Debug.LogWarning("[MetaverseDedicatedServerBootstrap] Runtime config not found in Resources.");
            return null;
        }

        if (!config.AutoInstallSpawnSystem)
        {
            if (config.LogInstall) Debug.Log("[MetaverseDedicatedServerBootstrap] Spawn system auto install is disabled.");
            return MetaverseSpawnManager.Instance;
        }

        MetaverseSpawnManager manager = MetaverseSpawnSystemInstaller.Install(config);
        installed = manager != null;
        return manager;
    }

    public static void ResetInstallStateForTests()
    {
        installed = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstallAfterSceneLoad()
    {
        MetaverseDedicatedServerRuntimeConfig config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
        if (config == null || !config.AutoInstallSpawnSystem) return;
        InstallSpawnSystem(config);
    }
}
