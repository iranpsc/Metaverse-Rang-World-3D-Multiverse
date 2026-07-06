using Meta.Vehicle;
using Mirror;
using UnityEngine;

[CreateAssetMenu(fileName = "DestroyVehicle", menuName = "Meta/Command/Destroy")]
public class DestroyCommand : ConsoleCommandSO
{
    [Header("Raycast Settings")]
    public float RayDistance = 4f;
    public LayerMask VehicleLayer;

    public override void ExecuteLocal(CommandContext _Context)
    {
        if (_Context.Sender == null)
        {
            _Context.SetResult("No sender found", CommandResultType.Error);
            return;
        }

        // فقط روی LocalPlayer اجرا شود
        if (!_Context.Sender.isLocalPlayer)
        {
            _Context.SetResult("Command must be run by the local player", CommandResultType.Error);
            return;
        }

        Camera playerCamera = _Context.Sender.GetComponentInChildren<Camera>();
        if (playerCamera == null)
        {
            _Context.SetResult("Player camera not found", CommandResultType.Error);
            return;
        }

        // Raycast روی Client
        if (!Physics.Raycast(playerCamera.transform.position, playerCamera.transform.forward, out RaycastHit hit, RayDistance, VehicleLayer))
        {
            _Context.SetResult("No vehicle in sight to destroy", CommandResultType.Error);
            return;
        }

        Meta_VehicleBase vehicle = hit.collider.GetComponentInParent<Meta_VehicleBase>();
        if (vehicle == null)
        {
            _Context.SetResult("No vehicle hit", CommandResultType.Error);
            return;
        }

        // NetId به Server بفرست
        NetworkCommandExecutor.Instance.ExecuteCommandNetworkDestroy(vehicle.netIdentity, _Context);
    }

    public override void ExecuteServer(CommandContext _Context)
    {
        // سرور خودش کاری انجام نمی‌دهد
        // همه منطق Server داخل NetworkCommandExecutor انجام می‌شود
    }
}
