# CHANGELOG — سیستم مدیریت موقعیت ریسپان آواتار (متارنج)

نسخهٔ جاری: **v3.9.5**

---

## v3.9.0 - دکمهٔ محیط لابی ⇒ create-env ⇒ Context قفل ⇒ اسپان
- صحنه‌های قطعی کشف و در Tools ثبت شد: لابی `Lobby 1 WebGL` / محیط `WebGL_Enviroment`
  (منبع: `DedicatedGameServerRealtimeRoomBinderWebGL.cs:19-20`).
- `EnvironmentCreator`:
  - `NormalizeEnvironmentName(string)` جدید — هم‌راستا با `safeName` سرور
    (لاتین/رقم/`-`/`_` + بازهٔ فارسی U+0600-U+06FF، حذف فاصله و نیم‌فاصله، بریدن تا ۶۴).
  - `EnsureEnvironment(server, envName, reply)` جدید — `POST /api/create-env` با نام صریح؛
    `201` و `409` هر دو موفق‌اند (idempotent)، `400` شکست با Context دست‌نخورده.
  - `LockStoredName(string)` جدید — قفل کردن Context مشترک روی env لابی.
  - overload قدیمی `EnsureEnvironment(server, reply)` بدون تغییر باقی ماند (سازگاری OwnerPanel).
- `MetaRangeLobbyEnvironmentBridge`: `HandleRoomJoined` حالا نام دکمه را نرمال می‌کند،
  محیط را روی سرور لوکال تضمین می‌کند، `Context.env` را قفل می‌کند و بعد سراغ
  `list-positions` → انتخاب نقطه → `get-position` → `ApplyPoseWhenPlayerReady` می‌رود.
  لیست خالی ⇒ فقط Context قفل می‌شود و خطایی داده نمی‌شود.
- Tools: لاگ راه‌اندازی نام صحنهٔ فعال/لابی/محیط و Context را نشان می‌دهد؛
  منوی «بررسی اتصال لابی» بخش صحنه‌ها و Context را اضافه کرد؛
  منوی جدید «نمایش Context محیط فعلی».
- تست جدید `envtest` (۳۲ assertion): نرمال‌سازی، Ensure با ۲۰۱/۴۰۹/۴۰۰، نام خالی
  بدون درخواست، Lock/Clear، و سازگاری overload قدیمی.
- `Docs/10_جریان_لابی_تا_لینک.md` افزوده شد.
- **صریح:** سرور لوکال = JSON و اسپان. حرکت و authority آواتار = سرور شبکهٔ آنلاین.
  هیچ فایلی از `Assets/Scripts/Network_A` تغییر نکرد.
### v3.9.4 — اسکرول کل پنل مالک (رفع خروج لیست از صفحه)
- **مشکل:** بعد از ثبت موقعیت، بخش «لینک و QR» باز می‌شد و بخش
  «موقعیت‌های ذخیره‌شده» را از صفحه هل می‌داد بیرون ⇒ هیچ راهی برای دیدن/ویرایش
  کارت‌ها نبود.
- **رفع:** `MetaRangeRootPanel` حالا یک **viewport اسکرول‌شونده** است:
  - خودِ پنل: `ScrollRect` عمودی (`Clamped`) + `Mask` (بریدن محتوای اضافه)
  - فرزند جدید `PanelContent`: `VerticalLayoutGroup` + `ContentSizeFitter(vertical = PreferredSize)`
    ⇒ ارتفاع محتوا با بزرگ‌شدن بخش‌ها رشد می‌کند و کل پنل اسکرول می‌خورد.
  - همهٔ بخش‌ها (`HeaderTitle`, `Section_Owner`, `Section_Result`, `Section_PositionList`)
    حالا فرزند `PanelContent` هستند، نه خود پنل.
  - نوار اسکرول عمودی باریک در **لبهٔ چپ** پنل (تا متن RTL را نپوشاند)،
    `AutoHide` و `scrollSensitivity = 40`.
- اسکرول داخلی کارت‌ها (`PositionsScroll`) با ارتفاع ثابت ۲۶۰–۳۲۰ سر جایش ماند،
  پس ویرایش کارت‌ها مثل قبل کار می‌کند.
