using UnityEngine;
using System;

namespace Meta
{
    // This is the data structure passed by your UI buttons.
    [Serializable]
    public class VehicleSpawnData
    {
        [Tooltip("The actual Network Prefab to be spawned.")]
        public GameObject VehiclePrefab;

        [Tooltip("The offset from the spawn point to place the preview.")]
        public Vector3 InitialOffset = new Vector3(0, 0, 3f);
    }
}