using UnityEngine;

namespace Meta.Config
{
    [CreateAssetMenu(fileName = "Meta_PlayerConfig", menuName = "Meta/PlayerConfig")]
    public class Meta_PlayerConfig : ScriptableObject
    {
        [Range(0,10)]
        public float WalkSpeed;
        [Range(0, 10)]
        public float RunSpeed;
        [Range(0, 10)]
        public float CrouchSpeed;

        public float CurrentSpeed;

        [Range(0, 10)]
        public float JumpForce;

        public float Gravity;

        public GroundCheck GroundDiagnosis;

        [Space(10)]
        public bool LogMessage;
    }


    public class Player
    {
        public CharacterController Controller;
    }

    [System.Serializable]
    public class GroundCheck
    {
        [Header("<color=green>References</color>")]
        public LayerMask GroundMask;
        public Transform GroundCheckPoint;

        [Header("<color=green>Values</color>")]
        public float CheckRadius;
        public float MaxSlopeAngle;

        [Header("<color=red>Results</color>")]
        public bool IsGrounded;
        public float GroundAngle;

    }
}