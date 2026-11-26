#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Production HUD setup for the URP CameraForceRender controls
    /// (menu: Tools/Meta/CameraForce/Setup).
    ///
    /// The helper camera is ALWAYS Untagged, has no AudioListener and is disabled, so it can
    /// never take over the avatar/player MainCamera view. The control card targets this helper,
    /// never a camera tagged MainCamera.
    /// </summary>
    public static class CameraForceRender_TestRigCreator
    {
        public const string RIG_ROOT_NAME = "Meta_CameraForce_Rig";
        public const string HUD_NAME = "Meta_CameraForce_HUD";                 // legacy duplicate canvas
        public const string CONTROLS_NAME = "Meta_CameraForce_Controls";       // legacy dev card
        private const string CAMERA_NAME = "Meta_CameraForce_Camera";

        // SINGLE menu entry for the CameraForce module: builds the rig + control card.
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

            var rig = FindActiveSceneRoot(RIG_ROOT_NAME);
            if (forceRecreate && rig != null)
            {
                Undo.DestroyObjectImmediate(rig);
                rig = null;
                Debug.Log("[CameraForce] Alt+Setup = force recreate (old rig removed).");
            }

            if (rig == null)
            {
                rig = new GameObject(RIG_ROOT_NAME);
                Undo.RegisterCreatedObjectUndo(rig, "Create CameraForce Rig");
            }

            rig.SetActive(true);

            var camera = EnsureRigCamera(rig.transform);
            EnsureForceComponent(camera);

            CleanupLegacyDuplicates();

            Selection.activeGameObject = rig;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            // Build or repair the Play-mode card after the target camera exists.
            CameraForce_ControlPanel_Setup.BuildOrSelect(forceRecreate);

            Debug.Log("[CameraForce] Rig ready: helper camera is Untagged, disabled, and has no " +
                      "AudioListener. Control card targets the helper, never the player MainCamera.");
        }

        /// <summary>
        /// Finds or creates the helper camera under the rig root and makes it safe:
        /// Untagged, disabled, depth -100, no culling, no AudioListener.
        /// </summary>
        private static Camera EnsureRigCamera(Transform rig)
        {
            Camera camera = rig.GetComponentInChildren<Camera>(true);
            GameObject cameraGO;
            bool created = false;

            if (camera == null)
            {
                cameraGO = new GameObject(CAMERA_NAME);
                Undo.RegisterCreatedObjectUndo(cameraGO, "Create CameraForce Camera");
                cameraGO.transform.SetParent(rig, false);
                cameraGO.transform.localPosition = new Vector3(0f, 10f, 0f);
                cameraGO.transform.localRotation = Quaternion.Euler(20f, 30f, 0f);

                camera = cameraGO.AddComponent<Camera>();
                created = true;
            }
            else
            {
                cameraGO = camera.gameObject;
            }

            cameraGO.SetActive(true);

            // NEVER use "MainCamera" tag - it belongs to the avatar/player camera. Only a camera
            // under our own rig is re-tagged, and only to Untagged.
            if (cameraGO.tag != "Untagged")
            {
                Debug.Log($"[CameraForce] Re-tagging tool-owned helper '{cameraGO.name}' from " +
                          $"'{cameraGO.tag}' to Untagged.");
                cameraGO.tag = "Untagged";
            }

            // The real MainCamera owns the scene's AudioListener. Remove any listener that an old
            // RequireComponent chain may have added under this tool-owned helper.
            foreach (var listener in cameraGO.GetComponentsInChildren<AudioListener>(true))
            {
                if (listener == null) continue;
                Undo.DestroyObjectImmediate(listener);
                Debug.Log("[CameraForce] Removed AudioListener from the helper camera.");
            }

            // A disabled camera cannot render or clear the player's view. If someone enables it
            // manually, depth -100 + no culling keeps it behind/empty.
            camera.enabled = false;
            camera.depth = -100f;
            camera.clearFlags = CameraClearFlags.Depth;
            camera.cullingMask = 0;

            if (created)
                Debug.Log("[CameraForce] Created helper camera (Untagged, disabled, no AudioListener).");

            return camera;
        }

        private static void EnsureForceComponent(Camera camera)
        {
            if (camera == null) return;

            var force = camera.GetComponent<CameraForceRenderByIndex>();
            if (force == null)
                force = Undo.AddComponent<CameraForceRenderByIndex>(camera.gameObject);

            force.RendererIndex = 0;
            force.EnableLog = false;
        }

        /// <summary>
        /// Removes only objects this tool family created in older versions. It never destroys the
        /// current camera rig, the current control runner/HUD, or the current card panel.
        /// </summary>
        internal static void CleanupLegacyDuplicates()
        {
            // Legacy dev card may be a root object or live under the legacy HUD canvas.
            var legacyHud = FindActiveSceneRoot(HUD_NAME) ?? FindCanvasByName(HUD_NAME);

            var legacyControls = FindActiveSceneRoot(CONTROLS_NAME);
            if (legacyControls == null && legacyHud != null)
            {
                var child = legacyHud.transform.Find(CONTROLS_NAME);
                if (child != null) legacyControls = child.gameObject;
            }

            if (legacyControls != null && LooksLikeLegacyControls(legacyControls))
            {
                Undo.DestroyObjectImmediate(legacyControls);
                Debug.Log($"[CameraForce] Removed legacy dev card '{CONTROLS_NAME}'.");
            }

            if (legacyHud != null && LooksLikeLegacyCanvas(legacyHud))
            {
                Undo.DestroyObjectImmediate(legacyHud);
                Debug.Log($"[CameraForce] Removed legacy duplicate HUD '{HUD_NAME}'.");
            }

            var legacyTestCanvas = FindActiveSceneRoot("Meta_CameraForce_TestCanvas");
            if (legacyTestCanvas != null && LooksLikeLegacyCanvas(legacyTestCanvas) &&
                LooksLikeLegacyControls(legacyTestCanvas))
            {
                Undo.DestroyObjectImmediate(legacyTestCanvas);
                Debug.Log("[CameraForce] Removed legacy test canvas 'Meta_CameraForce_TestCanvas'.");
            }
        }

        private static bool LooksLikeLegacyControls(GameObject go)
        {
            if (go == null) return false;

            return go.transform.Find("Apply Index 0") != null ||
                   go.transform.Find("Apply Invalid 99") != null ||
                   go.transform.Find("InfoText") != null ||
                   go.GetComponentInChildren<Slider>(true) != null;
        }

        private static bool LooksLikeLegacyCanvas(GameObject go)
        {
            return go != null &&
                   go.GetComponent<Canvas>() != null &&
                   go.GetComponentInChildren<Button>(true) != null;
        }

        private static GameObject FindActiveSceneRoot(string name)
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root != null && root.name == name)
                    return root;
            }

            return null;
        }

        private static GameObject FindCanvasByName(string name)
        {
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (canvas == null || canvas.gameObject.name != name) continue;
                if (canvas.gameObject.scene != SceneManager.GetActiveScene()) continue;
                return canvas.gameObject;
            }

            return null;
        }
    }
}
#endif