- ارتفاع دکمه‌ها (۶۴) و تیک (۴۸) و فونت‌های فارسی RTL **بدون تغییر** ماند.
- **منطق ثبت موقعیت، ساخت لینک، QR و ویرایش کارت‌ها اصلاً تغییر نکرد**
  (`OwnerPanel.cs` و `PositionCardUI.cs` دست‌نخورده‌اند؛ فقط `MetaRangeRespawnSetupTool.cs` layout).
- **صریح:** هیچ فایلی از `Assets/Scripts/Network_A` تغییر نکرد.
### v3.9.5 — تغییر پایهٔ لینک به دامنهٔ dev
- `playBaseUrl` از `https://metarange.adfam.com/play` به **`https://dev-world-3d.metarang.com/game`** تغییر کرد.
  لینک خروجی: `https://dev-world-3d.metarang.com/game?env=<env>&spawn=<id>`
- در **دو جا** ست شد (لازم است، چون مقدار قبلی در صحنه serialize شده بود):
  1. `OwnerPanel.playBaseUrl` — پیش‌فرض فیلد
  2. `MetaRangeRespawnSetupTool.PlayBaseUrl` — که با `SetString` هنگام اجرای
     «Tools ▸ متارنج ▸ راه‌اندازی سیستم ریسپان آواتار» مقدارِ Inspector را هم به‌روز می‌کند.
  ⇒ **بدون اجرای دوبارهٔ Tool، مقدارِ ذخیره‌شدهٔ صحنه همچنان آدرس قدیمی می‌ماند.**
- `ShowResult`: اسلش انتهاییِ پایه یک‌بار حذف می‌شود ⇒ `game/?env=…` و نه `game//?env=…`.
- ساخت لینک، نام فیلدها، QR و رفتار کپی/دانلود **بدون تغییر** ماند.
- آدرس‌های نمونه در مستندات به‌روز شد.

## v3.8.0 - اتصال دکمهٔ محیط لابی به متارنج
- فایل جدید `Scripts/MetaRangeLobbyEnvironmentBridge.cs`:
  - `MetaRangeSpawnList`: پارسر مستقل `GET /api/list-positions` (چون `JsonUtility` دیکشنری نمی‌خواند).
  - `LobbyNetworkHooks`: دسترسی اختیاری از راه reflection به `OnRoomJoinedFor3D` / `OnRoomLeftFor3D` / `CurrentRoomName` / `CurrentRoomId` / `IsJoinedRoom` / `MetaverseNetworkClient.userId`.
  - `MetaRangeLobbyEnvironmentBridge`: `env` = کد ساختمان (`CurrentRoomName`) ⇒ انتخاب نقطه ⇒ `ApplyPoseWhenPlayerReady`. خودش `DontDestroyOnLoad` می‌شود چون لابی با `LoadSceneMode.Single` به صحنهٔ گیم‌پلی می‌رود.
