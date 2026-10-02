/**
 * متارنج v3.1 — سرور مدیریت موقعیت ریسپان آواتار
 * ------------------------------------------------------------
 * Base: http://localhost:4000
 *
 *   POST /api/create-env        { environmentName }                  → 201 | 409 | 400
 *   POST /api/add-position      { environmentName, positionId,
 *                                position{x,y,z}, rotation{x,y,z,w} } → 201 | 409 | 404
 *   GET  /api/get-position      ?env=&spawn=                        → 200 | 404
 *   PUT  /api/update-position   (همان بدن add-position)              → 200 | 404
 *   PUT  /api/rename-position   { environmentName,
 *                                positionId, newPositionId }        → 200 | 404 | 409
 *   GET  /api/list-positions    ?env=                               → 200 | 404
 *   GET  /api/qr                 ?data=<lینک>                      → 200 image/png
 *   GET  /api/health                                               → 200
 *
 * داده‌ها در data/environments/<name>.json ذخیره می‌شوند.
 */

const express = require('express');
const fs = require('fs');
const path = require('path');
const cors = require('cors');
const QRCode = require('qrcode');

const app = express();
const PORT = process.env.PORT || 4000;
// بدون host → Node روی :: (dual-stack) بسته می‌شود؛ هم localhost/IPv6 و هم 127.0.0.1/IPv4 کار می‌کند
const HOST = process.env.HOST; // فقط اگر صراحاً ست شود محدود می‌شود
const ENV_DIR = path.join(__dirname, 'data', 'environments');
const LOG_DIR = path.join(__dirname, 'logs');
const LOG_FILE = path.join(LOG_DIR, 'server.log');

/* لاگ هم در کنسول و هم در فایل (logs/server.log) */
try {
  fs.mkdirSync(LOG_DIR, { recursive: true });
  const logStream = fs.createWriteStream(LOG_FILE, { flags: 'a' });
  const rawLog = console.log;
  console.log = (...args) => {
    rawLog.apply(console, args);
    try {
      logStream.write(args.map(a => (typeof a === 'string' ? a : String(a))).join(' ') + '\n');
    } catch { /* ignore */ }
  };
} catch (e) {
  console.warn('[متارنج] لاگ فایلی در دسترس نیست:', e.message);
}

/* ------------------------------------------------------------------ */
/* CORS — برای Unity Editor و WebGL                                   */
/* ------------------------------------------------------------------ */
app.use((req, res, next) => {
  res.header('Access-Control-Allow-Origin', '*');
  res.header('Access-Control-Allow-Methods', 'GET,POST,PUT,DELETE,OPTIONS');
  res.header('Access-Control-Allow-Headers', 'Content-Type, Authorization, Accept');
  res.header('Access-Control-Max-Age', '86400');
  if (req.method === 'OPTIONS') return res.sendStatus(204);
  next();
});
app.use(cors());

/* ------------------------------------------------------------------ */
/* لاگ درخواست‌ها                                                      */
/* ------------------------------------------------------------------ */
app.use((req, res, next) => {
  const started = Date.now();
  res.on('finish', () => {
    const ms = Date.now() - started;
    const summary = summarize(req);
    console.log(
      `[${new Date().toISOString()}] ${req.method} ${req.originalUrl} -> ${res.statusCode} (${ms}ms)` +
      (summary ? `  body: ${summary}` : '')
    );
  });
  next();
});

function summarize(req) {
  if (req.method === 'GET') {
    const q = req.query;
    return Object.keys(q).length ? JSON.stringify(q) : '';
  }
  if (req.body && typeof req.body === 'object') {
    const b = req.body;
    const parts = [];
    if (b.environmentName) parts.push(`env=${b.environmentName}`);
    if (b.positionId) parts.push(`id=${b.positionId}`);
    if (b.position) parts.push(`pos=(${b.position.x},${b.position.y},${b.position.z})`);
    return parts.length ? parts.join(' ') : JSON.stringify(b).slice(0, 200);
  }
  return '';
}

/* ------------------------------------------------------------------ */
/* Body parser                                                         */
/* ------------------------------------------------------------------ */
app.use(express.json({ limit: '1mb' }));
app.use((err, req, res, next) => {
  if (err && err.type === 'entity.parse.failed') {
    return res.status(400).json({ error: 'Invalid JSON body' });
  }
  next(err);
});

/* ------------------------------------------------------------------ */
/* ذخیره‌سازی                                                          */
/* ------------------------------------------------------------------ */
if (!fs.existsSync(ENV_DIR)) fs.mkdirSync(ENV_DIR, { recursive: true });

