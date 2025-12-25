using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_LoginManager")]
    [HelpURL("https://google.com")]
    public class Meta_LoginManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private TMP_InputField UsernameInput;
        [SerializeField] private TMP_InputField PasswordInput;

        [SerializeField] private Toggle RememberMeToggle;

        [SerializeField] private Button LoginButton;
        [SerializeField] private Button RegisterButton;

        [SerializeField] private Button ForgetPassword;
        [SerializeField] private Button SupportButton;

        [SerializeField] private TMP_Text ErrorText;

        [Header("API Endpoint")]
        [SerializeField] private string GetRedirectUrl;
        [SerializeField] private string GetAuthenticatedUserDataUrl;

        [Header("Setting")]
        //[SerializeField] private bool UseLocalHostRedirect = true;
        [SerializeField] private string LocalRedirectUrl;
        [SerializeField] private string Scene;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;
        [SerializeField] private string TemporaryToken;
        [SerializeField] private bool IsProcessing;

        private TMP_Text _loginButtonText;

        private void Start()
        {
            ErrorText.text = "";
            LoadSavedLogin();

            // Cache the button label
            _loginButtonText = LoginButton.GetComponentInChildren<TMP_Text>();

            UsernameInput.onValueChanged.AddListener(delegate { CheckInputValidity(); });
            PasswordInput.onValueChanged.AddListener(delegate { CheckInputValidity(); });

            LoginButton.onClick.AddListener(OnLoginPressed);
            RegisterButton.onClick.AddListener(OnRegisterPressed);
            ForgetPassword.onClick.AddListener(OnForgotPasswordPressed);
            SupportButton.onClick.AddListener(OnSupportPressed);

            CheckInputValidity();
        }

        private void CheckInputValidity()
        {
            string _username = UsernameInput.text.Trim();
            string _password = PasswordInput.text.Trim();

            bool _hasUsername = !string.IsNullOrEmpty(_username);
            bool _hasPassword = !string.IsNullOrEmpty(_password);

            // Enable if username exists
            //LoginButton.interactable = _hasUsername && !IsProcessing;

            // Change button text
            if (!_hasUsername && !_hasPassword)
                _loginButtonText.text = "مهمان";
            else if (_hasUsername && _hasPassword)
                _loginButtonText.text = "ورود";
            else
                _loginButtonText.text = "ورود";

            if (EnableLog)
                Debug.Log($"[Meta_LoginManager] Username={_hasUsername}, Password={_hasPassword}, Button={_loginButtonText.text}");
        }

        private void LoadSavedLogin()
        {
            if (PlayerPrefs.HasKey("SavedUsername"))
                UsernameInput.text = PlayerPrefs.GetString("SavedUsername");
            if (PlayerPrefs.HasKey("SavedPassword"))
                PasswordInput.text = PlayerPrefs.GetString("SavedPassword");

            RememberMeToggle.isOn = true;
        }

        private void SaveLogin()
        {
            if (RememberMeToggle.isOn)
            {
                PlayerPrefs.SetString("SavedUsername", UsernameInput.text);
                PlayerPrefs.SetString("SavedPassword", PasswordInput.text);
            }
            else
            {
                PlayerPrefs.DeleteKey("SavedUsername");
                PlayerPrefs.DeleteKey("SavedPassword");
            }
            PlayerPrefs.Save();
        }

        public void OnLoginPressed()
        {
            if (IsProcessing) return;

            string _username = UsernameInput.text.Trim();
            string _password = PasswordInput.text.Trim();

            if (string.IsNullOrEmpty(_username))
            {
                ErrorText.text = "Please enter a username.";
                //return;
            }

            // Guest login (username only)
            if (!string.IsNullOrEmpty(_username) && string.IsNullOrEmpty(_password))
            {
                if (EnableLog) Debug.Log($"[Meta_LoginManager] Logging in as guest ({_username})...");
                StartGuestLogin(_username);
                return;
            }

            // Normal login
            if (EnableLog) Debug.Log($"[Meta_LoginManager] Logging in as normal user ({_username})...");
            SaveLogin();
            SceneManager.LoadScene(Scene);
        }

        private void StartGuestLogin(string username)
        {
            // Here you can store temporary guest data
            PlayerPrefs.SetString("GuestUsername", username);
            PlayerPrefs.Save();

            // You could also assign a random guest token if needed
            TemporaryToken = "GUEST_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            if (EnableLog) Debug.Log($"[Meta_LoginManager] Guest token created: {TemporaryToken}");

            SceneManager.LoadScene(Scene);
        }

        private void OnRegisterPressed()
        {
            Application.OpenURL(GetRedirectUrl);
        }

        private void OnForgotPasswordPressed()
        {
            Application.OpenURL("https://accounts.irpsc.com/password/reset");
        }

        private void OnSupportPressed()
        {
            Application.OpenURL("https://accounts.irpsc.com/password/contactus.html");
        }

        public void OpenKeyboard(TMP_InputField _Input)
        {
            EventSystem.current.SetSelectedGameObject(_Input.gameObject);
            _Input.Select();
            _Input.ActivateInputField();
        }
    }
}