- `MetaRangeNetworkSpawnBridge`: متدهای عمومی `ApplyPoseWhenPlayerReady` و `ApplyPoseNowIfPossible` + قفل `applyRoutineRunning`.
- `MetaRangeRespawnSetupTool`: نصب و bind خودکار بریج لابی + منوی «بررسی اتصال لابی (Lobby 1 WebGL)».
- **باگ رفع‌شده:** پارسر لیست موقعیت‌ها کلید بستن `}` را مصرف نمی‌کرد ⇒ با بیش از یک نقطه فقط اولین نقطه خوانده می‌شد.
- `Network_A` دست‌نخورده (فقط reflection برای اتصال).
## v3.7.0 - ادغام با برنج gRPC (Network_A / Dedicated)
- فایل جدید `Scripts/MetaRangeNetworkSpawnBridge.cs`: کلاس `MetaverseNetworkHooks` (دسترسی اختیاری از راه reflection به `MetaverseNetworkClient.TryGetLocalPlayer`، `MetaverseSpawnManager.Instance.GetSpawnedObjects()`، `MetaverseNetworkIdentity.IsLocalPlayer/IsLocalOwner`) + کلاس بریج.
- جریان: URL ⇒ `GET /api/get-position` (فقط مختصات) ⇒ انتظار local player شبکه ⇒ `TryApplyPose` روی همان Transform موجود (بدون ساخت آواتار دوم) ⇒ bind به `OwnerPanel`.
- `SpawnFromURL` وقتی بریج فعال است دیگر آواتار آفلاین را جابه‌جا نمی‌کند.
- `OwnerPanel.EnsureAvatar`: اولویت ① local player شبکه ② تگ `Player` ③ نام‌های رایج.
- Tool بریج را خودکار روی `MetaRange_SpawnSystem` نصب و bind می‌کند.
- صادقانه: روی گِرَپ‌سی سرور برای حرکت authority دارد، پس جای‌گذاری **best-effort** است و پنجرهٔ reassert (پیش‌فرض ۲.۵ ثانیه) snap-back را خنثی می‌کند.
- `Docs/09_یکپارچه‌سازی_با_برنچ_gRPC.md` افزوده شد. **هیچ فایلی از `Assets/Scripts/Network_A/` تغییر نکرد.**
- تست: `bridgetest` ۲۵/۲۵ PASS (با فیک‌هایی که نام و امضای آن‌ها عیناً مطابق گِرَپ‌سی است) + رگرسیون‌ها سبز.
## v3.6.0 - چرخش زنده در ویرایش کارت (Live Rotation)
- سه فیلد چرخش `Rx/Ry/Rz` (درجه، از `avatar.eulerAngles`) با لیبل اختصاصی به کارت ویرایش اضافه شد؛ تا وقتی `isEditing` است هر فریم زنده به‌روز می‌شوند (بدون شبکه).
- با «تأیید»، مقدار فیلدها با `ParseFloat` خوانده و `Quaternion.Euler` ساخته و همراه `position` در `PUT /api/update-position` ذخیره می‌شود؛ `MoveAvatarTo` نیز موقعیت **و چرخش** را اعمال می‌کند.
- محافظ‌های قبلی حفظ شد: `isFocused` (تایپ کاربر پاک نشود)، `isBusy` (فریز هنگام ذخیره)، انصراف بدون ذخیره، و ترتیب `rename` سپس `update`.
- پریفب قدیمی (بدون فیلد چرخش) خودکار با `EnsureCardPrefab` بازسازی می‌شود؛ در نبود فیلدها، چرخش ثبت‌شدهٔ قبلی حفظ می‌گردد.
- سرور بدون تغییر (ساختار `rotation{x,y,z,w}` از قبل وجود داشت) و ثبت موقعیت جدید همچنان `avatar.rotation` را می‌فرستد.
- تست: **۳۷ assertion** با کد واقعی (`PullFromAvatar`, `SetField`, `ConfirmRoutine`, `ReadRotation`, …) ⇒ همه PASS.

## v3.5.0 - کپی لینک + دانلود لینک متنی (Link Copy / TXT Download)
در کنار QR دو دکمهٔ جدید اضافه شد (بدون تغییر در منطق ثبت/ویرایش/سرور):

| دکمه | رفتار |
|------|-------|
| **کپی لینک** | `GUIUtility.systemCopyBuffer = link` ⇒ پیام سبز «لینک کپی شد»؛ در صورت خطا (WebGL/مرورگر) پیام «دستی انتخاب کنید» + `LogWarning` بدون کرش |
| **دانلود لینک** | فایل `spawn_{env}_{positionId}.txt` شامل لینک در **یک خط** (UTF-8 بدون BOM)؛ ادیتور: `SaveFilePanel` (لغو ⇒ «ذخیره لغو شد») و Build: `Application.persistentDataPath` |

- نام فایل با `SanitizePositionName` پاک‌سازی و به ۴۰ کاراکتر محدود می‌شود ⇒ بدون کاراکتر غیرمجاز ویندوز؛ اگر `env` خالی باشد `env` جایگزین می‌شود.
- پیام‌های وضعیت در `linkStatusText` نمایش داده می‌شود: آماده / کپی شد / ذخیره شد / خطا در ذخیره فایل.
- تا وقتی لینکی ساخته نشده، هر دو دکمه **غیرفعال** هستند (`UpdateLinkButtonsState`) و پیام «ابتدا یک موقعیت ثبت کنید» نشان داده می‌شود.
- UI: ردیف جدید `LinkButtons` (کنار `DownloadQrButton`) + `LinkStatusText` در `Section_Result`.
- تست: **۳۶ assertion** با کد واقعی (`OnCopyLink`, `OnDownloadLink`, `UpdateLinkButtonsState`, `SetLinkStatus`, `ShowResult`) در دو شاخهٔ `runtime` و `UNITY_EDITOR` ⇒ همه PASS.

