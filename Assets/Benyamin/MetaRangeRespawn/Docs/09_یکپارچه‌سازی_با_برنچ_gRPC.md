# ۰۹ — یکپارچه‌سازی با برنج `gRPC` (Network_A / Dedicated) — v3.7.0

این سند توضیح می‌دهد MetaRangeRespawn چگونه با سیستم شبکهٔ آنلاین (`Network_A`) **همکاری** می‌کند.

> **اصل بنیادی:** MetaRange **جایگزین** `MetaverseSpawnManager` نیست.
> MetaRange فقط **نقطهٔ شروع (spawn point)** را از سرور Node می‌آورد؛ ساخت و مالکیت آواتار آنلاین
> همیشه از مسیر Network_A / Dedicated انجام می‌شود.

---

## ۱) نقشهٔ کد شبکه (کد واقعی برنج `gRPC`)

| فایل | نقش | چیزی که به ما می‌دهد |
|------|-----|----------------------|
| `Assets/Scripts/Network_A/MetaverseDedicatedServer/Spawn/MetaverseSpawnManager.cs` | ساخت/نابودی آبجکت‌های شبکه | `static MetaverseSpawnManager.Instance` · `event ClientObjectSpawned` · `GetSpawnedObjects()` |
| `…/Spawn/MetaverseSpawnNetworkBridge.cs` | پیام spawn/despawn/snapshot روی WS | `OutboundMessageReady` |
| `…/Protocol/MetaverseSpawnPayload.cs` | پیام spawn | `position` · `rotation` · `localPlayer` · `ownerUserId/ownerPlayerId` · `roomId` · `prefabId` |
| `…/Core/MetaverseNetworkIdentity.cs` | هویت شبکهٔ هر آبجکت | `IsLocalPlayer` · `IsLocalOwner` · `HasAuthority` · `NetId` · `RoomId` |
| `…/PlayerObject/MetaverseNetworkPlayerObjectServer.cs` | ساخت player object در سرور | `SpawnPlayerObject(session, out identity)` |
| `…/PlayerMovement/MetaverseNetworkPlayerMovementBridge.cs` | **حرکت authority-محور** | `SendOwnerInput(...)` → سرور: `transform.position += move * speed * dt` |
| `…/StateSync/MetaverseNetworkStateSyncBridge.cs` | همگام‌سازی ترنسفرم | `SendNetworkTransform` · کلاینت در `ApplyNetworkTransform` مقدار محلی را **بازنویسی می‌کند** |
| `…/NetworkBehaviour/MetaverseNetworkClient.cs` | کلاینت | `static TryGetLocalPlayer(out identity)` · `isReady` · `userId` · `playerId` |
| `…/Spawn/MetaverseSpawnSystemInstaller.cs` | نصب سیستم | `InstallFromResources()` |

> این کلاس‌ها در **فضای‌نام سراسری** هستند (بدون `namespace`).

---

## ۲) جریان runtime

```
① لینک بازیکن:  https://dev-world-3d.metarang.com/game?env=test_webgl&spawn=ورودی_شمالی
        │
② MetaRangeNetworkSpawnBridge (متارنج)
        │  SpawnFromURL.TryGetUrlParams(env, spawn)      ← همان پارسر URL قبلی
        │  GET http://localhost:3000/api/get-position?env=&spawn=
        │  ⇒ فقط position + rotation  (ساخت آواتار با شبکه است، نه با متارنج)
        │
③ انتظار برای local player شبکه  (pollInterval / waitTimeoutSeconds)
        │  ① MetaverseNetworkClient.TryGetLocalPlayer(out identity)
        │  ② fallback: MetaverseSpawnManager.Instance.GetSpawnedObjects()
        │              با IsLocalPlayer و سپس IsLocalOwner
        │  ③ fallback آفلاین: تگ Player / نام‌های رایج
        │
④ TryApplyPose(transform, isNetworkPlayer: true)
        │  • CharacterController موقتاً خاموش
        │  • سرعت Rigidbody صفر
        │  • SetPositionAndRotation (position + rotation)
        │  • OwnerPanel.SetAvatar(transform)  ← مالک از همین آواتار ثبت می‌کند
        │
⑤ پنجرهٔ authority-guard (reassertSeconds)
        │  اگر سرور snap-back کرد ⇒ پوز دوباره assert می‌شود
        │  اگر بازیکن از پوز دور شد (giveUpDriftMeters) ⇒ دست برداشته می‌شود
```

### ترتیب دقیق رویدادها

