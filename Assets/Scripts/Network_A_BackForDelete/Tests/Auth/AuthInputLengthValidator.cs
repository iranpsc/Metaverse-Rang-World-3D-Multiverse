using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AuthInputLengthValidator : MonoBehaviour
{
    [Header("Inputs")]
    [SerializeField] private TMP_InputField usernameInput;
    [SerializeField] private TMP_InputField passwordInput;

    [Header("Buttons")]
    [SerializeField] private Button loginButton;
    [SerializeField] private Button registerButton;

    [Header("Warning UI")]
    [SerializeField] private TextMeshProUGUI warningText;

    [Header("Rules")]
    [SerializeField] private int minUsernameLength = 7;
    [SerializeField] private int minPasswordLength = 7;
    [SerializeField] private bool showWarningWhileTyping = true;

    private const string EmptyWarning = "";
    private const string UsernameWarning = "نام کاربری باید حداقل ۷ کاراکتر باشد";
    private const string PasswordWarning = "رمز عبور باید حداقل ۷ کاراکتر باشد";
    private const string BothWarning = "نام کاربری و رمز عبور باید حداقل ۷ کاراکتر باشند";

    private void Awake()
    {
        BindInputEvents();
        RefreshValidationState();
    }

    private void OnEnable()
    {
        RefreshValidationState();
    }

    private void OnDestroy()
    {
        UnbindInputEvents();
    }

    private void BindInputEvents()
    {
        if (usernameInput != null) usernameInput.onValueChanged.AddListener(HandleInputChanged);
        if (passwordInput != null) passwordInput.onValueChanged.AddListener(HandleInputChanged);
    }

    private void UnbindInputEvents()
    {
        if (usernameInput != null) usernameInput.onValueChanged.RemoveListener(HandleInputChanged);
        if (passwordInput != null) passwordInput.onValueChanged.RemoveListener(HandleInputChanged);
    }

    private void HandleInputChanged(string value)
    {
        RefreshValidationState();
    }

    private void RefreshValidationState()
    {
        bool isValid = IsUsernameValid() && IsPasswordValid();

        SetButtonInteractable(loginButton, isValid);
        SetButtonInteractable(registerButton, isValid);

        if (showWarningWhileTyping) SetWarningText(isValid ? EmptyWarning : BuildWarningMessage());
        else SetWarningText(EmptyWarning);
    }

    public bool CanSubmit()
    {
        bool isValid = IsUsernameValid() && IsPasswordValid();

        SetButtonInteractable(loginButton, isValid);
        SetButtonInteractable(registerButton, isValid);
        SetWarningText(isValid ? EmptyWarning : BuildWarningMessage());

        return isValid;
    }

    private bool IsUsernameValid()
    {
        return GetInputLength(usernameInput) >= minUsernameLength;
    }

    private bool IsPasswordValid()
    {
        return GetInputLength(passwordInput) >= minPasswordLength;
    }

    private int GetInputLength(TMP_InputField input)
    {
        return string.IsNullOrEmpty(input?.text) ? 0 : input.text.Trim().Length;
    }

    private string BuildWarningMessage()
    {
        bool usernameValid = IsUsernameValid();
        bool passwordValid = IsPasswordValid();

        if (!usernameValid && !passwordValid) return BothWarning;
        if (!usernameValid) return UsernameWarning;
        if (!passwordValid) return PasswordWarning;

        return EmptyWarning;
    }

    private void SetButtonInteractable(Button button, bool interactable)
    {
        if (button != null) button.interactable = interactable;
    }

    private void SetWarningText(string message)
    {
        if (warningText == null) return;

        warningText.text = message;
        warningText.gameObject.SetActive(!string.IsNullOrWhiteSpace(message));
    }
}