## v3.4.2 - رفع دو باگ QA
### 🐛 دو باگ رفع‌شده در QA (تست end-to-end)
1. **`ParseFloat` با کاما/ممیز عربی**: کاراکترهای «،» (U+060C) و «٫» (U+066B) در کد به `?` تبدیل شده بودند
   ⇒ `ParseFloat("1,5")` صفر برمی‌گرداند و مختصات اشتباه ذخیره می‌شد.
   **رفع:** بازنویسی با escape یونیکد (`\u066B`/`\u060C`) + نادیده‌گرفتن فاصله/نیم‌فاصله؛
   تست: `1.5` ✓ `1,5` ✓ `1٫5` ✓ `12,50` ✓
2. **`editingCard` نمونه‌ای بود**: فقط در همان نمونهٔ `OwnerPanel` نگهداری می‌شد ⇒ اگر دو کارت
   نمونه‌های متفاوت داشته باشند، کارت اول در حالت ویرایش می‌ماند.
   **رفع:** `private static PositionCardUI editingCard` + پاک‌سازی در `ClearCards()`.
   تست: با ویرایش کارت B، کارت A بسته می‌شود ✓

### 🧪 گزارش QA (v3.4.2)
| حوزه | نتیجه |
|-------|--------|
| API سرور (۲۷ سناریو) | **۲۶ PASS / ۱ غیرمرتبط** (آن یکی خطای خودِ اسکریپت تست بود، نه سرور) |
| رفتار UI با کد واقعی (۱۹ سناریو) | **۱۹ PASS** |
| SpawnFromURL با کد واقعی (۱۱ سناریو) | **۱۱ PASS** |
| رگرسیون‌ها | ۴ از ۴ تأیید شد |
| کامپایل | `csc exit: 0` |

---

## v3.2 تا v3.4.1 — نام یونیک، لیبل فیلدها، پیش‌نمایش زنده و اتصال آواتار

> بخش‌های زیر به‌ترتیبِ اضافه‌شدن در نسخه‌های `v3.2`، `v3.3`، `v3.4.0` و `v3.4.1` نوشته شده‌اند و تاریخچهٔ کامل حفظ شده است.

| نسخه | خلاصهٔ تغییر |
|-----|-------------|
| `v3.4.0` | رفع بریده‌شدن متن «موقعیت زنده» (ارتفاع ثابت ۷۸)، لیبل‌های `X`/`Y`/`Z` در کارت، اتصال خودکار آواتار (`EnsureAvatar` + `ContextMenu`)، پارسر عمومی `ParseDict` |
| `v3.4.1` | **پیش‌نمایش زندهٔ X/Y/Z در کارتِ در حال ویرایش** — هر فریم از بازیکن، بدون شبکه، با محافظ `isBusy`/`isFocused` |
| `v3.3` | متن زندهٔ سه‌خطی + چرخش، فرمت `InvariantCulture` |
| `v3.2` | نام یونیک موقعیت، `rename-position`، انتقال آواتار پس از ویرایش، QR محلی سرور |
### 👁️ پیش‌نمایش زندهٔ کارتِ در حال ویرایش (Live Edit Preview)
- `PositionCardUI` یک `Update()` دارد: تا وقتی `isEditing` است، X/Y/Z **هر فریم** از
  `owner.AvatarPosition` خوانده می‌شود (بدون هیچ درخواست شبکه).
- `OnEdit` ⇒ `isEditing=true` + `SetMode(true)` + `PullFromAvatar()`
- `OnConfirm` ⇒ `isEditing=false` و بعد فقط از مقدار داخل Inputها `PUT` می‌رود
- `OnCancel` / `ExitEditMode` ⇒ `isEditing=false` و بازگشت به مقادیر قبلی (بدون ذخیره)
- دو محافظ: (۱) هنگام ذخیره‌سازی (`isBusy`) فیلدها ثابت می‌مانند
  (۲) فیلدی که کاربر در آن تایپ می‌کند (`isFocused`) بازنویسی نمی‌شود
- پیام وضعیت: «ویرایش: فیلدها زنده از موقعیت بازیکن — ذخیره فقط با «تأیید»»

