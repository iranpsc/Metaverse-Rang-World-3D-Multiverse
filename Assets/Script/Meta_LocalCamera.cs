using UnityEngine;
using Mirror;

public class Meta_LocalCamera : NetworkBehaviour
{
    public override void OnStartAuthority()
    {
        if (!isLocalPlayer) this.gameObject.SetActive(false);
    }
}