#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Production HUD setup for the Day/Night controls
    /// (menu: Tools/Meta/DayNight/Setup Controls In Scene).
    /// </summary>
    public static class DayNight_TestRigCreator
    {
        public const string RIG_ROOT_NAME = "Meta_DayNight_Rig";
        public const string HUD_NAME = "Meta_DayNight_HUD";
        public const string CONTROLS_NAME = "Meta_DayNight_Controls";

        /// <summary>Default PLACE id whose time of day is persisted. Change per lobby/room scene.</summary>
        public const string EnvironmentId = "lobby_1";

        // SINGLE menu entry for the DayNight module: builds the cycle controls + ON/OFF control panel.
        // Alt + click = force recreate everything for this module.
        [MenuItem("Tools/Meta/DayNight/Setup", priority = 101)]
        public static void CreateTestRig()
        {
            CreateTestRig(EnvironmentId);
        }

        /// <summary>Builds the rig bound to a specific PLACE (scene/room) for persistence.</summary>
        public static void CreateTestRig(string environmentId)
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[DayNight] Setup only in Edit mode. Exit Play first.");
                return;
            }

            bool forceRecreate = Event.current != null && Event.current.alt;

            var existing = GameObject.Find(RIG_ROOT_NAME);
            if (existing != null && !forceRecreate)
            {
                Selection.activeGameObject = existing;
                Debug.Log($"[DayNight] Controls already set up ({RIG_ROOT_NAME}). Alt+click this menu to force recreate.");
                DayNight_ControlPanel_Setup.BuildOrSelect(false);
                return;
            }

            if (forceRecreate)
            {
                var oldHud = GameObject.Find(DayNight_ControlPanel_Setup.HUD_NAME);
                var orphanDisc = FindAnyGameObject(Meta_DayNightCycle.SUN_DISC_NAME);
                if (existing != null) Undo.DestroyObjectImmediate(existing);
                if (oldHud != null) Undo.DestroyObjectImmediate(oldHud);
                if (orphanDisc != null) Undo.DestroyObjectImmediate(orphanDisc);
                Debug.Log("[DayNight] Alt+Setup = force recreate (rig + sun disc + control HUD removed).");
            }

            var rigRoot = new GameObject(RIG_ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(rigRoot, "Create DayNight Rig");

            var dayNight = Undo.AddComponent<Meta_DayNightCycle>(rigRoot);
            var tool = Undo.AddComponent<DayNight_SceneTool>(rigRoot);
            tool.ButtonsWiredExternally = true;

            Light sun = FindOrCreateSun(rigRoot);
            dayNight.Sun = sun;

            // Single control card is built by DayNight_ControlPanel_Setup (bottom-left stack).
            // The old duplicate slider/Toggle card is removed below.

            dayNight.MaxIntensity = 1.2f;
            dayNight.MinIntensity = 0f;
            dayNight.RotationSpeed = 2f;        // 2 deg/s -> full 360 day in 180 s
            dayNight.RotationAxis = Vector3.right;
            dayNight.EnableShadows = true;
            dayNight.ShadowType = LightShadows.Soft;
            dayNight.DayShadowStrength = 0.9f;
            dayNight.NightShadowStrength = 0.15f;
            dayNight.AmbientDayIntensity = 1f;
            dayNight.AmbientNightIntensity = 0.2f;
            dayNight.TintAmbientColor = true;
            dayNight.AmbientDayColor = new Color(0.66f, 0.63f, 0.58f, 1f);      // warm daylight bounce
            dayNight.AmbientNightColor = new Color(0.13f, 0.17f, 0.30f, 1f);   // soft blue night
            dayNight.IsEnabled = false;

            // The module defaults to ApplyOnly (host wiring arrives in a later step). The lobby rig IS the
            // environment owner, so it simulates locally and the ON / OFF / Reset card works.
            dayNight.AuthorityMode = Meta.EnvironmentAuthorityMode.LocalAuthoritySimulate;

            // Persist the PLACE, not the citizen: one environment id per lobby scene.
            dayNight.EnvironmentId = environmentId;

            // Light colour along the curve: warm white noon -> orange horizon -> cool twilight.
            dayNight.SunDayColor = new Color(1f, 0.957f, 0.878f, 1f);          // #FFF4E0
            dayNight.SunDuskColor = new Color(1f, 0.706f, 0.541f, 1f);         // #FFB48A
            dayNight.SunNightColor = new Color(0.56f, 0.66f, 0.85f, 1f);

            // A Directional Light renders nothing on its own - the module needs a VISIBLE sun too.
            dayNight.CreateVisibleSunIfMissing = true;
            dayNight.SunDiscDistance = 300f;
            dayNight.SunDiscScale = 25f;
            dayNight.ShowDiscAtNight = false;
            dayNight.DiscBaseColor = new Color(1f, 0.824f, 0.478f, 1f);      // #FFD27A
            dayNight.DiscEmissionIntensity = 2.2f;
            dayNight.DiscGlow = true;
            dayNight.DiscGlowScale = 1.45f;
            dayNight.DiscGlowAlpha = 0.45f;
            dayNight.DiscPulse = 0.02f;
            dayNight.VisibleSun = FindOrCreateSunDisc(rigRoot, sun).transform;

            // A visible sun only reads when the player faces it: aim it at the current view.
            bool aimed = dayNight.AimSunInFrontOfCamera(0.38f);
            if (!aimed) sun.transform.rotation = Quaternion.Euler(155f, 0f, 0f);
            PlaceSunDiscForSun(rigRoot, sun);

            Selection.activeGameObject = rigRoot;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[DayNight] Rig ready: directional sun '{sun.name}' + visible disc " +
                      $"'{Meta_DayNightCycle.SUN_DISC_NAME}' ({(aimed ? "aimed at the current view" : "default morning angle")}). " +
                      "Day/NIGHT ON to run the cycle.");

            CleanupDuplicates();
            DayNight_ControlPanel_Setup.BuildOrSelect(forceRecreate);
        }

        /// <summary>Removes the old duplicate HUD card for this module.</summary>
        internal static void CleanupDuplicates()
        {
            DestroyIfFound(HUD_NAME);                 // Meta_DayNight_HUD (duplicate card canvas)
            DestroyIfFound("Meta_DayNight_Controls"); // duplicate panel
            DestroyIfFound("Meta_DayNight_TestCanvas");
        }

        internal static void DestroyIfFound(string name)
        {
            var go = FindAnyGameObject(name);
            if (go != null) Undo.DestroyObjectImmediate(go);
        }

        /// <summary>GameObject.Find misses inactive objects, so scan the whole active scene too.</summary>
        internal static GameObject FindAnyGameObject(string name)
        {
            var go = GameObject.Find(name);
            if (go != null) return go;

            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t.gameObject;
            }
            return null;
        }

        private static Light FindOrCreateSun(GameObject rigRoot)
        {
            Light sun = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l != null && l.type == LightType.Directional) { sun = l; break; }
            }

            if (sun == null)
            {
                var sunGO = new GameObject(Meta_DayNightCycle.SUN_LIGHT_NAME);
                Undo.RegisterCreatedObjectUndo(sunGO, "Create Sun");
                sunGO.transform.SetParent(rigRoot.transform, false);
                sunGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                sun = sunGO.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.2f;
                sun.shadows = LightShadows.Soft;   // shadows are mandatory
                sun.shadowStrength = 0.9f;
                sun.shadowBias = 0.05f;
                sun.shadowNormalBias = 0.5f;
            }
            else if (sun.transform.parent != rigRoot.transform)
            {
                Undo.SetTransformParent(sun.transform, rigRoot.transform, "Parent Sun to Rig");
            }

            return sun;
        }

        /// <summary>
        /// Ensures a VISIBLE sun sphere exists in the scene (a Directional Light alone is invisible)
        /// and parks it along the current sun direction so it reads in the Scene view before Play.
        /// </summary>
        private static GameObject FindOrCreateSunDisc(GameObject rigRoot, Light sun)
        {
            var disc = FindAnyGameObject(Meta_DayNightCycle.SUN_DISC_NAME);

            if (disc == null)
            {
                disc = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                disc.name = Meta_DayNightCycle.SUN_DISC_NAME;
                Undo.RegisterCreatedObjectUndo(disc, "Create Visible Sun Disc");
                Undo.DestroyObjectImmediate(disc.GetComponent<Collider>());   // pure visual
                Undo.SetTransformParent(disc.transform, rigRoot.transform, "Parent Sun Disc");
            }

            var focus = rigRoot.transform.position;
            var pos = focus - sun.transform.forward * 300f;
            disc.transform.position = pos;
            disc.transform.rotation = Quaternion.LookRotation((focus - pos).normalized, Vector3.up);
            disc.transform.localScale = Vector3.one * 25f;

            // The real materials are generated at runtime; in Edit mode the sphere still has to
            // read as a sun, so give it a plain unlit gold so nobody mistakes it for a grey ball.
            var renderer = disc.GetComponent<Renderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                if (shader != null && (renderer.sharedMaterial == null ||
                    renderer.sharedMaterial.name.StartsWith("Default", System.StringComparison.OrdinalIgnoreCase)))
                {
                    var preview = new Material(shader) { name = "Meta_Sun_Disc_Preview" };
                    if (preview.HasProperty("_BaseColor")) preview.SetColor("_BaseColor", new Color(1f, 0.82f, 0.48f, 1f));
                    if (preview.HasProperty("_Color")) preview.SetColor("_Color", new Color(1f, 0.82f, 0.48f, 1f));
                    renderer.sharedMaterial = preview;
                }
            }

            return disc;
        }

        /// <summary>Re-parks the disc after the sun was re-aimed, so the scene view shows it in the sky.</summary>
        private static void PlaceSunDiscForSun(GameObject rigRoot, Light sun)
        {
            var disc = FindAnyGameObject(Meta_DayNightCycle.SUN_DISC_NAME);
            if (disc == null) return;

            var focus = rigRoot.transform.position;
            var pos = focus - sun.transform.forward * 300f;
            disc.transform.position = pos;
            disc.transform.rotation = Quaternion.LookRotation((focus - pos).normalized, Vector3.up);
        }
    }
}
#endif