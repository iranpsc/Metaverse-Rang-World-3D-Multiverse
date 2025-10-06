using Mirror;
using UnityEngine;

public class Meta_LocalPlayer : MonoBehaviour
{
    private void Start()
    {
        // Find the NetworkIdentity in the parent hierarchy
        NetworkIdentity _Identity = GetComponentInParent<NetworkIdentity>();

        if (_Identity == null || !_Identity.isLocalPlayer)
        {
            // Not the local player → disable this object
            gameObject.SetActive(false);
        }
    }
}
