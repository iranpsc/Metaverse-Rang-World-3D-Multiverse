using Mirror;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class ConsoleManager : MonoBehaviour
{
    public static ConsoleManager LocalInstance;

    [Header("UI")]
    public GameObject Panel;
    public TMP_InputField InputField;
    public TMP_Text PreviewText;
    public ScrollRect ScrollRect;

    [Header("Input")]
    public InputActionReference ToggleInput;

    private CommandProcessor Processor;

    private void Awake()
    {
        LocalInstance = this;
        Processor = new CommandProcessor();
        Panel.SetActive(false);
    }
    private void OnEnable()
    {
        ToggleInput.action.Enable();
        ToggleInput.action.performed += Toggle;
    }
    private void OnDisable()
    {
        ToggleInput.action.performed -= Toggle;
        ToggleInput.action.Disable();
    }
    public void Toggle(InputAction.CallbackContext _Ctx)
    {
        Panel.SetActive(!Panel.activeSelf);
        if (Panel.activeSelf)
            InputField.ActivateInputField();
    }

    public void OnSubmit()
    {
        string _Input = InputField.text;
        InputField.text = "";

        var _Ctx = new CommandContext
        {
            Sender = NetworkClient.localPlayer,
            Console = this
        };

        Processor.Execute(_Input, _Ctx);
    }

    public void Log(string _Msg, Color _Color)
    {
        PreviewText.text += $"<color=#{ColorUtility.ToHtmlStringRGB(_Color)}>{_Msg}</color>\n";
        StartCoroutine(ScrollToBottomCoroutine());
    }

    public void LogSuccess(string _Msg) => Log(_Msg, Color.green);
    public void LogError(string _Msg) => Log(_Msg, Color.red);
    public void LogInfo(string _Msg) => Log(_Msg, Color.blue);

    private IEnumerator ScrollToBottomCoroutine()
    {
        yield return new WaitForEndOfFrame();
        ScrollRect.verticalNormalizedPosition = 0f;
    }
}
