#if UNITY_SERVER
using Network_A.GameServer;
using Network_A.GameServer.Gameplay;
using UnityEngine;

namespace Network_A.Voice.RecordingSpatial.Dedicated
{
    [DefaultExecutionOrder(12000)]
    [DisallowMultipleComponent]
    public sealed class VoiceSpatialTimelineDedicatedInstaller : MonoBehaviour
    {
        private const string RootName =
            "Voice_Spatial_Timeline_Dedicated";

        private VoiceSpatialTimelineDedicatedSender sender;
        private float nextBindAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            VoiceSpatialTimelineDedicatedInstaller existing =
                FindObjectOfType<VoiceSpatialTimelineDedicatedInstaller>(true);

            if (existing != null) return;

            GameObject root = new GameObject(RootName);
            DontDestroyOnLoad(root);

            root.AddComponent<VoiceSpatialTimelineDedicatedInstaller>();

            Debug.Log(
                "VOICE_SPATIAL_TIMELINE_DEDICATED_INSTALLER=READY" +
                " | wrapperOnly=True" +
                " | existingFilesModified=False");
        }

        private void Awake()
        {
            sender = gameObject.AddComponent<VoiceSpatialTimelineDedicatedSender>();
        }

        private void Update()
        {
            if (sender == null || sender.IsConfigured) return;
            if (Time.realtimeSinceStartup < nextBindAt) return;

            nextBindAt = Time.realtimeSinceStartup + 1.0f;

            DedicatedPlayerStateStore stateStore =
                FindObjectOfType<DedicatedPlayerStateStore>(true);

            DedicatedServerRuntime runtime =
                FindObjectOfType<DedicatedServerRuntime>(true);

            GameServerControlDedicatedClient controlClient =
                FindObjectOfType<GameServerControlDedicatedClient>(true);

            if (stateStore == null || runtime == null || controlClient == null)
            {
                return;
            }

            sender.Configure(
                stateStore,
                runtime,
                controlClient);

            Debug.Log(
                "VOICE_SPATIAL_TIMELINE_DEDICATED_BIND=PASS" +
                " | stateStore=" + stateStore.name +
                " | runtime=" + runtime.name +
                " | controlClient=" + controlClient.name);
        }
    }
}
#endif
