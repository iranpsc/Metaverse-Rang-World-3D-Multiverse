'use strict';

/**
 * Meta environment JSON store - phase 1.
 *
 * One small process, JSON files on disk, no database and no auth service. It serves exactly the
 * two documents the Unity client already speaks:
 *
 *   GET|PUT|DELETE /environments/:environmentId/minimap/:citizenId.json   (per citizen)
 *   GET|PUT         /environments/:environmentId/daynight.json            (per place)
 *   GET             /health
 *
 * Layout under DATA_ROOT (default ./data):
 *   environments/<environmentId>/daynight.json
 *   environments/<environmentId>/minimap/<citizenId>.json
 *
 * Design notes, all deliberate for phase 1:
 *   - Ids are sanitised the same way the client does (MiniMapCitizenPath.Sanitize) and then
 *     REJECTED if they could still walk the tree. A traversal attempt is a 400, not a silent rewrite.
 *   - Writes are atomic: temp file in the same directory, then rename.
 *   - Concurrent PUTs to the same file: last write wins. No lock service.
 *   - Ownership of a document is decided by the URL path, never by the request body.
 */

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const express = require('express');
const cors = require('cors');

// ---------------------------------------------------------------- configuration

const PORT = Number(process.env.PORT || 8788);
const DATA_ROOT = path.resolve(process.env.DATA_ROOT || path.join(__dirname, 'data'));
const CORS_ORIGIN = process.env.CORS_ORIGIN || '*';
// Phase 1 accepts daynight writes without auth. Set REQUIRE_OWNER=true to demand the header.
const REQUIRE_OWNER = /^(1|true|yes)$/i.test(String(process.env.REQUIRE_OWNER || ''));

const SCHEMA_VERSION = 1;
const MAX_BODY_BYTES = 64 * 1024;   // these documents are a handful of numbers
const MAX_ID_LENGTH = 128;

// ---------------------------------------------------------------- errors

class HttpError extends Error {
  constructor(status, code, message) {
    super(message || code);
    this.status = status;
    this.code = code;
  }
}

const badRequest = (code, message) => new HttpError(400, code, message);
const notFound = () => new HttpError(404, 'not_found', 'No stored document at that path.');

// ---------------------------------------------------------------- ids and paths

/**
 * Allow only [A-Za-z0-9._-]; everything else becomes '_' (same rule as the Unity client), then the
 * result is rejected if it is empty, hidden, or still contains '..'.
 * @returns {string|null} sanitised id, or null when the input must be refused.
 */
function sanitizeId(raw) {
  if (typeof raw !== 'string' || raw.length === 0) return null;

  const sanitized = raw.replace(/[^A-Za-z0-9._-]/g, '_');
  if (sanitized.length === 0 || sanitized.length > MAX_ID_LENGTH) return null;
  if (sanitized === '.' || sanitized === '..') return null;
  if (sanitized.includes('..')) return null;      // traversal attempt, e.g. ../etc
  if (sanitized.startsWith('.')) return null;     // hidden files and .git must stay unreachable

  return sanitized;
}

function requireId(raw, what) {
  const id = sanitizeId(raw);
  if (id === null) throw badRequest('invalid_id', `Invalid ${what}: ${JSON.stringify(raw)}`);
  return id;
}

/** Express captures ":citizenId.json" as one segment; keep the id, drop the suffix. */
function stripJsonSuffix(segment) {
  return typeof segment === 'string' && segment.endsWith('.json')
    ? segment.slice(0, -'.json'.length)
    : segment;
}

/** Belt and braces: the resolved file must stay under the expected root. */
function assertInside(root, target) {
  const rel = path.relative(path.resolve(root), path.resolve(target));
  if (rel === '' || rel.startsWith('..') || path.isAbsolute(rel)) {
    throw badRequest('invalid_path', 'Resolved path escapes the data root.');
  }
}

const environmentsRoot = () => path.join(DATA_ROOT, 'environments');
const minimapDir = (environmentId) => path.join(environmentsRoot(), environmentId, 'minimap');
const minimapFile = (environmentId, citizenId) => path.join(minimapDir(environmentId), `${citizenId}.json`);
const daynightFile = (environmentId) => path.join(environmentsRoot(), environmentId, 'daynight.json');

// ---------------------------------------------------------------- file helpers

