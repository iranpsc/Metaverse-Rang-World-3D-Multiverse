#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Meta;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Maintenance for the persisted day/night store. The PlayerPrefs key
    /// (<c>env_daynight_&lt;placeId&gt;</c>) keeps the LAST owner snapshot (time, running, speed)
    /// and is applied in Awake via <see cref="Meta_DayNightCycle.LoadEnvironmentState"/>, so a stale
    /// stored speed (e.g. an old 200/12 deg/s demo value) is ignored by the restore band, but the
    /// stored key itself can still be removed here. This menu deletes the stored state for every
    /// unique place id referenced by Meta_DayNightCycle instances in the open scene, so the next
    /// Play starts from scene values.
    ///
    /// HTTP-store users: the bundled Node service still has no daynight DELETE route. Stop the
    /// service and delete <c>&lt;Server&gt;/data/environments/&lt;placeId&gt;/daynight.json</c>
    /// (or send PUT with the scene-authored snapshot) instead.
    /// </summary>
    public static class DayNight_StoreMaintenance
    {
        [MenuItem("Tools/Meta/DayNight/Clear Stored Day-Night For Scene Places", priority = 105)]
        private static void ClearStoredForScenePlaces()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[DayNightStoreMaintenance] Exit Play first - storage writes are gated to Edit mode here.");
                return;
            }

            var store = new EnvironmentDayNightPlayerPrefsStore();
            var places = new HashSet<string>();
            foreach (var cycle in Object.FindObjectsByType<Meta_DayNightCycle>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (cycle == null || string.IsNullOrEmpty(cycle.EnvironmentId)) continue;
                places.Add(cycle.EnvironmentId);
            }

            foreach (string placeId in places)
            {
                store.Delete(placeId);
                Debug.Log($"[DayNightStoreMaintenance] Cleared persisted day/night state for place " +
                          $"'{placeId}' (PlayerPrefs key '{EnvironmentDayNightPlayerPrefsStore.KeyPrefix}{placeId}').");
            }

            if (places.Count == 0)
                Debug.Log("[DayNightStoreMaintenance] No Meta_DayNightCycle with a place id found in the scene - nothing stored to clear.");
            else
                Debug.Log($"[DayNightStoreMaintenance] {places.Count} stored place state(s) cleared - next Play starts from the scene values. " +
                          "HTTP-store places: delete {base}/environments/<placeId>/daynight.json on the server disk as well.");

            // The scene itself never changed, but mark it so the log action shows up intentionally.
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }
    }
}
#endif
