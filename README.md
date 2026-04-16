# VR Exposure Therapy (Unity)

This repository is a Unity 6 project for generating and running VR exposure-therapy scenes.

It combines:
- Unity XR runtime scenes
- A local HTTP server inside Unity (`localhost:8080`)
- Dynamic object/environment building from JSON requests
- Optional AI-assisted object placement
- Optional Picture Room image generation pipeline

If you want Copilot to understand this project quickly, use the **Copilot Primer** section below.

## README Maintenance Rule

Whenever code is changed in this repository, update this README in the same change set when behavior, architecture, configuration, endpoints, setup steps, or file responsibilities are affected.

Minimum expectation for each code change:
- Add or update the relevant section in this README.
- Keep examples and endpoint/configuration details in sync with implementation.
- If no README text needs to change, explicitly note that in the PR/commit message.

## Copilot Primer (Paste-Friendly)

Use this as context when prompting Copilot:

```
Project: VR Exposure Therapy (Unity 6)
Unity version: 6000.3.9f1
Primary runtime scene entry points: Assets/Scenes/RelaxationScene.unity, Assets/Scenes/SampleScene.unity

Core behavior:
- Unity starts an in-process HTTP server on localhost:8080 via HttpServerBootstrap.
- API supports GET /status and POST /build-scene.
- POST /build-scene receives JSON (BuildRequest), validates it, resolves environment from EnvironmentRegistry, loads scene/prefab, and spawns objects.
- Object spawn path:
  1) Try AI placement through MERN endpoint (AIPlacementOrchestrator -> http://localhost:5000/api/ai-placement).
  2) If AI fails/timeouts, AI-only mode skips spawning (no ObjectSpawner placement fallback).
- Local object resolution uses StreamingAssets/AssetManifest.json and Resources-loaded prefabs.
- Exposure intensity (0.0-1.0) is applied through PhobiaController.
- Picture Room environment can preload/generate 7 images via image API (default http://127.0.0.1:8000/generate).

Important folders:
- Assets/Scripts/HttpServer: runtime HTTP/API orchestration
- Assets/Scripts/SceneGeneration: scene asset pipeline (local -> Objaverse -> AI generation)
- Assets/Resources: registry assets and Resources-loaded prefabs
- Assets/StreamingAssets/AssetManifest.json: runtime asset catalog

External services expected in local dev:
- Unity HTTP API: localhost:8080 (inside this project)
- MERN backend: localhost:5000
- Objaverse bridge: localhost:5001
- Image generation API: 127.0.0.1:8000

Main classes to inspect first:
- Assets/Scripts/HttpServer/UnityHttpServer.cs
- Assets/Scripts/HttpServer/SceneBuildController.cs
- Assets/Scripts/HttpServer/ObjectSpawner.cs
- Assets/Scripts/HttpServer/AIPlacementOrchestrator.cs
- Assets/Scripts/HttpServer/PictureRoomImageOrchestrator.cs
- Assets/Scripts/HttpServer/EnvironmentRegistry.cs
- Assets/Scripts/HttpServer/AssetManifestLoader.cs
```

## What This Project Does

At runtime, therapist/backend tooling can send scene specifications to Unity. Unity then:
1. Validates request payloads.
2. Loads the requested environment.
3. Spawns requested objects through AI placement only.
4. Applies exposure intensity.
5. Optionally captures a preview and returns response JSON.

This enables gradual, programmable exposure scenarios for VR therapy.

## Tech Stack

- Unity Editor: `6000.3.9f1`
- XR: `com.unity.xr.interaction.toolkit`, `com.unity.xr.management`, OpenXR/Oculus packages
- JSON: `Newtonsoft.Json`
- Rendering: URP (`com.unity.render-pipelines.universal`)
- Optional tooling: `com.ivanmurzak.unity.mcp`

## Runtime Architecture

### 1) Bootstrap
- `HttpServerBootstrap` creates a persistent runtime object `HttpServerRuntime`.
- It ensures required singletons exist before requests arrive:
  - `ObjectSpawner`
  - `PreviewCaptureService`
  - `SceneBuildController`
  - `RelaxationSceneLoadButton`
  - `UnityHttpServer`

