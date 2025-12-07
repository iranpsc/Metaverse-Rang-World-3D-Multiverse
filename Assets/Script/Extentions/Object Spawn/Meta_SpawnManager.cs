using Mirror;
using System;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_SpawnManager")]
    [HelpURL("https://google.com")]
    public class Meta_SpawnManager : NetworkBehaviour
    {
        public static Meta_SpawnManager instance;
        public GameObject CurrentObjectToSpawn;
        public GameObject[] SpawnablePrefabs;

        public event Action<GameObject[]> OnSpawnableReady;

        private void Awake()
        {
            if (instance == null)
                instance = this;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            OnSpawnableReady?.Invoke(SpawnablePrefabs);
        }

    }
}