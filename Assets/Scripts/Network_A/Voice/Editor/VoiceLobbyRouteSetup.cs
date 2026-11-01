#if UNITY_EDITOR
using System;
using Network_A.Lobby;
using Network_A.Voice.Client.Routing;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Editor
{
    public static class VoiceLobbyRouteSetup
    {
        private const string LobbyScenePath = "Assets/Scenes/Grpc/Lobby 1.unity";
        private const string VoiceScenePath = "Assets/Scenes/Grpc/Grpc_Enviroment_Voice.unity";
        private const string LobbyControllerObjectName = "Lobby_1_Realtime_Scene_Controller";
        private const string LobbyCanvasObjectName = "Canvas_RoomList";
        private const string VoiceButtonObjectName = "Btn_Voice";
        private const string VoiceButtonLabelObjectName = "Txt_Voice";

        //* این تابع منوی ویرایشگر را به نصب قابل‌تکرار دکمه و مسیر صوت متصل می‌کند.
        [MenuItem("Tools/Network A/Voice/Apply Lobby Voice Route")]
        public static void ApplyFromEditorMenu()
        {
            ApplySetup();
        }

        //* این تابع اجرای خط فرمان را به همان نصب قطعی و بدون ساخت خروجی متصل می‌کند.
        public static void ApplyFromCommandLine()
        {
            ApplySetup();
        }

        //* این تابع صحنه لابی را باز می‌کند، دکمه صوت و کنترلر آن را می‌سازد و وجود مقصد صوت را بررسی می‌کند.
        private static void ApplySetup()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LobbyScenePath) == null)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=lobby_scene_missing | path=" + LobbyScenePath);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(VoiceScenePath) == null)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=voice_scene_missing | path=" + VoiceScenePath);

            bool voiceSceneEnabled = false;
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;

            for (int index = 0; index < buildScenes.Length; index++)
            {
                EditorBuildSettingsScene buildScene = buildScenes[index];

                if (buildScene.enabled &&
                    string.Equals(buildScene.path, VoiceScenePath, StringComparison.Ordinal))
                {
                    voiceSceneEnabled = true;
                    break;
                }
            }

            if (!voiceSceneEnabled)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=voice_scene_not_enabled_in_build_settings");

            Scene lobbyScene = EditorSceneManager.OpenScene(LobbyScenePath, OpenSceneMode.Single);
            GameObject lobbyControllerObject = GameObject.Find(LobbyControllerObjectName);
            GameObject lobbyCanvasObject = GameObject.Find(LobbyCanvasObjectName);

            if (lobbyControllerObject == null || lobbyControllerObject.GetComponent<Lobby1RealtimeSceneController>() == null)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=lobby_controller_missing");

            if (lobbyCanvasObject == null || lobbyCanvasObject.GetComponent<Canvas>() == null)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=lobby_canvas_missing");

            VoiceLobbyRouteButtonController routeButtonController =
                lobbyControllerObject.GetComponent<VoiceLobbyRouteButtonController>();

            if (routeButtonController == null)
                routeButtonController = lobbyControllerObject.AddComponent<VoiceLobbyRouteButtonController>();

            Transform existingVoiceButtonTransform = lobbyCanvasObject.transform.Find(VoiceButtonObjectName);
            GameObject voiceButtonObject = existingVoiceButtonTransform != null
                ? existingVoiceButtonTransform.gameObject
                : new GameObject(VoiceButtonObjectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));

            voiceButtonObject.transform.SetParent(lobbyCanvasObject.transform, false);
            voiceButtonObject.layer = lobbyCanvasObject.layer;
            voiceButtonObject.SetActive(true);

            RectTransform voiceButtonRect = voiceButtonObject.GetComponent<RectTransform>();

            if (voiceButtonRect == null)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=existing_voice_button_is_not_ui_object");

            voiceButtonRect.anchorMin = new Vector2(1f, 1f);
            voiceButtonRect.anchorMax = new Vector2(1f, 1f);
            voiceButtonRect.pivot = new Vector2(1f, 1f);
            voiceButtonRect.anchoredPosition = new Vector2(-40f, -40f);
            voiceButtonRect.sizeDelta = new Vector2(220f, 72f);
            voiceButtonRect.localScale = Vector3.one;

            Image voiceButtonImage = voiceButtonObject.GetComponent<Image>();
            if (voiceButtonImage == null) voiceButtonImage = voiceButtonObject.AddComponent<Image>();
            voiceButtonImage.color = new Color(0.04f, 0.55f, 0.72f, 0.96f);

            Button voiceButton = voiceButtonObject.GetComponent<Button>();
            if (voiceButton == null) voiceButton = voiceButtonObject.AddComponent<Button>();
            ColorBlock buttonColors = voiceButton.colors;
            buttonColors.normalColor = Color.white;
            buttonColors.highlightedColor = new Color(0.85f, 1f, 1f, 1f);
            buttonColors.pressedColor = new Color(0.65f, 0.9f, 0.95f, 1f);
            buttonColors.selectedColor = buttonColors.highlightedColor;
            buttonColors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.5f);
            voiceButton.colors = buttonColors;
            voiceButton.targetGraphic = voiceButtonImage;

            Transform existingLabelTransform = voiceButtonObject.transform.Find(VoiceButtonLabelObjectName);
            GameObject labelObject = existingLabelTransform != null
                ? existingLabelTransform.gameObject
                : new GameObject(VoiceButtonLabelObjectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));

            labelObject.transform.SetParent(voiceButtonObject.transform, false);
            labelObject.layer = voiceButtonObject.layer;
            labelObject.SetActive(true);

            RectTransform labelRect = labelObject.GetComponent<RectTransform>();

            if (labelRect == null)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=existing_voice_label_is_not_ui_object");

            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            labelRect.localScale = Vector3.one;

            TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
            if (label == null) label = labelObject.AddComponent<TextMeshProUGUI>();
            label.text = "Voice";
            label.fontSize = 30f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null) label.font = TMP_Settings.defaultFontAsset;

            SerializedObject serializedController = new SerializedObject(routeButtonController);
            SerializedProperty voiceButtonProperty = serializedController.FindProperty("voiceButton");

            if (voiceButtonProperty == null)
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=voice_button_field_missing");

            voiceButtonProperty.objectReferenceValue = voiceButton;
            serializedController.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(lobbyScene);

            if (!EditorSceneManager.SaveScene(lobbyScene))
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=lobby_scene_save_failed");

            VoiceLobbyRouteSelection.SelectNormalMode();
            string normalTarget = VoiceLobbyRouteSelection.ResolveGameplaySceneName(
                VoiceLobbyRouteSelection.NormalGameplaySceneName);
            VoiceLobbyRouteSelection.SelectVoiceMode();
            string voiceTarget = VoiceLobbyRouteSelection.ResolveGameplaySceneName(
                VoiceLobbyRouteSelection.NormalGameplaySceneName);
            VoiceLobbyRouteSelection.SelectNormalMode();

            if (!string.Equals(normalTarget, VoiceLobbyRouteSelection.NormalGameplaySceneName, StringComparison.Ordinal) ||
                !string.Equals(voiceTarget, VoiceLobbyRouteSelection.VoiceGameplaySceneName, StringComparison.Ordinal))
                throw new InvalidOperationException("VOICE_LOBBY_ROUTE_SETUP=FAIL | reason=route_resolution_failed");

            Debug.Log(
                "VOICE_LOBBY_ROUTE_SETUP=PASS" +
                " | lobbyScene=" + lobbyScene.path +
                " | button=" + VoiceButtonObjectName +
                " | normalTarget=" + normalTarget +
                " | voiceTarget=" + voiceTarget +
                " | buildExecuted=NO");
        }
    }
}

/*
توضیح فایل:
این فایل فقط در ویرایشگر اجرا می‌شود و دکمه صوت را به‌صورت قابل‌تکرار در صحنه لابی می‌سازد، مرجع آن را تنظیم می‌کند و مقصد صوت فعال در تنظیمات ساخت را بررسی می‌کند.
*/
#endif
