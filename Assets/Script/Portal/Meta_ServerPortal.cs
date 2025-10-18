using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

namespace Meta
{
    [DisallowMultipleComponent]
    public class Meta_ServerPortal : NetworkBehaviour
    {
        [Header("Portal Settings")]
        [Tooltip("Where the player will be teleported to.")]
        public Transform Destination;
        [Tooltip("Delay before teleport (seconds).")]
        public float TeleportDelay = 2f;

        [Header("Optional Visuals")]
        [Tooltip("Particle effect played during teleport delay.")]
        public GameObject TeleportEffectPrefab;

        private void OnTriggerEnter(Collider other)
        {
        if (!other.CompareTag("Player"))
            return;

        // Only server decides teleportation
            NetworkIdentity _identity = other.GetComponent<NetworkIdentity>();
            if (_identity != null)
            {
                StartCoroutine(ServerTeleportPlayer(_identity));
            }
        }

        private IEnumerator ServerTeleportPlayer(NetworkIdentity _identity)
        {
            GameObject _player = _identity.gameObject;

            // Tell all clients to play effect
            RpcPlayTeleportEffect(_player);

            yield return new WaitForSeconds(TeleportDelay);

            // Move player on server, automatically syncs with all clients
            if (Destination != null)
            {
                CharacterController _controller = _player.GetComponent<CharacterController>();
                if (_controller != null)
                {
                    _controller.enabled = false;
                    _player.transform.position = Destination.position;
                    _controller.enabled = true;
                }
                else
                {
                    _player.transform.position = Destination.position;
                }
            }

            RpcOnTeleported(_player);
        }

        [ClientRpc]
        private void RpcPlayTeleportEffect(GameObject _player)
        {
            if (TeleportEffectPrefab == null)
                return;

            GameObject _effect = Instantiate(TeleportEffectPrefab, _player.transform);
            _effect.transform.localPosition = Vector3.zero;
            Destroy(_effect, TeleportDelay + 1f);
        }

        [ClientRpc]
        private void RpcOnTeleported(GameObject _player)
        {
            // Optional: play post-teleport effect, sound, etc.
        }
    }
}
