using UnityEngine;
using Mirror;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerAnimationSystem")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerAnimationSystem : NetworkBehaviour
{
        [Header("References")]
        [SerializeField] private Animator Anim;
        [SerializeField] private float Acceleration = 0.1f;

        private int HashPosX;
        private int HashPosZ;
        private int HashGrounded;
        private int HashWalkJump;
        private int HashRunJump;

        private void Awake()
        {
            if (Anim == null)
                Anim = GetComponentInChildren<Animator>();

            HashPosX = Animator.StringToHash("PosX");
            HashPosZ = Animator.StringToHash("PosZ");
            HashGrounded = Animator.StringToHash("IsGrounded");
            HashWalkJump = Animator.StringToHash("WalkJump");
            HashRunJump = Animator.StringToHash("RunJump");
        }

        [Command]
        public void CmdSyncAnim(Vector2 move, bool grounded, bool walkJump, bool runJump)
        {
            RpcSyncAnim(move, grounded, walkJump, runJump);
        }

        [ClientRpc]
        private void RpcSyncAnim(Vector2 move, bool grounded, bool walkJump, bool runJump)
        {
            if (Anim == null) return;
            Anim.SetFloat(HashPosX, move.x, Acceleration, Time.deltaTime);
            Anim.SetFloat(HashPosZ, move.y, Acceleration, Time.deltaTime);
            Anim.SetBool(HashGrounded, grounded);
            Anim.SetBool(HashWalkJump, walkJump);
            Anim.SetBool(HashRunJump, runJump);
        }
    }
}