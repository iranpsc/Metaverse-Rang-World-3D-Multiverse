using Mirror;
using UnityEngine;
using TMPro;

namespace Meta
{
    [AddComponentMenu("Meta/Network Console Behaviour")]
    [HelpURL("https://github.com/DreamFaver")]
    public class NetworkConsoleBehaviour : NetworkBehaviour
    {

        [Header("References")]


        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[NetworkConsoleBehaviour] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}