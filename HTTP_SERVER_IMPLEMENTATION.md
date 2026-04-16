## VR Exposure Therapy — HTTP Server Implementation Summary

This document summarizes the complete HTTP server implementation for the VR scene builder.

### Architecture Overview

**HTTP Server Stack:**
- UnityHttpServer.cs: Core HTTP listener on localhost:8080
- Runs on port 8080, responds to GET /status and POST /build-scene
- Uses ConcurrentQueue to marshal work from background HTTP thread to Unity main thread
- All responses are JSON with Content-Type: application/json

**Scene Building Pipeline:**
1. Request arrives → Validation (BuildRequest structure, coordinates, environment ID)
2. Environment Registry lookup (with hyphen/underscore normalization)
3. Load base environment (scene or prefab)
4. Set camera position
5. For each object:
   - Asset resolution (exact match or tag-based scoring)
   - Prefab loading from Resources/
   - Instantiation at requested position
   - Fallback: magenta placeholder cube with warning
6. Apply intensity to PhobiaController
7. Capture optional preview
8. Return response or error

### Files Implemented/Modified

**New Files:**
1. **AssetManifestLoader.cs**
   - Parses StreamingAssets/AssetManifest.json
   - Provides access to asset metadata (id, addressableKey, category, tags, bounds)
   - Singleton pattern with static access methods
   - Validates manifest exists and has entries

2. **LocalAssetResolver.cs**
   - Matches scene object.type to asset entries
   - Scoring system: exact match (+100), tag overlap (+5/10/20), category (+15)
   - Returns best match or null with reason
   - Logs resolution attempts for debugging

3. **ObjectSpawner.cs** (Enhanced)
   - Loads actual prefabs from Resources/ using addressableKey
   - Creates magenta placeholder cubes for unmatched assets
   - Parents all spawned objects under "SpawnedObjects" container
   - Clears previous spawns before each new build
   - Clamps y position to floor (0) if below

4. **PhobiaController.cs**
   - Base class for phobia-specific exposure controllers
   - ApplyIntensity(0.0 to 1.0) method for exposure adjustment
   - ArachnophobiaController implementation:
     - Adjusts spider count, scale, speed, lighting based on intensity
     - Example: At intensity 1.0 → 15 spiders, size 2.0, speed 3.0

**Modified Files:**
1. **SceneBuildController.cs**
   - Added ValidateRequest() method with specific error messages:
     - Missing scene/environment fields
     - NaN in coordinates
     - Invalid object/camera structures
   - Enhanced BuildSceneNow() with comprehensive logging
   - Added intensity application to PhobiaController
   - Logs every step: environment, object count, placement details

2. **Models/Models.cs**
   - Added intensity field to BuildRequest (float?, 0.0-1.0)

3. **UnityHttpServer.cs** (Already in place)
   - GET /status → Returns ready status with version and capabilities
   - POST /build-scene → Routes to SceneBuildController with validation
   - Thread-safe main thread queue implementation

### API Endpoints

#### GET /status
**Response:**
```json
{
  "status": "ready",
  "version": "1.0.0",
  "capabilities": ["build-scene", "capture-preview"]
}
```

