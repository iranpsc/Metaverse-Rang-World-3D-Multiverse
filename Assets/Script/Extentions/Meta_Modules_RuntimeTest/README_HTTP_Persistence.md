# Module HTTP Persistence (MiniMap + DayNight)

The MiniMap and DayNight modules can persist their state as JSON through the
in-module Node settings service. That service (see the module `Server` folder)
is the only backend contract; do not stand up a second server.

## Inspector fields

`Meta_MiniMap`

- `Use Http Citizen Store`
- `Http Base Url`
- `Citizen Id` / `Environment Id`

`Meta_DayNightCycle`

- `Use Http Store`
- `Http Base Url`
- `Environment Id`

If HTTP is enabled but `Http Base Url` is empty, the component logs one clear
warning and falls back to PlayerPrefs. Local persistence keeps working.

## Base URL examples

- Local: `http://127.0.0.1:8788`
- Remote: `http://217.218.238.201:8788`

Keep the URL already serialized in a scene. Leave the field empty for offline
builds.

## JSON paths

- MiniMap: `environments/{env}/minimap/{citizen}.json`
- DayNight: `environments/{env}/daynight.json`
