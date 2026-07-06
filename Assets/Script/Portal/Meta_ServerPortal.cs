using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using Meta.Base;

namespace Meta
{
    [DisallowMultipleComponent]
    public class Meta_ServerPortal : NetworkBehaviour
    {
        public Meta_PortalBase Base;

        public void OnTriggerEnter(Collider _Other)
        {
            if (!_Other.CompareTag("Player")) return;

            NetworkIdentity _Identity = _Other.GetComponent<NetworkIdentity>();

            if (_Identity == null) return;
                
            Base.Thread = StartCoroutine(Base.TeleportPlayer(_Identity.gameObject));
        }

        public void OnTriggerExit(Collider _Other)
        {
            if (!_Other.CompareTag("Player")) return;

            NetworkIdentity _Identity = _Other.GetComponent<NetworkIdentity>();

            if (_Identity == null) return;
            
            StopCoroutine(Base.Thread);
            Base.Thread = null;
            Base.Effect.StopEffect();
        }
    }
}
