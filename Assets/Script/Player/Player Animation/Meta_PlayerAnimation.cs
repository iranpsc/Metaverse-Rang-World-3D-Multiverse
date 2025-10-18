using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerAnimation")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerAnimation : MonoBehaviour
    {

        [Header("References")]

        [Header("Animations")]
        [SerializeField] private Animator Anim;
        [SerializeField] private float CurrentPosZ;
        [SerializeField] private float CurrentPosX;
        [SerializeField] private float Acceleration = 0.1f;

        [Header("Settings")]
        [SerializeField] private float CurrentSpeed;

        [Header("Inputs")]
        [SerializeField] private InputActionReference MoveAction;

        // Temporary
        private Vector2 MoveInput;

        private bool IsMoving;

        // Caching
        int HashPosX;
        int HashPosZ;
        int HashWalkJump;
        int HashRunJump;
        int HashGrounded;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            HashPosX = Animator.StringToHash("PosX");
            HashPosZ = Animator.StringToHash("PosZ");
            HashWalkJump = Animator.StringToHash("WalkJump");
            HashRunJump = Animator.StringToHash("RunJump");
            HashGrounded = Animator.StringToHash("IsGrounded");
        }

        void Update()
        {
            InputInitialize();
            AnimationHandler();
        }
        private void InputInitialize()
        {
            MoveInput = MoveAction.action.ReadValue<Vector2>();
            IsMoving = MoveInput.sqrMagnitude > 0.01f;
        }
        private void AnimationHandler()
        {
            CurrentPosX = MoveInput.x * CurrentSpeed;
            CurrentPosZ = MoveInput.y * CurrentSpeed;

            Vector2 NetAnim = new Vector2(CurrentPosX, CurrentPosZ);

            Anim.SetFloat(HashPosX, CurrentPosX, Acceleration, Time.deltaTime);
            Anim.SetFloat(HashPosZ, CurrentPosZ, Acceleration, Time.deltaTime);

            if (!IsMoving)
            {
                if (Mathf.Abs(Anim.GetFloat(HashPosX)) < 0.01f)
                    Anim.SetFloat(HashPosX, 0f);
                if (Mathf.Abs(Anim.GetFloat(HashPosZ)) < 0.01f)
                    Anim.SetFloat(HashPosZ, 0f);
            }
        }
    }
}