async function readJson(filePath) {
  let raw;
  try {
    raw = await fsp.readFile(filePath, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT') throw notFound();
    throw err;
  }

  try {
    return JSON.parse(raw);
  } catch {
    // A corrupt file must not take the service down: report it as missing so the client falls back.
    throw notFound();
  }
}

/** Write via a temp file in the same directory, then rename: readers never see partial JSON. */
async function writeJsonAtomic(filePath, value) {
  await fsp.mkdir(path.dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.${process.pid}.${Date.now()}.tmp`;
  const body = `${JSON.stringify(value, null, 2)}\n`;

  await fsp.writeFile(tempPath, body, 'utf8');
  try {
    await fsp.rename(tempPath, filePath);
  } catch (err) {
    await fsp.rm(tempPath, { force: true });   // never leave temp files behind
    throw err;
  }
}

// ---------------------------------------------------------------- validation

const isPlainObject = (value) =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

function requireVersion(body) {
  const version = body.version;
  if (typeof version !== 'number' || !Number.isInteger(version)) {
    throw badRequest('missing_or_invalid_version', 'Field "version" must be an integer.');
  }
  if (version > SCHEMA_VERSION) {
    throw badRequest('unsupported_version', `Unsupported version ${version}; this service supports up to ${SCHEMA_VERSION}.`);
  }
  if (version < 1) {
    throw badRequest('missing_or_invalid_version', 'Field "version" must be >= 1.');
  }
  return version;
}

function finiteNumber(value, fallback) {
  const n = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(n) ? n : fallback;
}

const clamp01 = (n) => Math.min(1, Math.max(0, n));

/** citizenId comes from the URL, never from the body. */
function buildMiniMapDocument(body, citizenId) {
  if (!isPlainObject(body)) throw badRequest('invalid_json', 'Body must be a JSON object.');
  const version = requireVersion(body);

  return {
    version,
    citizenId,
    radarEnabled: Boolean(body.radarEnabled),
    zoomOrOrthoSize: finiteNumber(body.zoomOrOrthoSize, 40),
    offsetY: finiteNumber(body.offsetY, 0),
    updatedAt: new Date().toISOString(),
  };
}

/** environmentId comes from the URL, never from the body. */
function buildDayNightDocument(body, environmentId) {
  if (!isPlainObject(body)) throw badRequest('invalid_json', 'Body must be a JSON object.');
  const version = requireVersion(body);

  const document = {
    version,
    environmentId,
    time01: clamp01(finiteNumber(body.time01, 0)),
    cycleRunning: Boolean(body.cycleRunning),
    updatedAt: new Date().toISOString(),
    updatedBy: typeof body.updatedBy === 'string' ? body.updatedBy : '',
  };

  // Optional passthrough from the Unity remote model: the owner's simulation speed and the
  // canonical base rotation survive a round trip. Not required by phase 1; ignored when absent.
  const rotationSpeed = finiteNumber(body.rotationSpeed, -1);
  if (rotationSpeed >= 0) document.rotationSpeed = rotationSpeed;

  const base = body.baseRotation;
  if (isPlainObject(base) && [base.x, base.y, base.z, base.w].every((v) => Number.isFinite(Number(v)))) {
    document.baseRotation = {
      x: Number(base.x), y: Number(base.y), z: Number(base.z), w: Number(base.w),
    };
  }

  return document;
}

function requireOwnerHeader(req) {
  if (!REQUIRE_OWNER) return;                     // phase 1: open
  const header = String(req.get('X-Is-Environment-Owner') || '').toLowerCase();
  if (header !== 'true' && header !== '1') {
    throw new HttpError(403, 'not_environment_owner',
      'Day/night writes require the header X-Is-Environment-Owner: true.');
  }
}

// ---------------------------------------------------------------- app

const app = express();

app.disable('x-powered-by');
app.use(cors({ origin: CORS_ORIGIN }));
app.use(express.json({ limit: MAX_BODY_BYTES }));

// One log line per request: method path status durationMs
app.use((req, res, next) => {
  const startedAt = process.hrtime.bigint();
  res.on('finish', () => {
    const durationMs = Number(process.hrtime.bigint() - startedAt) / 1e6;
    console.log(`${req.method} ${req.originalUrl} ${res.statusCode} ${durationMs.toFixed(1)}ms`);
  });
  next();
});

app.get('/health', (_req, res) => {
  res.json({ ok: true, dataRoot: DATA_ROOT });
});

// ---------------------------------------------------------------- minimap

app.get('/environments/:environmentId/minimap/:file', async (req, res, next) => {
  try {
    const environmentId = requireId(req.params.environmentId, 'environmentId');
    const citizenId = requireId(stripJsonSuffix(req.params.file), 'citizenId');

    const filePath = minimapFile(environmentId, citizenId);
    assertInside(minimapDir(environmentId), filePath);

    res.json(await readJson(filePath));
  } catch (err) {
    next(err);
  }
});

app.put('/environments/:environmentId/minimap/:file', async (req, res, next) => {
  try {
    const environmentId = requireId(req.params.environmentId, 'environmentId');
    const citizenId = requireId(stripJsonSuffix(req.params.file), 'citizenId');

    // Any citizen may write, but only their OWN file: the id is taken from the path.
    const document = buildMiniMapDocument(req.body, citizenId);

    const filePath = minimapFile(environmentId, citizenId);
    assertInside(minimapDir(environmentId), filePath);
    await writeJsonAtomic(filePath, document);

    res.status(200).json(document);
  } catch (err) {
    next(err);
  }
});

app.delete('/environments/:environmentId/minimap/:file', async (req, res, next) => {
  try {
    const environmentId = requireId(req.params.environmentId, 'environmentId');
    const citizenId = requireId(stripJsonSuffix(req.params.file), 'citizenId');

    const filePath = minimapFile(environmentId, citizenId);
    assertInside(minimapDir(environmentId), filePath);

    try {
      await fsp.unlink(filePath);
    } catch (err) {
      if (err.code === 'ENOENT') throw notFound();
      throw err;
    }

    res.status(200).json({ deleted: true, environmentId, citizenId });
  } catch (err) {
    next(err);
  }
});

// ---------------------------------------------------------------- daynight

app.get('/environments/:environmentId/daynight.json', async (req, res, next) => {
  try {
    const environmentId = requireId(req.params.environmentId, 'environmentId');

    const filePath = daynightFile(environmentId);
    assertInside(environmentsRoot(), filePath);

    res.json(await readJson(filePath));
  } catch (err) {
    next(err);
  }
});

app.put('/environments/:environmentId/daynight.json', async (req, res, next) => {
  try {
    requireOwnerHeader(req);

    const environmentId = requireId(req.params.environmentId, 'environmentId');
    const document = buildDayNightDocument(req.body, environmentId);

    const filePath = daynightFile(environmentId);
    assertInside(environmentsRoot(), filePath);
    await writeJsonAtomic(filePath, document);

    res.status(200).json(document);
  } catch (err) {
    next(err);
  }
});

// ---------------------------------------------------------------- errors

app.use((req, res) => {
  res.status(404).json({ error: 'not_found' });
});

// eslint-disable-next-line no-unused-vars
app.use((err, _req, res, _next) => {
  if (err instanceof HttpError) {
    return res.status(err.status).json({ error: err.code, message: err.message });
  }
  if (err && err.type === 'entity.parse.failed') {
    return res.status(400).json({ error: 'invalid_json', message: 'Body is not valid JSON.' });
  }
  if (err && err.type === 'entity.too.large') {
    return res.status(400).json({ error: 'body_too_large', message: `Body exceeds ${MAX_BODY_BYTES} bytes.` });
  }

  // Only genuinely unexpected filesystem/programming errors reach here.
  console.error('Unhandled error:', err);
  return res.status(500).json({ error: 'internal_error' });
});

// ---------------------------------------------------------------- boot

function ensureDataRoot() {
  fs.mkdirSync(path.join(DATA_ROOT, 'environments'), { recursive: true });
}

if (require.main === module) {
  ensureDataRoot();
  app.listen(PORT, () => {
    console.log(`meta-environment-json-server listening on http://127.0.0.1:${PORT}`);
    console.log(`DATA_ROOT = ${DATA_ROOT}`);
    console.log(`CORS_ORIGIN = ${CORS_ORIGIN} | REQUIRE_OWNER = ${REQUIRE_OWNER}`);
  });
}

// Exported so tests can mount the app without binding a port.
module.exports = { app, sanitizeId, buildMiniMapDocument, buildDayNightDocument, DATA_ROOT };