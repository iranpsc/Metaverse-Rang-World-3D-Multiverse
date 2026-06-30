using UnityEngine;

[CreateAssetMenu(fileName = ResourcesAssetName, menuName = "Metaverse/Dedicated Server/Runtime Config")]
public class MetaverseDedicatedServerRuntimeConfig : ScriptableObject
{
    public const string ResourcesAssetName = "MetaverseDedicatedServerRuntimeConfig";

    [Header("Spawn System")]
    [SerializeField] private MetaverseNetworkPrefabRegistry prefabRegistry;
    [SerializeField] private bool autoInstallSpawnSystem = true;
    [SerializeField] private bool dontDestroyOnLoad = true;
    [SerializeField] private string spawnManagerObjectName = "MetaverseSpawnManager";
    [SerializeField] private string spawnedRootObjectName = "Metaverse_Spawned_Root";

    [Header("Network Bridge")]
    [SerializeField] private bool autoInstallSpawnNetworkBridge = true;
    [SerializeField] private string spawnBridgeObjectName = "MetaverseSpawnNetworkBridge";
    [SerializeField] private bool autoInstallNetworkRpcBridge = true;
    [SerializeField] private string networkRpcBridgeObjectName = "MetaverseNetworkRpcBridge";

    [Header("Diagnostics")]
    [SerializeField] private bool logInstall = true;

    [Header("Phase 18.3 Test Tools")]
    [SerializeField] private bool enableRuntimeSpawnTestPrefab;
    [SerializeField] private bool enableSpawnRouteSmokeTest;
    [SerializeField] private bool enableClientSpawnRouteSmokeReporter;

    [Header("Phase 19 Test Tools")]
    [SerializeField] private bool enableNetworkBehaviourLifecycleSmokeTest = false;
    [SerializeField] private string networkBehaviourLifecycleSmokePrefabId = "metaverse_network_behaviour_probe";
    [SerializeField] private int networkBehaviourLifecycleSmokeSpawnRequiredPlayers = 1;
    [SerializeField] private int networkBehaviourLifecycleSmokeSnapshotRequiredPlayers = 3;
    [SerializeField] private float networkBehaviourLifecycleSmokeInitialDelaySeconds = 5f;
    [SerializeField] private float networkBehaviourLifecycleSmokeMinimumAliveSeconds = 25f;
    [SerializeField] private float networkBehaviourLifecycleSmokeMaxWaitBeforeSpawnSeconds = 120f;
    [SerializeField] private float networkBehaviourLifecycleSmokeMaxSnapshotWaitSeconds = 180f;
    [SerializeField] private float networkBehaviourLifecycleSmokeDespawnDelayAfterSnapshotSeconds = 8f;

    [Header("Phase 20 Test Tools")]
    [SerializeField] private bool enableNetworkRpcSmokeTest = false;
    [SerializeField] private string networkRpcSmokePrefabId = "metaverse_network_rpc_probe";
    [SerializeField] private int networkRpcSmokeSpawnRequiredPlayers = 1;
    [SerializeField] private int networkRpcSmokeSnapshotRequiredPlayers = 3;
    [SerializeField] private float networkRpcSmokeInitialDelaySeconds = 5f;
    [SerializeField] private float networkRpcSmokeMinimumAliveSeconds = 30f;
    [SerializeField] private float networkRpcSmokeDespawnDelayAfterSnapshotSeconds = 10f;

