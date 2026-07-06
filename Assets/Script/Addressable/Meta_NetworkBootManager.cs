using Mirror;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_NetworkBootManager")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_NetworkBootManager : MonoBehaviour
    {
        [Header("Scene Addresses")]
        public string LoginScene = "Login";
        public string OfflineScene = "Offline";

        [Header("Optional UI")]
        public TMP_Text StatusText;
        public Slider ProgressBar;

        private void Start()
        {
            if (ProgressBar != null)
            {
                ProgressBar.value = 0f;
            }

            StartCoroutine(BootFlow());
        }

        private IEnumerator BootFlow()
        {
            SetStatus("بررسی برای بروزرسانی");


            var _CheckHandle = Addressables.CheckForCatalogUpdates(false);
            yield return _CheckHandle;

            if (_CheckHandle.Status == AsyncOperationStatus.Succeeded && _CheckHandle.Result != null && _CheckHandle.Result.Count > 0)
            {
                SetStatus("بروزرسانی دیتا...");
                var _UpdateHandler = Addressables.UpdateCatalogs(_CheckHandle.Result, false);
                yield return _UpdateHandler;
            }

            SetStatus("آماده سازی محیط...");
            yield return DownloadScene(LoginScene);
            yield return DownloadScene(OfflineScene);

            bool _Online = IsOnline();

            string _TargetScene = _Online ? LoginScene : OfflineScene;
            SetStatus("بارگذاری " + _TargetScene + "...");

            yield return Addressables.LoadSceneAsync(_TargetScene, LoadSceneMode.Single);
        }

        private IEnumerator DownloadScene(string _SceneAddress)
        {
            var _SizeHandler = Addressables.GetDownloadSizeAsync(_SceneAddress);
            yield return _SizeHandler;

            if (_SizeHandler.Result > 0)
            {
                var _DownloadHandler = Addressables.DownloadDependenciesAsync(_SceneAddress);
                while(!_DownloadHandler.IsDone)
                {
                    if (ProgressBar != null)
                    {
                        ProgressBar.value = _DownloadHandler.PercentComplete;
                    }
                    yield return null;
                }
            }
        }

        private bool IsOnline()
        {
            return Application.internetReachability != NetworkReachability.NotReachable;
        }
        private void SetStatus(string _Status)
        {
            Debug.Log("[Boot] "+ _Status);
            if (StatusText != null)
                StatusText.text = _Status;
        }
    }
}