/** جلوگیری از path traversal و نام‌های غیرمجاز در نام فایل */
function safeName(name) {
  if (typeof name !== 'string') return null;
  const n = name.trim();
  if (!n) return null;
  if (n.length > 64) return null;
  if (!/^[A-Za-z0-9_\-؀-ۿ]+$/.test(n)) return null;   // حروف فارسی مجاز
  if (n === '.' || n === '..') return null;
  return n;
}

function readEnv(name) {
  const n = safeName(name);
  if (!n) return null;
  const p = path.join(ENV_DIR, `${n}.json`);
  if (!fs.existsSync(p)) return null;
  try {
    return JSON.parse(fs.readFileSync(p, 'utf8'));
  } catch (e) {
    console.error(`[متارنج] فایل خراب: ${p} → ${e.message}`);
    return null;
  }
}

function writeEnv(name, data) {
  const n = safeName(name);
  if (!n) throw new Error('invalid environmentName');
  fs.writeFileSync(path.join(ENV_DIR, `${n}.json`), JSON.stringify(data, null, 2), 'utf8');
}

function isVec3(v) {
  return v && typeof v === 'object' &&
    Number.isFinite(v.x) && Number.isFinite(v.y) && Number.isFinite(v.z);
}

function isQuat(v) {
  return v && typeof v === 'object' &&
    Number.isFinite(v.x) && Number.isFinite(v.y) &&
    Number.isFinite(v.z) && Number.isFinite(v.w);
}

/* ------------------------------------------------------------------ */
/* 0) سلامت                                                            */
/* ------------------------------------------------------------------ */
app.get('/api/health', (req, res) => {
  let count = 0;
  try { count = fs.readdirSync(ENV_DIR).filter(f => f.endsWith('.json')).length; } catch { }
  res.json({ ok: true, service: 'metarange-respawn', version: '3.1', environments: count });
});

/* ------------------------------------------------------------------ */
/* 1) ساخت محیط                                                        */
/* ------------------------------------------------------------------ */
app.post('/api/create-env', (req, res) => {
  const name = (req.body || {}).environmentName;
  const clean = safeName(name);
  if (!clean) {
    return res.status(400).json({ error: 'environmentName is required (letters, digits, -, _)' });
  }
  if (readEnv(clean)) {
    return res.status(409).json({ error: 'Environment already exists' });
  }
  const data = {
    environmentName: clean,
    createdAt: new Date().toISOString(),
    positions: {}
  };
  writeEnv(clean, data);
  res.status(201).json(data);
});

/* ------------------------------------------------------------------ */
/* 2) ثبت موقعیت                                                       */
/* ------------------------------------------------------------------ */
app.post('/api/add-position', (req, res) => {
  const { environmentName, positionId, position, rotation } = req.body || {};
  const env = safeName(environmentName);
  const id = safeName(positionId);

  if (!env || !id || !isVec3(position) || !isQuat(rotation)) {
    return res.status(400).json({
      error: 'Missing/invalid fields: environmentName, positionId, position{x,y,z}, rotation{x,y,z,w}'
    });
  }

  const data = readEnv(env);
  if (!data) return res.status(404).json({ error: 'Environment not found' });
  if (!data.positions) data.positions = {};
  if (data.positions[id]) {
    return res.status(409).json({ error: 'Position already exists' });
  }

  data.positions[id] = {
    position: { x: position.x, y: position.y, z: position.z },
    rotation: { x: rotation.x, y: rotation.y, z: rotation.z, w: rotation.w },
    createdAt: new Date().toISOString()
  };
  writeEnv(env, data);
  res.status(201).json(data.positions[id]);
});

/* ------------------------------------------------------------------ */
/* 3) دریافت یک موقعیت                                                 */
/* ------------------------------------------------------------------ */
app.get('/api/get-position', (req, res) => {
  const env = safeName(req.query.env);
  const spawn = safeName(req.query.spawn);
  if (!env || !spawn) return res.status(400).json({ error: 'env and spawn required' });

  const data = readEnv(env);
  if (!data) return res.status(404).json({ error: 'Environment not found' });
  if (!data.positions || !data.positions[spawn]) {
    return res.status(404).json({ error: 'Position not found' });
  }
  res.json(data.positions[spawn]);
});