### 🐛 رفع: متن «موقعیت زنده» بریده/خالی دیده می‌شد
- **علت:** در v3.3 متن زنده سه‌خطی شد ولی ارتفاعش (۲۹px از LayoutElement) تغییر نکرد؛
  نتیجه: فقط خط اول دیده می‌شد و چون `avatar` نامعتبر بود، پیام خطا هم بریده می‌شد ⇒
  عملاً متن خالی به نظر می‌رسید.
- **رفع:** `SetFixedHeight(livePos, 78f)` در Tool ⇒ ارتفاع صریح (LayoutElement + RectTransform).
- `Update` مقاوم‌تر شد: وضعیت از `ownerToggle.isOn` خوانده می‌شود (نه پروندهٔ داخلی)،
  فرمت با `InvariantCulture` (همیشه رقم لاتین و نقطهٔ اعشار) و **نام آواتار در خط اول**.
- تشخیص آواتار گسترش یافت: تگ `Player` سپس نام‌های رایج (`Player`, `MetaRangeAvatar`, …)؛
  در صورت نیافتن، فهرست آبجکت‌های ریشهٔ صحنه در Console چاپ می‌شود تا علت فوراً معلوم شود.
  لاگ‌ها: `آواتار خودکار وصل شد (Update): Player | موقعیت: (51.1, -37.9, 0.0)` یا
  `آواتار پیدا نشد (Update) …` به‌همراه `آبجکت‌های ریشهٔ صحنه: …`
- کارت‌های ویرایش همچنان از `owner.AvatarPosition` پر می‌شوند (و اگر آواتار نبود، از دادهٔ سرور).

### 🏷️ لیبل برای فیلدهای حالت ویرایش کارت
- هر `TMP_InputField` در حالت ویرایش حالا **لیبل اختصاصی** دارد: «نام»، «X»، «Y»، «Z»
- چیدمان: هر فیلد در ردیف مستقل (لیبل راست‌چین با عرض ثابت ۵۲px + فیلد انعطاف‌پذیر)
- لیبل‌ها `RTLTextMeshPro` با `FontStyles.Bold` و رنگ عنوان سکشن
- `EditRow` از `HorizontalLayoutGroup` به `VerticalLayoutGroup` تغییر کرد (۴ ردیف)
- پریفب قدیمی (بدون لیبل) با بررسی `CardHasFieldLabels()` خودکار بازسازی می‌شود
- **منطق ویرایش/تأیید تغییری نکرد** — فقط UI لیبل اضافه شد

### ❤️ اتصال مطمئن آواتار برای «موقعیت زنده»
- `EnsureAvatar(reason)`: اگر `avatar` در زمان اجرا نامعتبر شد (حذف/تعویض صحنه توسط بازی)،
  خودکار با تگ `Player` دوباره پیدا می‌کند و لاگ می‌دهد — صدا زده می‌شود در `Start`،
  هنگام تیک «مالک هستم» و در `Update`.
- `SetAvatar(Transform)` برای اتصال دستی از اسکریپت بازی
- `RebindAvatarByTag()` با `[ContextMenu]` برای اتصال از Inspector
- متن زنده اکنون ۴ خط و خوانا است (موقعیت + چرخش در خط جدا) و اگر آواتار نباشد
  صریحاً «آواتار پیدا نشد (tag=Player)» نشان می‌دهد.

### 🐛 رفع باگ: نام‌های دلخواه در لیست نمایش داده نمی‌شدند
- **علت:** `OwnerPanel.ParseDict` فقط کلیدهایی را پیدا می‌کرد که با `"pos_` شروع شوند.
  از آنجا که اکنون نام موقعیت را **کاربر تعیین می‌کند** (مثلاً `موقعیت_3` یا `LRUDJ_4`)،
  پارسر هیچ‌کدام را نمی‌دید و لیست خالی می‌ماند — با اینکه داده در JSON و سرور موجود بود.
- **رفع:** پارسر بازنویسی شد و حالا **هر نام کلیدی** را می‌خواند (فارسی یا لاتین، با/بدون `pos_`)،
  همراه با هندل کردن escape و عمق‌شماری رشته‌ها؛ حالت `{"positions":{...}}` هم پشتیبانی می‌شود.
