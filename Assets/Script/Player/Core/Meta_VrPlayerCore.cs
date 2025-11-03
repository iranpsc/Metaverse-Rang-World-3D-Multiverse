using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VrPlayerCore")]
    [HelpURL("https://google.com")]
    public class Meta_VrPlayerCore : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_VrPlayerCore] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}