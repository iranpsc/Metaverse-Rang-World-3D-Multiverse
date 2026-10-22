# MiniMap — module-bundled Environment JSON Server

Self-contained copy of the shared `tools/meta-environment-json-server` so
`feature/meta/minimap-citizen-persist` can be QA'd without cloning the whole
repo tree. Same API contract; this README documents **only the minimap
endpoints** (the shared `server.js` also serves day/night but that is another
module's contract).

## Run (local)

```bash
cd "Assets/Script/Extentions/Mini Map/Server"
npm install
npm start
# http://127.0.0.1:8788/health  ->  {"ok":true,"dataRoot":".../data"}
```

| Env var | Default | Meaning |
|---|---|---|
| `PORT` | `8788` | listen port |
| `DATA_ROOT` | `./data` | JSON tree root (this bundle ships an empty `data/.gitkeep`) |
| `REQUIRE_OWNER` | `false` | strict header mode for day/night writes (minimap writes never need it) |

## Contract — minimap only

```text
GET  /health
GET  /environments/:environmentId/minimap/:citizenId.json
PUT  /environments/:environmentId/minimap/:citizenId.json      body = settings JSON
GET  /environments/:environmentId/minimap/missing_user.json    -> 404 {"error":"not_found"}
```

- `citizenId` in the body is ignored: the **URL wins** and is stored/stamped.
- Every write adds `updatedAt`; version field must be present (`"version":1`).
- Path-traversal ids (e.g. `..%2F..`) → `400 {"error":"invalid_id"}`.

## Curl (minimap only)

```bash
# save radar=on, zoom 28, altitude 60 for citizen e2e_user in place lobby_1
curl -s -X PUT "http://127.0.0.1:8788/environments/lobby_1/minimap/e2e_user.json" ^
  -H "Content-Type: application/json" ^
  --data '{"version":1,"citizenId":"ignored","radarEnabled":true,"zoomOrOrthoSize":28,"offsetY":60}'
# -> 200 {"version":1,"citizenId":"e2e_user",...,"updatedAt":"..."}

curl -s "http://127.0.0.1:8788/environments/lobby_1/minimap/e2e_user.json"             # 200 round-trip
curl -s "http://127.0.0.1:8788/environments/lobby_1/minimap/other_user.json"           # 404
```
