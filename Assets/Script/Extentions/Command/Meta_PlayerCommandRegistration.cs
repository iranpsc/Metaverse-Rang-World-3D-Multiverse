using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerCommandRegistration")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerCommandRegistration : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_PlayerCommandRegistration] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}