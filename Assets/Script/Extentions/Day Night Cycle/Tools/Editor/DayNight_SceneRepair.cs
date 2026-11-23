#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Meta.EditorTools
{
    /// <summary>
    /// One-shot repair for the shipped Lobby scene. Runs the same non-destructive DayNight Setup
    /// path (existing rig kept, sun repaired, ghost/duplicate cards removed) and saves the scene.
    /// Menu: Tools/Meta/DayNight/Repair Lobby Scene Wiring.
    /// Batch: -executeMethod Meta.EditorTools.DayNight_SceneRepair.RepairLobbySceneBatch
    /// </summary>
    public static class DayNight_SceneRepair
    {
        public const string LobbyScenePath = "Assets/Scenes/Grpc/Lobby 1 WebGL/Lobby 1 WebGL.unity";

        [MenuItem("Tools/Meta/DayNight/Repair Lobby Scene Wiring", priority = 106)]
        public static void RepairLobbyScene()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[DayNightRepair] Exit Play mode first.");
                return;
            }

            var scene = EditorSceneManager.OpenScene(LobbyScenePath, OpenSceneMode.Single);
            Debug.Log($"[DayNightRepair] Opened '{scene.path}'.");

            DayNight_TestRigCreator.CreateTestRig(DayNight_TestRigCreator.EnvironmentId);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log("[DayNightRepair] Lobby scene repaired and saved " +
                      "(sun -> enabled directional, ghost cycle removed, duplicate card removed, refs wired).");
        }

        /// <summary>Batch-mode alias (Unity's -executeMethod needs a public static method).</summary>
        public static void RepairLobbySceneBatch() => RepairLobbyScene();
    }
}
#endif
