using Meta.Vehicle;
using Mirror;
using System.Collections.Generic;
using UnityEngine;

public class NetworkCommandExecutor : NetworkBehaviour
{
    public static NetworkCommandExecutor Instance;

    private void Awake()
    {
        Instance = this;
    }

    public void ExecuteCommand(ConsoleCommandSO _Command, CommandContext _Context)
    {
        CmdExecute(_Command.CommandName, _Context.Args);
    }

    [Command(requiresAuthority = false)]
    private void CmdExecute(string _CommandName, string[] _Args, NetworkConnectionToClient _Sender = null)
    {
        var _Command = ConsoleDatabase.Instance.GetCommand(_CommandName);
        if (_Command == null) return;

        var _Ctx = new CommandContext
        {
            Sender = _Sender.identity,
            Args = _Args,
            Console = ConsoleManager.LocalInstance
        };

        _Command.ExecuteServer(_Ctx);
        TargetCommandResult(_Sender, _Ctx.ResultMessage, _Ctx.ResultType);
    }

    [TargetRpc]
    public void TargetCommandResult(NetworkConnection _Target, string _Message, CommandResultType _ResultType)
    {
        var _Console = ConsoleManager.LocalInstance;

        if (_Console == null) return;

        switch (_ResultType)
        {
            case CommandResultType.Success:
                _Console.LogSuccess(_Message);
                break;
            case CommandResultType.Error:
                _Console.LogError(_Message);
                break;
            default:
                _Console.LogInfo(_Message);
                break;
        }
    }

    [TargetRpc]
    public void TargetTeleport(NetworkConnection target, Vector3 position) // اسکریپتبل آبجکت نمیتونه دستور میررور رو اجرا کنه پس اینجا باید نوشته بشه
    {
        // فقط Player Local خود Client
        var player = NetworkClient.localPlayer.gameObject;

        if (player == null)
            return;

        // اگر CharacterController داری، disable/enable
        if (player.TryGetComponent<CharacterController>(out var cc))
            cc.enabled = false;

        player.transform.position = position;

        if (player.TryGetComponent<CharacterController>(out cc))
            cc.enabled = true;
    }

    public void ExecuteCommandNetworkDestroy(NetworkIdentity targetVehicle, CommandContext _Context)
    {
        if (targetVehicle == null)
        {
            _Context.SetResult("Invalid vehicle reference", CommandResultType.Error);
            TargetCommandResult(_Context.Sender.connectionToClient, _Context.ResultMessage, _Context.ResultType);
            return;
        }

        CmdDestroyVehicle(targetVehicle, _Context.Sender.connectionToClient);
    }

    [Command(requiresAuthority = false)]
    private void CmdDestroyVehicle(NetworkIdentity vehicleIdentity, NetworkConnectionToClient sender = null)
    {
        if (vehicleIdentity == null || !NetworkServer.active)
            return;

        Meta_VehicleBase vehicle = vehicleIdentity.GetComponent<Meta_VehicleBase>();
        if (vehicle == null)
            return;

        // بررسی خالی بودن وسیله نقلیه
        foreach (var seat in vehicle._SeatState)
        {
            if (seat.OccupantNetId != 0)
            {
                TargetCommandResult(sender, "Vehicle is occupied and cannot be destroyed", CommandResultType.Error);
                return;
            }
        }

        NetworkServer.Destroy(vehicle.gameObject);
        TargetCommandResult(sender, $"Vehicle {vehicle.name} destroyed successfully", CommandResultType.Success);
    }

    [Command(requiresAuthority = false)]
    public void CmdRequestUnstuck()
    {
        List<Transform> _SpawnPoints = NetworkManager.startPositions;
        if (_SpawnPoints == null || _SpawnPoints.Count == 0)
            return;

        Vector3 _TargetPos = _SpawnPoints[0].position;

        transform.position = _TargetPos;

        CharacterController _Controller = GetComponent<CharacterController>();
        if (_Controller != null)
        {
            _Controller.enabled = false;
            _Controller.enabled = true;
        }

        TargetUnstuckResult(connectionToClient, _TargetPos);
    }

    [TargetRpc]
    private void TargetUnstuckResult(NetworkConnection _Target, Vector3 _Pos)
    {
        ConsoleManager.LocalInstance.LogSuccess(
            $"Unstuck → {_Pos.x:F1}, {_Pos.y:F1}, {_Pos.z:F1}"
        );
    }
}
