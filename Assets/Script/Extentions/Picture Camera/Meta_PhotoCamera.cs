using Mirror;
using System.Collections;
using System.IO;
using TMPro;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
        [SerializeField] private CinemachineCamera VirtualCamera;
        [SerializeField] private Slider ZoomSlider;
        [SerializeField] private Volume CameraVolume;

        [Header("UI")]
        [SerializeField] private TMP_Text PhotoPathText;

        [Header("Settings")]
        [SerializeField] private int CapturePadding = 100;
        [SerializeField] private float MinZoom = 30f;
        [SerializeField] private float MaxZoom = 70f;
        [SerializeField] private float ZoomSpeed = 50f;

        private float DefaultFOV;

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

        private ColorAdjustments ColorAdjustments;

        private void OnEnable() => TakePicture?.action.Enable();
        private void OnDisable() => TakePicture?.action.Disable();

        private void Start()
        {
            // پیدا کردن Local Player
            var localPlayer = NetworkClient.localPlayer;
            if (localPlayer == null)
            {
                StartCoroutine(WaitForLocalPlayer());
            }
            else
            {
                InitializeCamera(localPlayer);
            }

            ScreenCapture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        }

        private IEnumerator WaitForLocalPlayer()
        {
            while (NetworkClient.localPlayer == null)
                yield return null;

            InitializeCamera(NetworkClient.localPlayer);
        }

        private void InitializeCamera(NetworkIdentity localPlayer)
        {
            VirtualCamera = localPlayer.GetComponentInChildren<CinemachineCamera>();
            if (VirtualCamera == null)
                Debug.LogError("No Virtual Camera found in Local Player!");
            else
                DefaultFOV = VirtualCamera.Lens.FieldOfView;

            if (ZoomSlider != null)
            {
                ZoomSlider.minValue = 0f;
                ZoomSlider.maxValue = 1f;
                ZoomSlider.value = Mathf.InverseLerp(MaxZoom, MinZoom, DefaultFOV);
                ZoomSlider.onValueChanged.AddListener(SetZoomFromSlider);
            }

            if (CameraVolume != null)
            {
                if (!CameraVolume.profile.TryGet(out ColorAdjustments))
                    Debug.LogWarning("ColorAdjustments not found in Camera Volume!");
            }
        }

        private void Update()
        {
            if (!NetworkClient.localPlayer) return;

            if (PhotoMod && TakePicture.action.triggered)
            {
                if (!ViewingPhoto)
                    TakePhoto = StartCoroutine(CapturePhoto());
                else
                    RemovePhoto();
            }
        }

        public void SetZoomFromSlider(float _Value)
        {
            if (VirtualCamera != null)
                VirtualCamera.Lens.FieldOfView =
                    Mathf.Lerp(MaxZoom, MinZoom, _Value);
        }

        public void ActivatePhotoMod()
        {
            PhotoMod = true;

            if (ZoomSlider != null)
            {
                ZoomSlider.gameObject.SetActive(true);
                ZoomSlider.value = Mathf.InverseLerp(MaxZoom, MinZoom, DefaultFOV);
            }

            ApplyCameraEffect(CurrentEffect);
        }

        public void DeactivatePhotoMod()
        {
            PhotoMod = false;

            if (ZoomSlider != null)
                ZoomSlider.gameObject.SetActive(false);

            if (VirtualCamera != null)
                VirtualCamera.Lens.FieldOfView = DefaultFOV;

            ResetCameraEffects();
        }

        #region Capture
        IEnumerator CapturePhoto()
        {
            ViewingPhoto = true;
            MainUI.SetActive(false);

            yield return new WaitForEndOfFrame();

            int _ScreenWidth = Screen.width;
            int _ScreenHeight = Screen.height;
            int _SquarSize = Mathf.Min(_ScreenWidth, _ScreenHeight) - (CapturePadding * 2);

            if (_SquarSize <= 0) yield break;

            int _StartX = (_ScreenWidth - _SquarSize) / 2;
            int _StartY = (_ScreenHeight - _SquarSize) / 2;

            Rect _RegionToRead = new Rect(_StartX, _StartY, _SquarSize, _SquarSize);

            ScreenCapture = new Texture2D(_SquarSize, _SquarSize, TextureFormat.RGB24, false);
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

            ResetCameraEffects();
        }
        #endregion

        #region Effects
        private void ApplyEffect(PhotoEffectType _Effect)
        {
            // افکت روی عکس
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

            // افکت روی دوربین پلیر (Preview)
            ApplyCameraEffect(_Effect);
        }

        private void ApplyCameraEffect(PhotoEffectType _Effect)
        {
            if (ColorAdjustments == null) return;

            ResetCameraEffects();

            switch (_Effect)
            {
                case PhotoEffectType.BlackAndWhite:
                    ColorAdjustments.saturation.value = -100f;
                    break;
                case PhotoEffectType.HighContrast:
                    ColorAdjustments.contrast.value = 50f;
                    break;
                case PhotoEffectType.ColorBlind:
                    ColorAdjustments.colorFilter.value = new Color(0.8f, 0.7f, 0.6f);
                    break;
                case PhotoEffectType.None:
                default:
                    break;
            }
        }

        private void ResetCameraEffects()
        {
            if (ColorAdjustments == null) return;

            ColorAdjustments.saturation.value = 0f;
            ColorAdjustments.contrast.value = 0f;
            ColorAdjustments.colorFilter.value = Color.white;
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
        public void SetEffect_None()
        {
            CurrentEffect = PhotoEffectType.None;
            ApplyCameraEffect(CurrentEffect);
        }

        public void SetEffect_BlackAndWhite()
        {
            CurrentEffect = PhotoEffectType.BlackAndWhite;
            ApplyCameraEffect(CurrentEffect);
        }

        public void SetEffect_HighContrast()
        {
            CurrentEffect = PhotoEffectType.HighContrast;
            ApplyCameraEffect(CurrentEffect);
        }

        public void SetEffect_ColorBlind()
        {
            CurrentEffect = PhotoEffectType.ColorBlind;
            ApplyCameraEffect(CurrentEffect);
        }
        #endregion
    }
}
