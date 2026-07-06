using Mirror;
using UnityEngine;
using UnityEngine.XR;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VRPlayerMovementSystem")]
    [HelpURL("https://google.com")]
    public class Meta_VRPlayerMovementSystem : NetworkBehaviour
{
        [Header("References")]
        [SerializeField] private Transform XRRoot;
        [SerializeField] private Transform XRHead;
        [SerializeField] private Transform LeftHand;
        [SerializeField] private Transform RightHand;
        [SerializeField] private Meta_PlayerAnimationSystem AnimSync;

        private Vector3 _LastHeadPos;
        private float _Speed;

        private void Start()
        {
            _LastHeadPos = XRHead.localPosition;
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            HandleMovementAnim();
            SyncBodyParts();
        }

        private void HandleMovementAnim()
        {
            Vector3 _HeadDelta = XRHead.localPosition - _LastHeadPos;
            _LastHeadPos = XRHead.localPosition;

            _HeadDelta.y = 0;
            _Speed = _HeadDelta.magnitude / Time.deltaTime;
            _Speed = Mathf.Clamp(_Speed * 4f, 0, 4f); // match blend tree 0–4 range

            Vector2 _Anim = new Vector2(0, _Speed);
            AnimSync.CmdSyncAnim(_Anim, true, false, false);
        }

        private void SyncBodyParts()
        {
            CmdSyncTransform(XRHead.localPosition, XRHead.localRotation,
                             LeftHand.localPosition, LeftHand.localRotation,
                             RightHand.localPosition, RightHand.localRotation);
        }

        [Command]
        private void CmdSyncTransform(Vector3 headPos, Quaternion headRot,
                                      Vector3 leftPos, Quaternion leftRot,
                                      Vector3 rightPos, Quaternion rightRot)
        {
            RpcSyncTransform(headPos, headRot, leftPos, leftRot, rightPos, rightRot);
        }

        [ClientRpc]
        private void RpcSyncTransform(Vector3 headPos, Quaternion headRot,
                                      Vector3 leftPos, Quaternion leftRot,
                                      Vector3 rightPos, Quaternion rightRot)
        {
            if (isLocalPlayer) return;

            XRHead.localPosition = headPos;
            XRHead.localRotation = headRot;
            LeftHand.localPosition = leftPos;
            LeftHand.localRotation = leftRot;
            RightHand.localPosition = rightPos;
            RightHand.localRotation = rightRot;
        }
    }
}