using Mirror;
using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Meta
{
    /// <summary>
    /// this script made for android touch input
    /// shuld be off in other build.
    /// its Android web only script
    /// </summary>
    [AddComponentMenu("Meta/Meta_CinemachineInput")]
    [HelpURL("https://google.com")]
    public class Meta_CinemachineInput : MonoBehaviour
    {
        [Tooltip("Reference your Cinemachine Input Axis Controller component here")]
        public CinemachineInputAxisController InputProvider;
        private PlatformType PlatformType;
        private bool _wasOverUI;

        private void Awake()
        {
            PlatformType = Meta_PlatformDetector.GetPlatformType();
            if (PlatformType != PlatformType.Android)
            {
                this.enabled = false;
            }
        }
        private void Update()
        {
            if (InputProvider == null || EventSystem.current == null)
                return;

            bool _isOverUI = IsPointerOverUI();

            InputProvider.enabled = !_isOverUI;

            _wasOverUI = _isOverUI;
        }

        private bool IsPointerOverUI()
        {
            if (EventSystem.current == null)
                return false;

            Vector2 _pos;
            bool _pressed = false;

            // Use Pointer.current (handles both mouse & touch in new Input System)
            if (Pointer.current != null)
            {
                _pos = Pointer.current.position.ReadValue();
                _pressed = Pointer.current.press.isPressed;
            }
            else
            {
                // Fallback for mouse-only environments
                if (Mouse.current != null)
                {
                    _pos = Mouse.current.position.ReadValue();
                    _pressed = Mouse.current.leftButton.isPressed;
                }
                else return false;
            }

            if (!_pressed)
                return false;

            PointerEventData _eventData = new(EventSystem.current)
            {
                position = _pos
            };

            List<RaycastResult> _results = new();
            EventSystem.current.RaycastAll(_eventData, _results);

            if (_results.Count > 0)
            {
                return true;
            }

            return false;
        }
    }
}
