# ۰۶ — مرجع API سرور (v3.5.0 پکیج / سرور v3.1)

| مورد | مقدار |
|------|-------|
| Base | `http://localhost:3000` |
| فرمت بدنه | `application/json` (UTF-8) |
| سقف حجم بدنه | ۱ مگابایت |
| CORS | `Access-Control-Allow-Origin: *` · `Allow-Methods: GET,POST,PUT,DELETE,OPTIONS` · `OPTIONS -> 204` |
| ذخیره‌سازی | `data/environments/<environmentName>.json` |
| لاگ | `logs/server.log` (رویداد `finish` هر درخواست) |
| محدودیت نام | `^[A-Za-z0-9_\-؀-ۿ]+$` · حداکثر ۶۴ کاراکتر · `path traversal` مسدود |

---

## خلاصهٔ endpointها

| متد | مسیر | ورودی | موفق | خطاهای رایج |
|-----|------|-------|------|--------------|
| GET | `/api/health` | — | `200` | — |
| POST | `/api/create-env` | `{ environmentName }` | `201` | `400` نام نامعتبر · `409` تکراری |
| POST | `/api/add-position` | `{ environmentName, positionId, position{x,y,z}, rotation{x,y,z,w} }` | `201` | `400` · `404` محیط · `409` شناسه تکراری |
| GET | `/api/get-position` | `?env=&spawn=` | `200` | `400` · `404` محیط/موقعیت |
| PUT | `/api/update-position` | همان بدنهٔ `add-position` | `200` | `400` · `404` |
| PUT | `/api/rename-position` | `{ environmentName, positionId, newPositionId }` | `200` | `400` · `404` · `409` نام تکراری |
| GET | `/api/list-positions` | `?env=` | `200` | `400` · `404` |
| GET | `/api/qr` | `?data=<متن>` | `200 image/png` | `400` نبودن/طول زیاد · `500` خطای تولید |
| هر چیز دیگر زیر `/api` | — | — | — | `404 Unknown endpoint: …` |

بدنهٔ خطا همیشه شکل `{"error": "…"}` دارد.

---

## ۰) سلامت

```http
GET /api/health
```

```json
{ "ok": true, "service": "metarange-respawn", "version": "3.1", "environments": 2 }
```

`environments` تعداد فایل‌های `.json` در `data/environments` است.

---

## ۱) ساخت محیط

```http
POST /api/create-env
{ "environmentName": "demo_hall" }
```

| کد | معنی |
|----|------|
| `201` | محیط ساخته شد — پاسخ کل فایل: `{ environmentName, createdAt, positions: {} }` |
| `400` | `environmentName` نامعتبر/ناموجود |
| `409` | `Environment already exists` |

این endpoint **idempotent** است؛ `EnvironmentCreator.EnsureEnvironment` هر دو کد `201` و `409` را موفق می‌شمارد.

---

## ۲) ثبت موقعیت

```http
POST /api/add-position
{
  "environmentName": "demo_hall",
  "positionId": "ورودی_اصلی",
  "position":  { "x": 12.5, "y": 0.0, "z": -8.3 },
  "rotation":  { "x": 0, "y": 0, "z": 0, "w": 1 }
}
```

پاسخ `201` — فقط همان موقعیت:

```json
{
  "position": { "x": 12.5, "y": 0.0, "z": -8.3 },
  "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 },
  "createdAt": "2026-09-30T21:29:34.121Z"
}
```

| کد | معنی |
|----|------|
| `400` | `Missing/invalid fields: environmentName, positionId, position{x,y,z}, rotation{x,y,z,w}` |
| `404` | `Environment not found` (محیط روی سرور نیست — `OwnerPanel` خودش محیط را می‌سازد و تلاش مجدد می‌کند) |
| `409` | `Position already exists` |

اعتبارسنجی مختصات: هر سه مؤلفه باید `Number.isFinite` باشند؛ کوارنیون هر چهار مؤلفه.

---

## ۳) دریافت یک موقعیت

```http
GET /api/get-position?env=demo_hall&spawn=%D9%88%D8%B1%D9%88%D8%AF%DB%8C_%D8%A7%D8%B5%D9%84%DB%8C
```

پاسخ `200` همان ساختار `add-position` است (به‌علاوهٔ `updatedAt` در صورت وجود).

| کد | معنی |
|----|------|
| `400` | `env and spawn required` |
| `404` | `Environment not found` یا `Position not found` |

نام فارسی باید percent-encode شود؛ سرور پس از decode با `safeName` اعتبارسنجی می‌کند.

---

## ۴) ویرایش موقعیت

```http
PUT /api/update-position
{
  "environmentName": "demo_hall",
  "positionId": "ورودی_اصلی",
  "position":  { "x": 194.5, "y": -37.9, "z": 0 },
  "rotation":  { "x": 0, "y": 0, "z": 0, "w": 1 }
}
```

پاسخ `200` با ساختار موقعیت به‌علاوهٔ:

| فیلد | توضیح |
|------|-------|
| `createdAt` | حفظ می‌شود (اگر نبود، زمان حال) |
| `updatedAt` | زمان این ویرایش |

