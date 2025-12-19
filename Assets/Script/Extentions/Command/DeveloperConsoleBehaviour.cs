using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/DeveloperConsoleBehaviour")]
    [HelpURL("https://github.com/DreamFaver")]
    public class DeveloperConsoleBehaviour : NetworkBehaviour
    {
        [SerializeField] private string Prefix = string.Empty;
        [SerializeField] private ConsoleCommand[] Commands = new ConsoleCommand[0];

        [Header("UI References")]
        [SerializeField] private GameObject ConsolePanel = null;
        [SerializeField] private TMP_InputField InputField = null;
        [SerializeField] private TMP_Text LogPanel = null;
        [SerializeField] private Scrollbar LogScrollRect = null;

        private static DeveloperConsoleBehaviour instance;

        private DeveloperConsole developerConsole;

        public DeveloperConsole DeveloperConsole
        {
            get
            {
                if (developerConsole != null) { return developerConsole; }
                return developerConsole = new DeveloperConsole(Prefix, Commands);
            }
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        
        public void Toggle(InputAction.CallbackContext _Ctx)
        {
            if (!isLocalPlayer || !_Ctx.action.triggered) return;

            if (ConsolePanel.activeSelf)
            {
                ConsolePanel.SetActive(false);
            }
            else
            {
                ConsolePanel.SetActive(true);
                InputField.ActivateInputField();
            }
        }
        public void ExecuteCommand(string _InputValue)
        {
            if (string.IsNullOrEmpty(_InputValue)) return;

            CmdProccessCommandOnServer(_InputValue);

            InputField.text = string.Empty;
            InputField.ActivateInputField();
        }

        [Command]
        private void CmdProccessCommandOnServer(string _InputValue)
        {
            string _Output = DeveloperConsole.ExecuteCommand(_InputValue);
            
            TargetReceiveLog(connectionToClient, _Output);
        }

        [TargetRpc]
        private void TargetReceiveLog(NetworkConnection _Target, string _Message)
        {
            if (!string.IsNullOrEmpty(_Message))
            {
                string _LogEntry = $"{_Message}\n";
                LogPanel.text += _LogEntry;

                if (LogScrollRect != null)
                {
                    LogScrollRect.value = -.5f;
                }
            }
        }
    }
}
