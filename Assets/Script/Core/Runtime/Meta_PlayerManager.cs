using Mirror;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerManager")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerManager : MonoBehaviour
    {

        [Header("References")]
        public NetworkManager Manager;

        [Header("Settings")]
        public GameObject PlayerPrefab;
        public GameObject XRPlayerPrefab;

        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        public void Start()
        {
            if (XRCheck.IsVRActive())
            {
                Manager.playerPrefab = XRPlayerPrefab;
            }
            else
            {
                Manager.playerPrefab = PlayerPrefab;
            }
        }
    }
    public static class XRCheck
    {
        public static bool IsVRActive()
        {
            var _XrManager = XRGeneralSettings.Instance?.Manager;
            return _XrManager != null && _XrManager.isInitializationComplete && _XrManager.activeLoader != null;
        }
    }
}