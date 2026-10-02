#if UNITY_EDITOR
using System;
using TMPro;
using RTLTMPro;   // package com.nosuchstudio.rtltmpro
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MetaRange.Avatar.EditorLayer
{
    /// <summary>
    /// سیستم مدیریت موقعیت ریسپان آواتار — متارنگ v3.1
    /// Tools → متارنگ → راه‌اندازی سیستم ریسپان آواتار
    /// پنل UI فقط زیر Canvas ساخته می‌شود.
    /// </summary>
    public static class MetaRangeRespawnSetupTool
    {
        const string ProductName = "سیستم مدیریت موقعیت ریسپان آواتار — متارنگ v3.5";
        const string MenuPath = "Tools/متارنگ/راه‌اندازی سیستم ریسپان آواتار";
        const string PackageRoot = "Assets/Benyamin/MetaRangeRespawn";
        const string SystemRootName = "MetaRange_SpawnSystem";
        const string PanelName = "MetaRangeRootPanel";          // پنل راست — فقط مالک
        const string CreateCanvasName = "MetaRange_CreateEnvCanvas";   // Canvas مستقل پنل چپ
        const string CreatePanelName = "MetaRange_LeftCreatePanel";    // پنل چپ — ساخت فایل محیط
        const string AvatarName = "MetaRangeAvatar";
        const string PrefabPath = PackageRoot + "/Prefabs/PositionCard.prefab";
        const string ServerUrl = "http://localhost:3000";
        const string PlayBaseUrl = "https://metarange.adfam.com/play";

        // نام صحنه‌های قطعی پروژه (از DedicatedGameServerRealtimeRoomBinderWebGL استخراج شده:
        // WebGLLobbySceneName / WebGLGameplaySceneName). مبنای تشخیص و لاگ هستند.
        const string LobbySceneName = "Lobby 1 WebGL";
        const string GameplaySceneName = "WebGL_Enviroment";

        [MenuItem(MenuPath)]
        public static void Setup()
        {
            EnsureFolders();

            // آواتار اول مشخص می‌شود: اگر پیدا نشد، هیچ تغییری در صحنه داده نمی‌شود
            Transform avatar = ResolveAvatar();
            if (avatar == null) return;

            // ریشه منطقی (بدون UI)
            GameObject systemRoot = FindOrCreateSceneObject(SystemRootName);

            // =============================================================
            // بخش چپ — ساخت فایل JSON محیط (EnvironmentCreator)
            // Canvas مستقل تا با پنل مالک هیچ تداخلی نداشته باشد
            // =============================================================
            Canvas createCanvas = EnsureCreateCanvas();
            DestroyAllByName(CreatePanelName);                 // پنل چپ قبلی پاک شود
            GameObject createPanel = CreateCreateEnvPanel(createCanvas.transform);

            TMP_InputField envInput = createPanel.GetComponentInChildren<TMP_InputField>(true);
            Button createBtn = createPanel.GetComponentInChildren<Button>(true);
            RTLTextMeshPro createResult = null;
            foreach (Transform t in createPanel.GetComponentsInChildren<Transform>(true))
                if (t.name == "CreateEnvResult") createResult = t.GetComponent<RTLTextMeshPro>();

            var envCreator = createPanel.GetComponent<EnvironmentCreator>() ?? Undo.AddComponent<EnvironmentCreator>(createPanel);
            BindEnvironmentCreator(envCreator, envInput, createBtn, createResult);

            // =============================================================
            // بخش راست — پنل مالک (OwnerPanel)
            // =============================================================
            Canvas canvas = EnsureCanvas();
            EnsureEventSystem();
            DestroyAllByName(PanelName);                       // پنل قبلی پاک شود

            GameObject panel = CreateRootPanel(canvas.transform);

            // ۱) عنوان سیستم (داخل CreateRootPanel ساخته می‌شود)

            // ۲) مالک: تیک، موقعیت زنده، نام یونیک، دکمه ثبت — فرزند مستقیم این سکشن
            GameObject secOwner = CreateSection(panel.transform, "Section_Owner", "۱) پنل مالک", 260f);
            Toggle ownerToggle = CreateToggle(secOwner.transform, "OwnerToggle", "مالک هستم");
            RTLTextMeshPro livePos = CreateRtlTmp(secOwner.transform, "LivePositionText", "موقعیت زنده:  X: 0   Y: 0   Z: 0", 15, TextAlignmentOptions.MidlineRight);
            SetFixedHeight(livePos, 78f);   // سه خط متن زنده نباید بریده شود
            TMP_InputField posNameInput = CreateTmpInput(secOwner.transform, "PositionNameInput", "نام موقعیت (یونیک — خالی = خودکار)");
            Button registerBtn = CreateButton(secOwner.transform, "RegisterPositionButton", "ثبت موقعیت", new Color(0.18f, 0.68f, 0.38f));
            RTLTextMeshPro registerStatus = CreateRtlTmp(secOwner.transform, "RegisterStatusText", "", 13, TextAlignmentOptions.MidlineRight);

            // ۳) نتیجه ثبت — فرزند مستقیم پنل (قبل از لیست)، تا فعال شدن، لیست جا باز نمی‌کند
            GameObject secResult = CreateSection(panel.transform, "Section_Result", "لینک و QR", 0f);
            RTLTextMeshPro linkText = CreateRtlTmp(secResult.transform, "LinkText", "", 13, TextAlignmentOptions.MidlineRight);

            // QR در ردیف ثابت (بدون LayoutGroup) تا مربع بماند و کشیده نشود
            GameObject qrRow = CreateUi("QrRow", secResult.transform);
            LayoutElement qrRowLe = qrRow.AddComponent<LayoutElement>();
            qrRowLe.minHeight = 124f;
            qrRowLe.preferredHeight = 124f;
            RawImage qrImage = CreateQrImage(qrRow.transform, "QrRawImage", 120f);

            // دکمه‌های کنترل لینک/QR (کپی + دانلود متنی + دانلود تصویر)
            GameObject linkBtns = CreateUi("LinkButtons", secResult.transform);
            HorizontalLayoutGroup lbg = linkBtns.AddComponent<HorizontalLayoutGroup>();
            lbg.spacing = 6;
            lbg.childAlignment = TextAnchor.UpperCenter;
            lbg.childControlWidth = true;
            lbg.childControlHeight = true;
            lbg.childForceExpandWidth = true;
            lbg.childForceExpandHeight = true;
            LayoutElement lbgLe = linkBtns.AddComponent<LayoutElement>();
            lbgLe.minHeight = 44f;
            lbgLe.preferredHeight = 44f;

            Button copyLinkBtn = CreateButton(linkBtns.transform, "CopyLinkButton", "کپی لینک", new Color(0.20f, 0.50f, 0.75f));
            Button downloadLinkBtn = CreateButton(linkBtns.transform, "DownloadLinkButton", "دانلود لینک", new Color(0.25f, 0.45f, 0.60f));
            Button downloadQr = CreateButton(linkBtns.transform, "DownloadQrButton", "دانلود QR", new Color(0.35f, 0.35f, 0.40f));

            RTLTextMeshPro linkStatus = CreateRtlTmp(secResult.transform, "LinkStatusText", "", 13, TextAlignmentOptions.MidlineRight);
            SetFixedHeight(linkStatus, 30f);

            // ۴) لیست موقعیت‌ها — Scroll فقط و فقط اینجاست، بعد از دکمه ثبت
            GameObject secList = CreateSection(panel.transform, "Section_PositionList", "۲) موقعیت‌های ذخیره‌شده", 260f);
            RTLTextMeshPro emptyList = CreateRtlTmp(secList.transform, "EmptyListText", "هنوز موقعیتی ثبت نشده", 14, TextAlignmentOptions.MidlineRight);
            RectTransform cardContainer = CreateScrollContent(secList.transform, "PositionsScroll");

            // ارتفاع واقعی هر سکشن محاسبه می‌شود تا overlap نداشته باشیم
            FinalizeSection(secOwner);
            FinalizeSection(secResult);   // هنوز فعال است
            FinalizeSection(secList);
            secResult.SetActive(false);   // مخفی تا ثبت موفق

            GameObject cardPrefab = EnsureCardPrefab();

            var ownerPanel = panel.GetComponent<OwnerPanel>() ?? Undo.AddComponent<OwnerPanel>(panel);
            BindOwnerPanel(
                ownerPanel,
                ownerToggle, avatar, livePos, posNameInput, registerBtn, registerStatus,
                secResult, linkText, qrImage, downloadQr, copyLinkBtn, downloadLinkBtn, linkStatus,
                secList, emptyList, cardContainer, cardPrefab);

            var spawn = systemRoot.GetComponent<SpawnFromURL>() ?? Undo.AddComponent<SpawnFromURL>(systemRoot);
            BindSpawnFromURL(spawn, avatar);

            // بریج ادغام با Network_A (برنچ gRPC) — اختیاری و بدون وابستگی سخت
            System.Type bridgeType = FindBridgeType();
            if (bridgeType != null)
            {
                Component bridge = systemRoot.GetComponent(bridgeType) ?? Undo.AddComponent(systemRoot, bridgeType);
                var bso = new SerializedObject(bridge);
                SetRef(bso, "ownerPanel", ownerPanel);
                SetString(bso, "serverUrl", ServerUrl);
                bso.ApplyModifiedPropertiesWithoutUndo();
                LogNulls(bso, "MetaRangeNetworkSpawnBridge");
                Debug.Log("[متارنج] بریج شبکه (Network_A) به MetaRange_SpawnSystem اضافه شد.");

                // بریج دکمهٔ محیط لابی (Lobby 1 WebGL) → نام اتاق به‌عنوان env متارنج.
                // این کامپوننت خودش DontDestroyOnLoad می‌شود تا با لود صحنهٔ گیم‌پلی از بین نرود،
                // پس فقط bind لازم دارد و می‌تواند روی همان ریشهٔ سیستم بنشیند.
                System.Type lobbyBridgeType = FindLobbyBridgeType();
                if (lobbyBridgeType != null)
                {
                    Component lobbyBridge = systemRoot.GetComponent(lobbyBridgeType) ?? Undo.AddComponent(systemRoot, lobbyBridgeType);
                    var lso = new SerializedObject(lobbyBridge);
                    SetRef(lso, "networkSpawnBridge", bridge);
                    SetString(lso, "serverUrl", ServerUrl);
                    lso.ApplyModifiedPropertiesWithoutUndo();
                    LogNulls(lso, "MetaRangeLobbyEnvironmentBridge");
                    Debug.Log("[متارنج] بریج لابی (دکمهٔ محیط) به MetaRange_SpawnSystem اضافه شد.");
                }
            }

            Selection.activeGameObject = panel;
            EditorGUIUtility.PingObject(panel);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            Debug.Log("[متارنگ] راه‌اندازی کامل شد — " + ProductName + "\n" +
                      "صحنهٔ فعال: " + SceneManager.GetActiveScene().name +
                      "   |   لابی: " + LobbySceneName +
                      "   |   محیط: " + GameplaySceneName + "\n" +
                      "پنل چپ (ساخت محیط): " + CreateCanvasName + " → " + CreatePanelName + "\n" +
                      "پنل راست (مالک): " + PanelName + "\n" +
                      "بریج لابی (create-env + Context.env): " +
                          (FindLobbyBridgeType() != null ? "نصب‌شده" : "★ موجود نیست") +
                          "   |   Context.env فعلی: " +
                          (string.IsNullOrEmpty(EnvironmentCreator.StoredEnvironmentName)
                              ? "(قفل نشده — از دکمهٔ لابی پر می‌شود)"
                              : EnvironmentCreator.StoredEnvironmentName) + "\n" +
                      "آواتار: " + avatar.name + "   |   سرور: " + ServerUrl);
        }

        // =====================================================================
        // وضعیت Context مشترک محیط (برای بازرسی سریع)
        // =====================================================================

        [MenuItem("Tools/متارنج/نمایش Context محیط فعلی")]
        public static void ShowEnvironmentContext()
        {
            Debug.Log(
                "[متارنج] Context محیط" +
                " | StoredEnvironmentName = " +
                (string.IsNullOrEmpty(EnvironmentCreator.StoredEnvironmentName)
                    ? "(خالی)" : EnvironmentCreator.StoredEnvironmentName) +
                " | صحنهٔ فعال = " + SceneManager.GetActiveScene().name +
                " | لابی = " + LobbySceneName +
                " | محیط = " + GameplaySceneName);
        }

        // =====================================================================
        // Bind
        // =====================================================================

        static void BindEnvironmentCreator(EnvironmentCreator c, TMP_InputField input, Button btn, RTLTextMeshPro result)
        {
            var so = new SerializedObject(c);
            SetRef(so, "nameInput", input);
            SetRef(so, "createButton", btn);
            SetRef(so, "resultText", result);
            SetString(so, "serverUrl", ServerUrl);
            so.ApplyModifiedPropertiesWithoutUndo();
            LogNulls(so, "EnvironmentCreator");
        }

        static void BindOwnerPanel(
            OwnerPanel c,
            Toggle toggle, Transform avatar, RTLTextMeshPro livePos,
            TMP_InputField posNameInput, Button register, RTLTextMeshPro registerStatus,
            GameObject sectionResult, RTLTextMeshPro linkText, RawImage qr, Button downloadQr,
            Button copyLink, Button downloadLink, RTLTextMeshPro linkStatus,
            GameObject sectionList, RTLTextMeshPro emptyList, RectTransform content, GameObject cardPrefab)
        {
            var so = new SerializedObject(c);
            SetRef(so, "ownerToggle", toggle);
            SetRef(so, "avatar", avatar);
            SetRef(so, "livePosText", livePos);
            SetRef(so, "positionNameInput", posNameInput);
            SetRef(so, "registerButton", register);
            SetRef(so, "registerStatusText", registerStatus);
            SetRef(so, "sectionResult", sectionResult);
            SetRef(so, "linkText", linkText);
            SetRef(so, "qrImage", qr);
            SetRef(so, "downloadQrButton", downloadQr);
            SetRef(so, "copyLinkButton", copyLink);
            SetRef(so, "downloadLinkButton", downloadLink);
            SetRef(so, "linkStatusText", linkStatus);
            SetRef(so, "sectionPositionList", sectionList);
            SetRef(so, "emptyListText", emptyList);
            SetRef(so, "cardContainer", content);
            SetRef(so, "cardPrefab", cardPrefab);
            SetString(so, "serverUrl", ServerUrl);
            SetString(so, "playBaseUrl", PlayBaseUrl);
            so.ApplyModifiedPropertiesWithoutUndo();
            LogNulls(so, "OwnerPanel");
        }

        static void BindSpawnFromURL(SpawnFromURL c, Transform avatar)
        {
            var so = new SerializedObject(c);
            SetRef(so, "avatarTransform", avatar);
            SetRef(so, "avatar", avatar);
            SetString(so, "serverUrl", ServerUrl);
            so.ApplyModifiedPropertiesWithoutUndo();
            LogNulls(so, "SpawnFromURL");
        }

        static void SetRef(SerializedObject so, string prop, UnityEngine.Object value)
        {
            SerializedProperty p = so.FindProperty(prop);
            if (p != null)
                p.objectReferenceValue = value;
        }

        static void SetString(SerializedObject so, string prop, string value)
        {
            SerializedProperty p = so.FindProperty(prop);
            if (p != null)
                p.stringValue = value;
        }

        static void SetBool(SerializedObject so, string prop, bool value)
        {
            SerializedProperty p = so.FindProperty(prop);
            if (p != null)
                p.boolValue = value;
        }

        static void LogNulls(SerializedObject so, string label)
        {
            so.Update();
            SerializedProperty it = so.GetIterator();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue == null)
                    Debug.LogWarning("[MetaRange] " + label + " فیلد خالی: " + it.name);
            }
        }

        // =====================================================================
        // نگهبان قرارداد دکمهٔ محیط لابی (Lobby 1 WebGL)
        // =====================================================================

        [MenuItem("Tools/متارنج/بررسی اتصال لابی (Lobby 1 WebGL)")]
        public static void VerifyLobbyContract()
        {
            const string managerTypeName = "Network_A.Realtime.Controllers.RealtimeRoomGameServerManager";
            const string clientTypeName = "MetaverseNetworkClient";

            System.Text.StringBuilder report = new System.Text.StringBuilder();
            report.AppendLine("=== اتصال دکمهٔ محیط لابی ⇒ متارنج ===");

            System.Type managerType = FindTypeByName(managerTypeName);
            System.Type clientType = FindTypeByName(clientTypeName);
            System.Type lobbyControllerType = FindTypeByName("Network_A.Lobby.Lobby1RealtimeSceneController");
            System.Type itemViewType = FindTypeByName("Network_A.Lobby.CompletedBuildingRoomListItemView");

            report.AppendLine(MemberRow("RealtimeRoomGameServerManager", managerType == null, "کلاس مدیر ریل‌تایم"));
            report.AppendLine(MemberRow("Lobby1RealtimeSceneController", lobbyControllerType == null, "کنترلر صحنهٔ لابی (سازندهٔ دکمه‌ها)"));
            report.AppendLine(MemberRow("CompletedBuildingRoomListItemView", itemViewType == null, "ویوی آیتم روم (کلیک دکمه)"));

            if (managerType != null)
            {
                System.Reflection.EventInfo joined = managerType.GetEvent("OnRoomJoinedFor3D",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                System.Reflection.EventInfo left = managerType.GetEvent("OnRoomLeftFor3D",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                System.Reflection.PropertyInfo roomName = managerType.GetProperty("CurrentRoomName",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                report.AppendLine(MemberRow("OnRoomJoinedFor3D (static event)", joined == null, "رویداد ورود به محیط"));
                report.AppendLine(MemberRow("OnRoomLeftFor3D (static event)", left == null, "رویداد خروج از محیط"));
                report.AppendLine(MemberRow("CurrentRoomName", roomName == null, "کد ساختمان = نام محیط متارنج"));
            }

            if (clientType != null)
            {
                System.Reflection.PropertyInfo userId = clientType.GetProperty("userId",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                report.AppendLine(MemberRow("MetaverseNetworkClient.userId", userId == null, "انتخاب نقطهٔ اسپان بین بازیکنان"));
            }

            System.Type bridgeType = FindTypeByName("MetaRange.Avatar.MetaRangeLobbyEnvironmentBridge");
            MonoBehaviour instanceInScene = bridgeType == null
                ? null
                : UnityEngine.Object.FindAnyObjectByType(bridgeType) as MonoBehaviour;

            report.AppendLine(MemberRow("MetaRangeLobbyEnvironmentBridge (کلاس)", bridgeType == null, "بریج لابی"));
            report.AppendLine(MemberRow("MetaRangeLobbyEnvironmentBridge (نمونه در صحنه)", instanceInScene == null,
                "اگر نبود، خودش هنگام اجرا ساخته می‌شود"));

            report.AppendLine();
            report.AppendLine(LobbyNetworkHooks.Describe());
            report.AppendLine("نگاشت: env = کد ساختمان (CurrentRoomName)  |  spawn = یکی از نقاط /api/list-positions?env=<کد ساختمان>");
            report.AppendLine("جریان: OnRoomJoinedFor3D ⇒ create-env (idempotent) ⇒ Context.env قفل ⇒ list-positions ⇒ get-position ⇒ TryApplyPose روی local player شبکه");
            report.AppendLine();

            report.AppendLine("— صحنه‌ها —");
            report.AppendLine("  لابی      : " + LobbySceneName + "   موجود در Build Settings: " + SceneInBuildSettings(LobbySceneName));
            report.AppendLine("  محیط      : " + GameplaySceneName + "   موجود در Build Settings: " + SceneInBuildSettings(GameplaySceneName));
            report.AppendLine("  صحنهٔ فعال: " + SceneManager.GetActiveScene().name);
            report.AppendLine("  LoadMode  : Single  ⇒  ریشهٔ متارنج باید DontDestroyOnLoad باشد (AutoBootstrap)");

            report.AppendLine();
            report.AppendLine("— Context مشترک محیط —");
            report.AppendLine("  EnvironmentCreator.StoredEnvironmentName = " +
                (string.IsNullOrEmpty(EnvironmentCreator.StoredEnvironmentName)
                    ? "(خالی — با کلیک روی دکمهٔ محیط قفل می‌شود)"
                    : EnvironmentCreator.StoredEnvironmentName));

            report.AppendLine();
            report.AppendLine("— سرور لوکال متارنج —");
            report.AppendLine("  پیش‌فرض serverUrl: http://localhost:3000   (GET /api/health باید {\"ok\":true} بدهد)");

            Debug.Log(report.ToString());
        }

        static bool SceneInBuildSettings(string sceneName)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i] != null &&
                    string.Equals(System.IO.Path.GetFileNameWithoutExtension(scenes[i].path), sceneName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        static string MemberRow(string member, bool missing, string role)
        {
            return "  " + (missing ? "MISS" : "OK  ") + "  " + member + "  ← " + role;
        }

        static System.Type FindTypeByName(string fullName)
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // =====================================================================
        // Canvas / EventSystem / Folders
        // =====================================================================

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Benyamin"))
                AssetDatabase.CreateFolder("Assets", "Benyamin");
            if (!AssetDatabase.IsValidFolder(PackageRoot))
                AssetDatabase.CreateFolder("Assets/Benyamin", "MetaRangeRespawn");
            if (!AssetDatabase.IsValidFolder(PackageRoot + "/Prefabs"))
                AssetDatabase.CreateFolder(PackageRoot, "Prefabs");
            if (!AssetDatabase.IsValidFolder(PackageRoot + "/Scripts"))
                AssetDatabase.CreateFolder(PackageRoot, "Scripts");
            if (!AssetDatabase.IsValidFolder(PackageRoot + "/Editor"))
                AssetDatabase.CreateFolder(PackageRoot, "Editor");
            if (!AssetDatabase.IsValidFolder(PackageRoot + "/Docs"))
                AssetDatabase.CreateFolder(PackageRoot, "Docs");
        }

        static Canvas EnsureCanvas()
        {
            Canvas c = UnityEngine.Object.FindAnyObjectByType<Canvas>();
            if (c != null)
            {
                if (c.renderMode != RenderMode.ScreenSpaceOverlay)
                    c.renderMode = RenderMode.ScreenSpaceOverlay;
                return c;
            }

            GameObject go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(go, "Create Canvas");
            c = go.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;

            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            return c;
        }

        static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;
            GameObject go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(go, "Create EventSystem");
        }

        static GameObject FindOrCreateSceneObject(string name)
        {
            GameObject existing = GameObject.Find(name);
            if (existing != null) return existing;
            GameObject go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        static void DestroyAllByName(string name)
        {
            // همه نمونه‌های هم‌نام در صحنه (حتی غیرفعال)
            Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t.name != name) continue;
                if (string.IsNullOrEmpty(t.gameObject.scene.name)) continue; // prefab asset نه
                Undo.DestroyObjectImmediate(t.gameObject);
            }
        }

        // =====================================================================
        // UI builders
        // =====================================================================

        // رنگ‌های مشترک پنل (کنتراست بالا روی زمینهٔ تیره)
        static readonly Color PanelBg = new Color(0.08f, 0.09f, 0.12f, 0.96f);
        static readonly Color SectionBg = new Color(1f, 1f, 1f, 0.07f);
        static readonly Color HeaderColor = new Color(1f, 1f, 1f, 1f);
        static readonly Color SectionTitleColor = new Color(0.85f, 0.90f, 1f, 1f);
        static readonly Color BodyTextColor = new Color(0.93f, 0.93f, 0.96f, 1f);

        /// <summary>Canvas مستقل پنل چپ (ساخت فایل محیط) — تا با پنل راست تداخل نکند</summary>
        static Canvas EnsureCreateCanvas()
        {
            GameObject existing = GameObject.Find(CreateCanvasName);
            if (existing != null)
            {
                Canvas found = existing.GetComponent<Canvas>();
                if (found != null)
                {
                    found.renderMode = RenderMode.ScreenSpaceOverlay;
                    found.sortingOrder = 100;      // همیشه روی Canvas اصلی
                    EnsureRaycaster(existing);
                    return found;
                }
            }

            GameObject go = new GameObject(CreateCanvasName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Create " + CreateCanvasName);
            go.layer = LayerMask.NameToLayer("UI");

            Canvas c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 100;
            go.AddComponent<GraphicRaycaster>();

            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            return c;
        }

        static void EnsureRaycaster(GameObject canvasGo)
        {
            if (canvasGo.GetComponent<GraphicRaycaster>() == null)
                Undo.AddComponent<GraphicRaycaster>(canvasGo);
            if (canvasGo.GetComponent<CanvasScaler>() == null)
                Undo.AddComponent<CanvasScaler>(canvasGo);
        }

        /// <summary>پنل چپ: فقط ساخت فایل JSON محیط (Create Environment)</summary>
        static GameObject CreateCreateEnvPanel(Transform createCanvasTransform)
        {
            GameObject panel = CreateUi(CreatePanelName, createCanvasTransform);

            Image img = panel.AddComponent<Image>();
            img.color = PanelBg;

            RectTransform rt = panel.GetComponent<RectTransform>();
            // چسبیده به چپِ بالای صفحه
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(16f, -16f);
            rt.sizeDelta = new Vector2(380f, 260f);

            VerticalLayoutGroup vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(16, 16, 16, 16);
            vlg.spacing = 14;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;     // فرزندان از LayoutElement/TMP اندازه می‌گیرند
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;   // ارتفاع خودکار

            RTLTextMeshPro header = CreateRtlTmp(panel.transform, "CreateEnvHeader",
                "ساخت فایل محیط (Create Environment)", 18, TextAlignmentOptions.MidlineRight);
            header.color = HeaderColor;
            header.fontStyle = FontStyles.Bold;

            TMP_InputField nameInput = CreateTmpInput(panel.transform, "EnvNameInput", "نام محیط (خالی = خودکار)");
            Button createBtn = CreateButton(panel.transform, "CreateEnvButton",
                "ساخت فایل محیط", new Color(0.20f, 0.45f, 0.85f));
            RTLTextMeshPro result = CreateRtlTmp(panel.transform, "CreateEnvResult", "", 14, TextAlignmentOptions.MidlineRight);

            return panel;
        }

        static GameObject CreateRootPanel(Transform canvasTransform)
        {
            GameObject panel = CreateUi(PanelName, canvasTransform);

            Image img = panel.AddComponent<Image>();
            img.color = PanelBg;

            RectTransform rt = panel.GetComponent<RectTransform>();
            // چسبیده به راست صفحه، تمام ارتفاع با margin
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(-16f, 0f);
            rt.sizeDelta = new Vector2(390f, -32f); // width=390 ، بالا/پایین 16px

            VerticalLayoutGroup vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(16, 16, 16, 16);
            vlg.spacing = 14;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            RTLTextMeshPro header = CreateRtlTmp(panel.transform, "HeaderTitle", ProductName, 20, TextAlignmentOptions.MidlineRight);
            header.color = HeaderColor;
            header.fontStyle = FontStyles.Bold;
            return panel;
        }

        static GameObject CreateSection(Transform parent, string name, string title, float minHeight)
        {
            GameObject sec = CreateUi(name, parent);

            LayoutElement le = sec.AddComponent<LayoutElement>();
            le.minHeight = minHeight;
            le.flexibleHeight = 0f;

            VerticalLayoutGroup vlg = sec.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(6, 6, 10, 10);
            vlg.spacing = 8;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;    // ارتفاع فرزندان از LayoutElement/TMP محاسبه شود
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            Image bg = sec.AddComponent<Image>();
            bg.color = SectionBg;

            if (!string.IsNullOrEmpty(title))
            {
                RTLTextMeshPro t = CreateRtlTmp(sec.transform, "Title", title, 16, TextAlignmentOptions.MidlineRight);
                t.color = SectionTitleColor;
                t.fontStyle = FontStyles.Bold;
            }

            return sec;
        }

        static GameObject CreateUi(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        static RTLTextMeshPro CreateRtlTmp(Transform parent, string name, string text, int size, TextAlignmentOptions align)
        {
            GameObject go = CreateUi(name, parent);
            RTLTextMeshPro tmp = go.AddComponent<RTLTextMeshPro>();

            tmp.text = text;                 // متن فارسی
            tmp.fontSize = size;
            tmp.alignment = align;
            tmp.color = BodyTextColor;       // کنتراست بالا روی زمینهٔ تیره
            tmp.enableAutoSizing = false;    // سایز ثابت و قابل پیش‌بینی

            // تنظیمات مهم فارسی
            tmp.Farsi = true;                 // شکل‌دهی حروف فارسی/عربی
            tmp.PreserveNumbers = true;       // اعداد لاتین نشکند
            tmp.ForceFix = true;              // اصلاح شکل حروف
            tmp.FixTags = true;               // اصلاح تگ‌های رنگی
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;

            LayoutElement le = go.AddComponent<LayoutElement>();
            le.minHeight = size + 12;
            le.preferredHeight = size + 14;

            // برای فرزندان مستقیم پنل (childControlHeight=false) ارتفاع باید صریح باشد
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, size + 14f);

            TMP_FontAsset font = GetFont();
            if (font != null) tmp.font = font;
            return tmp;
        }

        // نام قدیمی نگه داشته شد تا همه call-siteها بدون تغییر کار کنند
        static TextMeshProUGUI CreateTmp(Transform parent, string name, string text, int size, TextAlignmentOptions align)
        {
            return CreateRtlTmp(parent, name, text, size, align);
        }

        static TMP_FontAsset GetFont()
        {
            string[] candidates = {
                "Vazir SDF", "Vazirmatn SDF", "Vazir", "Vazirmatn",
                "IRANSansX SDF", "IRANSans SDF", "IRANSans", "IRANYekan SDF",
                "Yekan SDF", "Estedad SDF", "Sahel SDF", "Shabnam SDF",
                "Noto Naskh Arabic", "Noto Sans Arabic"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                TMP_FontAsset f = Resources.Load<TMP_FontAsset>("Fonts/" + candidates[i]);
                if (f != null) return f;
            }

            // جست‌وجوی خودکار بین همه Font Assetهای پروژه
            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (path.IndexOf("Packages/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                TMP_FontAsset f = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (f != null) return f;
            }

            try
            {
                TMP_FontAsset def = TMP_Settings.defaultFontAsset;
                if (def != null && !warnedAboutFont)
                {
                    warnedAboutFont = true;
                    Debug.LogWarning("[MetaRange] هیچ Font Asset فارسی در پروژه پیدا نشد؛ از فونت پیش‌فرض TMP (" +
                                     def.name + ") استفاده شد. برای نمایش درست حروف فارسی، با Font Asset Creator یک فونت Vazir/IRANSans بسازید.");
                }
                return def;
            }
            catch { }
            return null;
        }

        static bool warnedAboutFont;

        /// <summary>
        /// ارتفاع ثابت برای متن‌های چندخطی (تا با LayoutGroup بریده نشوند).
        /// هم LayoutElement و هم خود RectTransform به‌روزرسانی می‌شوند.
        /// </summary>
        static void SetFixedHeight(Component graphic, float height)
        {
            if (graphic == null) return;

            var le = graphic.GetComponent<LayoutElement>();
            if (le == null) le = graphic.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;

            var rt = graphic.GetComponent<RectTransform>();
            if (rt != null)
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
        }

        /// <summary>
        /// ردیف «لیبل + فیلد» برای حالت ویرایش کارت.
        /// لیبل سمت راست (RTL) با عرض ثابت و فیلد انعطاف‌پذیر در باقی‌ماندهٔ عرض.
        /// </summary>
        static TMP_InputField CreateLabeledField(Transform parent, string rowName, string labelText,
                                                  string fieldName, string placeholder)
        {
            GameObject row = CreateUi(rowName, parent);

            HorizontalLayoutGroup h = row.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8;
            h.childAlignment = TextAnchor.MiddleRight;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;

            // لیبل
            RTLTextMeshPro label = CreateRtlTmp(row.transform, rowName + "Label", labelText, 15, TextAlignmentOptions.MidlineRight);
            label.fontStyle = FontStyles.Bold;
            label.color = SectionTitleColor;
            LayoutElement labelLe = label.GetComponent<LayoutElement>();
            labelLe.minWidth = 52f;
            labelLe.preferredWidth = 52f;
            labelLe.flexibleWidth = 0f;
            labelLe.minHeight = 40f;
            labelLe.preferredHeight = 40f;

            // فیلد ورودی
            TMP_InputField field = CreateTmpInput(row.transform, fieldName, placeholder);
            LayoutElement fieldLe = field.GetComponent<LayoutElement>();
            fieldLe.minHeight = 40f;
            fieldLe.preferredHeight = 40f;
            fieldLe.flexibleWidth = 1f;   // فضای باقی‌ماندهٔ ردیف

            return field;
        }

        static TMP_InputField CreateTmpInput(Transform parent, string name, string placeholder)
        {
            GameObject go = CreateUi(name, parent);
            Image img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.95f);

            LayoutElement le = go.AddComponent<LayoutElement>();
            le.minHeight = 40;
            le.preferredHeight = 40;

            GameObject textGo = CreateUi("Text", go.transform);
            RTLTextMeshPro text = textGo.AddComponent<RTLTextMeshPro>();
            text.fontSize = 16;
            text.color = Color.black;
            text.alignment = TextAlignmentOptions.MidlineRight;
            text.Farsi = true;
            text.PreserveNumbers = true;
            text.ForceFix = true;
            text.FixTags = true;
            text.enableAutoSizing = false;
            SetStretch(textGo.GetComponent<RectTransform>(), 8f);

            GameObject phGo = CreateUi("Placeholder", go.transform);
            RTLTextMeshPro ph = phGo.AddComponent<RTLTextMeshPro>();
            ph.text = placeholder;
            ph.fontSize = 16;
            ph.fontStyle = FontStyles.Italic;
            ph.color = new Color(0.35f, 0.35f, 0.35f, 0.85f);
            ph.alignment = TextAlignmentOptions.MidlineRight;
            ph.Farsi = true;
            ph.PreserveNumbers = true;
            ph.ForceFix = true;
            ph.FixTags = true;
            ph.enableAutoSizing = false;
            SetStretch(phGo.GetComponent<RectTransform>(), 8f);

            TMP_FontAsset font = GetFont();
            if (font != null) { text.font = font; ph.font = font; }

            TMP_InputField input = go.AddComponent<TMP_InputField>();
            input.textComponent = text;   // RTLTextMeshPro از TextMeshProUGUI ارث می‌برد
            input.placeholder = ph;
            input.lineType = TMP_InputField.LineType.SingleLine;
            return input;
        }

        static Button CreateButton(Transform parent, string name, string label, Color color)
        {
            GameObject go = CreateUi(name, parent);
            Image img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = true;   // دکمه باید کلیک‌پذیر باشد

            LayoutElement le = go.AddComponent<LayoutElement>();
            le.minHeight = 44;
            le.preferredHeight = 44;

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            ColorBlock cb = btn.colors;
            cb.highlightedColor = color * 1.15f;
            cb.pressedColor = color * 0.82f;
            btn.colors = cb;

            TextMeshProUGUI tmp = CreateTmp(go.transform, "Label", label, 16, TextAlignmentOptions.Center);
            tmp.color = Color.white;
            SetStretch(tmp.rectTransform, 4f);   // برچسب کل دکمه را بپوشاند
            return btn;
        }

        static Toggle CreateToggle(Transform parent, string name, string label)
        {
            GameObject go = CreateUi(name, parent);
            LayoutElement le = go.AddComponent<LayoutElement>();
            le.minHeight = 34;
            le.preferredHeight = 34;

            Toggle toggle = go.AddComponent<Toggle>();

            GameObject bg = CreateUi("Background", go.transform);
            Image bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0.35f, 0.35f, 0.42f, 1f);
            RectTransform bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(1f, 0.5f);
            bgRt.anchorMax = new Vector2(1f, 0.5f);
            bgRt.pivot = new Vector2(1f, 0.5f);
            bgRt.sizeDelta = new Vector2(26f, 26f);
            bgRt.anchoredPosition = Vector2.zero;

            GameObject check = CreateUi("Checkmark", bg.transform);
            Image checkImg = check.AddComponent<Image>();
            checkImg.color = new Color(0.25f, 0.85f, 0.40f, 1f);
            SetStretch(check.GetComponent<RectTransform>(), 3f);

            toggle.targetGraphic = bgImg;
            toggle.graphic = checkImg;

            TextMeshProUGUI labelText = CreateTmp(go.transform, "Label", label, 16, TextAlignmentOptions.MidlineRight);
            RectTransform labelRt = labelText.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(4f, 0f);
            labelRt.offsetMax = new Vector2(-34f, 0f);   // جا برای چک‌باکس سمت راست
            labelRt.localScale = Vector3.one;
            return toggle;
        }

        static RectTransform CreateScrollContent(Transform parent, string scrollName)
        {
            GameObject scrollGo = CreateUi(scrollName, parent);
            LayoutElement scrollLe = scrollGo.AddComponent<LayoutElement>();
            scrollLe.minHeight = 200;
            scrollLe.preferredHeight = 240;

            Image scrollBg = scrollGo.AddComponent<Image>();
            scrollBg.color = new Color(0f, 0f, 0f, 0.30f);

            ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            GameObject viewport = CreateUi("Viewport", scrollGo.transform);
            Image vpImg = viewport.AddComponent<Image>();
            vpImg.color = new Color(1f, 1f, 1f, 0.02f);
            Mask mask = viewport.AddComponent<Mask>();
            mask.showMaskGraphic = false;
            SetStretch(viewport.GetComponent<RectTransform>(), 2f);

            GameObject contentGo = CreateUi("Content", viewport.transform);
            RectTransform content = contentGo.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);

            VerticalLayoutGroup vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(4, 4, 4, 4);
            vlg.spacing = 6;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;    // کارت‌ها ارتفاع محتوای خودشان را بگیرند
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = content;
            return content;
        }

        static void SetStretch(RectTransform rt, float pad)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
            rt.localScale = Vector3.one;
        }

        /// <summary>
        /// ارتفاع واقعی سکشن را محاسبه و در sizeDelta.y می‌نویسد.
        /// پنل VLG با childControlHeight=false کار می‌کند (طبق spec)؛ پس ارتفاع
        /// هر سکشن باید صریح باشد تا سکشن بعدی روی قبلی نیفتد.
        /// </summary>
        static void FinalizeSection(GameObject section)
        {
            RectTransform rt = section.GetComponent<RectTransform>();
            bool wasActive = section.activeSelf;
            if (!wasActive) section.SetActive(true);   // تا layout قابل محاسبه باشد

            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

            VerticalLayoutGroup vlg = section.GetComponent<VerticalLayoutGroup>();
            float sum = 0f;
            int activeCount = 0;
            foreach (Transform child in rt)
            {
                if (!child.gameObject.activeSelf) continue;
                RectTransform crt = child as RectTransform;
                if (crt == null) continue;
                sum += crt.rect.height;
                activeCount++;
            }

            float h = sum;
            if (vlg != null)
            {
                if (activeCount > 1) h += vlg.spacing * (activeCount - 1);
                if (vlg.padding != null) h += vlg.padding.top + vlg.padding.bottom;
            }

            LayoutElement le = section.GetComponent<LayoutElement>();
            if (le != null) h = Mathf.Max(h, le.minHeight);

            rt.sizeDelta = new Vector2(rt.sizeDelta.x, Mathf.Ceil(h));

            if (!wasActive) section.SetActive(false);
        }

        /// <summary>QR مربعی ۱۲۰px — داخل QrRow (بدون LayoutGroup) تا کشیده نشود.</summary>
        static RawImage CreateQrImage(Transform parent, string name, float size)
        {
            GameObject go = CreateUi(name, parent);
            RawImage raw = go.AddComponent<RawImage>();
            raw.color = Color.white;
            raw.raycastTarget = false;

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;
            return raw;
        }

        // =====================================================================
        // Avatar
        // =====================================================================

        /// <summary>
        /// اولویت آواتار: ۱) انتخاب فعلی کاربر در Hierarchy  ۲) Tag=Player  ۳) نام‌های رایج
        /// هیچ آبجکتی خودکار ساخته نمی‌شود؛ اگر پیدا نشد به کاربر اطلاع می‌دهیم.
        /// </summary>
        static Transform ResolveAvatar()
        {
            // ۱) انتخاب فعلی کاربر در ادیتور
            GameObject selected = Selection.activeGameObject;
            if (selected != null && selected.scene.IsValid())
            {
                TrySetPlayerTag(selected);
                Debug.Log("[متارنگ] آواتار از انتخاب شما: " + selected.name);
                return selected.transform;
            }

            // ۲) Tag = Player
            try
            {
                GameObject tagged = GameObject.FindGameObjectWithTag("Player");
                if (tagged != null)
                {
                    Debug.Log("[متارنگ] آواتار با تگ Player پیدا شد: " + tagged.name);
                    return tagged.transform;
                }
            }
            catch { /* تگ Player تعریف نشده */ }

            // ۳) نام‌های رایج
            string[] names = { AvatarName, "Player", "Avatar", "LocalPlayer", "XR Origin", "XR Rig" };
            for (int i = 0; i < names.Length; i++)
            {
                GameObject go = GameObject.Find(names[i]);
                if (go != null)
                {
                    TrySetPlayerTag(go);
                    Debug.Log("[متارنگ] آواتار با نام «" + names[i] + "» پیدا شد.");
                    return go.transform;
                }
            }

            // ۴) نساز — به کاربر بگو اول Player را انتخاب کند
            EditorUtility.DisplayDialog(
                "آواتار پیدا نشد",
                "لطفاً ابتدا آبجکت Player را در Hierarchy انتخاب کنید، سپس دوباره Tool را اجرا کنید.\n\n" +
                "اگر آبجکت Player ندارید، یک کپسول بسازید، نامش را Player بگذارید و انتخاب کنید.",
                "باشه");
            Debug.LogWarning("[متارنگ] آواتار پیدا نشد — آبجکت Player را انتخاب کنید و دوباره Tool را بزنید.");
            return null;
        }

        static void TrySetPlayerTag(GameObject go)
        {
            try
            {
                go.tag = "Player";
                return;
            }
            catch { }

            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return;

            SerializedObject tagSo = new SerializedObject(assets[0]);
            SerializedProperty tags = tagSo.FindProperty("tags");
            bool exists = false;
            for (int i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == "Player")
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
            {
                tags.InsertArrayElementAtIndex(tags.arraySize);
                tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = "Player";
                tagSo.ApplyModifiedProperties();
            }

            try { go.tag = "Player"; }
            catch { /* ignore */ }
        }

        // =====================================================================
        // PositionCard Prefab
        // =====================================================================

        static GameObject EnsureCardPrefab()
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null && CardPrefabIsBound(existing)) return existing;

            EnsureFolders();

            GameObject card = new GameObject("PositionCard", typeof(RectTransform));
            Image img = card.AddComponent<Image>();
            img.color = new Color(0.18f, 0.20f, 0.25f, 0.95f);

            RectTransform rt = card.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(340f, 120f);

            VerticalLayoutGroup vlg = card.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.spacing = 4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;    // کارت با محتوا رشد کند (حالت ویرایش)
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            RTLTextMeshPro idLabel = CreateRtlTmp(card.transform, "IdLabel", "pos_xxxxxx", 14, TextAlignmentOptions.MidlineRight);
            RTLTextMeshPro posLabel = CreateRtlTmp(card.transform, "PosLabel", "X:0 Y:0 Z:0", 14, TextAlignmentOptions.MidlineRight);
            RTLTextMeshPro dateLabel = CreateRtlTmp(card.transform, "DateLabel", "", 12, TextAlignmentOptions.MidlineRight);

            Button editBtn = CreateButton(card.transform, "EditButton", "ویرایش", new Color(0.25f, 0.45f, 0.75f));
            Button confirm = CreateButton(card.transform, "ConfirmButton", "تأیید", new Color(0.20f, 0.65f, 0.35f));
            confirm.gameObject.SetActive(false);
            Button cancel = CreateButton(card.transform, "CancelButton", "انصراف", new Color(0.50f, 0.30f, 0.30f));
            cancel.gameObject.SetActive(false);

            GameObject editRow = CreateUi("EditRow", card.transform);
            editRow.SetActive(false);
            VerticalLayoutGroup ev = editRow.AddComponent<VerticalLayoutGroup>();
            ev.padding = new RectOffset(0, 0, 0, 0);
            ev.spacing = 6;
            ev.childAlignment = TextAnchor.UpperCenter;
            ev.childControlWidth = true;
            ev.childControlHeight = true;
            ev.childForceExpandWidth = true;
            ev.childForceExpandHeight = false;

            // هر فیلد با لیبل خودش: «نام»، «X»، «Y»، «Z» و چرخش «Rx»، «Ry»، «Rz»
            TMP_InputField nameField = CreateLabeledField(editRow.transform, "NameRow", "نام", "NameField", "نام موقعیت");
            TMP_InputField xField = CreateLabeledField(editRow.transform, "XRow", "X", "XField", "0");
            TMP_InputField yField = CreateLabeledField(editRow.transform, "YRow", "Y", "YField", "0");
            TMP_InputField zField = CreateLabeledField(editRow.transform, "ZRow", "Z", "ZField", "0");
            TMP_InputField rxField = CreateLabeledField(editRow.transform, "RxRow", "Rx", "RxField", "0");
            TMP_InputField ryField = CreateLabeledField(editRow.transform, "RyRow", "Ry", "RyField", "0");
            TMP_InputField rzField = CreateLabeledField(editRow.transform, "RzRow", "Rz", "RzField", "0");

            System.Type cardType = FindCardType();
            if (cardType == null)
            {
                UnityEngine.Object.DestroyImmediate(card);
                Debug.LogError("[MetaRange] PositionCardUI پیدا نشد — اول پروژه را کامپایل کنید.");
                return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            }

            Component comp = card.AddComponent(cardType);
            var so = new SerializedObject(comp);
            SetRef(so, "idLabel", idLabel);
            SetRef(so, "posLabel", posLabel);
            SetRef(so, "dateLabel", dateLabel);
            SetRef(so, "editButton", editBtn);
            SetRef(so, "editRow", editRow);
            SetRef(so, "nameField", nameField);
            SetRef(so, "xField", xField);
            SetRef(so, "yField", yField);
            SetRef(so, "zField", zField);
            SetRef(so, "rxField", rxField);
            SetRef(so, "ryField", ryField);
            SetRef(so, "rzField", rzField);
            SetRef(so, "confirmButton", confirm);
            SetRef(so, "cancelButton", cancel);
            SetBool(so, "moveAvatarOnConfirm", true);   // بعد از ویرایش، آواتار جابه‌جا شود
            so.ApplyModifiedPropertiesWithoutUndo();
            LogNulls(so, "PositionCardUI");

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
                AssetDatabase.DeleteAsset(PrefabPath);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(card, PrefabPath);
            UnityEngine.Object.DestroyImmediate(card);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return prefab;
        }

        static System.Type FindCardType()
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType("MetaRange.Avatar.PositionCardUI");
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>نوع بریج ادغام با Network_A (اختیاری)</summary>
        static System.Type FindBridgeType()
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType("MetaRange.Avatar.MetaRangeNetworkSpawnBridge");
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>نوع بریج دکمهٔ محیط لابی (اختیاری)</summary>
        static System.Type FindLobbyBridgeType()
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType("MetaRange.Avatar.MetaRangeLobbyEnvironmentBridge");
                if (t != null) return t;
            }
            return null;
        }

        // =====================================================================
        // نگهبان قرارداد Network_A — اگر تیم شبکه امضایی را عوض کرد، لوگراهگی نشود
        // =====================================================================

        [MenuItem("Tools/متارنگ/بررسی قرارداد Network_A (gRPC)")]
        public static void VerifyNetworkContract()
        {
            // (نوع، نام عضو، پارامترها، باید استاتیک باشد؟)
            var required = new System.Collections.Generic.List<object[]>
            {
                new object[] { "MetaverseNetworkClient",      "TryGetLocalPlayer",       new string[]{"MetaverseNetworkIdentity&"}, true },
                new object[] { "MetaverseNetworkClient",      "isReady",                 new string[0],                  true },
                new object[] { "MetaverseNetworkClient",      "userId",                  new string[0],                  true },
                new object[] { "MetaverseNetworkClient",      "playerId",                new string[0],                  true },
                new object[] { "MetaverseSpawnManager",       "GetSpawnedObjects",       new string[0],                  false },
                new object[] { "MetaverseNetworkIdentity",    "get_IsLocalPlayer",       new string[0],                  false },
                new object[] { "MetaverseNetworkIdentity",    "get_IsLocalOwner",        new string[0],                  false },
                new object[] { "MetaverseNetworkIdentity",    "get_HasAuthority",        new string[0],                  false },
                new object[] { "MetaverseNetworkIdentity",    "get_NetId",               new string[0],                  false },
            };

            int ok = 0, missing = 0;
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== قرارداد MetaRange ⇄ Network_A ===");

            // پیدا کردن کلاس‌ها در اسمبلی‌های بارگذاری‌شده یا در سورس پروژه
            foreach (object[] row in required)
            {
                string typeName = (string)row[0];
                string member = (string)row[1];
                string[] paramTypes = (string[])row[2];
                bool wantStatic = (bool)row[3];

                string detail;
                if (FindTypeOrSource(typeName, member, paramTypes, wantStatic, out detail))
                {
                    ok++;
                    report.AppendLine("  OK    " + typeName + "." + member + "  " + detail);
                }
                else
                {
                    missing++;
                    report.AppendLine("  MISS  " + typeName + "." + member + "  ← " + detail);
                }
            }

            report.AppendLine("نتیجه: " + ok + " OK / " + missing + " MISS");
            if (missing == 0)
                report.AppendLine("✓ بریج می‌تواند local player شبکه را پیدا کند.");
            else
                report.AppendLine("✗ قرارداد ناسازگار است — MetaRange به حالت fallback می‌رود. " +
                                   "نسخهٔ Network_A را با برنج gRPC هم‌تراز کنید.");
            report.AppendLine(MetaverseNetworkHooks.Describe());

            Debug.Log(report.ToString());
        }

        /// <summary>عضو موردنیاز را در اسمبلی‌ها یا (در صورت کامپایل نشدن) در سورس پروژه پیدا می‌کند</summary>
        static bool FindTypeOrSource(string typeName, string member, string[] paramTypes,
                                     bool wantStatic, out string detail)
        {
            System.Type t = null;
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(typeName);
                if (t != null) break;
            }

            if (t != null)
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;

                if (member.StartsWith("get_"))
                {
                    System.Reflection.PropertyInfo pi = t.GetProperty(member.Substring(4), flags);
                    detail = "property (کلاس کامپایل‌شده)";
                    if (pi == null) { detail = "property یافت نشد در کلاس کامپایل‌شده"; return false; }
                    return true;
                }

                System.Reflection.MethodInfo[] all = t.GetMethods(flags);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].Name != member) continue;
                    if (wantStatic && !all[i].IsStatic) continue;
                    detail = all[i].ReturnType.Name + "(" + all[i].GetParameters().Length + " پارامتر)";
                    return true;
                }
                detail = "متد یافت نشد در کلاس کامپایل‌شده";
                return false;
            }

            // کلاس کامپایل نشده ⇒ جست‌وجوی متنی در سورس پروژه (حالت Editor که Network_A هنوز کامپایل نشده)
            string[] roots = { "Assets/Scripts/Network_A", "Assets/Scripts" };
            foreach (string root in roots)
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                string[] guids = AssetDatabase.FindAssets("t:TextAsset", new string[] { root });
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (!path.EndsWith(".cs")) continue;
                    string text = System.IO.File.ReadAllText(path);
                    if (text.IndexOf("class " + typeName, StringComparison.Ordinal) < 0 &&
                        text.IndexOf(typeName, StringComparison.Ordinal) < 0) continue;
                    if (text.IndexOf(member, StringComparison.Ordinal) < 0) continue;
                    detail = "در سورس یافت شد: " + path;
                    return true;
                }
            }

            detail = "کلاس/عضو یافت نشد (نه در اسمبلی، نه در سورس)";
            return false;
        }

        static bool CardPrefabIsBound(GameObject prefab)
        {
            System.Type cardType = FindCardType();
            if (cardType == null) return false;

            Component comp = prefab.GetComponent(cardType);
            if (comp == null) return false;

            var so = new SerializedObject(comp);
            string[] required = {
                "idLabel", "posLabel", "dateLabel", "editButton", "editRow",
                "nameField", "xField", "yField", "zField", "rxField", "ryField", "rzField",
                "confirmButton", "cancelButton"
            };
            for (int i = 0; i < required.Length; i++)
            {
                SerializedProperty p = so.FindProperty(required[i]);
                if (p == null || p.objectReferenceValue == null) return false;
            }

            // پریفب‌های قدیمی (childControlHeight=false) بازسازی شوند تا چیدمان جدید اعمال شود
            VerticalLayoutGroup vlg = prefab.GetComponent<VerticalLayoutGroup>();
            if (vlg == null || !vlg.childControlHeight) return false;

            // پریفب‌هایی که لیبل فیلدها را ندارند بازسازی شوند
            if (!CardHasFieldLabels(prefab)) return false;

            // پریفب‌هایی که «انتقال آواتار» خاموش دارند بازسازی شوند (تا ویرایش، آواتار را جابه‌جا کند)
            SerializedProperty moveProp = so.FindProperty("moveAvatarOnConfirm");
            if (moveProp != null && !moveProp.boolValue) return false;

            return true;
        }

        /// <summary>بررسی وجود لیبل‌های نام/X/Y/Z و چرخش Rx/Ry/Rz در کارت (برای تشخیص پریفب قدیمی)</summary>
        static bool CardHasFieldLabels(GameObject prefab)
        {
            string[] required = {
                "NameRowLabel", "XRowLabel", "YRowLabel", "ZRowLabel",
                "RxRowLabel", "RyRowLabel", "RzRowLabel"
            };
            var all = prefab.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < required.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < all.Length; j++)
                {
                    if (all[j].name == required[i]) { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }
    }
}
#endif