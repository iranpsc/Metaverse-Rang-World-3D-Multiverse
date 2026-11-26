# Meta Three Modules - Install

Package: `Meta_ThreeModules_Complete.unitypackage`

## 1) Import

1. Install the target project's packages first: **Universal RP**, **Input System**, **uGUI**, and **Mirror** (MiniMap uses `NetworkClient` / `NetworkIdentity`).
2. Import `Meta_ThreeModules_Complete.unitypackage` via **Assets > Import Package > Custom Package**.

## 2) Open a sample scene or set up in scene

- If `Assets/Script/Extentions/Modules/Meta_Modules_Sample.unity` exists, open it.
- Otherwise open your scene and run **Tools > Meta > Modules Dropdown > Setup In Scene**.

The `Meta_Modules.prefab` root contains `Meta_Modules_DropdownHUD`, the three module rigs, the three control HUDs, and an EventSystem. Drop it into the scene if you prefer manual placement.

## 3) Module setup

- **Tools > Meta > MiniMap > Setup**
- **Tools > Meta > DayNight > Setup**
- **Tools > Meta > CameraForce > Setup**

## 4) HTTP (optional)

- Base URL: `http://127.0.0.1:8788`
- MiniMap: `environments/{env}/minimap/{citizen}.json`
- DayNight: `environments/{env}/daynight.json`

Start either in-module Node stub from its `Server` folder (`npm install`, then `npm start`):

- `Assets/Script/Extentions/Mini Map/Server`
- `Assets/Script/Extentions/Day Night Cycle/Server`

If `Http Base Url` is empty the modules fall back to PlayerPrefs.

## 5) DayNight sky

Assign **Sky Day**, **Sky Dusk**, and **Sky Night** on `Meta_DayNightCycle` if the slots are empty after import.
Included materials: `Sky_Anime_01_Day_a`, `Sky_Anime_01_dusk_b`, `Sky_Anime_01_Night_c`.

## 6) Camera rule

**Never tag module cameras `MainCamera`.** The MiniMap and CameraForce cameras are module-owned and are driven by their rigs.

---

See `Meta_ThreeModules_Complete_Manifest.txt` for the full include list and exclusions.
