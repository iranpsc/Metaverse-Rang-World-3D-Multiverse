using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

public class G7Realtime3DMultiplayerController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private G7ThreeDModeController threeDModeController;
    [SerializeField] private MonoBehaviour dedicatedGameServerConnector;
    [SerializeField] private MonoBehaviour dedicatedRemotePlayerViewController;

    [Header("Dedicated Game Server Mode")]
    [SerializeField] private bool ensureLocalPlayerWhenThreeDStarts = true;
    [SerializeField] private bool autoConnectDedicatedWhenThreeDStarts = false;
    [SerializeField] private bool disconnectDedicatedWhenThreeDExits = false;
    [SerializeField] private bool clearRemotePlayersOnDedicatedDisconnect = true;

    [Header("Local Player")]
    [SerializeField] private string fallbackLocalPlayerName = "Player";

    private bool isBound;
    private bool isDedicatedConnectionRequested;
    private bool isDedicatedAuthenticated;
    private string dedicatedRoomId = string.Empty;
    private string dedicatedPlayerId = string.Empty;
    private string dedicatedPlayerName = string.Empty;

    private static readonly string[] DedicatedConnectorTypeNames =
    {
        "DedicatedGameServerRealtimeRoomBinder",
        "DedicatedGameServerAutoConnectController",
        "DedicatedGameServerClientController",
        "DedicatedGameServerConnectionController"
    };

    private static readonly string[] DedicatedRemoteViewTypeNames =
    {
        "DedicatedRemotePlayerViewController",
        "DedicatedRemotePlayerStateReceiver"
    };

    private static readonly string[] DedicatedConnectMethodNames =
    {
        "ConnectGameServer",
        "ConnectToGameServer",
        "OnConnectGameServerClicked",
        "OnConnectClicked",
        "StartConnectFlow",
        "StartAutoConnectFlow",
        "StartAutoConnectFlowAsync",
        "ConnectAsync"
    };

    private static readonly string[] DedicatedDisconnectMethodNames =
    {
        "DisconnectGameServer",
        "DisconnectFromGameServer",
        "OnDisconnectGameServerClicked",
        "OnDisconnectClicked",
        "Disconnect",
        "Close",
        "Stop"
    };

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();
        BindEvents();
    }

    private void OnDisable()
    {
        UnbindEvents();
    }

    private void ResolveReferences()
    {
        if (threeDModeController == null) threeDModeController = FindObjectOfType<G7ThreeDModeController>();
        if (dedicatedGameServerConnector == null) dedicatedGameServerConnector = FindFirstMonoBehaviourByTypeNames(DedicatedConnectorTypeNames);
        if (dedicatedRemotePlayerViewController == null) dedicatedRemotePlayerViewController = FindFirstMonoBehaviourByTypeNames(DedicatedRemoteViewTypeNames);
    }

    private void BindEvents()
    {
        if (isBound) UnbindEvents();

        if (threeDModeController != null)
        {
            threeDModeController.OnThreeDModeEntered += HandleThreeDModeEntered;
            threeDModeController.OnThreeDModeExited += HandleThreeDModeExited;
        }

        isBound = true;
    }

    private void UnbindEvents()
    {
        if (!isBound) return;

        if (threeDModeController != null)
        {
            threeDModeController.OnThreeDModeEntered -= HandleThreeDModeEntered;
            threeDModeController.OnThreeDModeExited -= HandleThreeDModeExited;
        }

        isBound = false;
    }

    private void HandleThreeDModeEntered()
    {
        EnsureLocalPlayerForDedicatedMode();
        if (autoConnectDedicatedWhenThreeDStarts) ConnectGameServer();
        Debug.Log("[G7-3D-MP] Dedicated-only mode is active. Legacy realtime 3D route is removed.");
    }

    private void HandleThreeDModeExited()
    {
        if (disconnectDedicatedWhenThreeDExits) DisconnectGameServer();
    }

    public void ConnectGameServer()
    {
        ResolveReferences();
        EnsureLocalPlayerForDedicatedMode();

        if (dedicatedGameServerConnector == null)
        {
            Debug.LogWarning("[G7-3D-MP] Dedicated game server connector was not found. Assign the dedicated connector in Inspector.");
            return;
        }

        isDedicatedConnectionRequested = true;

        if (!TryInvokeFirstAvailableMethod(dedicatedGameServerConnector, DedicatedConnectMethodNames, "connect"))
        {
            Debug.LogWarning("[G7-3D-MP] No compatible connect method was found on " + dedicatedGameServerConnector.GetType().Name + ". Use the existing Connect Game Server button or expose a public connect method.");
        }
    }

    public void DisconnectGameServer()
    {
        ResolveReferences();
        isDedicatedConnectionRequested = false;
        isDedicatedAuthenticated = false;
        dedicatedPlayerId = string.Empty;

        bool invoked = dedicatedGameServerConnector != null && TryInvokeFirstAvailableMethod(dedicatedGameServerConnector, DedicatedDisconnectMethodNames, "disconnect");
        if (!invoked && dedicatedRemotePlayerViewController != null) invoked = TryInvokeFirstAvailableMethod(dedicatedRemotePlayerViewController, DedicatedDisconnectMethodNames, "disconnect");

        if (!invoked)
        {
            Debug.LogWarning("[G7-3D-MP] No compatible disconnect method was found. Use the existing Disconnect from Game Server button or expose a public disconnect method.");
        }
    }

    public void NotifyDedicatedGameServerAuthenticated(string roomId)
    {
        NotifyDedicatedGameServerAuthenticated(roomId, string.Empty, string.Empty);
    }

    public void NotifyDedicatedGameServerAuthenticated(string roomId, string playerId)
    {
        NotifyDedicatedGameServerAuthenticated(roomId, playerId, string.Empty);
    }

    public void NotifyDedicatedGameServerAuthenticated(string roomId, string playerId, string playerName)
    {
        isDedicatedConnectionRequested = false;
        isDedicatedAuthenticated = true;
        dedicatedRoomId = SafeTrim(roomId);
        dedicatedPlayerId = SafeTrim(playerId);
        dedicatedPlayerName = SafeTrim(playerName);
        EnsureLocalPlayerForDedicatedMode();
        Debug.Log("[G7-3D-MP] Dedicated authenticated | roomId=" + dedicatedRoomId + " | playerId=" + dedicatedPlayerId);
    }

    public void NotifyDedicatedGameServerDisconnected(string reason)
    {
        isDedicatedConnectionRequested = false;
        isDedicatedAuthenticated = false;
        dedicatedPlayerId = string.Empty;

        if (clearRemotePlayersOnDedicatedDisconnect) ClearRemotePlayers("dedicated_disconnected:" + SafeTrim(reason));
        Debug.Log("[G7-3D-MP] Dedicated disconnected | reason=" + SafeTrim(reason));
    }

    public bool IsDedicatedGameServerFlowActive()
    {
        return isDedicatedConnectionRequested || isDedicatedAuthenticated;
    }

    public bool IsLegacyRealtime3DRouteRemoved()
    {
        return true;
    }

    private void EnsureLocalPlayerForDedicatedMode()
    {
        if (!ensureLocalPlayerWhenThreeDStarts) return;
        if (threeDModeController == null || !threeDModeController.IsThreeDModeActive) return;
        threeDModeController.EnsureLocalPlayerSpawned();
        SyncLocalPlayerNameText();
    }

    private void SyncLocalPlayerNameText()
    {
        if (threeDModeController == null) return;
        string displayName = !string.IsNullOrWhiteSpace(dedicatedPlayerName) ? dedicatedPlayerName : fallbackLocalPlayerName;
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "Player";
        threeDModeController.SetLocalPlayerDisplayName(displayName);
    }

    private void ClearRemotePlayers(string reason)
    {
        if (threeDModeController == null) return;
        threeDModeController.ClearRemotePlayers();
        Debug.Log("[G7-3D-MP] Remote players cleared | reason=" + SafeTrim(reason));
    }

    private MonoBehaviour FindFirstMonoBehaviourByTypeNames(string[] typeNames)
    {
        if (typeNames == null || typeNames.Length == 0) return null;

#if UNITY_2020_1_OR_NEWER
        MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>(true);
#else
        MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();
#endif

        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (behaviour == null) continue;

            string behaviourTypeName = behaviour.GetType().Name;
            for (int j = 0; j < typeNames.Length; j++)
            {
                if (string.Equals(behaviourTypeName, typeNames[j], StringComparison.Ordinal)) return behaviour;
            }
        }

        return null;
    }

    private bool TryInvokeFirstAvailableMethod(MonoBehaviour target, string[] methodNames, string action)
    {
        if (target == null || methodNames == null) return false;

        Type targetType = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        for (int i = 0; i < methodNames.Length; i++)
        {
            string methodName = methodNames[i];
            MethodInfo method = targetType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            if (method == null) continue;

            try
            {
                object result = method.Invoke(target, null);
                HandlePossibleAsyncInvokeResult(target, methodName, result);
                Debug.Log("[G7-3D-MP] Dedicated " + action + " invoked | target=" + targetType.Name + " | method=" + methodName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G7-3D-MP] Dedicated " + action + " invoke failed | target=" + targetType.Name + " | method=" + methodName + " | error=" + ex.Message);
                return false;
            }
        }

        return false;
    }

    private void HandlePossibleAsyncInvokeResult(MonoBehaviour target, string methodName, object result)
    {
        if (result is Task task)
        {
            _ = AwaitInvokedTaskAsync(target, methodName, task);
            return;
        }

        if (result is IEnumerator routine && target != null)
        {
            target.StartCoroutine(routine);
        }
    }

    private async Task AwaitInvokedTaskAsync(MonoBehaviour target, string methodName, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            string targetName = target == null ? "null" : target.GetType().Name;
            Debug.LogWarning("[G7-3D-MP] Invoked task failed | target=" + targetName + " | method=" + methodName + " | error=" + ex.Message);
        }
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