- **تست:** کد پارسر به‌صورت مستقل استخراج و روی پاسخ واقعی سرور اجرا شد:
  `موقعیت_3` و `LRUDJ_4` ⇒ **۲ آیتم**؛ لیست خالی ⇒ ۰؛ پاسخ wrapped ⇒ ۲؛ کلید قدیمی `pos_` ⇒ ۱.

### 🖼️ QR روی سرور خودمان (Local QR Generator) — رفع قطعی مشکل QR
- endpoint جدید: `GET /api/qr?data=<لینک>` که با ماژول `qrcode` (نصب‌شده در همین سرور)
  تصویر PNG می‌سازد: **۵۱۲px، حاشیهٔ ۲، تصحیح خطای M** و پشتیبانی کامل از متن فارسی.
- در `OwnerPanel` منبع اول QR حالا سرور خودماست (`useLocalServerQr = true`)،
  و سرویس‌های عمومی فقط **fallback** هستند.
- مزیت: بدون اینترنت، بدون بلاک/ریت‌لیمیت سرویس‌های ثالث، و کار با نام‌های فارسی.
- علت خطای قبلی: درخواست از داخل Unity توسط سرویس‌های عمومی پذیرفته نمی‌شد
  (`Access denied`) در حالی که همان درخواست با curl `200` می‌داد.
- تست زنده: `GET /api/qr` با لینک فارسی ⇒ `200 image/png` با امضای PNG معتبر (۳۹۴۴ بایت)؛
  بدون `data` ⇒ `400`.

### 🏷️ ویرایش نام موقعیت در حالت Edit (Rename)
- نام موقعیت **در حالت ویرایش نمایش داده می‌شود** (قبلاً پنهان بود) و با فیلد `NameField` قابل تغییر است.
- endpoint جدید سرور: `PUT /api/rename-position` با بدنهٔ
  `{ environmentName, positionId, newPositionId }`
  → `200` (تغییر انجام شد) | `200 renamed:false` (نام یکسان) | `400` (نام نامعتبر) | `404` (پیدا نشد) | `409` (تکراری)
- مختصات و `createdAt` حفظ می‌شوند و `updatedAt` به‌روز می‌گردد؛ ترتیب کلیدهای JSON هم حفظ می‌شود.
- جریان کارت ویرایش (`ConfirmRoutine`): ① در صورت تغییر نام → rename ② سپس update مختصات
  ③ در صورت خطای ۴۰۹ کارت در حالت ویرایش می‌ماند. دکمهٔ «تأیید» هنگام ذخیره‌سازی قفل می‌شود (`isBusy`).
- بعد از ذخیره، لیست دوباره ساخته می‌شود ⇒ **نام و مختصات جدید در کارت دیده می‌شوند**.
- **هشدار:** لینک قبلی پس از تغییر نام دیگر کار نمی‌کند (`404 Position not found`).
- تست زندهٔ سناریوی rename: **۱۳/۱۳ PASS**


### 🏷️ نام یونیک برای هر موقعیت (Unique Position Name)
- فیلد جدید `PositionNameInput` در پنل راست: کاربر برای هر موقعیت یک **نام یونیک** می‌نویسد
  (مثال: `ورودی اصلی سالن` یا `start_point`). خالی ⇒ نام خودکار `pos_xxxxxx`.
- `OwnerPanel.SanitizePositionName()`: فقط حروف (لاتین/فارسی)، عدد، `_` و `-`؛ فاصله→`_`؛ حداکثر ۴۰ کاراکتر.
- بررسی تکراری بودن **قبل از ارسال** با `HashSet knownIds` (پیام قرمز «نام تکراری است»)؛
  سرور هم با `409 Position already exists` محافظت می‌کند.
- همین نام **کلید JSON** و بخش `spawn` لینک بازیکن است — پس URL خوانا و یونیک می‌شود.
- پیام وضعیت زیر دکمهٔ ثبت (`RegisterStatusText`): «در حال ثبت…»، «نام تکراری است»،
  «اول از پنل چپ…»، «ویرایش ذخیره شد: …».

