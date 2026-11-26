# Day/Night — module-bundled Environment JSON Server

Self-contained copy of the shared `tools/meta-environment-json-server` so
`feature/meta/daynight-env-persist` can be QA'd without cloning the whole repo
tree. Same API contract; this README documents **only the day/night endpoints**
(the shared `server.js` also serves minimap but that is another module's
contract).

## Run (local)

```bash
cd "Assets/Script/Extentions/Day Night Cycle/Server"
npm install
npm start
# http://127.0.0.1:8788/health  ->  {"ok":true,"dataRoot":".../data"}
```

| Env var | Default | Meaning |
|---|---|---|
| `PORT` | `8788` | listen port |
| `DATA_ROOT` | `./data` | JSON tree root (this bundle ships an empty `data/.gitkeep`) |
| `REQUIRE_OWNER` | `false` | when `true`, day/night writes WITHOUT the owner header → `403` |

## Contract — day/night only

```text
GET  /health
GET  /environments/:environmentId/daynight.json
PUT  /environments/:environmentId/daynight.json
     Header: X-Is-Environment-Owner: true      (required in REQUIRE_OWNER mode)
     Body:   {"version":1,"time01":0.25,"cycleRunning":true,"rotationSpeed":1.0}
GET  /environments/:environmentId/missing.json  -> 404 {"error":"not_found"}
```

- `environmentId` is forced from the URL (`environmentId` in the body cannot win).
- `rotationSpeed` / canonical mapping are preserved so the owner reload keeps its cycle speed.
- Visitors (`IsEnvironmentOwner = false`) never save — the server rejects them in
  strict mode and the module itself no-ops the save locally.

## Curl (day/night only)

```bash
# owner persists the current time of day for the place lobby_1
curl -s -X PUT "http://127.0.0.1:8788/environments/lobby_1/daynight.json" ^
  -H "Content-Type: application/json" ^
  -H "X-Is-Environment-Owner: true" ^
  --data '{"version":1,"time01":0.42,"cycleRunning":true,"rotationSpeed":1.5}'
# -> 200 {"version":1,"environmentId":"lobby_1","time01":0.42,...}

curl -s "http://127.0.0.1:8788/environments/lobby_1/daynight.json"    # 200 round-trip

# strict mode check (restart with REQUIRE_OWNER=true first)
curl -s -X PUT "http://127.0.0.1:8788/environments/lobby_1/daynight.json" ^
  -H "Content-Type: application/json" ^
  --data '{"version":1,"time01":0.5}'                                  # -> 403 not_environment_owner
```