| ترتیب | اتفاق | لاگ |
|-------|--------|-----|
| 1 | `Awake` بریج ⇒ `MetaRangeNetworkSpawnBridge.IsActive = true` | — |
| 2 | `SpawnFromURL.FetchSpawn` می‌بیند بریج فعال است ⇒ **نمی‌آید آواتار آفلاین را جابه‌جا کند** | `بریج شبکه فعال است` |
| 3 | بریج پوز را از Node می‌گیرد | `پوز دریافت شد | pos=… rot=…` |
| 4 | هر ~۰.۲۵ ثانیه دنبال local player می‌گردد | — |
| 5 | به‌محض آماده شدن ⇒ اعمال پوز + bind به OwnerPanel | `پوز اعمال شد | player=… pos=…` |
| 6 | هشدار محدودیت authority | `هشدار authority: سرور gRPC مالک حرکت است…` |

---

## ۳) فایل‌های اضافه/تغییرکرده (فقط داخل پکیج)

| فایل | تغییر |
|------|-------|
| `Scripts/MetaRangeNetworkSpawnBridge.cs` | **جدید** — `MetaverseNetworkHooks` (دسترسی reflection) + کلاس بریج |
| `Scripts/SpawnFromURL.cs` | اگر بریج فعال باشد، جابه‌جایی آفلاین انجام نمی‌دهد |
| `Scripts/OwnerPanel.cs` | `EnsureAvatar`: اولویت ① local player شبکه، بعد ② تگ `Player`، بعد ③ نام‌های رایج |
| `Editor/MetaRangeRespawnSetupTool.cs` | نصب خودکار بریج روی `MetaRange_SpawnSystem` |
| `Docs/09_یکپارچه‌سازی_با_برنچ_gRPC.md` | **جدید** — همین سند |

**هیچ فایلی از `Assets/Scripts/Network_A/` تغییر نکرده است.**

---

## ۴) چرا Reflection و نه ارجاع مستقیم؟

`Network_A` فقط روی برنج شبکه وجود دارد. اگر `MetaRangeNetworkSpawnBridge` مستقیماً
`MetaverseNetworkClient` را صدا می‌زد، در پروژه‌های بدون شبکه کامپایل نمی‌شد.
بنابراین کلاس `MetaverseNetworkHooks` با `Assembly.GetType(name)` نام کلاس‌ها را پیدا می‌کند:

| نام جست‌وجوشده | معادل در برنج gRPC |
|----------------|-------------------|
| `MetaverseNetworkClient` | `TryGetLocalPlayer` · `isReady` |
| `MetaverseSpawnManager` | `Instance` (property یا field) · `GetSpawnedObjects()` |
| `MetaverseNetworkIdentity` | `IsLocalPlayer` · `IsLocalOwner` |

اگر این کلاس‌ها نباشند: `Available == false`، همه‌چیز `false` برمی‌گردد، و سیستم بی‌صدا به
**حالت آفلاین قبلی** برمی‌گردد (بدون exception).

---

## ۵) محدودیت‌ها و ریسک‌ها (صادقانه)

| موضوع | وضعیت |
|-------|-------|
| **authority سرور** | روی `gRPC` **سرور** مالک حرکت است: `identity.transform.position += move * speed * dt` و سپس `SendNetworkTransform` می‌فرستد. کلاینت در `ApplyNetworkTransform` مقدار محلی را بازنویسی می‌کند ⇒ **تلپورتِ کلاینت‌ساید ماندگار نیست** |
| راهکار فعلی | `reassertSeconds` (پیش‌فرض ۲.۵ ثانیه) پوز را در برابر snap-back دوباره اعمال می‌کند؛ بعد از آن بازیکن آزاد است حرکت کند |
| راهکار درست (خارج از محدودهٔ این پکیج) | پاس دادن پوز به سرور هنگام join و استفاده از آن در `MetaverseNetworkPlayerObjectServer.SpawnPlayerObject(session, out identity)` ⇒ جای‌گذاری **دائمی و authority-محور** |
| تأخیر اسپان شبکه | اگر local player بعد از `waitTimeoutSeconds` ساخته نشود ⇒ پوز اعمال نمی‌شود و لاگ هشدار می‌دهد |
| تداخل با snapshot | با پنجرهٔ reassert کم می‌شود؛ برای محیط چندنفره ممکن است بازیکن دیگری چند فریم جابه‌جایی ببیند |
| WebGL | `try/catch` دور کلیهٔ کارها ⇒ در نبود مرورگر/کلیپ‌بورد فقط پیام می‌دهد، کرش نمی‌کند |
| Without link | هیچ درخواستی زده نمی‌شود؛ رفتار شبکهٔ قبلی دست‌نخورده می‌ماند (تست رگرسیون) |

---

## ۶) تست دستی (در پروژهٔ برنج `gRPC`)