### 🚶 انتقال آواتار پس از ویرایش (Avatar Move on Edit) — رفع باگ
- `OwnerPanel.MoveAvatarTo(pos, rot)` اضافه شد:
  - `CharacterController` موقتاً خاموش و دوباره فعال می‌شود (وگرنه موقعیت را برمی‌گرداند)
  - `Rigidbody`: صفر کردن `linearVelocity`/`angularVelocity` و سپس `MovePosition`
  - سپس `SetPositionAndRotation` و **لاگ قطعی**: `آواتار منتقل شد → (x, y, z)`
- پریفب کارت با `moveAvatarOnConfirm = true` بازسازی می‌شود؛ اگر پریفب قدیمی این پرچم
  خاموش را داشته باشد، Tool آن را تشخیص داده و بازسازی می‌کند.

### 🎯 انتخاب تک‌کارت در حالت ویرایش
- فقط **یک** کارت هم‌زمان در حالت ویرایش است: `NotifyEditStarted/NotifyEditEnded` + `ExitEditMode()`.
- کارتِ در حال ویرایش **هایلایت آبی** می‌گیرد (تغییر رنگ `Image` کارت).

### 🔢 خواندن اعداد مستقل از locale
- `OwnerPanel.ParseFloat()` جایگزین `float.TryParse` شد تا `1.5`، `1٫5` (ممیز فارسی)
  و `1,5` (کاما در برخی زبان‌ها) همگی درست خوانده شوند.

### 🧪 تست شده
- سناریوی نام یونیک روی سرور زنده: **۱۰/۱۰ PASS** (ساخت با نام فارسی، تکراری ⇒ ۴۰۹،
  ویرایش PUT، کلید فارسی در لیست، اسپان با نام فارسی)
- کامپایل یونیتی: **۰ خطا، ۰ هشدار**

---

## v3.1 — دو پنل و مستند یکپارچه‌سازی

### 🖼️ بازگشت دکمهٔ «ساخت محیط» به پنل چپ جدا (Create Environment Panel)
- ساخت فایل JSON محیط دوباره به UI برگشت، اما این‌بار **جدا از پنل مالک**:
  - **پنل چپ:** `MetaRange_CreateEnvCanvas` (Canvas مستقل، `sortingOrder=100`) → `MetaRange_LeftCreatePanel`
    شامل: عنوان، `EnvNameInput` (خالی = نام خودکار `env_yyyyMMdd_HHmmss`)، دکمهٔ «ساخت فایل محیط»، پیام نتیجه
  - **پنل راست:** `MetaRangeRootPanel` — فقط مالک (تیک، موقعیت زنده، ثبت، لینک/QR، لیست)
- دکمهٔ «ثبت موقعیت» **دیگر محیط نمی‌سازد**. اگر محیط ساخته نشده باشد:
  هشدار واضح «ابتدا از پنل چپ «ساخت فایل محیط» را بزنید» و **هیچ درخواستی ارسال نمی‌شود**.
- `EnvironmentCreator` دوباره به‌صورت کامل bind می‌شود (`nameInput`, `createButton`, `resultText`).

### 🔌 هوک‌های یکپارچه‌سازی با برنامهٔ اصلی (Integration Hooks)
- `SpawnFromURL` اکنون API عمومی دارد:
  - `static bool TryGetUrlParams(out string env, out string spawn)` — خواندن پارامترهای لینک
  - `bool ApplySpawnFromUrl()` — اعمال اسپان از URL جاری (برای بازی‌هایی با Scene Loader)
  - `Coroutine ApplySpawn(string env, string spawn)` — اسپان با مقادیر مشخص
  - `static string GetCurrentUrl()` — منبع URL (WebGL / آرگومان `url=…`)
- لاگ‌ها واضح‌تر شدند: نبود پارامتر ⇒ هشدار + اسپان در موقعیت پیش‌فرض.

### 🔗 پایداری سرویس QR (QR Resilience)
- خطای `Access denied` سرویس عمومی QR رفع شد:
  - زنجیرهٔ fallback: `api.qrserver.com` → `quickchart.io`
  - هر دو قالب در Inspector قابل تغییرند (`qrPrimaryTemplate` / `qrFallbackTemplate` با توکن `{DATA}`)
  - ارسال `User-Agent` مرورگرگونه (بعضی سرویس‌ها درخواست غیرمرورگری را رد می‌کنند)
  - لاگ دقیق: `responseCode` + متن پاسخ سرور برای هر تلاش
  - در صورت ناموفقیت هر دو، یادآوری می‌شود که **لینک متنی معتبر است**

