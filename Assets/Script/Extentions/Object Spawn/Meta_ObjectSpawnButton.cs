using UnityEngine;
using UnityEngine.UI;
using Meta;

[RequireComponent(typeof(Button))]
public class Meta_ObjectSpawnButton : MonoBehaviour
{
    public Meta_ObjectSpawnerSystem SpawnSystem;
    public GameObject Prefab;
    private Button SpawnButton;

    private void Start()
    {
        SpawnSystem = Meta_ObjectSpawnerSystem.Instance;
        SpawnButton = GetComponent<Button>();
        SpawnButton.onClick.AddListener(Spawn); // FIXED
    }

    private void Spawn()
    {
        if (SpawnSystem == null) return;
        SpawnSystem.ObjectToSpawn = Prefab;
        SpawnSystem.DoSpawn();
    }
}
