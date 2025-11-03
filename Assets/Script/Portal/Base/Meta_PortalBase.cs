using Mirror;
using System.Collections;
using UnityEngine;

namespace Meta.Base
{
    [AddComponentMenu("Meta/Meta_PortalBase")]
    [HelpURL("https://google.com")]
    [System.Serializable]
    [RequireComponent(typeof(Collider))]
    public class Meta_PortalBase
    {
        public Meta_PortalTeleport Teleport = new Meta_PortalTeleport();
        public Meta_PortalEffect Effect = new Meta_PortalEffect();
        public Coroutine Thread;
        public virtual IEnumerator TeleportPlayer(GameObject _Player)
        {
            Effect.PlayEffect();
            yield return new WaitForSeconds(Teleport.TeleportionDelay);
            Effect.StopEffect();

            if (Teleport.TeleportXYZ) Teleport.TeleportToXYZ(_Player);
            if (Teleport.TeleportObject) Teleport.TeleportToObject(_Player);
        }
    }

    [System.Serializable]
    public class Meta_PortalTeleport
    {
        public float TeleportionDelay;
        public Vector3 DestinationXYZ;
        public bool TeleportXYZ;
        public GameObject DestinationObject;
        public bool TeleportObject;
        public virtual void TeleportToXYZ(GameObject _Player) => _Player.transform.position = DestinationXYZ;
        public virtual void TeleportToObject(GameObject _Player) => _Player.transform.position = DestinationObject.transform.position;
    }

    [System.Serializable]
    public class Meta_PortalEffect
    {
        public ParticleSystem Effect;

        public virtual void PlayEffect() => Effect?.Play();
        public virtual void StopEffect() => Effect?.Stop();
    }
}