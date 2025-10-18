using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerConfig")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerConfig : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_PlayerConfig] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}