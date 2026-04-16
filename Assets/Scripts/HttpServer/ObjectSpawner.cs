using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Spawns scene objects by resolving them through the asset system.
/// Instantiates prefabs from Resources and optionally falls back to Objaverse.
/// All spawned objects are tagged "SpawnedObject" and parented to "SpawnedObjects" container.
/// </summary>
public class ObjectSpawner : MonoBehaviour {

    public class SpawnBatchResult {
        public int localCount;
        public int objaverseCount;
        public int placeholderCount;
    }

    public static ObjectSpawner Instance { get; private set; }

    Transform spawnedObjectsContainer;
    Transform spawnParentOverride;
    const int VisibilityPlacementRings = 4;
    const float VisibilityPlacementStep = 0.6f;
    const float ForcedVisibleDistance = 1.8f;
    const float ForcedVisibleWorldSize = 0.35f;

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        
        EnsureContainer();
        SmartPlacer.EnsureInstance();
    }

    void EnsureContainer() {
        if (spawnedObjectsContainer != null) return;

        var existing = GameObject.Find("SpawnedObjects");
        if (existing != null) {
            spawnedObjectsContainer = existing.transform;
        } else {
            var container = new GameObject("SpawnedObjects");
            spawnedObjectsContainer = container.transform;
        }
    }

    public void ClearSpawnedObjects() {
        if (spawnedObjectsContainer == null)
            EnsureContainer();

        foreach (Transform child in spawnedObjectsContainer) {
            Destroy(child.gameObject);
        }

        Debug.Log("[Spawner] Cleared all spawned objects");
    }

    public void SetSpawnParent(Transform parent) {
        spawnParentOverride = parent;
        EnsureContainer();

        if (spawnParentOverride != null && spawnedObjectsContainer != null && spawnedObjectsContainer.parent != spawnParentOverride) {
            spawnedObjectsContainer.SetParent(spawnParentOverride, worldPositionStays: false);
            spawnedObjectsContainer.localPosition = Vector3.zero;
            spawnedObjectsContainer.localRotation = Quaternion.identity;
            spawnedObjectsContainer.localScale = Vector3.one;
        }
    }

    public void SpawnObjects(SceneObjectData[] objects) {
        if (objects == null || objects.Length == 0) return;

        EnsureContainer();
        SmartPlacer.EnsureInstance().ClearRegisteredBounds();
        ClearSpawnedObjects();

        int successCount = 0;
        int skippedCount = 0;

        foreach (var obj in objects) {
            if (obj == null) continue;

            if (string.IsNullOrEmpty(obj.type)) {
                Debug.LogWarning($"[Spawner] Skipping object with missing type");
                continue;
            }

            EnsureRuntimeObjectId(obj);

            PositionData pos = obj.position ?? new PositionData { x = 0, y = 0, z = 0 };

            // Clamp y to floor level if below
            if (pos.y < 0) pos.y = 0;

            // Try to resolve and load actual asset
            var resolution = LocalAssetResolver.Resolve(obj.id, obj.type);

            if (resolution.found && resolution.asset != null) {
                if (TrySpawnPrefab(obj, resolution, pos)) {
                    successCount++;
                    continue;
                }
            }

            skippedCount++;
            Debug.LogWarning($"[Spawner] No local asset for '{obj.type}' and placeholder spawning is disabled; skipping.");
        }

        Debug.Log($"[Spawner] Spawned {successCount} local objects; skipped {skippedCount} missing objects");
    }

    public async Task<SpawnBatchResult> SpawnObjectsWithObjaverseFallback(SceneObjectData[] objects) {
        var summary = new SpawnBatchResult();
        if (objects == null || objects.Length == 0)
            return summary;

        EnsureContainer();
        SmartPlacer.EnsureInstance().ClearRegisteredBounds();
        ClearSpawnedObjects();

        foreach (var obj in objects) {
            if (obj == null)
                continue;

            if (string.IsNullOrEmpty(obj.type)) {
                Debug.LogWarning("[Spawner] Skipping object with missing type");
                continue;
            }

            EnsureRuntimeObjectId(obj);

            PositionData pos = obj.position ?? new PositionData { x = 0, y = 0, z = 0 };
            if (pos.y < 0)
                pos.y = 0;

            var resolution = LocalAssetResolver.Resolve(obj.id, obj.type);
            if (resolution.found && resolution.asset != null) {
                if (TrySpawnPrefab(obj, resolution, pos)) {
                    summary.localCount++;
                    continue;
                }
            }

            bool spawnedFromObjaverse = await TrySpawnFromObjaverse(obj, pos);
            if (spawnedFromObjaverse) {
                summary.objaverseCount++;
                continue;
            }

            summary.placeholderCount++;
            Debug.LogWarning($"[Spawner] No local or Objaverse asset found for '{obj.type}' -> skipping (no placeholder).");
        }

        Debug.Log($"[Spawner] Spawned local={summary.localCount}, objaverse={summary.objaverseCount}, skipped={summary.placeholderCount}");
        return summary;
    }

    async Task<bool> TrySpawnFromObjaverse(SceneObjectData obj, PositionData pos) {
        if (obj == null || string.IsNullOrWhiteSpace(obj.type))
            return false;

        var resolver = EnsureObjaverseResolver();
        if (resolver == null)
            return false;

        var requestAsset = BuildObjaverseRequestAsset(obj);
        Debug.Log($"[Spawner] Trying Objaverse for type='{obj.type}' id='{requestAsset.id}' category='{requestAsset.category}' tags=[{string.Join(",", requestAsset.tags ?? new string[0])}]");
        var downloaded = await resolver.TryResolve(requestAsset);
        if (downloaded == null)
            return false;

        downloaded.name = $"OBJAVERSE_{requestAsset.id}";
        TryAssignSpawnedTag(downloaded);
        downloaded.transform.SetParent(spawnedObjectsContainer, worldPositionStays: false);

        Vector3 requestedPos = new Vector3(pos.x, pos.y, pos.z);
        if (spawnParentOverride != null) {
            TryPlaceVisibleLocalAI(downloaded, requestedPos, PlacementType.Floor);
            ConstrainInsideParentBounds(downloaded.transform);
        } else {
            bool placed = SmartPlacer.EnsureInstance().TryPlace(downloaded, requestedPos, PlacementType.Floor);
            if (!placed) {
                downloaded.transform.position = requestedPos;
                downloaded.transform.rotation = Quaternion.identity;
            }

            TryImproveVisibilityWorldAI(downloaded, requestedPos);
            EnsureVisibleScale(downloaded.transform);
        }

        ForceVisibleFallback(downloaded);
        Debug.Log($"[Spawner] Objaverse fallback placed {obj.type} '{obj.id}' at {downloaded.transform.position}");
        return true;
    }

    ObjaverseResolver EnsureObjaverseResolver() {
        if (ObjaverseResolver.Instance != null)
            return ObjaverseResolver.Instance;

        var host = GameObject.Find("HttpServerRuntime");
        if (host == null)
            host = gameObject;

        var resolver = host.GetComponent<ObjaverseResolver>();
        if (resolver == null)
            resolver = host.AddComponent<ObjaverseResolver>();

        return resolver;
    }

    static SceneAsset BuildObjaverseRequestAsset(SceneObjectData obj) {
        string normalizedType = obj.type.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");

        // Use provided category and tags if available (from inferred objects)
        string category = !string.IsNullOrWhiteSpace(obj.category) ? obj.category : obj.type.Replace("_", " ");
        string[] tags = (obj.tags != null && obj.tags.Length > 0) ? obj.tags : new[] { obj.type };

        // Ensure tags include the base type for fallback matching
        var tagsList = new List<string>(tags);
        if (!tagsList.Contains(obj.type))
            tagsList.Add(obj.type);

        string asSpaces = obj.type.Replace("_", " ").Replace("-", " ");
        if (!string.Equals(asSpaces, obj.type, System.StringComparison.OrdinalIgnoreCase) && !tagsList.Contains(asSpaces))
            tagsList.Add(asSpaces);

        return new SceneAsset {
            id = string.IsNullOrWhiteSpace(obj.id) ? normalizedType : obj.id,
            category = category,
            tags = tagsList.ToArray(),
            preferredStyle = string.Empty
        };
    }

    static void EnsureRuntimeObjectId(SceneObjectData obj) {
        if (obj == null || !string.IsNullOrWhiteSpace(obj.id) || string.IsNullOrWhiteSpace(obj.type))
            return;

        string seed = $"{obj.type}|{obj.position?.x:F2}|{obj.position?.y:F2}|{obj.position?.z:F2}";
        uint hash = 2166136261;

        for (int i = 0; i < seed.Length; i++) {
            hash ^= seed[i];
            hash *= 16777619;
        }

        obj.id = $"{obj.type.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_')}_{hash:x8}";
    }

    bool TrySpawnPrefab(SceneObjectData obj, LocalAssetResolver.ResolutionResult resolution, PositionData pos) {
        GameObject prefab = null;
        string[] pathVariants = GetResourcePathVariants(resolution.prefabPath);

        // Try multiple path variations for resource loading
        foreach (var resourcePath in pathVariants) {
            prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab != null) {
                Debug.Log($"[Spawner] Loaded prefab from Resources/{resourcePath}");
                break;
            }
        }

        if (prefab == null) {
            prefab = TryLoadEditorAssetFallback(obj, resolution);
        }

        if (prefab == null) {
            Debug.LogWarning($"[Spawner] Prefab not found for '{resolution.prefabPath}' (tried: {string.Join(", ", pathVariants)})");
            return false;
        }

        GameObject instance = Instantiate(prefab);
        instance.name = $"{resolution.asset.id}_{obj.id}";
        TryAssignSpawnedTag(instance);
        instance.transform.SetParent(spawnedObjectsContainer, worldPositionStays: false);

        var rules = resolution.asset.placement;
        var placementType = PlacementRulesResolver.Resolve(rules);

        Vector3 requestedPos = new Vector3(pos.x, pos.y, pos.z);
        requestedPos = PlacementRulesResolver.ApplyHeightConstraint(requestedPos, rules);

        if (!PlacementRulesResolver.ValidateDistance(requestedPos, rules))
            requestedPos = PlacementRulesResolver.EnforceDistance(requestedPos, rules);

        if (spawnParentOverride != null) {
            TryPlaceVisibleLocalAI(instance, requestedPos, placementType);
            Debug.Log($"[Spawner] Local-placed {instance.name} near {requestedPos} under '{spawnedObjectsContainer.parent?.name}'");
        } else {
            bool placed = SmartPlacer.EnsureInstance().TryPlace(instance, requestedPos, placementType);
            if (!placed) {
                // Last-resort fallback so object still appears even if smart placement cannot resolve.
                instance.transform.position = requestedPos;
                instance.transform.rotation = Quaternion.identity;
                Debug.LogWarning($"[Spawner] Smart placement failed for {instance.name}; used direct position fallback at {requestedPos}");
            }
            TryImproveVisibilityWorldAI(instance, requestedPos);
            EnsureVisibleScale(instance.transform);
        }

        ForceVisibleFallback(instance);
        Debug.Log($"[Spawner] Placed {obj.type} '{obj.id}' at {instance.transform.position}");
        return true;
    }

    Vector3 NormalizeToParentLocalSpace(Vector3 requestedPos) {
        if (spawnParentOverride == null)
            return requestedPos;

        // If the requested point falls within the environment's world bounds,
        // it is very likely a world-space coordinate from AI placement.
        if (TryGetRenderableBounds(spawnParentOverride.gameObject, out var parentBounds)) {
            if (parentBounds.Contains(requestedPos)) {
                var converted = spawnParentOverride.InverseTransformPoint(requestedPos);
                Debug.Log($"[Spawner] Converted world position {requestedPos} to local {converted} under '{spawnParentOverride.name}'");
                return converted;
            }
        }

        return requestedPos;
    }

    Vector3 ConvertModelUnitsToLocal(Vector3 modelUnitsPos) {
        if (spawnParentOverride == null)
            return modelUnitsPos;

        var s = spawnParentOverride.lossyScale;
        float sx = Mathf.Abs(s.x) < 0.0001f ? 1f : s.x;
        float sy = Mathf.Abs(s.y) < 0.0001f ? 1f : s.y;
        float sz = Mathf.Abs(s.z) < 0.0001f ? 1f : s.z;

        return new Vector3(modelUnitsPos.x / sx, modelUnitsPos.y / sy, modelUnitsPos.z / sz);
    }

    void ConstrainInsideParentBounds(Transform target) {
        if (target == null || spawnParentOverride == null)
            return;

        if (!TryGetRenderableBounds(spawnParentOverride.gameObject, out var bounds))
            return;

        const float padding = 0.05f;
        var world = target.position;
        world.x = Mathf.Clamp(world.x, bounds.min.x + padding, bounds.max.x - padding);
        world.y = Mathf.Clamp(world.y, bounds.min.y + 0.02f, bounds.max.y - 0.02f);
        world.z = Mathf.Clamp(world.z, bounds.min.z + padding, bounds.max.z - padding);
        target.position = world;
    }

    static bool TryGetRenderableBounds(GameObject root, out Bounds bounds) {
        bounds = default;
        if (root == null)
            return false;

        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
        if (renderers == null || renderers.Length == 0)
            return false;

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return true;
    }

    static void EnsureVisibleScale(Transform target) {
        if (target == null)
            return;

        var renderers = target.GetComponentsInChildren<Renderer>(includeInactive: false);
        if (renderers == null || renderers.Length == 0)
            return;

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (maxDim >= 0.08f)
            return;

        float factor = Mathf.Clamp(0.2f / Mathf.Max(maxDim, 0.001f), 1f, 20f);
        target.localScale *= factor;
        Debug.LogWarning($"[Spawner] Auto-scaled tiny object '{target.name}' by x{factor:F2} for visibility");
    }

    void TryPlaceVisibleLocalAI(GameObject instance, Vector3 requestedPos, PlacementType placementType) {
        if (instance == null)
            return;

        Vector3 bestLocal = Vector3.zero;
        float bestScore = float.NegativeInfinity;
        bool foundVisible = false;

        foreach (var candidate in EnumerateCandidates(requestedPos)) {
            Vector3 normalized = NormalizeToParentLocalSpace(candidate);
            Vector3 local = ConvertModelUnitsToLocal(normalized);

            instance.transform.localPosition = local;
            AlignLocalRotation(instance.transform, placementType);
            ConstrainInsideParentBounds(instance.transform);
            EnsureVisibleScale(instance.transform);

            float score = ScoreVisibility(instance);
            if (score > bestScore) {
                bestScore = score;
                bestLocal = instance.transform.localPosition;
            }

            if (score >= 1f) {
                foundVisible = true;
                break;
            }
        }

        if (!foundVisible) {
            instance.transform.localPosition = bestLocal;
            Debug.LogWarning($"[SpawnerAI] No fully visible local placement for '{instance.name}', used best candidate (score {bestScore:F2})");
        }
    }

    void TryImproveVisibilityWorldAI(GameObject instance, Vector3 requestedPos) {
        if (instance == null)
            return;

        float current = ScoreVisibility(instance);
        if (current >= 1f)
            return;

        Vector3 bestWorld = instance.transform.position;
        float bestScore = current;

        foreach (var candidate in EnumerateCandidates(requestedPos)) {
            instance.transform.position = candidate;
            float score = ScoreVisibility(instance);
            if (score > bestScore) {
                bestScore = score;
                bestWorld = candidate;
            }

            if (score >= 1f)
                break;
        }

        instance.transform.position = bestWorld;
        if (bestScore < 1f) {
            Debug.LogWarning($"[SpawnerAI] World placement for '{instance.name}' is partially visible (score {bestScore:F2})");
        }
    }

    IEnumerable<Vector3> EnumerateCandidates(Vector3 origin) {
        yield return origin;

        for (int ring = 1; ring <= VisibilityPlacementRings; ring++) {
            float d = ring * VisibilityPlacementStep;
            for (int angle = 0; angle < 360; angle += 45) {
                float rad = angle * Mathf.Deg2Rad;
                yield return origin + new Vector3(Mathf.Cos(rad) * d, 0f, Mathf.Sin(rad) * d);
            }
        }
    }

    float ScoreVisibility(GameObject go) {
        if (go == null)
            return -1f;

        var cam = Camera.main;
        if (cam == null)
            return 0.5f;

        var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: false);
        if (renderers == null || renderers.Length == 0)
            return -1f;

        var planes = GeometryUtility.CalculateFrustumPlanes(cam);
        bool anyInFrustum = false;
        Bounds merged = renderers[0].bounds;
        for (int i = 0; i < renderers.Length; i++) {
            merged.Encapsulate(renderers[i].bounds);
            if (GeometryUtility.TestPlanesAABB(planes, renderers[i].bounds))
                anyInFrustum = true;
        }

        if (!anyInFrustum)
            return 0f;

        Vector3 from = cam.transform.position;
        Vector3 to = merged.center;
        Vector3 dir = to - from;
        float dist = dir.magnitude;
        if (dist < 0.001f)
            return 1f;

        dir /= dist;
        if (Physics.Raycast(from, dir, out var hit, dist, ~0, QueryTriggerInteraction.Ignore)) {
            if (hit.collider != null && !hit.collider.transform.IsChildOf(go.transform))
                return 0.4f;
        }

        return 1f;
    }

    void ForceVisibleFallback(GameObject go) {
        if (go == null)
            return;

        float score = ScoreVisibility(go);
        if (score >= 1f)
            return;

        var cam = Camera.main;
        if (cam == null)
            return;

        Vector3 worldTarget = cam.transform.position + cam.transform.forward * ForcedVisibleDistance;
        worldTarget += Vector3.down * 0.15f;

        if (spawnParentOverride != null) {
            go.transform.localPosition = spawnParentOverride.InverseTransformPoint(worldTarget);
        } else {
            go.transform.position = worldTarget;
        }

        go.transform.rotation = Quaternion.LookRotation(-cam.transform.forward, Vector3.up);
        ForceRenderersOn(go);
        EnsureMinimumWorldSize(go.transform, ForcedVisibleWorldSize);

        Debug.LogWarning($"[SpawnerAI] Forced visible fallback for '{go.name}' (score {score:F2}) near camera");
    }

    static void ForceRenderersOn(GameObject go) {
        if (go == null)
            return;

        var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < renderers.Length; i++) {
            if (renderers[i] == null)
                continue;
            renderers[i].enabled = true;
        }
    }

    static void EnsureMinimumWorldSize(Transform target, float minSize) {
        if (target == null)
            return;

        var renderers = target.GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers == null || renderers.Length == 0)
            return;

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        if (maxDim >= minSize)
            return;

        float factor = Mathf.Clamp(minSize / Mathf.Max(maxDim, 0.001f), 1f, 50f);
        target.localScale *= factor;
    }

    static void AlignLocalRotation(Transform target, PlacementType placementType) {
        if (target == null) return;

        switch (placementType) {
            case PlacementType.Wall:
                target.localRotation = Quaternion.Euler(0f, 180f, 0f);
                break;
            case PlacementType.Ceiling:
                target.localRotation = Quaternion.Euler(180f, 0f, 0f);
                break;
            case PlacementType.Floor:
            case PlacementType.Ground:
                target.localRotation = Quaternion.identity;
                break;
        }
    }

    string[] GetResourcePathVariants(string addressableKey) {
        var variants = new List<string>();

        if (string.IsNullOrEmpty(addressableKey)) return variants.ToArray();

        // Original path
        variants.Add(addressableKey);

        // Strip "PreloadedAssets/" prefix if present
        if (addressableKey.StartsWith("PreloadedAssets/")) {
            string withoutPrefix = addressableKey.Substring("PreloadedAssets/".Length);
            variants.Add(withoutPrefix);
        }

        // Try just filename without path
        int lastSlash = addressableKey.LastIndexOf('/');
        if (lastSlash >= 0) {
            string filename = addressableKey.Substring(lastSlash + 1);
            variants.Add(filename);
        }

        return variants.ToArray();
    }

    GameObject TryLoadEditorAssetFallback(SceneObjectData obj, LocalAssetResolver.ResolutionResult resolution) {
#if UNITY_EDITOR
        bool isSpider = (obj != null && !string.IsNullOrEmpty(obj.type) && obj.type.ToLowerInvariant().Contains("spider")) ||
                        (resolution?.asset != null && !string.IsNullOrEmpty(resolution.asset.id) && resolution.asset.id.ToLowerInvariant().Contains("spider"));

        // Prefer the specific spider in PreloadedAssets requested by the user.
        if (isSpider) {
            const string preloadedSpiderFbxPath = "Assets/PreloadedAssets/Spiders/spider_myOldOne.FBX";
            var preloadedSpider = AssetDatabase.LoadAssetAtPath<GameObject>(preloadedSpiderFbxPath);
            if (preloadedSpider != null) {
                Debug.Log($"[Spawner] Loaded editor fallback spider from {preloadedSpiderFbxPath}");
                return preloadedSpider;
            }
        }

        // Generic asset-db fallback using manifest addressable key, to support non-Resources assets in Editor.
        if (resolution != null && !string.IsNullOrEmpty(resolution.prefabPath)) {
            string basePath = "Assets/" + resolution.prefabPath;
            string[] candidatePaths = {
                basePath + ".prefab",
                basePath + ".fbx",
                basePath + ".FBX"
            };

            foreach (var path in candidatePaths) {
                var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (loaded != null) {
                    Debug.Log($"[Spawner] Loaded editor fallback asset from {path}");
                    return loaded;
                }
            }
        }

        return null;
#else
        return null;
#endif
    }

    void SpawnPlaceholder(SceneObjectData obj, PositionData pos) {
        GameObject placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            placeholder.name = $"PLACEHOLDER_{obj.type}_{(string.IsNullOrWhiteSpace(obj.id) ? "runtime" : obj.id)}";
        TryAssignSpawnedTag(placeholder);
        placeholder.transform.SetParent(spawnedObjectsContainer, worldPositionStays: false);
        placeholder.transform.localScale = Vector3.one * 0.3f;

        // Make placeholder magenta for visibility
        Renderer renderer = placeholder.GetComponent<Renderer>();
        if (renderer != null) {
            Material mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(1f, 0f, 1f, 1f); // Magenta
            renderer.material = mat;
        }

        // Remove collider from placeholder to not interfere with gameplay
        Collider col = placeholder.GetComponent<Collider>();
        if (col != null) Destroy(col);

        if (spawnParentOverride != null) {
            var local = NormalizeToParentLocalSpace(new Vector3(pos.x, pos.y, pos.z));
            placeholder.transform.localPosition = ConvertModelUnitsToLocal(local);
            ConstrainInsideParentBounds(placeholder.transform);
        } else {
            SmartPlacer.EnsureInstance().TryPlace(
                placeholder,
                new Vector3(pos.x, pos.y, pos.z),
                PlacementType.Floor);
        }

        // Ensure placeholder is visible even if smart placement fails.
        if (placeholder.transform.position == Vector3.zero && (pos.x != 0f || pos.y != 0f || pos.z != 0f)) {
            placeholder.transform.position = new Vector3(pos.x, pos.y, pos.z);
        }

        Debug.LogWarning($"[Spawner] Placeholder for {obj.type} at ({pos.x}, {pos.y}, {pos.z})");
    }

    static void TryAssignSpawnedTag(GameObject go) {
        if (go == null)
            return;

        try {
            go.tag = "SpawnedObject";
        } catch (UnityException) {
            // Tag might not exist in Tag Manager. Continue without tag.
        }
    }
}
