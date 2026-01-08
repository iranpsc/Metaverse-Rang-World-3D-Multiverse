using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_OnDemandDownloader")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_OnDemandDownloader : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_OnDemandDownloader] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}