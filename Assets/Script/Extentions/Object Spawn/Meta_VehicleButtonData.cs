using UnityEngine;
using Meta; // Ensure this matches your namespace for VehicleSpawnEvents and VehicleSpawnData

// This script holds the specific data for ONE vehicle button.
public class Meta_VehicleButtonData : MonoBehaviour
{
    [Tooltip("Configuration for the vehicle this button will spawn.")]
    public VehicleSpawnData SpawnConfig;

    // NOTE: We no longer need a reference to the SpawnController!

    // This method is called by the Unity Button's OnClick() event.
    public void OnButtonClick()
    {
        if (SpawnConfig.VehiclePrefab == null)
        {
            Debug.LogError($"SpawnConfig is missing the Vehicle Prefab on button {gameObject.name}!");
            return;
        }

        // 1. Invoke the static event, sending the spawn data.
        // Only the local player's spawn controller, if it exists, will respond.
        VehicleSpawnEvents.OnStartPreviewRequested?.Invoke(SpawnConfig);

        Debug.Log($"Broadcasted spawn request for: {SpawnConfig.VehiclePrefab.name}");
    }
}