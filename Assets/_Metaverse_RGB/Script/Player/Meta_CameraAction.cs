using Mirror;
using UnityEngine;

public class Meta_CameraAction : NetworkBehaviour
{
    public GameObject CameraBrain;

    private void Start()
    {
        if(!isLocalPlayer)
        {
            CameraBrain.SetActive(false);
        }
    }
}
