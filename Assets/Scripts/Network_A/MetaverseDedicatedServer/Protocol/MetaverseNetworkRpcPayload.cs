using System;

[Serializable]
public class MetaverseNetworkRpcPayload
{
    public string type;
    public int netId;
    public string prefabId;
    public string behaviourName;
    public string methodName;
    public string payloadJson;
    public string roomId;

    public string senderConnectionId;
    public string senderUserId;
    public string senderPlayerId;

    public string targetConnectionId;
    public string targetUserId;
    public string targetPlayerId;

    public long sequence;
    public long clientTimeUnixMs;
    public long serverTimeUnixMs;
}