### 2) HTTP Layer
- `UnityHttpServer` listens on `http://localhost:8080/`.
- Uses `HttpListener` + `ConcurrentQueue<Action>` to marshal Unity API work onto the main thread.
- Routes:
  - `GET /status`
  - `POST /build-scene`

### 3) Scene Build Orchestration
- `SceneBuildController` is the core coordinator.
- Responsibilities:
  - Request validation
  - Environment lookup via `EnvironmentRegistry`
  - Scene/prefab environment load
  - Camera setup
  - Object placement
  - Intensity application via `PhobiaController`
  - Picture Room image flow

### 4) Object Placement Strategy
- `AIPlacementOrchestrator` (primary placement path)
  - Captures scene views/inventory (`SceneScanner`)
  - Calls MERN AI endpoint
  - Injects base environment JSON and local preloaded asset descriptors into prompt context
  - Parses placement results and instantiates resolved prefabs
  - Resolves local assets first, then Objaverse as strict fallback
  - Skips placement when neither local nor Objaverse provides an asset

### Objaverse Runtime Notes

- Objaverse search may succeed while runtime instantiation still fails if GLB import is not compatible with the installed GLTFast constructor signature.
- `GLBLoader` now attempts multiple `GLTFast.GltfImport` constructor signatures via reflection (not only parameterless construction), improving compatibility across GLTFast versions.
- Scene inventory sent to AI placement now avoids raw `Vector3` serialization to prevent Json.NET self-referencing loop errors.
- If the `SpawnedObject` tag is missing in Unity Tag Manager, spawn/AI paths now continue safely without throwing tag assignment/comparison exceptions.

### 5) Picture Room Flow
- `PictureRoomImageOrchestrator` supports environments identified as `PictureRoom`.
- Expects 7 prompts.
- Generates/downloads images from local image API.
- Applies textures to frame renderers in the scene.

## API Contract

### `GET /status`

Returns readiness and capabilities.

Example response:

```json
{
  "status": "ready",
  "version": "1.0.0",
  "capabilities": ["build-scene", "capture-preview"]
}
```

### `POST /build-scene`

Primary endpoint for scene generation.

Minimal example:

```json
{
  "scene": {
    "environment": "living-room",
    "objects": [
      {
        "id": "spider_001",
        "type": "spider_small",
        "position": { "x": 1.0, "y": 0.0, "z": 2.0 }
      }
    ],
    "camera": {
      "startPosition": { "x": 0, "y": 1.6, "z": 0 },
      "allowMovement": true
    }
  },
  "intensity": 0.5,
  "requestPreview": false
}
```

Example success:

```json
{
  "status": "ok",
  "previewURL": ""
}
```

Example error:

```json
{
  "status": "error",
  "error": "Missing 'scene.environment' field"
}
```

## Key Data Sources

### Environment Registry
- Asset path: `Assets/Resources/EnvironmentRegistry.asset`
- Type: `EnvironmentRegistry` ScriptableObject
- Maps `environmentId` to `sceneName` or `prefab`

### Asset Manifest
- Path: `Assets/StreamingAssets/AssetManifest.json`
- Used for runtime asset lookup and matching.
- Each asset includes fields such as:
  - `id`
  - `addressableKey` (Resources path)
  - `category`
  - `tags`
  - `bounds`

## Base Environment Definitions (Detailed)

Each base environment now has a JSON definition with explicit object anchors and coordinates used by AI placement.

- `Assets/BaseEnvironments/LivingRoom/LivingRoom_config.json`
  - Contains detailed `objects` with `id`, `name`, `type`, `role`, `position`, and `scale`.
  - Key anchors include couch, coffee table, bookshelf, window, floor center, and ceiling center.
- `Assets/BaseEnvironments/Elevator/Elevator.json`
  - Includes floor, left/right/back walls, mirror, control panel, doorway, and ceiling anchors with exact local-space coordinates.
