using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PhotoCamera")]
    [HelpURL("https://google.com")]
    public class Meta_PhotoCamera : MonoBehaviour
    {
        [Header("References")]
        private Texture2D ScreenCapture;
        [SerializeField] private Image PhotoDisplayArea;
        [SerializeField] private GameObject PhotoFrame;
        [SerializeField] private Animator FadeAnimation;
        [SerializeField] private GameObject MainUI;

        [Header("UI")]
        [SerializeField] private TMP_Text PhotoPathText;

        [Header("Settings")]
        private bool PhotoMod;
        private bool ViewingPhoto;
        private Coroutine TakePhoto;

        [Header("Inputs")]
        public InputActionReference TakePicture;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        public enum PhotoEffectType
        {
            None,
            BlackAndWhite,
            HighContrast,
            ColorBlind
        }

        [Header("Effects")]
        public PhotoEffectType CurrentEffect = PhotoEffectType.None;

        private void OnEnable() => TakePicture?.action.Enable();
        private void OnDisable() => TakePicture?.action.Disable();

        private void Start()
        {
            ScreenCapture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        }

        private void Update()
        {
            if (PhotoMod && TakePicture.action.triggered)
            {
                if (!ViewingPhoto)
                    TakePhoto = StartCoroutine(CapturePhoto());
                else
                    RemovePhoto();
            }
        }

        public void ActivatePhotoMod() => PhotoMod = true;
        public void DeactivatePhotoMod() => PhotoMod = false;

        IEnumerator CapturePhoto()
        {
            ViewingPhoto = true;
            MainUI.SetActive(false);

            yield return new WaitForEndOfFrame();

            Rect _RegionToRead = new Rect(0, 0, Screen.width, Screen.height);
            ScreenCapture.ReadPixels(_RegionToRead, 0, 0, false);
            ScreenCapture.Apply();

            ApplyEffect(CurrentEffect);
            string _SavedPath = SavePhoto();
            ShowPhoto(_SavedPath);

            yield return new WaitForSeconds(5);
            RemovePhoto();
        }

        private void ShowPhoto(string _Path)
        {
            Sprite _PhotoSprite = Sprite.Create(ScreenCapture, new Rect(0.0f, 0.0f, ScreenCapture.width, ScreenCapture.height), new Vector2(0.5f, 0.5f), 100.0f);
            PhotoDisplayArea.sprite = _PhotoSprite;

            PhotoFrame.SetActive(true);
            MainUI.SetActive(true);
            FadeAnimation.Play("Photo_Animation");

            if (PhotoPathText != null)
                PhotoPathText.text = $"Saved to: {_Path}";
        }

        private void RemovePhoto()
        {
            ViewingPhoto = false;
            PhotoFrame.SetActive(false);

            if (TakePhoto != null)
            {
                StopCoroutine(TakePhoto);
                TakePhoto = null;
            }
        }

        #region Image Effects
        private void ApplyEffect(PhotoEffectType _Effect)
        {
            Color[] _Pixels = ScreenCapture.GetPixels();

            for (int i = 0; i < _Pixels.Length; i++)
            {
                Color _C = _Pixels[i];
                switch (_Effect)
                {
                    case PhotoEffectType.BlackAndWhite:
                        float _Gray = (_C.r + _C.g + _C.b) / 3f;
                        _C = new Color(_Gray, _Gray, _Gray, 1f);
                        break;

                    case PhotoEffectType.HighContrast:
                        _C.r = Mathf.Clamp01((_C.r - 0.5f) * 2f + 0.5f);
                        _C.g = Mathf.Clamp01((_C.g - 0.5f) * 2f + 0.5f);
                        _C.b = Mathf.Clamp01((_C.b - 0.5f) * 2f + 0.5f);
                        break;

                    case PhotoEffectType.ColorBlind:
                        _C = new Color(
                            _C.r * 0.625f + _C.g * 0.7f,
                            _C.r * 0.7f + _C.g * 0.625f,
                            _C.b
                        );
                        break;

                    case PhotoEffectType.None:
                    default:
                        break;
                }

                _Pixels[i] = _C;
            }

            ScreenCapture.SetPixels(_Pixels);
            ScreenCapture.Apply();
        }
        #endregion

        #region Save Photo
        private string SavePhoto()
        {
            byte[] _Bytes = ScreenCapture.EncodeToPNG();
            string _FileName = $"MetaPhoto_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
            string _Path = "";

#if UNITY_ANDROID && !UNITY_EDITOR
            _Path = Path.Combine(Application.persistentDataPath, _FileName);
#elif UNITY_WEBGL
            _Path = _FileName;
            Application.ExternalEval($"downloadFile('{_FileName}', 'data:image/png;base64,{System.Convert.ToBase64String(_Bytes)}');");
#else
            _Path = Path.Combine(Application.persistentDataPath, _FileName);
#endif

            try
            {
                File.WriteAllBytes(_Path, _Bytes);
                if (EnableLog) Debug.Log($"[Meta_PhotoCamera] Photo saved: {_Path}");
            }
            catch (System.Exception _Ex)
            {
                Debug.LogError($"[Meta_PhotoCamera] Failed to save photo: {_Ex.Message}");
            }

            return _Path;
        }
        #endregion

        #region UI Buttons
        public void SetEffect_None() => CurrentEffect = PhotoEffectType.None;
        public void SetEffect_BlackAndWhite() => CurrentEffect = PhotoEffectType.BlackAndWhite;
        public void SetEffect_HighContrast() => CurrentEffect = PhotoEffectType.HighContrast;
        public void SetEffect_ColorBlind() => CurrentEffect = PhotoEffectType.ColorBlind;
        #endregion
    }
}
