using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Meta
{
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta LoginSystem")]
    public class Meta_LoginSystem : MonoBehaviour
    {
        [SerializeField] private TMP_InputField Username;
        [SerializeField] private TMP_InputField Password;
        [SerializeField] private Button Enter;
        [SerializeField] private Toggle RememberMe;

        private const string USERNAME = "Username";
        private const string PASSWORD = "Password";
        private const string REMEMBERME = "RememberMe";

        [Header("Debugger")]
        public bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_LoginSystem] Login System Initialized.");

            Enter.interactable = PlayerPrefs.HasKey(USERNAME) ? true : false;

            Username.text = PlayerPrefs.GetString(USERNAME);
            Password.text = PlayerPrefs.GetString(PASSWORD);
            RememberMe.isOn = PlayerPrefs.HasKey(REMEMBERME) ? true : false;
        }

        public void UserValidation()
        {
            bool _HasUsername = !string.IsNullOrEmpty(Username.text);
            bool _HasPassword = !string.IsNullOrEmpty(Password.text);

            if (_HasUsername)
            {
                Meta_UserGlobalData.Instance.Username = Username.text;
                Enter.interactable = true;
            }
            else
            {
                Enter.interactable = false;
            }
        }

        public void SaveData()
        {
            if (RememberMe.isOn)
            {
                PlayerPrefs.SetString(USERNAME, Username.text);
                PlayerPrefs.SetString(PASSWORD, Password.text);
                PlayerPrefs.SetInt(REMEMBERME, 1);
            }
            if (!RememberMe.isOn)
            {
                PlayerPrefs.DeleteKey("Username");
                PlayerPrefs.DeleteKey("Password");
                PlayerPrefs.DeleteKey("RememberMe");
            }
        }
    }
}