- `Assets/BaseEnvironments/Airport/Airport_Config.json`
  - Defines terminal center, check-in counters, security lane, gate seating, departure board, baggage carousel, and far concourse anchors.
- `Assets/BaseEnvironments/Airplane/Airplane_Config.json`
  - Defines aisle, left/right windows, seat rows, overhead bins, cockpit entry, and ceiling anchors.
- `Assets/BaseEnvironments/Building/Building_Config.json`
  - Defines lobby center, stair landing, balcony edge, rooftop center, window view, safety rail, warning sign, and elevator entry anchors.
- `Assets/BaseEnvironments/PictureRoom/PictureRoom_Config.json`
  - Defines wall frames, center surface, doorway, chair, and ceiling lamp anchors with local positions.
- `Assets/BaseEnvironments/Cliff/Cliff_Config.json`
  - Defines walkway, edge drop, guard rails, warning sign, lookout platform, boulder cluster, and tree landmark anchors.

### How Base Descriptions Are Used At Runtime

- `AIPlacementOrchestrator` loads the selected base environment JSON.
- It converts `objects` into a detailed text inventory (object name/type/role/position/scale).
- It also builds a list of base object identifiers/types.
- The LLM prompt explicitly tells the model to:
  - extract objects from therapist text,
  - exclude objects already present in the base environment,
  - place only new objects using room layout and anchor context.

## Text-To-Placement Flow (LLM)

1. Therapist sends free-text description (`placementText`/`description`/`prompt` aliases are accepted).
2. Runtime gathers base environment JSON + scene layout + live scene inventory.
3. LLM extracts object intents from text and returns structured placements.
4. LLM excludes objects already present in base environment JSON and local preloaded descriptor JSON.
5. LLM returns download candidates for objects not found locally.
6. Returned placements are clamped to room bounds and adjusted for surfaces (wall/floor/ceiling).
7. Objects are instantiated with overlap handling and visibility safeguards.

## Asset Resolution Order For Spawned Objects

Local-first resolution is enforced before any external fetch:

1. Resolve type against `AssetManifest.json` using `LocalAssetResolver`.
2. Attempt prefab load from Resources path variants, prioritizing `PreloadedAssets/...` keys.
3. If local prefab cannot be resolved, query Objaverse via `ObjaverseResolver`.
4. If Objaverse also fails, leave placement uninstantiated (no placeholder spawn).

## File Responsibilities

### `Assets/Scripts/HttpServer`

- `AIPlacementOrchestrator.cs`: Builds AI prompt, calls LLM, parses placements, and performs local-first then Objaverse object instantiation.
- `AssetManifestLoader.cs`: Loads/caches `Assets/StreamingAssets/AssetManifest.json` and exposes asset entries for lookup.
- `EnvironmentRegistry.cs`: ScriptableObject mapping `environmentId` to scene/prefab.
- `HttpServerBootstrap.cs`: Creates persistent runtime host objects and ensures service singletons exist.
- `LocalAssetResolver.cs`: Scores and resolves object types/ids to local manifest assets.
- `Models/Models.cs`: HTTP request/response DTOs and backward-compatible JSON alias parsing.
- `ObjectSpawner.cs`: Utility local/Objaverse resolver path (placeholder spawning disabled).
- `PhobiaController.cs`: Applies exposure intensity behavior to active scene/phobia effects.
- `PictureRoomImageOrchestrator.cs`: Generates/preloads/applies PictureRoom images from image service.
- `PlacementRules.cs`: Data model for placement constraints.
- `PlacementRulesResolver.cs`: Applies placement constraints (surface, distance, height).
- `PreviewCaptureService.cs`: Handles preview capture response path.
- `RelaxationSceneLoadButton.cs`: Handles therapist-selection apply flow from relaxation scene UI.
- `SceneBuildController.cs`: Main orchestrator for request validation, environment load, AI-only placement, and intensity application.
- `SceneScanner.cs`: Captures scene snapshots and inventory for AI context.
- `SmartPlacer.cs`: Collision/surface-aware placement utility.
- `UnityHttpServer.cs`: In-process HTTP server exposing `/status` and `/build-scene`.

