# VR Exposure Therapy — HTTP Server Quick Start

## Setup Instructions

### 1. Ensure EnvironmentRegistry is Configured
- File: `Assets/Resources/EnvironmentRegistry.asset`
- Should contain at minimum:
  - `living_room` or `living-room` → points to LivingRoom scene/prefab
  - Additional environments (cave, rooftop, etc.)

If missing, run the Editor setup:
- Editor will auto-create it when you open the project
- Check Debug logs for: `[SceneBuild] Added living_room mapping to EnvironmentRegistry.asset`

### 2. Populate AssetManifest.json
- File: `Assets/StreamingAssets/AssetManifest.json`
- Must contain asset entries with:
  - `id`: unique identifier (e.g., "spider_small")
  - `addressableKey`: Resources path without extension (e.g., "Creatures/Spiders/spider_small")
  - `tags`: array for matching (e.g., ["spider", "small", "creature"])
  - `bounds`: size information

Example entry:
```json
{
  "id": "spider_small",
  "addressableKey": "Creatures/Spiders/spider_small",
  "category": "creature",
  "tags": ["spider", "small", "arachnid"],
  "bounds": { "x": 0.2, "y": 0.1, "z": 0.2 }
}
```

### 3. Place Prefabs in Resources
- If assets use `addressableKey: "Creatures/Spiders/spider_small"`
- Create prefab at: `Assets/Resources/Creatures/Spiders/spider_small.prefab`
- Prefabs should be game-ready (renderer, colliders, etc.)

### 4. Start Play Mode
- Press Play in Unity Editor
- Watch Debug console for:
  ```
  [HttpServer] Listening on http://localhost:8080/
  [HttpServer] Runtime bootstrap created and listener starting
  [AssetManifest] Loaded N assets from manifest
  ```

## Testing the HTTP Server

### Test 1: Check Server Status
**Command:**
```bash
curl http://localhost:8080/status
```

**Expected Response:**
```json
{
  "status": "ready",
  "version": "1.0.0",
  "capabilities": ["build-scene", "capture-preview"]
}
```

### Test 2: Build a Simple Scene
**Command:**
```bash
curl -X POST http://localhost:8080/build-scene \
  -H "Content-Type: application/json" \
  -d '{
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
  }'
```

**Expected Response:**
```json
{
  "status": "ok",
  "previewURL": ""
}
```

**Console Output:**
```
[HttpServer] POST /build-scene received
[SceneBuild] environment: living-room
[SceneBuild] objects count: 1
[LocalResolver] Matched 'spider_small' → spider_small (exact match)
[Spawner] Placed spider_small at (1.0, 0.0, 2.0)
[Spawner] Spawned 1 objects + 0 placeholders
[SceneBuild] ✓ Scene built successfully
```

### Test 3: Validation Error (Missing Environment)
**Command:**
```bash
curl -X POST http://localhost:8080/build-scene \
  -H "Content-Type: application/json" \
  -d '{"scene": {"environment": "", "objects": []}}'
```

**Expected Response:**
```json
{
  "status": "error",
  "error": "Missing or empty 'scene.environment' field"
}
```

## Debug Troubleshooting

### HTTP Server Not Starting
**Check:**
- Play mode is active
- Port 8080 is not in use: `netstat -ano | findstr :8080` (Windows)
- Debug console shows: `[HttpServer] Listening on http://localhost:8080/`

### Prefabs Not Loading
**Check:**
- AssetManifest.json has correct `addressableKey`
- Prefab exists at `Assets/Resources/{addressableKey}.prefab`
- Run Log in console should show: `[LocalResolver] Matched 'type' → asset_id`
- If not found: will show `[Spawner] No asset for 'type' → using placeholder`

### Objects Appear as Magenta Cubes
**Reason:** Asset not found in manifest or prefab not in Resources
**Solution:**
- Add target asset to AssetManifest.json
- Create prefab at the Resources path specified by addressableKey
- Restart Play mode

### Invalid Camera Coordinates
**Example Error:**
```
{
  "status": "error",
  "error": "Invalid camera position: contains NaN values"
}
```

**Solution:**
- Ensure camera.startPosition has valid x, y, z numbers
- Avoid NaN, Infinity, null values
- Example valid position: `{ "x": 0, "y": 1.6, "z": 0 }`

## Architecture Highlights

### Thread Safety
- HTTP requests handled on background threads
- Work marshaled to main thread via ConcurrentQueue
- All Unity API calls happen on main thread only

### Asset Resolution Algorithm
1. Check for exact type match in asset manifest
2. If no exact match, score remaining assets by:
   - Tag overlap with requested type
   - Category matching
   - Keyword containment
3. Load highest-scoring asset or use placeholder

### Intensity System
- Values 0.0 to 1.0 mapped to exposure intensity
- For arachnophobia:
  - 0.0 = no spiders, bright lighting
  - 0.5 = moderate spiders, normal lighting
  - 1.0 = many spiders, dim lighting, fast movement

## Common Request Patterns

### Spawn Multiple Objects
```json
{
  "scene": {
    "environment": "living-room",
    "objects": [
      { "id": "spider_wall", "type": "spider_small", "position": { "x": -3.0, "y": 1.5, "z": 0 } },
      { "id": "spider_floor", "type": "spider_medium", "position": { "x": 1.0, "y": 0.0, "z": 2.0 } },
      { "id": "chair_01", "type": "chair_wood", "position": { "x": 0, "y": 0.0, "z": -2.0 } }
    ]
  }
}
```

### Progressive Intensity Build
Request 1: `"intensity": 0.25` → Few, small creatures, bright
Request 2: `"intensity": 0.50` → Moderate, normal size, normal light
Request 3: `"intensity": 0.75` → Many, large, dim
Request 4: `"intensity": 1.00` → Maximum exposure

## Performance Notes

- Asset manifest loaded once per application session
- Prefab instantiation time depends on prefab complexity
- Placeholder creation is instant (magenta cube)
- Multiple rapid requests are queued and processed sequentially

## Next Steps

1. **Create test assets:**
   - Make a sample spider prefab at `Assets/Resources/Creatures/Spiders/spider_small.prefab`
   - Update AssetManifest.json with entries for your creatures

2. **Connect MERN backend:**
   - Backend sends POST /build-scene requests to localhost:8080
   - Parse Unity responses (ok/error with previewURL)

3. **Implement custom PhobiaController:**
   - Extend `ArachnophobiaController` with actual spider AI behavior
   - Override `ApplyIntensity()` to drive behavior changes

4. **Add preview capture:**
   - Implement `PreviewCaptureService.Capture()` to return actual screenshot URLs
   - Currently returns empty string (mock)
