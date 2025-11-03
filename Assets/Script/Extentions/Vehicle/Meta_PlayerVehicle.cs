using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerVehicle")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerVehicle : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_PlayerVehicle] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}