```text
۱) سرور Node بالا:  cd metaverse-spawn-server && node index.js
     ⇒ GET http://localhost:3000/api/health ⇒ {"ok":true,…}

۲) یونیتی: Tools ▸ متارنج ▸ راه‌اندازی سیستم ریسپان آواتار
     ⇒ کنسول: «بریج شبکه (Network_A) به MetaRange_SpawnSystem اضافه شد.»

۳) Play ⇒ کنسول:
     [MetaRangeNetworkSpawnBridge] شروع | Network_A موجود | isReady=False | localPlayer=False
     (اگر نوشت Network_A یافت نشد ⇒ برنج شبکه نیست یا Network_A کامپایل نشده)

۴) پنل چپ: محیط بساز (مثلاً test_webgl)

۵) پنل راست: تیک «مالک هستم»
     ⇒ باید مختصات *آواتار شبکه* را نشان دهد، نه کپسول آفلاین:
        [OwnerPanel] آواتار = local player شبکه (OwnerToggle): <نام آبجکت>

۶) بازیکن را جابه‌جا کن ⇒ «ثبت موقعیت» ⇒ لینک را کپی کن

۷) همان لینک را در یک کلاینت/مرورگر دیگر باز کن (با احراز هویت متفاوت)
     کنسول انتظار:
       [MetaRangeNetworkSpawnBridge] پوز دریافت شد | env=test_webgl spawn=… | pos=…
       [MetaRangeNetworkSpawnBridge] پوز اعمال شد | player=… | pos=… rot=…
       [MetaRangeNetworkSpawnBridge] هشدار authority: …
       [MetaRangeNetworkSpawnBridge] بازیکن از پوز دور شد (drift=…) ⇒ دست از assert برداشته شد

۸) بازیکن دوم با لینکِ موقعیت دیگر ⇒ باید روی نقطهٔ خودش ظاهر شود

۹) بدون لینک ⇒ هیچ پیام جدیدی از بریج، و رفتار شبکهٔ قبلی سالم
```

لاگ تشخیصی مفید:

```csharp
Debug.Log(MetaverseNetworkHooks.Describe());
// نمونه: "Network_A موجود | isReady=True | localPlayer=True"
```

---

## ۷) API عمومی بریج

| عضو | کاربرد |
|-----|--------|
| `static bool MetaRangeNetworkSpawnBridge.IsActive` | `SpawnFromURL` با آن تداخل را تشخیص می‌دهد |
| `bool HasPose` / `Vector3 PosePosition` / `Quaternion PoseRotation` | وضعیت پوز خوانده‌شده از Node |
| `bool IsApplied` | آیا پوز روی آواتار اعمال شده |
| `bool TryApplyPose(Transform, bool isNetworkPlayer)` | اعمال دستی (همان هستهٔ متد `ReapplyNow`) |
| `bool ReapplyNow()` | دوباره بردن بازیکن به نقطهٔ ثبت‌شده (مناسب دکمهٔ «برو به نقطهٔ من») |
| `void SetPoseExternal(Vector3, Quaternion)` | پوز از منبع دیگر (مثلاً سرور شبکه) |
| `static bool MetaverseNetworkHooks.LocalPlayerReady` | آیا local player شبکه آماده است |
| `static string MetaverseNetworkHooks.Describe()` | خلاصهٔ وضعیت برای لاگ |

---

## ۸) تست خودکار انجام‌شده

| تست | نتیجه |
|------|-------|
| `bridgetest` — قرارداد reflection با نام‌ها/امضاهای واقعی gRPC + `TryApplyPose` + `ReassertPose` (کد واقعی استخراج‌شده) | **۲۵/۲۵ PASS** |
| رگرسیون `rotationtest` / `uitest` / `spawntest` / `linktest` | ۳۷ · ۲۰ · ۱۱ · ۳۳ PASS |
| کامپایل پکیج (`csc`) | بدون error |
| API سرور | بدون تغییر؛ `health` و `create-env`/`add-position`/`get-position` PASS |

مواردی که **بدون** اجرای واقعی شبکه قابل تست نبودند و نیاز به تست دستی دارند:
واقعی `TryGetLocalPlayer` روی کلاینت زنده · رفتار snap-back سرور در لحظهٔ join · چندنفره بودن.
---

## ۹) اتصال دکمهٔ محیط لابی (`Lobby 1 WebGL`) به متارنج

### ۹.۱ منطق موجود دکمه (بدون تغییر در Network_A)

