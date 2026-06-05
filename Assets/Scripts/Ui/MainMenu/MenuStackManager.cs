using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System;
namespace Project.UI.MainMenu
{
    public class MenuStackManager : MonoBehaviour
    {
        // ✅ Singleton برای دسترسی سراسری
        private static MenuStackManager _instance;
        public static MenuStackManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<MenuStackManager>();
                    if (_instance == null)
                        Debug.LogError("[MenuStackManager] Instance not found in scene!");
                }
                return _instance;
            }
        }

        [Header("UI Buttons")]
        [SerializeField] private Button btnSettings;
        [SerializeField] private Button btnBackAll;

        [Header("Main Menu Objects")]
        [SerializeField] private GameObject mainMenuRoot;

        private readonly Stack<MenuState> _menuStack = new();
        private MenuState _currentState;

        private void Awake()
        {
            // جلوگیری از چند نمونه‌ای شدن
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void Start()
        {
            if (btnBackAll != null) btnBackAll.onClick.AddListener(GoBack);
            if (btnSettings != null) btnSettings.onClick.AddListener(OpenSettings);

            // حالت اولیه
            if (mainMenuRoot != null)
            {
                var mainMenu = new MenuState("MainMenu", mainMenuRoot, hideSettings: false, showBack: false);
                PushMenu(mainMenu);
            }
        }

        // ✅ متد اصلی (توسط Instance صدا زده می‌شود)
        public void PushMenu(MenuState newState)
        {
            if (newState == null || newState.Panel == null)
            {
                Debug.LogError("[MenuStack] Invalid MenuState or Panel is null!");
                return;
            }

            _currentState?.OnExit?.Invoke();
            if (_currentState?.Panel != null)
                _currentState.Panel.SetActive(false);

            if (_currentState != null)
                _menuStack.Push(_currentState);

            _currentState = newState;
            _currentState.Panel.SetActive(true);
            _currentState.OnEnter?.Invoke();

            UpdateUIButtons();
        }

        // ✅ متد کمکی استاتیک (برای فراخوانی راحت بدون نیاز به Instance)
        public static void Push(string name, GameObject panel, bool hideSettings = true, bool showBack = true, Action onEnter = null, Action onExit = null)
        {
            if (Instance != null)
            {
                Instance.PushMenu(new MenuState(name, panel, hideSettings, showBack, onEnter, onExit));
            }
        }

        public void GoBack()
        {
            if (_menuStack.Count == 0) return;

            _currentState?.OnExit?.Invoke();
            _currentState.Panel.SetActive(false);

            _currentState = _menuStack.Pop();
            _currentState.Panel.SetActive(true);
            _currentState.OnEnter?.Invoke();

            UpdateUIButtons();
        }

        private void UpdateUIButtons()
        {
            if (btnSettings != null)
                btnSettings.gameObject.SetActive(!_currentState.HideSettings);

            if (btnBackAll != null)
                btnBackAll.gameObject.SetActive(_currentState.ShowBackButton);

            if (mainMenuRoot != null)
                mainMenuRoot.SetActive(_currentState.MenuName == "MainMenu");
        }

        private void OpenSettings() => Debug.Log("Opening Settings...");
    }
}