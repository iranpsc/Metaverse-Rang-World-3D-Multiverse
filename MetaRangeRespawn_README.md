# متارنج — سیستم مدیریت موقعیت ریسپان آواتار (MetaRangeRespawn)

**برنچ:** `feature/metarange-respawn`  
**مسیر پکیج:** `Assets/Benyamin/MetaRangeRespawn`  
**فضای‌نام:** `MetaRange.Avatar`  
**سرور همراه:** `metaverse-spawn-server` (نسخه API ‏۳٫۱)  
**پایهٔ بازی (WebGL):** `https://dev-world-3d.metarang.com/game/`

---

## فهرست مطالب

1. [این پکیج چیست؟](#1-این-پکیج-چیست)
2. [معماری کلی](#2-معماری-کلی)
3. [ساختار پوشه‌ها و فایل‌ها](#3-ساختار-پوشه‌ها-و-فایل‌ها)
4. [پیش‌نیازها](#4-پیش‌نیازها)
5. [نصب و راه‌اندازی سریع](#5-نصب-و-راه‌اندازی-سریع)
6. [تنظیم یک‌بارهٔ آدرس‌ها (MetaRangeConfig)](#6-تنظیم-یک‌بارهٔ-آدرس‌ها-metarangeconfig)
7. [جریان کامل کار مالک](#7-جریان-کامل-کار-مالک)
8. [جریان بازیکن و لینک اسپان](#8-جریان-بازیکن-و-لینک-اسپان)
9. [فرمت لینک و URL در WebGL](#9-فرمت-لینک-و-url-در-webgl)
10. [مرجع اسکریپت‌های یونیتی](#10-مرجع-اسکریپت‌های-یونیتی)
11. [مرجع API سرور Node](#11-مرجع-api-سرور-node)
12. [ساختار فایل JSON محیط](#12-ساختار-فایل-json-محیط)
13. [یکپارچه‌سازی با Network_A / gRPC](#13-یکپارچه‌سازی-با-network_a--grpc)
14. [پنل‌ها و UI](#14-پنل‌ها-و-ui)
15. [WebGL، HTTPS و Mixed Content](#15-webgl-https-و-mixed-content)
16. [عیب‌یابی رایج](#16-عیب‌یابی-رایج)
17. [محدودیت‌ها و نکات صادقانه](#17-محدودیت‌ها-و-نکات-صادقانه)
18. [چک‌لیست تحویل](#18-چک‌لیست-تحویل)

---

## ۱. این پکیج چیست؟

پکیج **MetaRangeRespawn** به مالک محیط اجازه می‌دهد:

1. یک **فایل JSON محیط** بسازد  
2. موقعیت و چرخش فعلی آواتار را با **نام یکتا** ثبت کند  
3. **لینک یکتا + QR Code** بگیرد  
4. موقعیت‌های قبلی را **ویرایش** کند  

هر بازیکنی که لینک را باز کند، آواتارش دقیقاً در همان مختصات و زاویه **اسپان** می‌شود.

```
مالک ──ثبت──► سرور Node (JSON) ──لینک──► بازیکن ──خواندن URL──► GET موقعیت ──► Spawn
```

---

## ۲. معماری کلی

```
┌─────────────────────────────────────────────────────────────┐
│  Unity (Editor / WebGL / Native)                            │
│  ┌─────────────────┐  ┌──────────────────────────────────┐  │
│  │ EnvironmentCreator│  │ OwnerPanel (ثبت، لیست، لینک/QR) │  │
│  └────────┬────────┘  └───────────────┬──────────────────┘  │
│           │                           │                     │
│  ┌────────▼───────────────────────────▼──────────────────┐  │
│  │ MetaRangeConfig  (یک منبع برای serverUrl / playBaseUrl)│  │
│  └────────────────────────┬──────────────────────────────┘  │
│                           │ HTTP(S)                         │
│  ┌────────────────────────▼──────────────────────────────┐  │
│  │ SpawnFromURL  +  MetaRangeNetworkSpawnBridge          │  │
│  │  Application.absoluteURL → env + spawn → API → Pose   │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────┬───────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│  metaverse-spawn-server (Node.js + Express)                 │
│  data/environments/<name>.json                              │
│  POST create-env / add-position                             │
│  GET  get-position / list-positions / qr / health           │
│  PUT  update-position / rename-position                     │
└─────────────────────────────────────────────────────────────┘
```

- **یونیتی** فقط UI و کلاینت HTTP است.  
- **Node** منبع حقیقت موقعیت‌هاست (فایل JSON روی دیسک).  
- **Network_A** اختیاری است؛ با Reflection به local player وصل می‌شود و آواتار دوم ساخته نمی‌شود.

---

## ۳. ساختار پوشه‌ها و فایل‌ها

```
Assets/Benyamin/MetaRangeRespawn/
├── README.md                          ← همین مستند (یا نسخهٔ کوتاه)
├── Scripts/
│   ├── MetaRangeConfig.cs             ← منبع واحد serverUrl و playBaseUrl
│   ├── MetaRangeModels.cs             ← مدل‌های JSON (PositionEntry و …)
│   ├── EnvironmentCreator.cs          ← ساخت فایل محیط (پنل چپ)
│   ├── OwnerPanel.cs                  ← پنل مالک: ثبت، لیست، لینک، QR
│   ├── PositionCardUI.cs              ← کارت هر موقعیت (نمایش + ویرایش)
│   ├── SpawnFromURL.cs                ← خواندن env/spawn از URL و اسپان آفلاین
│   ├── MetaRangeNetworkSpawnBridge.cs ← بریج Network_A + Resolve پوز
│   └── MetaRangeLobbyEnvironmentBridge.cs  ← اتصال به جریان لابی
├── Editor/
│   └── MetaRangeRespawnSetupTool.cs   ← Tools ▸ متارنج ▸ راه‌اندازی…
├── Prefabs/
│   └── PositionCard.prefab
└── Docs/
    ├── README.md
    ├── 01 … 10 (معماری، نصب، مالک، لینک، API، کد، عیب‌یابی، gRPC، لابی)
    └── CHANGELOG.md

metaverse-spawn-server/                ← ریشهٔ ریپو (خارج از Assets)
├── index.js
├── package.json
├── data/environments/                 ← فایل‌های JSON محیط
└── logs/
```

---

## ۴. پیش‌نیازها

| مورد | توضیح |
|------|--------|
| Unity 6 | پروژهٔ اصلی متارنج |
| `com.nosuchstudio.rtltmpro` | متن فارسی RTL |
| فونت TMP فارسی | مثلاً Vazir / Lalezar |
| Node.js 18+ | برای سرور ریسپان |
| npm: `express`, `cors`, `qrcode` | وابستگی‌های سرور |

`Network_A` **اجباری نیست**. بدون آن حالت آفلاین کار می‌کند؛ با آن اسپان روی local player شبکه اعمال می‌شود.

---

## ۵. نصب و راه‌اندازی سریع

### ۵.۱ سرور

```bash
cd metaverse-spawn-server
npm install
node index.js
# خروجی نمونه: متارنج v3.1 — سرور ریسپان آواتار روی پورت 3000
```

سلامت:

```bash
curl http://localhost:3000/api/health
```

### ۵.۲ یونیتی

1. برنچ `feature/metarange-respawn` را بگیرید.  
2. در Hierarchy آبجکت Player (یا صحنهٔ بازی) را انتخاب کنید.  
3. منو: **Tools ▸ متارنج ▸ راه‌اندازی سیستم ریسپان آواتار**  
4. در صحنه یک **MetaRangeConfig** بسازید / ست کنید (بخش ۶).  
5. Play.

### ۵.۳ جریان تست یک‌دقیقه‌ای

1. پنل چپ → نام محیط → **ساخت فایل محیط**  
2. پنل راست → تیک **مالک هستم** → (اختیاری) نام موقعیت → **ثبت موقعیت**  
3. **کپی لینک** / دانلود QR  
4. لینک را با همان `env` و `spawn` در Editor با `url=...` یا در WebGL باز کنید و اسپان را ببینید.

---

## ۶. تنظیم یک‌بارهٔ آدرس‌ها (MetaRangeConfig)

قبلاً `serverUrl` در چند کامپوننت تکرار می‌شد و یکی جا می‌ماند. حالا باید **یک جا** ست شود.

| فیلد | مثال Editor | مثال Production WebGL |
|------|-------------|------------------------|
| **ServerUrl** | `http://127.0.0.1:3000` | `https://…` (حتماً HTTPS زیر صفحهٔ HTTPS) |
| **PlayBaseUrl** | `https://dev-world-3d.metarang.com/game` | همان |

همهٔ اسکریپت‌ها (`EnvironmentCreator`, `OwnerPanel`, `SpawnFromURL`, بریج) از `MetaRangeConfig` می‌خوانند.

> **هشدار Mixed Content:** صفحهٔ `https://dev-world-3d.metarang.com/game/` نمی‌تواند به `http://IP:4000` درخواست بزند. API باید روی HTTPS (ترجیحاً همان دامنه با reverse proxy) باشد.

---

## ۷. جریان کامل کار مالک

```
۱) ساخت محیط     → POST /api/create-env
۲) تیک «مالک هستم» → Resolve آواتار (شبکه یا آفلاین) + نمایش موقعیت زنده
۳) ثبت موقعیت     → POST /api/add-position  + ساخت لینک و QR
۴) لیست کارت‌ها   → GET /api/list-positions
۵) ویرایش کارت    → PUT /api/update-position و/یا PUT /api/rename-position
```

- اگر محیط ساخته نشده باشد، ثبت موقعیت درخواست شبکه نمی‌فرستد و پیام راهنما می‌دهد.  
- نام موقعیت خالی ⇒ نام خودکار شبیه `pos_xxxxxx`.  
- موقعیت زنده از Transform آواتار ۳بعدی است؛ Canvas/UI به‌عنوان آواتار رد می‌شود.

---

## ۸. جریان بازیکن و لینک اسپان

1. بازیکن لینک را باز می‌کند.  
2. `SpawnFromURL.GetCurrentUrl()` در WebGL از `Application.absoluteURL` می‌خواند (بدون نیاز به JS سفارشی).  
3. `env` و `spawn` پارس می‌شوند.  
4. `GET /api/get-position?env=…&spawn=…`  
5. اگر `MetaRangeNetworkSpawnBridge` فعال باشد، پوز روی **local player شبکه** اعمال می‌شود؛ وگرنه روی Transform آواتار آفلاین.

---

## ۹. فرمت لینک و URL در WebGL

### لینک تولیدشده بعد از ثبت

```
https://dev-world-3d.metarang.com/game/?env=نام_محیط&spawn=شناسه_موقعیت
```

مثال:

```
https://dev-world-3d.metarang.com/game/?env=lobby_01&spawn=ورودی_شمالی
```

### خواندن در کد

| متد | فایل | نقش |
|-----|------|-----|
| `GetCurrentUrl()` | `SpawnFromURL.cs` | WebGL: `Application.absoluteURL` |
| `TryGetUrlParams(out env, out spawn)` | `SpawnFromURL.cs` | استخراج query |
| `GetParam(url, key)` | `SpawnFromURL.cs` | پارس `env=` / `spawn=` |
| `Start()` | `MetaRangeNetworkSpawnBridge.cs` | در صورت وجود پارامترها → Resolve و Apply |

**نیازی به پل JavaScript برای خواندن URL نیست**، به شرطی که صفحه با همان query باز شود و لودر WebGL query را حذف نکند.

در Editor می‌توانید آرگومان خط فرمان بدهید: `url=https://...?env=x&spawn=y`

---

## ۱۰. مرجع اسکریپت‌های یونیتی

### `MetaRangeConfig.cs`
منبع واحد تنظیمات.

- `ServerUrl` — پایهٔ API (بدون اسلش انتهایی ترجیحاً)  
- `PlayBaseUrl` — پایهٔ لینک بازیکن  

### `EnvironmentCreator.cs`
پنل چپ؛ `EnsureEnvironment` → `POST /api/create-env`  
نام محیط را برای بقیه در `StoredEnvironmentName` نگه می‌دارد.

### `OwnerPanel.cs`
پنل راست مالک.

| قابلیت | توضیح |
|--------|--------|
| تیک مالک | نمایش UI و شروع Resolve آواتار |
| موقعیت زنده | خواندن world position؛ رد کردن Canvas/UI |
| ثبت موقعیت | `add-position` + ساخت `playBaseUrl?env=&spawn=` + QR |
| لیست | کارت‌ها از `list-positions` |
| کپی / دانلود لینک / دانلود QR | خروجی برای اشتراک |

ساخت لینک (مفهومی):

```csharp
currentLink = playBaseUrl + "?env=" + env + "&spawn=" + posId;
```

### `PositionCardUI.cs`
هر کارت: نام، مختصات، تاریخ، دکمه ویرایش.  
در ویرایش فیلدها می‌توانند زنده از آواتار پر شوند؛ ذخیره فقط با تأیید (`update` / `rename`).

### `SpawnFromURL.cs`
اسپان آفلاین از URL. اگر بریج شبکه `IsActive` باشد، جابه‌جایی آفلاین را رها می‌کند.

### `MetaRangeNetworkSpawnBridge.cs`
1. خواندن `env`/`spawn` از URL  
2. گرفتن پوز از Node  
3. انتظار برای local player (Reflection روی Network_A)  
4. اعمال پوز best-effort + re-assert کوتاه  

**آواتار دوم ساخته نمی‌شود.**

### `MetaverseNetworkHooks` (داخل همان فایل بریج)
Resolve چندلایه: `TryGetLocalPlayer` → اسکن Identity → fallback.

### `MetaRangeLobbyEnvironmentBridge.cs`
اتصال جریان لابی به create-env و context محیط.

### `MetaRangeModels.cs`
`PositionEntry`, `Vec3Data`, `QuatData` و تبدیل به `Vector3` / `Quaternion`.

### `MetaRangeRespawnSetupTool.cs` (Editor)
ساخت پنل‌ها، Bind رفرنس‌ها، پریفب کارت، ارتفاع و اسکرول.

---

## ۱۱. مرجع API سرور Node

پایهٔ پیش‌فرض توسعه: `http://localhost:3000`

| متد | مسیر | کار | کدهای مهم |
|-----|------|-----|-----------|
| GET | `/api/health` | سلامت سرویس | 200 |
| POST | `/api/create-env` | ساخت محیط | 201, 409, 400 |
| POST | `/api/add-position` | ثبت موقعیت | 201, 409, 404 |
| GET | `/api/get-position?env=&spawn=` | خواندن یک موقعیت | 200, 404 |
| PUT | `/api/update-position` | ویرایش مختصات/چرخش | 200, 404 |
| PUT | `/api/rename-position` | تغییر شناسه موقعیت | 200, 404, 409 |
| GET | `/api/list-positions?env=` | لیست همه | 200, 404 |
| GET | `/api/qr?data=` | تصویر QR PNG | 200 |

### نمونه `add-position`

```json
{
  "environmentName": "lobby_01",
  "positionId": "ورودی_شمالی",
  "position": { "x": 9.33, "y": -3.18, "z": 16.19 },
  "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 }
}
```

### نمونه پاسخ `get-position`

```json
{
  "position": { "x": 9.33, "y": -3.18, "z": 16.19 },
  "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 },
  "createdAt": "2026-10-02T13:39:02.171Z",
  "updatedAt": "2026-10-02T13:39:02.171Z"
}
```

CORS برای Editor و WebGL باز است (`Access-Control-Allow-Origin: *`).

---

## ۱۲. ساختار فایل JSON محیط

مسیر روی سرور: `metaverse-spawn-server/data/environments/<name>.json`

```json
{
  "environmentName": "lobby_01",
  "createdAt": "2026-10-02T12:00:00.000Z",
  "positions": {
    "ورودی_شمالی": {
      "position": { "x": 9.33, "y": -3.18, "z": 16.19 },
      "rotation": { "x": 0, "y": 0.3827, "z": 0, "w": 0.9239 },
      "createdAt": "2026-10-02T13:39:02.171Z",
      "updatedAt": "2026-10-02T14:00:00.000Z"
    }
  }
}
```

کلید هر موقعیت همان `spawn` در لینک است.

---

## ۱۳. یکپارچه‌سازی با Network_A / gRPC

- ارجاع مستقیم به `Network_A` نیست → پروژه بدون آن هم کامپایل می‌شود.  
- با Reflection: `MetaverseNetworkClient.TryGetLocalPlayer`, `MetaverseNetworkIdentity`, …  
- بریج فقط Transform موجود را جابه‌جا می‌کند.  
- روی سرورهایی که authority حرکت سمت سرور است، پوز **best-effort** است و چند ثانیه re-assert می‌شود.  
- اسپان **دائمی سمت سرور** نیاز به تغییر `SpawnPlayerObject` در Network_A دارد (خارج از این پکیج).

---

## ۱۴. پنل‌ها و UI

| پنل | ریشه | کار |
|-----|------|-----|
| چپ | `MetaRange_CreateEnvCanvas` | نام محیط + ساخت فایل |
| راست | `MetaRangeRootPanel` | مالک، موقعیت زنده، ثبت، لینک/QR، لیست کارت |

**خروجی لینک:**

| دکمه | نتیجه |
|------|--------|
| کپی لینک | کلیپ‌بورد (در WebGL ممکن است محدود باشد) |
| دانلود لینک | فایل `spawn_{env}_{id}.txt` یک خطی UTF-8 |
| دانلود QR | PNG |

**کارت لیست (حالت نمایش):** نام موقعیت، مختصات X/Y/Z، (اختیاری چرخش)، تاریخ، دکمه ویرایش.

---

## ۱۵. WebGL، HTTPS و Mixed Content

| سناریو | نتیجه |
|--------|--------|
| صفحه HTTPS + API HTTP | مرورگر درخواست را **مسدود** می‌کند (Mixed Content) |
| صفحه HTTPS + API HTTPS | OK |
| Unity Editor + API HTTP | معمولاً OK برای توسعه |

پیشنهاد production: reverse proxy روی همان دامنه، مثلاً:

```
https://dev-world-3d.metarang.com/spawn-api  →  http://127.0.0.1:4000
```

و در Config:

```
ServerUrl = https://dev-world-3d.metarang.com/spawn-api
PlayBaseUrl = https://dev-world-3d.metarang.com/game
```

---

## ۱۶. عیب‌یابی رایج

| نشانه | علت محتمل | کار |
|--------|-----------|-----|
| `Cannot connect to destination host` / `responseCode=0` | سرور خاموش، IP/پورت غلط، فایروال، هنوز `127.0.0.1` در Config | `curl` از همان ماشین؛ Config را یک‌جا چک کنید |
| Mixed Content در کنسول مرورگر | API روی HTTP زیر صفحه HTTPS | API را HTTPS کنید |
| موقعیت زنده `(MainCanvas)` و اعداد شبیه 960/540 | مرجع UI به‌جای آواتار | نسخهٔ جدید UI را رد می‌کند؛ Resolve دوباره |
| کارت فقط تاریخ دارد | لیبل نام/مختصات bind یا ارتفاع | Setup Tool + `PositionCardUI` |
| `env/spawn` پیدا نشد | URL بدون query یا لودر query را برداشته | لاگ `GetCurrentUrl`؛ آدرس کامل را در نوار مرورگر ببینید |
| اسپان بعد از چند ثانیه برمی‌گردد | authority حرکت سمت سرور شبکه | محدودیت شناخته‌شده؛ نیاز به تغییر سرور Network_A |

---

## ۱۷. محدودیت‌ها و نکات صادقانه

1. **API زیر WebGL HTTPS باید HTTPS باشد** — راه کلاینتی برای دور زدن Mixed Content نیست.  
2. **اسپان شبکه دائمی** خارج از محدودهٔ این پکیج است (بهتر است سرور Network_A پوز اولیه را بداند).  
3. **یک Config** برای همهٔ آدرس‌ها؛ چند مقدار پراکنده در Inspector منبع باگ است.  
4. پکیج **`Assets/Scripts/Network_A` را تغییر نمی‌دهد**.

---

## ۱۸. چک‌لیست تحویل

- [ ] سرور Node بالا است و `/api/health` جواب می‌دهد  
- [ ] `MetaRangeConfig.ServerUrl` و `PlayBaseUrl` درست‌اند (در WebGL: HTTPS)  
- [ ] ساخت محیط موفق است  
- [ ] ثبت موقعیت لینک با `env` و `spawn` می‌سازد  
- [ ] `GET get-position` همان مختصات را برمی‌گرداند  
- [ ] باز کردن لینک در WebGL آواتار را جابه‌جا می‌کند (یا لاگ بریج موفقیت را نشان می‌دهد)  
- [ ] کارت‌ها نام + مختصات + تاریخ را نشان می‌دهند  
- [ ] موقعیت زنده روی آواتار ۳بعدی است نه Canvas  
- [ ] Network_A در diff پروژهٔ این پکیج ظاهر نشده  

---

## شروع دستورات خلاصه

```bash
# سرور
cd metaverse-spawn-server && npm install && node index.js

# تست API
curl http://localhost:3000/api/health
curl -X POST http://localhost:3000/api/create-env -H "Content-Type: application/json" -d "{\"environmentName\":\"test_env\"}"
```

**یونیتی:** Tools ▸ متارنج ▸ راه‌اندازی سیستم ریسپان آواتار → Play → ساخت محیط → مالک → ثبت → کپی لینک.

---

## مجوز و مالکیت ماژول

این ماژول روی برنچ جدا **`feature/metarange-respawn`** نگه داشته می‌شود تا تفکیک ماژول‌های زیرساخت بازی‌سازی در گیت‌هاب رعایت شود. تمام کد و دیتای مرتبط با ریسپان آواتار باید در همین برنچ / مسیر `Assets/Benyamin/MetaRangeRespawn` و `metaverse-spawn-server` بماند.

---

*مستند یکپارچه برای پکیج MetaRangeRespawn — مناسب تحویل به کارفرما و توسعه‌دهندگان.*
```
