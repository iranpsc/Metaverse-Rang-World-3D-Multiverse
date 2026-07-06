using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta GroundCheck")]
    public class Meta_GroundCheck : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform GroundCheckPoint;
        [SerializeField] private LayerMask GroundMask;
        [SerializeField] private CharacterController PlayerCollider;

        [Header("Settings")]
        [SerializeField] private float GroundCheckRadius = 0.25f;
        [SerializeField] private float MaxSlopeAngle = 60f;

        [Header("State")]
        [SerializeField] private bool IsGrounded;
        [SerializeField] private float GroundAngle;

        public bool Grounded => IsGrounded;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (GroundCheckPoint == null) GroundCheckPoint = transform;
            if (PlayerCollider == null) PlayerCollider = transform.root.GetComponent<CharacterController>();
        }

        void Update()
        {
            GroundCheck();
        }

        private void GroundCheck()
        {
            IsGrounded = false;
            GroundAngle = 0f;

            if (PlayerCollider != null)
            {
                // Prefer CharacterController’s built-in check
                IsGrounded = PlayerCollider.isGrounded;
                if (IsGrounded) return;
            }

            Vector3 _CheckPos = GroundCheckPoint.position + Vector3.down * 0.05f;
            Collider[] _Hits = Physics.OverlapSphere(_CheckPos, GroundCheckRadius, GroundMask, QueryTriggerInteraction.Ignore);

            foreach (var _Hit in _Hits)
            {
                if (_Hit.transform.root == transform.root)
                    continue;

                if (Physics.Raycast(_CheckPos, Vector3.down, out RaycastHit _RayHit, 1.5f, GroundMask, QueryTriggerInteraction.Ignore))
                {
                    if (_RayHit.transform.root == transform.root)
                        continue;

                    GroundAngle = Vector3.Angle(_RayHit.normal, Vector3.up);
                    if (GroundAngle <= MaxSlopeAngle)
                    {
                        IsGrounded = true;
                        break;
                    }
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (GroundCheckPoint != null)
            {
                Gizmos.color = IsGrounded ? Color.green : Color.red;
                Gizmos.DrawWireSphere(GroundCheckPoint.position + Vector3.down * 0.05f, GroundCheckRadius);
            }
        }

        public bool CheckForGround() => IsGrounded;
    }
}
