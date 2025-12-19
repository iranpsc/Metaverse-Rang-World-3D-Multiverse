using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/ConsoleUI")]
    [HelpURL("https://github.com/DreamFaver")]
    public class ConsoleUI : MonoBehaviour
    {
        public static ConsoleUI Instance;

        [SerializeField] private TMP_InputField Input;
        [SerializeField] private TMP_Text Output;
        [SerializeField] private ScrollRect Scroll;

        public GameObject ConsolePanel;
        private PlayerCommandSender Sender;

        private void Awake()
        {
            Instance = this;
        }
        public void Toggle(InputAction.CallbackContext _Ctx)
        {
            if (!_Ctx.action.triggered) return;

            if (ConsolePanel.activeSelf)
            {
                ConsolePanel.SetActive(false);
            }
            else
            {
                ConsolePanel.SetActive(true);
                Input.ActivateInputField();
            }
        }
        public void BindPlayer(PlayerCommandSender _Sender)
        {
            Sender = _Sender;
        }

        public void Submit()
        {
            if (Sender == null) return;

            string _Input = Input.text;
            Input.text = string.Empty;

            if (!_Input.StartsWith("/")) return;

            Sender.CmdSendCommand(_Input.Substring(1));
            if (Scroll != null)
            {
                StartCoroutine(ScrollToBottomCoroutine(Scroll));
            }
        }
        IEnumerator ScrollToBottomCoroutine(ScrollRect scrollRect)
        {
            // Wait for the end of the current frame
            yield return new WaitForEndOfFrame();

            scrollRect.verticalNormalizedPosition = 0f;
        }
        public void AddLog(string _Message)
        {
            if (!string.IsNullOrEmpty(_Message))
                Output.text += _Message + "\n";
        }
        public void ClearOutput()
        {
            Output.text = string.Empty;
        }
    }
}