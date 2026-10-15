#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Production HUD setup for the URP CameraForceRender controls
    /// (menu: Tools/Meta/CameraForce/Setup Controls In Scene).
    /// </summary>
    public static class CameraForceRender_TestRigCreator
    {
        public const string RIG_ROOT_NAME = "Meta_CameraForce_Rig";
        public const string HUD_NAME = "Meta_CameraForce_HUD";
        public const string CONTROLS_NAME = "Meta_CameraForce_Controls";
        private const string CAMERA_NAME = "Meta_CameraForce_Camera";

        // SINGLE menu entry for the CameraForce module: builds the renderer controls + ON/OFF control panel.
        // Alt + click = force recreate everything for this module.
        [MenuItem("Tools/Meta/CameraForce/Setup", priority = 102)]
        public static void CreateTestRig()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[CameraForce] Setup only in Edit mode. Exit Play first.");
                return;
            }

            bool forceRecreate = Event.current != null && Event.current.alt;

            var existing = GameObject.Find(RIG_ROOT_NAME);
            if (existing != null && !forceRecreate)
            {
                Selection.activeGameObject = existing;
                Debug.Log($"[CameraForce] Controls already set up ({RIG_ROOT_NAME}). Alt+click this menu to force recreate.");
                CameraForce_ControlPanel_Setup.BuildOrSelect(false);
                return;
            }

            if (forceRecreate)
            {
                var oldHud = GameObject.Find(CameraForce_ControlPanel_Setup.HUD_NAME);
                if (existing != null) Undo.DestroyObjectImmediate(existing);
                if (oldHud != null) Undo.DestroyObjectImmediate(oldHud);
                Debug.Log("[CameraForce] Alt+Setup = force recreate (rig + control HUD removed).");
            }

            var rigRoot = new GameObject(RIG_ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(rigRoot, "Create CameraForce Rig");

            var camGO = new GameObject(CAMERA_NAME);
            Undo.RegisterCreatedObjectUndo(camGO, "Create CameraForce Camera");
            camGO.transform.SetParent(rigRoot.transform, false);
            camGO.transform.localPosition = new Vector3(0f, 10f, 0f);
            camGO.transform.localRotation = Quaternion.Euler(20f, 30f, 0f);

            var cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;

            var forceRender = camGO.AddComponent<CameraForceRenderByIndex>();
            forceRender.RendererIndex = 0;
            forceRender.EnableLog = true;

            // Single control card is built by CameraForce_ControlPanel_Setup.
            // The old dev card (Apply 99 / Reinitialize / renderer dump) is removed below.

            Selection.activeGameObject = rigRoot;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[CameraForce] Rig ready. Control card handled by Setup.");

            CleanupDuplicates();
            CameraForce_ControlPanel_Setup.BuildOrSelect(forceRecreate);
        }

        /// <summary>Removes the old duplicate HUD card for this module.</summary>
        internal static void CleanupDuplicates()
        {
            DestroyIfFound(HUD_NAME);                // Meta_CameraForce_HUD (duplicate card canvas)
            DestroyIfFound("Meta_CameraForce_Controls");
            DestroyIfFound("Meta_CameraForce_ControlPanel");
            DestroyIfFound("Meta_CameraForce_TestCanvas");
        }

        internal static void DestroyIfFound(string name)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t.name == name) { Undo.DestroyObjectImmediate(t.gameObject); return; }
                }
                return;
            }
            Undo.DestroyObjectImmediate(go);
        }
    }
}
#endif