    public MetaverseNetworkPrefabRegistry PrefabRegistry => prefabRegistry;
    public bool AutoInstallSpawnSystem => autoInstallSpawnSystem;
    public bool DontDestroyOnLoad => dontDestroyOnLoad;
    public string SpawnManagerObjectName => string.IsNullOrWhiteSpace(spawnManagerObjectName) ? "MetaverseSpawnManager" : spawnManagerObjectName.Trim();
    public string SpawnedRootObjectName => string.IsNullOrWhiteSpace(spawnedRootObjectName) ? "Metaverse_Spawned_Root" : spawnedRootObjectName.Trim();
    public bool AutoInstallSpawnNetworkBridge => autoInstallSpawnNetworkBridge;
    public string SpawnBridgeObjectName => string.IsNullOrWhiteSpace(spawnBridgeObjectName) ? "MetaverseSpawnNetworkBridge" : spawnBridgeObjectName.Trim();
    public bool AutoInstallNetworkRpcBridge => autoInstallNetworkRpcBridge;
    public string NetworkRpcBridgeObjectName => string.IsNullOrWhiteSpace(networkRpcBridgeObjectName) ? "MetaverseNetworkRpcBridge" : networkRpcBridgeObjectName.Trim();
    public bool LogInstall => logInstall;
    public bool EnableRuntimeSpawnTestPrefab => enableRuntimeSpawnTestPrefab;
    public bool EnableSpawnRouteSmokeTest => enableSpawnRouteSmokeTest;
    public bool EnableClientSpawnRouteSmokeReporter => enableClientSpawnRouteSmokeReporter;
    public bool EnableNetworkBehaviourLifecycleSmokeTest => enableNetworkBehaviourLifecycleSmokeTest && Application.isEditor;
    public string NetworkBehaviourLifecycleSmokePrefabId => string.IsNullOrWhiteSpace(networkBehaviourLifecycleSmokePrefabId) ? "metaverse_network_behaviour_probe" : networkBehaviourLifecycleSmokePrefabId.Trim();
    public int NetworkBehaviourLifecycleSmokeSpawnRequiredPlayers => Mathf.Max(1, networkBehaviourLifecycleSmokeSpawnRequiredPlayers);
    public int NetworkBehaviourLifecycleSmokeSnapshotRequiredPlayers => Mathf.Max(NetworkBehaviourLifecycleSmokeSpawnRequiredPlayers, networkBehaviourLifecycleSmokeSnapshotRequiredPlayers);
    public float NetworkBehaviourLifecycleSmokeInitialDelaySeconds => Mathf.Max(0f, networkBehaviourLifecycleSmokeInitialDelaySeconds);
    public float NetworkBehaviourLifecycleSmokeMinimumAliveSeconds => Mathf.Max(1f, networkBehaviourLifecycleSmokeMinimumAliveSeconds);
    public float NetworkBehaviourLifecycleSmokeMaxWaitBeforeSpawnSeconds => Mathf.Max(1f, networkBehaviourLifecycleSmokeMaxWaitBeforeSpawnSeconds);
    public float NetworkBehaviourLifecycleSmokeMaxSnapshotWaitSeconds => Mathf.Max(1f, networkBehaviourLifecycleSmokeMaxSnapshotWaitSeconds);
    public float NetworkBehaviourLifecycleSmokeDespawnDelayAfterSnapshotSeconds => Mathf.Max(0f, networkBehaviourLifecycleSmokeDespawnDelayAfterSnapshotSeconds);
    public bool EnableNetworkRpcSmokeTest => enableNetworkRpcSmokeTest;
    public string NetworkRpcSmokePrefabId => string.IsNullOrWhiteSpace(networkRpcSmokePrefabId) ? "metaverse_network_rpc_probe" : networkRpcSmokePrefabId.Trim();
    public int NetworkRpcSmokeSpawnRequiredPlayers => Mathf.Max(1, networkRpcSmokeSpawnRequiredPlayers);
    public int NetworkRpcSmokeSnapshotRequiredPlayers => Mathf.Max(NetworkRpcSmokeSpawnRequiredPlayers, networkRpcSmokeSnapshotRequiredPlayers);
    public float NetworkRpcSmokeInitialDelaySeconds => Mathf.Max(0f, networkRpcSmokeInitialDelaySeconds);
    public float NetworkRpcSmokeMinimumAliveSeconds => Mathf.Max(1f, networkRpcSmokeMinimumAliveSeconds);
    public float NetworkRpcSmokeDespawnDelayAfterSnapshotSeconds => Mathf.Max(0f, networkRpcSmokeDespawnDelayAfterSnapshotSeconds);

    public static MetaverseDedicatedServerRuntimeConfig LoadDefault()
    {
        return Resources.Load<MetaverseDedicatedServerRuntimeConfig>(ResourcesAssetName);
    }

