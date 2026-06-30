using System;
using UnityEngine;

[Serializable]
public class MetaverseSpawnPayload
{
    public int netId;
    public string prefabId;
    public int ownerConnectionId;
    public bool serverOwned;
    public bool localPlayer;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
}
