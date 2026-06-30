using System;

[Serializable]
public class MetaverseSpawnEnvelope
{
    public int v = 1;
    public string type;
    public string messageId;
    public long ts;
    public MetaverseSpawnPayload spawn;
    public MetaverseDespawnPayload despawn;
    public MetaverseSpawnPayload[] spawns;
}