| کد | معنی |
|----|------|
| `400` | همان پیام اعتبارسنجی `add-position` |
| `404` | `Environment not found` / `Position not found` |

---

## ۵) تغییر نام موقعیت

```http
PUT /api/rename-position
{
  "environmentName": "demo_hall",
  "positionId": "ورودی_اصلی",
  "newPositionId": "ورودی_شمالی"
}
```

| حالت | کد | پاسخ |
|-------|-----|------|
| موفق | `200` | `{ "positionId": "ورودی_شمالی", "renamed": true, "position": {…} }` |
| نام یکسان | `200` | `{ "positionId": "…", "renamed": false, "position": {…} }` |
| نام تکراری | `409` | `{"error":"Position already exists"}` |
| نام نامعتبر | `400` | `Missing/invalid fields: environmentName, positionId, newPositionId` |
| محیط/موقعیت نبود | `404` | `Environment not found` / `Position not found` |

نکته‌ها:

- فقط **کلید** عوض می‌شود؛ مختصات دست‌نخورده می‌مانند.
- ترتیب کلیدها تا حد ممکن حفظ می‌شود (شیء از نو ساخته می‌شود).
- `updatedAt` موقعیت تغییرنام‌داده‌شده به‌روز می‌شود.
- **لینک‌های قدیمی دیگر کار نمی‌کنند** (`404 Position not found`) چون شناسه عوض شده است.

---

## ۶) لیست موقعیت‌ها

```http
GET /api/list-positions?env=demo_hall
```

پاسخ `200` — یک شیء سادهٔ `{ نام‌موقعیت: {...} }`:

```json
{
  "ورودی_اصلی": {
    "position": { "x": 194.5, "y": -37.9, "z": 0 },
    "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 },
    "createdAt": "2026-09-30T21:29:34.121Z"
  },
  "pos_4F7A21": { "…": "…" }
}
```

| کد | معنی |
|----|------|
| `400` | `env required` |
| `404` | `Environment not found` |

`OwnerPanel.ParseDict` این ساختار را می‌خواند و **هر کلیدی** را می‌پذیرد (نه فقط کلیدهای `pos_`). شکل `{"positions": {…}}` هم پشتیبانی می‌شود.

---

## ۷) تولید QR

```http
GET /api/qr?data=<لینک percent-encoded>
```

| ویژگی | مقدار |
|-------|-------|
| نوع پاسخ | `image/png` |
| عرض تصویر | ۵۱۲ پیکسل |
| سطح تصحیح خطا | `M` |
| حاشیه | ۲ ماژول |
| کش | `Cache-Control: no-store` |
| حداکثر طول `data` | ۱۲۰۰ کاراکتر |

| کد | معنی |
|----|------|
| `400` | `data query is required` یا `data too long` |
| `500` | `QR generation failed: …` |

پیاده‌سازی: بستهٔ npm `qrcode` (`QRCode.toBuffer`). هیچ سرویس بیرونی درگیر نیست — به همین دلیل منبع اول زنجیرهٔ QR است.

---

## نمونهٔ کامل با PowerShell

```powershell
$B = 'http://localhost:3000'

Invoke-RestMethod "$B/api/health"

Invoke-RestMethod "$B/api/create-env" -Method Post -ContentType 'application/json' `
  -Body (@{ environmentName = 'demo_hall' } | ConvertTo-Json)

Invoke-RestMethod "$B/api/add-position" -Method Post -ContentType 'application/json' -Body (@{
  environmentName = 'demo_hall'
  positionId      = 'ورودی_اصلی'
  position        = @{ x = 12.5; y = 0; z = -8.3 }
  rotation        = @{ x = 0; y = 0; z = 0; w = 1 }
} | ConvertTo-Json)

Invoke-RestMethod "$B/api/list-positions?env=demo_hall"
Invoke-RestMethod "$B/api/get-position?env=demo_hall&spawn=$([uri]::EscapeDataString('ورودی_اصلی'))"

Invoke-RestMethod "$B/api/rename-position" -Method Put -ContentType 'application/json' -Body (@{
  environmentName = 'demo_hall'; positionId = 'ورودی_اصلی'; newPositionId = 'ورودی_شمالی'
} | ConvertTo-Json)

Invoke-WebRequest "$B/api/qr?data=$([uri]::EscapeDataString('https://dev-world-3d.metarang.com/game?env=demo_hall&spawn=pos_4F7A21'))" -OutFile qr.png
```

نمونهٔ خطاها:

```json
{ "error": "Environment not found" }
{ "error": "Position not found" }
{ "error": "Position already exists" }
{ "error": "Unknown endpoint: GET /api/nope" }
{ "error": "Invalid JSON body" }
```

## نمونهٔ لاگ

```
[2026-09-30T21:29:34.804Z] PUT /api/rename-position -> 409 (2ms)  body: env=test_webgl id=ورودی_اصلی
[2026-09-30T21:29:34.842Z] GET /api/qr?data=https%3A%2F%2F… -> 200 (29ms)  body: {"data":"https://…"}
[2026-09-30T21:29:34.844Z] GET /api/qr -> 400 (0ms)
```