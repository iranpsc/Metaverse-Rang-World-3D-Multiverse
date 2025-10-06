using System;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta GroundCheck")]
    [HelpURL("https://google.com")]
    public class Meta_GroundCheck : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform GroundCheckPoint;
        [SerializeField] private LayerMask GroundMask;
        [SerializeField] private Collider PlayerCollider;

        [Header("Settings")]
        [SerializeField] private float GroundCheckRadius = 0.25f;
        [SerializeField] private float MaxSlopeAngle = 60f;

        [Header("State")]
        public bool IsGrounded;
        [SerializeField] private float GroundAngle;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        private void Start()
        {
            if (GroundCheckPoint == null) GroundCheckPoint = GetComponent<Transform>();
            if (PlayerCollider == null) PlayerCollider = transform.root.GetComponent<Collider>();
        }
        void Update()
        {
            CheckGround();
        }

        private void CheckGround()
        {
            IsGrounded = false;
            GroundAngle = 0f;

            if (GroundCheckPoint == null)
            {
                if (EnableLog) Debug.LogWarning("[Meta] GroundCheckPoint Not Assigned!");
                return;
            }

            Collider[] _Hits = Physics.OverlapSphere(GroundCheckPoint.position, GroundCheckRadius, GroundMask);

            foreach (var _Hit in _Hits)
            {
                if (_Hit == PlayerCollider)
                    continue;

                IsGrounded = true;

                if (Physics.Raycast(GroundCheckPoint.position + Vector3.up * 0.1f, Vector3.down, out RaycastHit _RayHit, 1f, GroundMask))
                {
                    GroundAngle = Vector3.Angle(_RayHit.normal, Vector3.up);
                }
                break;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (GroundCheckPoint != null)
            {
                Gizmos.color = IsGrounded ? Color.green : Color.red;
                Gizmos.DrawWireSphere(GroundCheckPoint.position, GroundCheckRadius);
            }
        }
    }
}