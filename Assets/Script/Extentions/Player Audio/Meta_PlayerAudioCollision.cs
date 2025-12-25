using Meta.PlayerAudio;
using Mirror;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerAudioCollision")]
    public class Meta_PlayerAudioCollision : NetworkBehaviour
    {
        [Header("References")]
        private Meta_PlayerAudioController AudioController;
        public CharacterController Controller;

        // این متغیر روی سرور تغییر میکند و خودکار به همه کلاینت‌ها اطلاع میدهد
        [SyncVar(hook = nameof(OnStateChanged))]
        public string movementState = "";

        void Start()
        {
            if (Controller == null) Controller = GetComponentInParent<CharacterController>();
            AudioController = GetComponent<Meta_PlayerAudioController>();
        }

        void Update()
        {
            // فقط پلیر صاحب این آبجکت باید محاسبات فیزیکی را به سرور بفرستد
            if (!isLocalPlayer) return;

            string newState = "";

            // شرط برای قطع صدا هنگام سوار شدن به ماشین (وقتی کنترلر غیرفعال میشود)
            if (Controller != null && Controller.enabled)
            {
                float speed = Controller.velocity.magnitude;

                if (speed > 0.1f)
                {
                    if (!Controller.isGrounded) newState = "Jumping";
                    else if (speed > 3f) newState = "Running";
                    else newState = "Walking";
                }
            }

            // فقط اگر وضعیت تغییر کرد، به سرور پیام بفرست (برای جلوگیری از ترافیک شبکه)
            if (newState != movementState)
            {
                CmdUpdateState(newState);
            }
        }

        [Command]
        void CmdUpdateState(string state)
        {
            movementState = state;
        }

        // این متد روی سیستم همه پلیرها (بقیه کلاینت‌ها) اجرا میشود
        void OnStateChanged(string oldState, string newState)
        {
            if (AudioController != null)
            {
                AudioController.UpdateMovementState(newState);
            }
        }
    }
}