using System; // ✅ حیاتی برای Action
using UnityEngine;

namespace Project.UI.MainMenu
{
    /// <summary>
    /// ساختار داده‌ای وضعیت منو (کاملاً مستقل)
    /// </summary>
    public class MenuState
    {
        public string MenuName { get; }
        public GameObject Panel { get; }
        public bool HideSettings { get; }
        public bool ShowBackButton { get; }
        public Action OnEnter { get; }
        public Action OnExit { get; }

        public MenuState(string name, GameObject panel, bool hideSettings = false,
            bool showBack = true, Action onEnter = null, Action onExit = null)
        {
            MenuName = name;
            Panel = panel;
            HideSettings = hideSettings;
            ShowBackButton = showBack;
            OnEnter = onEnter;
            OnExit = onExit;
        }
    }
}