#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Meta;

namespace Meta.EditorTools
{
    /// <summary>
    /// Maintenance for the persisted MiniMap citizen store. A stored blob with radarEnabled=true
    /// legitimately overrides the DEFAULT-OFF contract for a returning player, so verifying the
    /// fresh-citizen path needs those blobs removed. This menu deletes
    /// "citizen_minimap_&lt;place&gt;_&lt;citizen&gt;" PlayerPrefs keys (PLUS the legacy
    /// "citizen_minimap_&lt;citizen&gt;" shape, which the store still READS) for every
    /// Meta_MiniMap in the open scene.
    /// HTTP-store users: delete <c>{base}/environments/{environmentId}/minimap/{citizenId}.json</c>
    /// from the server payload instead.
    /// </summary>
    public static class MiniMap_StoreMaintenance
    {
        [MenuItem("Tools/Meta/MiniMap/Clear Stored MiniMap Citizen Settings", priority = 105)]
        private static void ClearStoredForSceneCitizens()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[MiniMapStore] Exit Play first - storage writes are gated to Edit mode here.");
                return;
            }

            int cleared = 0;
            foreach (var map in Object.FindObjectsByType<Meta_MiniMap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (map == null) continue;

                string citizen = map.CitizenId;
                if (string.IsNullOrEmpty(citizen)) continue;

                string place = map.EnvironmentId;
                string display = string.IsNullOrEmpty(place) ? "<default place>" : place;

                // Current key layout AND legacy shape (the store still reads it for old saves).
                PlayerPrefs.DeleteKey(MiniMapCitizenPath.BuildPlayerPrefsKey(place, citizen));
                PlayerPrefs.DeleteKey(MiniMapCitizenPath.BuildLegacyPlayerPrefsKey(citizen));
                cleared++;

                Debug.Log($"[MiniMapStore] Cleared citizen prefs citizen='{citizen}' place='{display}'.");
            }

            if (cleared == 0)
                Debug.Log("[MiniMapStore] No Meta_MiniMap with a CitizenId found in the scene - nothing to clear.");
            else
                Debug.Log($"[MiniMapStore] {cleared} citizen blob(s) cleared - next Play starts from the DEFAULT-OFF contract.");

            PlayerPrefs.Save();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }
    }
}
#endif