#### POST /build-scene
**Request:**
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
    "triggers": [],
    "camera": {
      "startPosition": { "x": 0, "y": 1.6, "z": 0 },
      "allowMovement": true
    }
  },
  "intensity": 0.5,
  "timestamp": "2026-04-02T...",
  "requestPreview": true
}
```

**Success Response (HTTP 200):**
```json
{
  "status": "ok",
  "previewURL": "/mock-preview.png"
}
```

**Error Response (HTTP 200 with error status):**
```json
{
  "status": "error",
  "error": "Missing 'scene.environment' field"
}
```

### Validation Rules

**Request Validation:**
✓ scene field exists and is not null
✓ environment field exists and is non-empty
✓ camera.startPosition fields (if provided) are valid floats (not NaN)
✓ each object has id, type, position
✓ each position has x, y, z as valid numbers (not NaN)
✓ intensity (if provided) is not NaN and in range 0.0-1.0

**Environment Validation:**
✓ environment ID found in EnvironmentRegistry
✓ Registry entry has either sceneName or prefab (not both empty)
✓ Can match "indoor-room" → "indoor_room" or similar variants

**Asset Validation:**
✓ AssetManifest.json exists and is valid JSON
✓ Asset matches are scored (exact > tag overlap > category)
✓ Unmatched objects still spawn as placeholders

### Logging Format

Every request logs with standardized prefixes:
```
[HttpServer] POST /build-scene received
[SceneBuild] environment: living-room
[SceneBuild] objects count: 3
[LocalResolver] Matched 'spider_small' → spider_small (exact match)
[Spawner] Placed spider_small at (1.0, 0.0, 2.0)
[Spawner] Spawned 3 objects + 0 placeholders
[SceneBuild] ✓ Scene built successfully
```

### Coordinate System

- X axis: left/right (negative = left, positive = right)
- Y axis: up/down (0 = floor, 1.5 = mid-wall, 3.0 = ceiling)
- Z axis: forward/back (positive = forward away from player start)
- Coordinates received from backend are already in Unity space (no transformation needed)

### Environment ID Mapping

Currently supported (from EnvironmentRegistry):
- living-room / living_room → LivingRoom scene or prefab
- cave / Cave → Cave scene or prefab
- (Additional environments configured in EnvironmentRegistry.asset)

The system normalizes IDs (removes hyphens/underscores) for flexible matching.

### Asset Resolution Scoring

When matching scene object.type to assets:
1. Exact ID match → score +100 (immediate winner)
2. If no exact match, score remaining assets:
   - Type found in tags → +20
   - Type partially in tag → +10
   - Type in category → +15
   - Object tags overlap with asset tags → +5 per match

Winner with highest score is selected as prefab source.

### Thread Safety

- HTTP requests handled on background threads
- All scene operations queued via ConcurrentQueue
- Main thread drains queue every frame in Update()
- Prevents crashes from concurrent Instantiate/UnityEngine API calls

### PhobiaController Integration

After scene build, intensity is applied:
```csharp
PhobiaController.Instance?.ApplyIntensity(intensity);
```

For arachnophobia, intensity drives:
- Spider spawn count: 0 at 0.0, 15 at 1.0
- Spider scale: 0.2 at 0.0, 2.0 at 1.0
- Spider speed: 0.0 at 0.0, 3.0 at 1.0
- Ambient light: 1.2 at 0.0, 0.25 at 1.0

### Testing Checklist

Before deployment, verify:

1. **HTTP Server Boot**
   - [ ] Play mode starts HttpServer on localhost:8080
   - [ ] GET /status returns 200 with ready status
   - [ ] DEBUG logs show "Listening on http://localhost:8080/"

2. **Basic Build Request**
   - [ ] POST /build-scene with valid request
   - [ ] Response is { "status": "ok", "previewURL": "..." }
   - [ ] Environment loads in scene
   - [ ] Objects spawn at correct positions

3. **Validation**
   - [ ] Missing environment → error response
   - [ ] NaN in position → error response
   - [ ] Invalid JSON → error response
   - [ ] All errors have specific, helpful messages

4. **Asset Resolution**
   - [ ] Exact type match finds prefab
   - [ ] Tag-based match works
   - [ ] Unmatched type uses magenta placeholder
   - [ ] Prefabs load from Resources/

5. **Intensity Application**
   - [ ] Intensity passed through
   - [ ] PhobiaController.ApplyIntensity() called
   - [ ] Arachnophobia effects visible (scale/speed changes)

6. **Logging**
   - [ ] Console shows all named tags: [HttpServer], [SceneBuild], [Spawner], [LocalResolver]
   - [ ] Each object placement logged
   - [ ] Errors logged with [HttpServer] or [SceneBuild] prefix

### Future Extensions

- Trigger support (beyond GET /build-scene parsing)
- Multiple simultaneous environment loadingPatterns (presets)
- Stress testing with rapid requests
- Performance profiling for large object counts
