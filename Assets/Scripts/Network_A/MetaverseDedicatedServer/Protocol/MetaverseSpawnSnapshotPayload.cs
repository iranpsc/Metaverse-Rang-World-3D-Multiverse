using System;

[Serializable]
public class MetaverseSpawnSnapshotPayload
{
    public string type;
    public MetaverseSpawnPayload[] spawns;
}