    public void SetPrefabRegistry(MetaverseNetworkPrefabRegistry registry) { prefabRegistry = registry; }
    public void SetAutoInstallSpawnSystem(bool value) { autoInstallSpawnSystem = value; }
    public void SetDontDestroyOnLoad(bool value) { dontDestroyOnLoad = value; }
    public void SetSpawnManagerObjectName(string value) { spawnManagerObjectName = string.IsNullOrWhiteSpace(value) ? "MetaverseSpawnManager" : value.Trim(); }
    public void SetSpawnedRootObjectName(string value) { spawnedRootObjectName = string.IsNullOrWhiteSpace(value) ? "Metaverse_Spawned_Root" : value.Trim(); }
    public void SetAutoInstallSpawnNetworkBridge(bool value) { autoInstallSpawnNetworkBridge = value; }
    public void SetSpawnBridgeObjectName(string value) { spawnBridgeObjectName = string.IsNullOrWhiteSpace(value) ? "MetaverseSpawnNetworkBridge" : value.Trim(); }
    public void SetAutoInstallNetworkRpcBridge(bool value) { autoInstallNetworkRpcBridge = value; }
    public void SetNetworkRpcBridgeObjectName(string value) { networkRpcBridgeObjectName = string.IsNullOrWhiteSpace(value) ? "MetaverseNetworkRpcBridge" : value.Trim(); }
    public void SetLogInstall(bool value) { logInstall = value; }
    public void SetEnableRuntimeSpawnTestPrefab(bool value) { enableRuntimeSpawnTestPrefab = value; }
    public void SetEnableSpawnRouteSmokeTest(bool value) { enableSpawnRouteSmokeTest = value; }
    public void SetEnableClientSpawnRouteSmokeReporter(bool value) { enableClientSpawnRouteSmokeReporter = value; }
    public void SetEnableNetworkBehaviourLifecycleSmokeTest(bool value) { enableNetworkBehaviourLifecycleSmokeTest = value; }
    public void SetNetworkBehaviourLifecycleSmokePrefabId(string value) { networkBehaviourLifecycleSmokePrefabId = string.IsNullOrWhiteSpace(value) ? "metaverse_network_behaviour_probe" : value.Trim(); }
    public void SetNetworkBehaviourLifecycleSmokeSpawnRequiredPlayers(int value) { networkBehaviourLifecycleSmokeSpawnRequiredPlayers = Mathf.Max(1, value); }
    public void SetNetworkBehaviourLifecycleSmokeSnapshotRequiredPlayers(int value) { networkBehaviourLifecycleSmokeSnapshotRequiredPlayers = Mathf.Max(1, value); }
    public void SetNetworkBehaviourLifecycleSmokeInitialDelaySeconds(float value) { networkBehaviourLifecycleSmokeInitialDelaySeconds = Mathf.Max(0f, value); }
    public void SetNetworkBehaviourLifecycleSmokeMinimumAliveSeconds(float value) { networkBehaviourLifecycleSmokeMinimumAliveSeconds = Mathf.Max(1f, value); }
    public void SetNetworkBehaviourLifecycleSmokeMaxWaitBeforeSpawnSeconds(float value) { networkBehaviourLifecycleSmokeMaxWaitBeforeSpawnSeconds = Mathf.Max(1f, value); }
    public void SetNetworkBehaviourLifecycleSmokeMaxSnapshotWaitSeconds(float value) { networkBehaviourLifecycleSmokeMaxSnapshotWaitSeconds = Mathf.Max(1f, value); }
    public void SetNetworkBehaviourLifecycleSmokeDespawnDelayAfterSnapshotSeconds(float value) { networkBehaviourLifecycleSmokeDespawnDelayAfterSnapshotSeconds = Mathf.Max(0f, value); }
    public void SetEnableNetworkRpcSmokeTest(bool value) { enableNetworkRpcSmokeTest = value; }
    public void SetNetworkRpcSmokePrefabId(string value) { networkRpcSmokePrefabId = string.IsNullOrWhiteSpace(value) ? "metaverse_network_rpc_probe" : value.Trim(); }
    public void SetNetworkRpcSmokeSpawnRequiredPlayers(int value) { networkRpcSmokeSpawnRequiredPlayers = Mathf.Max(1, value); }
    public void SetNetworkRpcSmokeSnapshotRequiredPlayers(int value) { networkRpcSmokeSnapshotRequiredPlayers = Mathf.Max(1, value); }
    public void SetNetworkRpcSmokeInitialDelaySeconds(float value) { networkRpcSmokeInitialDelaySeconds = Mathf.Max(0f, value); }
    public void SetNetworkRpcSmokeMinimumAliveSeconds(float value) { networkRpcSmokeMinimumAliveSeconds = Mathf.Max(1f, value); }
    public void SetNetworkRpcSmokeDespawnDelayAfterSnapshotSeconds(float value) { networkRpcSmokeDespawnDelayAfterSnapshotSeconds = Mathf.Max(0f, value); }
}