### `Assets/Scripts/SceneGeneration`

- `Core/AssetResolutionState.cs`: Resolution state enum/context used by generation pipeline.
- `Core/MatchResult.cs`: Match result data for local registry scoring.
- `Core/SceneAsset.cs`: Scene descriptor asset data (id/category/tags/transform/bounds).
- `Loader/GLBLoader.cs`: Loads GLB assets from URLs at runtime.
- `Loader/ScenePlacer.cs`: Places resolved objects into scene or creates placeholders.
- `Network/MERNBridge.cs`: Fetches scene descriptors from backend API.
- `Pipeline/AssetPipelineOrchestrator.cs`: Multi-stage resolver orchestration (local -> Objaverse -> AI generation).
- `Pipeline/ObjaverseSmokeTest.cs`: Runtime smoke-test helper for Objaverse flow.
- `Registry/LocalAssetRegistry.cs`: In-memory local asset index and matcher.
- `Registry/ManifestUpdater.cs`: Writes newly discovered assets back into manifest.
- `Resolvers/AIGenerationResolver.cs`: AI 3D generation fallback resolver.
- `Resolvers/LocalResolver.cs`: Local scene-generation resolver using local registry.
- `Resolvers/ObjaverseResolver.cs`: Objaverse search/download resolver and cache registration.

## Folder Guide

- `Assets/Scripts/HttpServer/`
  - HTTP listener, build orchestration, AI placement, local spawn, models
- `Assets/Scripts/SceneGeneration/`
  - Multi-stage asset resolution pipeline (local -> Objaverse -> AI generation)
- `Assets/Scenes/`
  - Includes `RelaxationScene.unity` and `SampleScene.unity`
- `Assets/Resources/`
  - Runtime-loadable prefabs and registry assets
- `Assets/StreamingAssets/`
  - Runtime JSON data, notably `AssetManifest.json`
- `Assets/BaseEnvironments/`
  - Environment content folders (LivingRoom, Elevator, Airport, PictureRoom, etc.)

## External Service Dependencies (Local Defaults)

- Unity HTTP server: `http://localhost:8080`
- AI placement service (MERN): `http://localhost:5000/api/ai-placement`
- Objaverse service: `http://localhost:5001`
- Image generation service: `http://127.0.0.1:8000/generate`

If these services are unavailable, the project logs warnings and skips object placement instead of spawning placeholders.

## Local Run / Smoke Test

1. Open project in Unity 6 (`6000.3.9f1`).
2. Press Play.
3. Confirm logs include server startup on `localhost:8080`.
4. Test:

```bash
curl http://localhost:8080/status
```

5. Test build scene:

```bash
curl -X POST http://localhost:8080/build-scene \
  -H "Content-Type: application/json" \
  -d '{
    "scene": {
      "environment": "living-room",
      "objects": [
        {"id": "obj_1", "type": "chair", "position": {"x": 0, "y": 0, "z": 2}}
      ]
    },
    "intensity": 0.3,
    "requestPreview": false
  }'
```

## Known Conventions and Compatibility

- Main-thread safety: Unity API operations are executed on the main thread via queueing.
- Backward-compatible JSON aliases are supported in `BuildRequest`/`SceneData`.
- Environment IDs are normalized for variants like hyphens/underscores.
- Missing assets are skipped (no placeholder spawning).

## Suggested Prompting Strategy for Copilot

When asking Copilot for changes, include:
- Target environment behavior (example: PictureRoom preloading, arachnophobia intensity behavior).
- Whether AI placement should remain required (AI-only placement mode).
- Whether changes must preserve HTTP request compatibility (`/build-scene` aliases).
- Which class is the integration point (`SceneBuildController` for orchestration, `ObjectSpawner` for local placement, `AIPlacementOrchestrator` for AI flow).

A good prompt format:

```
In this Unity VR project, update [class/file] to [goal].
Constraints:
1) Keep /build-scene request compatibility.
2) Preserve AI-only placement behavior.
3) Keep Unity API calls on main thread.
4) Add logs prefixed with [SceneBuild] or [Spawner].
```
