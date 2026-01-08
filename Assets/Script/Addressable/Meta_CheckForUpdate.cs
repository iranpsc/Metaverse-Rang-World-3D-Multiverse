using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_CheckForUpdate")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_CheckForUpdate : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_CheckForUpdate] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}