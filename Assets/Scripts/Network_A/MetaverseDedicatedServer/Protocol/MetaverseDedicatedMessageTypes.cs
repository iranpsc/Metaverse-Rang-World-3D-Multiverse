using Network_A.Realtime.Protocol;

public static class MetaverseDedicatedMessageTypes
{
    public const string Spawn = RealtimeMessageTypes.Spawn;
    public const string Despawn = RealtimeMessageTypes.Despawn;
    public const string Destroy = RealtimeMessageTypes.Despawn;
    public const string SpawnSnapshot = RealtimeMessageTypes.Snapshot;
    public const string LegacySpawnSnapshot = "spawn_snapshot";

    public const string Command = RealtimeMessageTypes.Command;
    public const string Cmd = RealtimeMessageTypes.Command;
    public const string ClientRpc = RealtimeMessageTypes.ClientRpc;
    public const string Rpc = RealtimeMessageTypes.ClientRpc;
    public const string TargetRpc = RealtimeMessageTypes.TargetRpc;
    public const string SyncVar = RealtimeMessageTypes.SyncVar;
    public const string NetworkTransform = RealtimeMessageTypes.NetworkTransform;
    public const string SyncTransform = RealtimeMessageTypes.NetworkTransform;
    public const string Ownership = RealtimeMessageTypes.Ownership;
    public const string PlayerInput = RealtimeMessageTypes.PlayerInput;
    public const string OwnerInput = RealtimeMessageTypes.PlayerInput;

    public const string MirrorRouteSpawn = "NetworkServer.Spawn";
    public const string MirrorRouteDespawn = "NetworkServer.Despawn";
    public const string MirrorRouteDestroy = "NetworkServer.Destroy";
    public const string MirrorRouteCmd = "Cmd";
    public const string MirrorRouteCommand = "Command";
    public const string MirrorRouteRpc = "Rpc";
    public const string MirrorRouteClientRpc = "ClientRpc";
    public const string MirrorRouteTargetRpc = "TargetRpc";
    public const string MirrorRouteSyncVar = "SyncVar";
    public const string MirrorRouteSyncTransform = "SyncTransform";
    public const string MirrorRouteAssignAuthority = "NetworkServer.AssignClientAuthority";
    public const string MirrorRouteRemoveAuthority = "NetworkServer.RemoveClientAuthority";
    public const string MirrorRouteOwnerInput = "OwnerInput";

    public static bool IsSpawnRoute(string messageType)
    {
        string safeType = SafeTrim(messageType);
        return safeType == Spawn || safeType == Despawn || safeType == SpawnSnapshot || safeType == LegacySpawnSnapshot;
    }

    public static bool IsRpcRoute(string messageType)
    {
        string safeType = SafeTrim(messageType);
        return safeType == Command || safeType == ClientRpc || safeType == TargetRpc;
    }

    public static bool IsStateSyncRoute(string messageType)
    {
        string safeType = SafeTrim(messageType);
        return safeType == SyncVar || safeType == NetworkTransform;
    }

    public static bool IsMirrorLikeGameplayRoute(string messageType)
    {
        string safeType = SafeTrim(messageType);
        return IsSpawnRoute(safeType) || IsRpcRoute(safeType) || IsStateSyncRoute(safeType) || safeType == Ownership || safeType == PlayerInput;
    }

    public static string ReadMirrorLikeRoute(string messageType)
    {
        string safeType = SafeTrim(messageType);
        if (safeType == Spawn) return MirrorRouteSpawn;
        if (safeType == Despawn) return MirrorRouteDespawn;
        if (safeType == SpawnSnapshot || safeType == LegacySpawnSnapshot) return "NetworkServer.SpawnSnapshot";
        if (safeType == Command) return MirrorRouteCmd;
        if (safeType == ClientRpc) return MirrorRouteRpc;
        if (safeType == TargetRpc) return MirrorRouteTargetRpc;
        if (safeType == SyncVar) return MirrorRouteSyncVar;
        if (safeType == NetworkTransform) return MirrorRouteSyncTransform;
        if (safeType == Ownership) return MirrorRouteAssignAuthority;
        if (safeType == PlayerInput) return MirrorRouteOwnerInput;
        return "unknown";
    }

    public static string ReadEnvelopeRouteForLog(string channel, string messageType)
    {
        string safeChannel = string.IsNullOrWhiteSpace(channel) ? "unknown" : channel.Trim();
        string safeType = string.IsNullOrWhiteSpace(messageType) ? "unknown" : messageType.Trim();
        return safeChannel + "/" + safeType + " | mirrorRoute=" + ReadMirrorLikeRoute(safeType);
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
