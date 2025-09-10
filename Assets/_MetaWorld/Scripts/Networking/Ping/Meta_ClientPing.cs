using Mirror;
using TMPro;
using UnityEngine;

namespace Meta
{
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta_ClientPing")]
    public class Meta_ClientPing : MonoBehaviour
    {
        [SerializeField] private TMP_Text Ping;

        [Header("Debugger")]
        public bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_ClientPing] Ping Log Enabled");
        }

        void Update()
        {
            if (!NetworkClient.active) return;

            string _Ping = Mathf.Round((float)(NetworkTime.rtt * 1000)).ToString();
            Ping.text = ($"ping: {_Ping}ms");
            Ping.color = NetworkClient.connectionQuality.ColorCode();
        }
    }
}