### 📄 مستند یکپارچه‌سازی جدید
- `Docs/05_یکپارچه‌سازی_با_برنامه_اصلی.md` — قرارداد Player/Tag/Spawn، الگوی اتصال به Scene Loader،
  hook‌ها، تنظیمات مشترک و چک‌لیست تحویل به تیم بازی.

### 🛡️ ترمیم خودکار محیط گمشده (Self-healing)
- اگر `add-position` خطای `404 Environment not found` بدهد (سرور ری‌استارت/داده پاک شده):
  `ClearStoredName()` → ساخت مجدد همان نام → **یک‌بار تلاش مجدد**.

### 🧹 رفع هم‌پوشانی UI (Overlap Fix)
- `Section_Result` فرزند مستقیم پنل است (نه داخل `Section_Owner`).
- `FinalizeSection` ارتفاع واقعی هر سکشن را محاسبه می‌کند.
- پنل چپ ارتفاع **خودکار** (`ContentSizeFitter.PreferredSize`) دارد.
- `ScrollRect` فقط داخل `Section_PositionList`؛ دکمهٔ ثبت همیشه کلیک‌پذیر.

### 🇮🇷 پشتیبانی RTL فارسی و خوانایی
- همهٔ متن‌ها `RTLTextMeshPro` (`Farsi`, `ForceFix`, `PreserveNumbers`, `FixTags`).
- اندازه‌ها: عنوان ۲۰pt بولد، عنوان سکشن ۱۶pt، متن ۱۳–۱۵pt، دکمه ۱۶pt؛ دکمه ۴۴px، تیک ۳۴px، ورودی ۴۰px، اسکرول ۲۴۰px.
- کنتراست بالا روی پس‌زمینهٔ تیره، `spacing=14`, `padding=16`.

### 🌐 سرور dual-stack + لاگ
- `app.listen(PORT)` بدون host → روی `::` (IPv6 + IPv4): `localhost` و `127.0.0.1` هر دو کار می‌کنند.
- لاگ هر درخواست در کنسول و `logs/server.log`؛ CORS کامل + پاسخ `204` به `OPTIONS`.
- `GET /api/health` برای بررسی سلامت.

### 📦 بسته‌بندی و مستندات
- ساختار: `Assets/Benyamin/MetaRangeRespawn/{Editor,Scripts,Prefabs,Docs}`.
- منو: **Tools ▸ متارنج ▸ راه‌اندازی سیستم ریسپان آواتار**.
- مستندات دوزبانه: `README`, `01` معماری، `02` نصب، `03` مالک، `04` لینک و اسپان،
  `05` یکپارچه‌سازی، `06` مرجع API، `07` مرجع کد، `08` عیب‌یابی، `CHANGELOG`.

---

## نسخه‌های پیشین (خلاصه)

| نسخه | تغییر کلیدی |
|------|-------------|
| v3.0 | دکمهٔ ثبت به‌صورت خودکار محیط می‌ساخت (این رفتار در v3.1 به پنل چپ منتقل شد) |
| v2.x | پنل RTL فارسی، انتخاب خودکار آواتار، ساخت پریفب کارت توسط Tool |
| v1.x | اسکلت اولیه: سرور Express + ثبت/دریافت موقعیت |

---

## جدول تغییرات کلیدی

| موضوع | v3.0 | v3.1 |
|-------|------|------|
| ساخت محیط | خودکار داخل دکمهٔ ثبت | **دکمهٔ اختصاصی در پنل چپ جدا** |
| تعداد پنل | یک پنل | **دو پنل** (چپ: محیط / راست: مالک) |
| ثبت بدون محیط | محیط خودکار ساخته می‌شد | **پیام راهنما، بدون درخواست** |
| Canvas | یک Canvas | Canvas اصلی + **Canvas مستقل چپ** (`sortingOrder=100`) |
| اسپان از لینک | فقط `Start()` | **`Start()` + hook‌های عمومی برای Scene Loader** |
| محیط گمشده روی سرور | retry | retry + **بازسازی با همان نام** |
| مستندات | ۴ فایل | **۹ فایل شامل قرارداد یکپارچه‌سازی** |