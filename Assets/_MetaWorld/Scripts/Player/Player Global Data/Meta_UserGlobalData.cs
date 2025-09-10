using Mirror;
using UnityEngine;

namespace Meta
{
    /// <summary>
    /// This Script Ment To Pass The User Data Like ID To All The Scene For Networking
    /// </summary>
    /// 
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta UserGlobalData")]
    public class Meta_UserGlobalData : MonoBehaviour
    {
        public static Meta_UserGlobalData Instance;

        public string Username;
        public bool MouseReleased;

        [Header("Debugger")]
        public bool EnableLog;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }
        private void Start()
        {
            if (EnableLog) Debug.Log("[Meta_UserGlobalData] Global Data Activated.");
        }
    }
}