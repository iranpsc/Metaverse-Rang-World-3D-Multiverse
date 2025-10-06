using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_GameManager")]
    [HelpURL("https://google.com")]
    public class Meta_GameManager : MonoBehaviour
    {

        [Header("References")]


        [Header("Settings")]
        [SerializeField] private string UserName;
        [SerializeField] private string UserID;

        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_GameManager] PutLogHere");
        }

        void Update()
        {
            
        }
    }
}