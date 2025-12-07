using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_CommandManager")]
    [HelpURL("https://google.com")]
    public class Meta_CommandManager : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_CommandManager] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}