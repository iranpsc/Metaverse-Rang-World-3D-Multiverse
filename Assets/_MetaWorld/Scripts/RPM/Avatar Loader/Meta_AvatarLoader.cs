using ReadyPlayerMe.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Meta
{
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta AvatarLoader")]
    public class Meta_AvatarLoader : MonoBehaviour
    {
        [SerializeField] private string AvatarUrl = "https://models.readyplayer.me/638df693d72bffc6fa17943c.glb";
        private GameObject Avatar;
        [SerializeField] private LayerMask Mask;
        [SerializeField] private AvatarRenderSettings RenderSetting;
        [SerializeField] private Image AvatarImage;

        [Header("Debugger")]
        public bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_AvatarLoader] Avatar Loader Activated");
            GetAvatarImage();
        }

        public void DeployAvatar()
        {
            var _AvatarLoader = new AvatarObjectLoader();

            _AvatarLoader.OnCompleted += (_, args) =>
            {
                Avatar = args.Avatar;
                
                AvatarAnimationHelper.SetupAnimator(args.Metadata, args.Avatar);
                Avatar.layer = Mask.value;
            };
            _AvatarLoader.LoadAvatar(AvatarUrl);
        }

        public void GetAvatarImage()
        {
            var _AvatarRenderLoader = new AvatarRenderLoader();
            _AvatarRenderLoader.OnCompleted = SetImage;
            _AvatarRenderLoader.LoadRender(AvatarUrl, RenderSetting);
        }

        void SetImage(Texture2D _Texture)
        {
            var _Sprite = Sprite.Create(_Texture, new Rect(0, 0, _Texture.width, _Texture.height), new Vector2(0.5f, 0.5f));
            AvatarImage.sprite = _Sprite;
            AvatarImage.preserveAspect = true;
        }

        private void OnDestroy()
        {
            if (Avatar != null) Destroy(Avatar);
        }
    }
}