/* ------------------------------------------------------------------ */
/* 4) ویرایش موقعیت                                                    */
/* ------------------------------------------------------------------ */
app.put('/api/update-position', (req, res) => {
  const { environmentName, positionId, position, rotation } = req.body || {};
  const env = safeName(environmentName);
  const id = safeName(positionId);

  if (!env || !id || !isVec3(position) || !isQuat(rotation)) {
    return res.status(400).json({
      error: 'Missing/invalid fields: environmentName, positionId, position{x,y,z}, rotation{x,y,z,w}'
    });
  }

  const data = readEnv(env);
  if (!data) return res.status(404).json({ error: 'Environment not found' });
  if (!data.positions || !data.positions[id]) {
    return res.status(404).json({ error: 'Position not found' });
  }

  const prev = data.positions[id];
  data.positions[id] = {
    position: { x: position.x, y: position.y, z: position.z },
    rotation: { x: rotation.x, y: rotation.y, z: rotation.z, w: rotation.w },
    createdAt: prev.createdAt || new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };
  writeEnv(env, data);
  res.json(data.positions[id]);
});

/* ------------------------------------------------------------------ */
/* 5) لیست موقعیت‌ها                                                   */
/* ------------------------------------------------------------------ */
app.get('/api/list-positions', (req, res) => {
  const env = safeName(req.query.env);
  if (!env) return res.status(400).json({ error: 'env required' });

  const data = readEnv(env);
  if (!data) return res.status(404).json({ error: 'Environment not found' });
  res.json(data.positions || {});
});

/* ------------------------------------------------------------------ */
/* 6) تغییر نام موقعیت (Rename Position)                              */
/* ------------------------------------------------------------------ */
app.put('/api/rename-position', (req, res) => {
  const { environmentName, positionId, newPositionId } = req.body || {};
  const env = safeName(environmentName);
  const oldId = safeName(positionId);
  const newId = safeName(newPositionId);

  if (!env || !oldId || !newId) {
    return res.status(400).json({
      error: 'Missing/invalid fields: environmentName, positionId, newPositionId'
    });
  }

  const data = readEnv(env);
  if (!data) return res.status(404).json({ error: 'Environment not found' });
  if (!data.positions || !data.positions[oldId]) {
    return res.status(404).json({ error: 'Position not found' });
  }

  // نام یکسان ⇒ کاری لازم نیست
  if (oldId === newId) {
    return res.json({ positionId: newId, renamed: false, position: data.positions[oldId] });
  }

  if (data.positions[newId]) {
    return res.status(409).json({ error: 'Position already exists' });
  }

  // کلید جدید ساخته و مقادیر حفظ می‌شوند (ترتیب کلیدها هم تا حد ممکن می‌ماند)
  const rebuilt = {};
  for (const key of Object.keys(data.positions)) {
    const value = data.positions[key];
    if (key === oldId) {
      value.updatedAt = new Date().toISOString();
    }
    rebuilt[key === oldId ? newId : key] = value;
  }
  data.positions = rebuilt;
  writeEnv(env, data);

  res.json({ positionId: newId, renamed: true, position: data.positions[newId] });
});

/* ------------------------------------------------------------------ */
/* 7) تولید QR روی خود سرور (Local QR Generator)                     */
/* ------------------------------------------------------------------ */
app.get('/api/qr', async (req, res) => {
  const data = req.query.data;
  if (!data) return res.status(400).json({ error: 'data query is required' });
  if (data.length > 1200) return res.status(400).json({ error: 'data too long' });

  try {
    const png = await QRCode.toBuffer(String(data), {
      type: 'png',
      errorCorrectionLevel: 'M',
      margin: 2,
      width: 512
    });
    res.set('Content-Type', 'image/png');
    res.set('Cache-Control', 'no-store');
    res.send(png);
  } catch (e) {
    res.status(500).json({ error: 'QR generation failed: ' + e.message });
  }
});

/* ------------------------------------------------------------------ */
/* 404 و خطای عمومی                                                    */
/* ------------------------------------------------------------------ */
app.use('/api', (req, res) => {
  res.status(404).json({ error: `Unknown endpoint: ${req.method} ${req.originalUrl}` });
});

app.use((err, req, res, next) => {   // eslint-disable-line no-unused-vars
  console.error('[متارنج] خطای داخلی:', err);
  res.status(500).json({ error: 'Internal server error' });
});

/* ------------------------------------------------------------------ */
function onListening() {
  console.log('──────────────────────────────────────────────');
  console.log(' متارنج v3.1 — سرور ریسپان آواتار');
  console.log(` listening : http://localhost:${PORT}  (http://127.0.0.1:${PORT})  dual-stack`);
  console.log(` data dir  : ${ENV_DIR}`);
  console.log(` log file  : ${LOG_FILE}`);
  console.log('──────────────────────────────────────────────');
}

if (HOST) app.listen(PORT, HOST, onListening);
else app.listen(PORT, onListening);   // :: — IPv4 + IPv6
