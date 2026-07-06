using System;
using UnityEngine;

[Serializable]
public class MetaverseSpawnPayload
{
    public int netId;
    public string prefabId;
    public int ownerConnectionId;
    public string ownerConnectionIdText;
    public string ownerUserId;
    public string ownerPlayerId;
    public bool serverOwned;
    public bool localPlayer;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;

    public string roomId;
    public string sceneName;
    public string spawnReason;
    public string mirrorRoute;
    public long sequence;
    public long spawnedAtUnixMs;
    public bool sceneObject;
    public bool destroyOnDespawn;

    public bool HasValidNetId => netId > 0;
    public bool HasValidPrefabId => !string.IsNullOrWhiteSpace(prefabId);
    public bool HasOwner => !serverOwned && (!string.IsNullOrWhiteSpace(ownerConnectionIdText) || !string.IsNullOrWhiteSpace(ownerUserId) || !string.IsNullOrWhiteSpace(ownerPlayerId) || ownerConnectionId >= 0);
    public bool IsMirrorLikeSpawn => string.Equals(mirrorRoute, "NetworkServer.Spawn", StringComparison.Ordinal) || string.Equals(mirrorRoute, "NetworkServer.SpawnPrefab", StringComparison.Ordinal);

    public bool IsValidForSpawn()
    {
        return HasValidNetId && HasValidPrefabId;
    }

    public bool IsOwnedByConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return false;
        return string.Equals(ownerConnectionIdText, connectionId.Trim(), StringComparison.Ordinal);
    }

    public bool IsOwnedByUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return string.Equals(ownerUserId, userId.Trim(), StringComparison.Ordinal);
    }

    public bool IsOwnedByPlayer(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId)) return false;
        return string.Equals(ownerPlayerId, playerId.Trim(), StringComparison.Ordinal);
    }

    public bool IsOwnedByAny(string connectionId, string userId, string playerId)
    {
        if (IsOwnedByConnection(connectionId)) return true;
        if (IsOwnedByUser(userId)) return true;
        if (IsOwnedByPlayer(playerId)) return true;
        return false;
    }

    public void NormalizeDefaults(string defaultReason = "network_server_spawn")
    {
        prefabId = SafeTrim(prefabId);
        ownerConnectionIdText = SafeTrim(ownerConnectionIdText);
        ownerUserId = SafeTrim(ownerUserId);
        ownerPlayerId = SafeTrim(ownerPlayerId);
        roomId = SafeTrim(roomId);
        sceneName = SafeTrim(sceneName);
        spawnReason = SafeReason(spawnReason, defaultReason);
        mirrorRoute = SafeReason(mirrorRoute, "NetworkServer.Spawn");
        if (ownerConnectionId < 0 && int.TryParse(ownerConnectionIdText, out int parsed)) ownerConnectionId = parsed;
        if (scale == Vector3.zero) scale = Vector3.one;
        if (spawnedAtUnixMs <= 0) spawnedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public MetaverseSpawnPayload Clone()
    {
        return new MetaverseSpawnPayload
        {
            netId = netId,
            prefabId = prefabId,
            ownerConnectionId = ownerConnectionId,
            ownerConnectionIdText = ownerConnectionIdText,
            ownerUserId = ownerUserId,
            ownerPlayerId = ownerPlayerId,
            serverOwned = serverOwned,
            localPlayer = localPlayer,
            position = position,
            rotation = rotation,
            scale = scale,
            roomId = roomId,
            sceneName = sceneName,
            spawnReason = spawnReason,
            mirrorRoute = mirrorRoute,
            sequence = sequence,
            spawnedAtUnixMs = spawnedAtUnixMs,
            sceneObject = sceneObject,
            destroyOnDespawn = destroyOnDespawn
        };
    }

    public string GetDebugSummary()
    {
        return "netId=" + netId +
               " | prefabId=" + SafeTrim(prefabId) +
               " | ownerConnectionId=" + SafeTrim(ownerConnectionIdText) +
               " | ownerUserId=" + SafeTrim(ownerUserId) +
               " | ownerPlayerId=" + SafeTrim(ownerPlayerId) +
               " | serverOwned=" + serverOwned +
               " | localPlayer=" + localPlayer +
               " | mirrorRoute=" + SafeTrim(mirrorRoute);
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string SafeReason(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
