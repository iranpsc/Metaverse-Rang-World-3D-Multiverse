using UnityEngine;
using UnityEngine.UI;
using Meta;

[RequireComponent(typeof(Button))]
public class Meta_ObjectSpawnButton : MonoBehaviour
{
    public int PrefabIndex = 0;
    private Button SpawnButton;

    private void Start()
    {
        SpawnButton = GetComponent<Button>();
        SpawnButton.onClick.AddListener(OnButtonClicked);
    }

    private void OnButtonClicked()
    {
        if (Meta_ObjectSpawnerSystem.Instance == null)
        {
            Debug.LogError("Spawner system not found in scene!");
            return;
        }

        Meta_ObjectSpawnerSystem.Instance.SelectedIndex = PrefabIndex;
        Meta_ObjectSpawnerSystem.Instance.DoSpawn();
    }
}