```
Lobby 1 WebGL.unity
└ Lobby_1_Realtime_Scene_Controller      → Lobby1RealtimeSceneController
   ├ roomListContent   = Canvas_RoomList / Scroll View / Viewport / Content
   └ roomListItemPrefab = Room_List_Prefab_Ui
        └ ریشه: CompletedBuildingRoomListItemView  (roomButton → Button فرزند، buildingCodeText → TMP فرزند)

Realtime_Room_Game_Server_Manager        → RealtimeRoomGameServerManager
   ├ GET CompletedBuildingsUrl?page=N                → CompletedBuildingDto
   ├ OnBuildingsUpdated  → ساخت/بازسازی دکمه‌ها در Content
   └ کلیک → EnterBuildingRoomAsync
        ├ ResolveBuildingRoomAsync: رومی با roomName == کد ساختمان، وگرنه CreateRoom
        ├ JoinRoomReliableAsync
        └ NotifyRoomJoined → OnRoomJoinedFor3D(roomId)

Dedicated_Game_Server_Client             → DedicatedGameServerRealtimeRoomBinderWebGL
   └ OnRoomJoinedFor3D → اتصال Game Server → LoadSceneAsync("WebGL_Enviroment")
```

نکتهٔ کلیدی: **نام محیط = `CompletedBuildingDto.feature_properties_id` (کد ساختمان)**
و پس از ورود، `RealtimeRoomGameServerManager.CurrentRoomName` دقیقاً همان کد است.

### ۹.۲ بریج جدید: `MetaRangeLobbyEnvironmentBridge`

| مرحله | کار |
|-------|-----|
| ۱ | به رویداد استاتیک `OnRoomJoinedFor3D` (از راه reflection) گوش می‌دهد |
| ۲ | `env` = `CurrentRoomName` (کد ساختمان) |
| ۳ | `GET /api/list-positions?env=<کد ساختمان>` و مرتب‌سازی کلیدها با `StringComparer.Ordinal` |
| ۴ | انتخاب نقطه: `PerUserStableHash` (هش پایدار `userId`) یا `FirstAvailable` |
| ۵ | `GET /api/get-position?env=&spawn=` (منبع حقیقت؛ در ۴۰۴ از مقدار همان لیست استفاده می‌شود) |
| ۶ | `MetaRangeNetworkSpawnBridge.ApplyPoseWhenPlayerReady(...)` ⇒ اعمال روی local player شبکه |

**ساخت یا جابه‌جایی آواتار در این کلاس انجام نمی‌شود.** آن کار فقط با Network_A است.

### ۹.۳ رفتار در نبود داده

| وضعیت | رفتار |
|-------|-------|
| محیط در متارنج نیست (۴۰۴) | هیچ جابه‌جایی؛ فقط لاگ هشدار (`skipWhenEnvironmentMissing=true`) |
| نام اتاق خالی و `roomId` هم خالی | `LastError = environment_name_unresolved` و هیچ کاری |
| `userId` خالی | هش روی `SystemInfo.deviceUniqueIdentifier` |
| خروج از اتاق | `OnRoomLeftFor3D` ⇒ پاک شدن وضعیت تا ورود بعدی تازه محاسبه شود |

### ۹.۴ عمر در صحنه

لابی با `LoadSceneMode.Single` به `WebGL_Enviroment` می‌رود، پس نمونهٔ بریج باید
`DontDestroyOnLoad` باشد. به همین دلیل یک `RuntimeInitializeOnLoadMethod(BeforeSceneLoad)`
دارد که اگر نمونه‌ای در صحنه نباشد، یک ریشهٔ دائمی می‌سازد.

### ۹.۵ بررسی سریع

```
Tools ▸ متارنج ▸ بررسی اتصال لابی (Lobby 1 WebGL)
```

جدول `OK / MISS` برای کلاس‌های لابی، رویدادها، `CurrentRoomName` و `userId` +
خلاصهٔ `LobbyNetworkHooks.Describe()`.

### ۹.۶ تست خودکار

`lobbytest` — کد واقعی `MetaRangeSpawnList` + `LobbyNetworkHooks` + کل کلاس بریج،
با فیک‌هایی که نام و امضای آن‌ها عیناً مطابق `gRPC` است:
پارسر تک‌نقطه/چندنقطه/کلید فارسی/مقدار ناشناخته/ورودی خراب · خواندن `CurrentRoomName`
· جریان کامل کلیک تا تحویل پوز به بریج شبکه · ۴۰۴ · خروج از اتاق · پایداری هش و پخش کاربران.

> این هارنس یک **باگ واقعی** در پارسر پیدا کرد: کلاس‌های `}` پایانی مصرف نمی‌شدند
> (برای یک نقطه تصادفی درست بود، برای چند نقطه نقطه‌های بعدی خوانده نمی‌شدند). اصلاح شد.