# متارنج — سرور ریسپان (metaverse-spawn-server)

سرور Node.js مدیریت موقعیت‌های ریسپان آواتار. این سرور با پکیج یونیتی
[`MetaRangeRespawn`](../Assets/Benyamin/MetaRangeRespawn) جفت است.

## اجرا

```bash
npm install
node index.js
```

خروجی موفق:

```
──────────────────────────────────────────────
 متارنج v3.1 — سرور ریسپان آواتار
 listening : http://localhost:3000  (http://127.0.0.1:3000)  dual-stack
 data dir  : .../data/environments
 log file  : .../logs/server.log
──────────────────────────────────────────────
```

## تنظیمات

| متغیر | پیش‌فرض | کاربرد |
|-------|---------|--------|
| `PORT` | `3000` | پورت سرور |
| `HOST` | تنظیم‌نشده ⇒ `::` (dual-stack) | اگر ست شود، فقط روی همان IP گوش می‌دهد |

## endpointها

| متد | مسیر |
|-----|------|
| GET | `/api/health` |
| POST | `/api/create-env` |
| POST | `/api/add-position` |
| GET | `/api/get-position?env=&spawn=` |
| PUT | `/api/update-position` |
| PUT | `/api/rename-position` |
| GET | `/api/list-positions?env=` |
| GET | `/api/qr?data=` |

مرجع کامل با بدنهٔ درخواست/پاسخ و کدهای وضعیت: [`../Assets/Benyamin/MetaRangeRespawn/Docs/06_مرجع_API_سرور.md`](../Assets/Benyamin/MetaRangeRespawn/Docs/06_مرجع_API_سرور.md)

## داده و لاگ

| مورد | مسیر | در git؟ |
|------|------|---------|
| فایل هر محیط | `data/environments/<name>.json` | خیر — در زمان اجرا ساخته می‌شود |
| لاگ درخواست‌ها | `logs/server.log` | خیر |

پشتیبان‌گیری = کپی گرفتن از `data/environments/`.