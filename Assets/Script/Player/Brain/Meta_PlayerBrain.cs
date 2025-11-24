using Mirror;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerBrain")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerBrain : NetworkBehaviour
    {

        [Header("References")]
        [SerializeField] private Transform PlayerSkin;
        [SerializeField] private bool TogglePlayerSkin;
        [SerializeField] private Transform PlayerCamera;
        [SerializeField] private bool TogglePlayerCamera;
        [SerializeField] private Transform PlayerCursor;
        [SerializeField] private bool TogglePlayerCursor;
        [SerializeField] private Transform PlayerUsername;
        [SerializeField] private bool TogglePlayerUsername;
        
        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        protected override void OnValidate()
        {
            PlayerSkin.gameObject.SetActive(TogglePlayerSkin);
            PlayerCamera.gameObject.SetActive(TogglePlayerCamera);
            PlayerCursor.gameObject.SetActive(TogglePlayerCursor);
            PlayerUsername.gameObject.SetActive(TogglePlayerUsername);
            Reset();
        }
        private void Reset()
        {

        }
    }
}