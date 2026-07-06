using Mirror;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_SceneFlowManager")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_SceneFlowManager : MonoBehaviour
    {
        public static Meta_SceneFlowManager Instance;

        [Header("UI")]
        public Canvas DownloadCanvas;
        public TMP_Text StatusText;
        public Slider ProgressBar;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            HideUI();
        }

        /* ====================
         * PUBLIC API
         * ==================== */

        public void LoadScene(string _SceneAddress)
        {
            StartCoroutine(LoadSceneRoutine(_SceneAddress));
        }

        public void AutoLoadNext(string _TargetScene)
        {
            StartCoroutine(LoadSceneRoutine(_TargetScene));
        }

        /* ====================
         * CORE
         * ==================== */

        private IEnumerator LoadSceneRoutine(string _SceneAddress)
        {
            ShowUI($"درحال بررسی {_SceneAddress}");

            var _SizeHandle = Addressables.GetDownloadSizeAsync(_SceneAddress);
            yield return _SizeHandle;

            if (_SizeHandle.Result > 0)
            {
                ShowUI($"درحال دانلود {_SceneAddress}");

                var _DownloadHandle = Addressables.DownloadDependenciesAsync(_SceneAddress);
                while (!_DownloadHandle.IsDone)
                {
                    ProgressBar.value = _DownloadHandle.PercentComplete;
                    yield return null;
                }

                if (_DownloadHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    ShowUI("خطا در دانلود");
                    yield break;
                }
            }

            ShowUI($"درحال بارگذاری {_SceneAddress}");

            var _LoadHandle = Addressables.LoadSceneAsync(_SceneAddress, LoadSceneMode.Single);
            yield return _LoadHandle;

            HideUI();
        }
        /* ====================
         * UI
         * ==================== */

        private void ShowUI(string _Message)
        {
            DownloadCanvas.enabled = true;
            StatusText.text = _Message;
            ProgressBar.value = 0f;
        }

        private void HideUI()
        {
            if (DownloadCanvas != null)
            {
                DownloadCanvas.enabled = false;
            }
        }
    }
}