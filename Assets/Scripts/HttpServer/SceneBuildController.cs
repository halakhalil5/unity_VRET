using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Linq;

public class SceneBuildController : MonoBehaviour {

    public static SceneBuildController Instance { get; private set; }

    [SerializeField] EnvironmentRegistry registry;

    BuildRequest pendingTherapistRequest;
    bool pendingTherapistSelectionReady = true;
    int pendingSelectionVersion;

    public bool HasPendingTherapistSelection => pendingTherapistRequest != null;
    public string PendingEnvironment => pendingTherapistRequest?.scene?.environment;
    public bool IsPendingTherapistSelectionReady => pendingTherapistSelectionReady;

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (registry == null)
            registry = EnvironmentRegistry.Instance;
    }

    public BuildResponse BuildScene(BuildRequest request) {
        if (IsRelaxationSceneActive()) {
            pendingTherapistRequest = request;
            pendingSelectionVersion++;
            pendingTherapistSelectionReady = !IsPictureRoomEnvironment(request?.scene?.environment);

            if (!pendingTherapistSelectionReady)
                _ = PreloadPendingPictureRoomImagesAsync(request, pendingSelectionVersion);

            Debug.Log($"[SceneBuild] Queued therapist selection '{request.scene.environment}' from RelaxationScene");

            return new BuildResponse {
                status = "ok",
                previewURL = request.requestPreview ? PreviewCaptureService.Instance.Capture() : ""
            };
        }

        return BuildSceneNow(request);
    }

    public BuildResponse ApplyPendingTherapistSelection() {
        if (pendingTherapistRequest == null) {
            return new BuildResponse {
                status = "error",
                error = "No pending therapist scene selection"
            };
        }

        if (!pendingTherapistSelectionReady) {
            return new BuildResponse {
                status = "error",
                error = "Picture Room is still preparing images. Please wait until all 7 images are ready."
            };
        }

        var request = pendingTherapistRequest;
        var result = BuildSceneNow(request);

        if (result.status == "ok") {
            pendingTherapistRequest = null;
            pendingTherapistSelectionReady = true;
        } else {
            // Keep request queued so the user can retry after fixing mapping/build settings.
            pendingTherapistRequest = request;
            Debug.LogError($"[SceneBuild] Could not apply pending selection '{request.scene.environment}': {result.error}");
        }

        return result;
    }

    BuildResponse BuildSceneNow(BuildRequest request) {
        string env = request.scene.environment;

        Debug.Log($"[HttpServer] POST /build-scene received");

        // Comprehensive validation
        string validationError = ValidateRequest(request);
        if (!string.IsNullOrEmpty(validationError)) {
            Debug.LogError($"[SceneBuild] Validation failed: {validationError}");
            return new BuildResponse {
                status = "error",
                error = validationError
            };
        }

        Debug.Log($"[SceneBuild] environment: {env}");
        int explicitObjects = request.scene.sceneObjects?.Length ?? 0;
        string requestPlacementText = request?.scene?.placementText ?? request?.placementText;
        bool hasDescription = !string.IsNullOrWhiteSpace(requestPlacementText);
        Debug.Log($"[SceneBuild] objects count: {explicitObjects} (explicit payload objects)");
        Debug.Log($"[SceneBuild] placement description present: {hasDescription}");

        if (registry == null)
            registry = EnvironmentRegistry.Instance;

        if (registry == null) {
            Debug.LogError("[SceneBuild] EnvironmentRegistry is missing");
            return new BuildResponse {
                status = "error",
                error  = "EnvironmentRegistry is missing. Create Assets/Resources/EnvironmentRegistry.asset and map 'living_room'."
            };
        }

        // Validate environment (allow underscore/hyphen variants)
        var entry = FindEnvironmentEntry(env);
        if (entry == null) {
            Debug.LogError($"[SceneBuild] Unknown environment: {env}");
            return new BuildResponse {
                status = "error",
                error  = $"Unknown environment: '{env}'. " +
                         $"Register it in Assets/Resources/EnvironmentRegistry.asset"
            };
        }

        if (string.IsNullOrEmpty(entry.sceneName) && entry.prefab == null) {
            Debug.LogError($"[SceneBuild] Environment '{entry.environmentId}' has no sceneName or prefab mapping");
            return new BuildResponse {
                status = "error",
                error  = $"Environment '{entry.environmentId}' is not mapped to a scene or prefab"
            };
        }

        // Load the environment
        string loadError;
        if (!LoadEnvironment(entry, request.scene, out loadError)) {
            return new BuildResponse {
                status = "error",
                error = loadError
            };
        }

        // Scene loading can finish on a later frame, so defer completion for scene-based environments.
        if (!string.IsNullOrEmpty(entry.sceneName)) {
            StartCoroutine(FinalizeBuildAfterSceneLoad(entry, request));
            Debug.Log($"[SceneBuild] Deferred object placement until scene '{entry.sceneName}' is active");
            return new BuildResponse {
                status = "ok",
                previewURL = ""
            };
        }

        return FinalizeBuildInCurrentScene(entry, request);
    }

    async Task PreloadPendingPictureRoomImagesAsync(BuildRequest request, int requestVersion) {
        if (request?.scene == null)
            return;

        var orchestrator = PictureRoomImageOrchestrator.EnsureInstance();
        if (orchestrator == null) {
            Debug.LogWarning("[SceneBuild] Could not create PictureRoomImageOrchestrator for preload");
            pendingTherapistSelectionReady = false;
            return;
        }

        bool ready = await orchestrator.PreloadImagesForPictureRoom(request.scene.environment, request.scene.picturePrompts);
        if (requestVersion != pendingSelectionVersion)
            return;

        pendingTherapistSelectionReady = ready;

        if (ready) {
            Debug.Log("[SceneBuild] Picture Room images are ready. Load button can now appear.");
        } else {
            Debug.LogWarning("[SceneBuild] Picture Room image preparation failed or incomplete. Button remains hidden.");
        }
    }

    BuildResponse FinalizeBuildInCurrentScene(EnvironmentRegistry.EnvironmentEntry entry, BuildRequest request) {
        // Handle camera
        if (request.scene.camera != null)
            SetupCamera(request.scene.camera);

        // Populate the environment with the selected scene's asset descriptor if available.
        if (AssetPipelineOrchestrator.Instance != null) {
            _ = AssetPipelineOrchestrator.Instance.LoadScene(entry.environmentId);
        }

        float intensity = Mathf.Clamp01(request.intensity ?? 0.5f);
        var objectsToSpawn = BuildObjectsToSpawn(request.scene);
        bool isPictureRoom = IsPictureRoomEnvironment(entry?.environmentId) ||
                             IsPictureRoomEnvironment(request.scene?.environment);

        if (isPictureRoom)
            objectsToSpawn = FilterOutPictureRoomFrameObjects(objectsToSpawn);

        var spawner = EnsureObjectSpawner();
        var spawnParent = FindEnvironmentAnchor(entry);

        if (spawner != null)
            spawner.SetSpawnParent(spawnParent);

        if (spawnParent == null) {
            objectsToSpawn = MapObjectsToEnvironmentSpace(entry, objectsToSpawn);
        } else {
            Debug.Log($"[SceneBuild] Using local-space spawn positions under '{spawnParent.name}'");
        }

        // Spawn objects
        bool hasPlacementDescription = !string.IsNullOrWhiteSpace(request?.scene?.placementText) ||
                                       !string.IsNullOrWhiteSpace(request?.placementText);

        Debug.Log($"[SceneBuild] effective spawn input count: {objectsToSpawn?.Length ?? 0}, description-driven mode: {hasPlacementDescription}");

        if ((objectsToSpawn != null && objectsToSpawn.Length > 0) || hasPlacementDescription) {
            if (spawner == null) {
                return new BuildResponse {
                    status = "error",
                    error = "ObjectSpawner is missing and could not be created"
                };
            }

            _ = PlaceObjectsWithAIOrFallback(spawner, objectsToSpawn ?? Array.Empty<SceneObjectData>(), intensity, request);
        } else {
            Debug.LogWarning("[SceneBuild] No sceneObjects/sceneTriggers or placement description to spawn");
        }

        // Apply intensity to phobia controller
        PhobiaController.Instance?.ApplyIntensity(intensity);

        TryStartPictureRoomImageGeneration(entry, request);

        // Capture preview
        string previewUrl = "";
        if (request.requestPreview)
            previewUrl = PreviewCaptureService.Instance.Capture();

        Debug.Log($"[SceneBuild] ✓ Scene built successfully");

        return new BuildResponse {
            status     = "ok",
            previewURL = previewUrl
        };
    }

    static SceneObjectData[] FilterOutPictureRoomFrameObjects(SceneObjectData[] objects) {
        if (objects == null || objects.Length == 0)
            return objects;

        var filtered = new List<SceneObjectData>(objects.Length);
        int skipped = 0;

        for (int i = 0; i < objects.Length; i++) {
            var obj = objects[i];
            if (obj == null) {
                continue;
            }

            string type = obj.type?.Trim().ToLowerInvariant();
            if (type == "picture_frame" || type == "photo_frame") {
                skipped++;
                continue;
            }

            filtered.Add(obj);
        }

        if (skipped > 0)
            Debug.Log($"[SceneBuild] Picture Room: skipped {skipped} frame placeholder objects; using existing in-scene frames");

        return filtered.ToArray();
    }

    void TryStartPictureRoomImageGeneration(EnvironmentRegistry.EnvironmentEntry entry, BuildRequest request) {
        if (request?.scene == null)
            return;

        bool isPictureRoom = IsPictureRoomEnvironment(entry?.environmentId) ||
                             IsPictureRoomEnvironment(request.scene.environment);
        if (!isPictureRoom)
            return;

        var prompts = request.scene.picturePrompts;
        if (prompts == null || prompts.Length == 0) {
            Debug.LogWarning("[SceneBuild] Picture Room selected but no picture prompts were provided");
            return;
        }

        var orchestrator = PictureRoomImageOrchestrator.EnsureInstance();
        if (orchestrator == null) {
            Debug.LogWarning("[SceneBuild] Could not create PictureRoomImageOrchestrator");
            return;
        }

        orchestrator.GenerateAndDisplayForPictureRoom(request.scene.environment, prompts);
    }

    static bool IsPictureRoomEnvironment(string environmentId) {
        return NormalizeEnvironmentIdForComparison(environmentId) == "pictureroom";
    }

    static string NormalizeEnvironmentIdForComparison(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value.ToLowerInvariant()
                    .Replace("-", "")
                    .Replace("_", "")
                    .Replace(" ", "");
    }

    async Task PlaceObjectsWithAIOrFallback(ObjectSpawner spawner, SceneObjectData[] objectsToSpawn, float intensity, BuildRequest request) {
        if (spawner == null) {
            Debug.LogError("[SceneBuild] Cannot place objects: ObjectSpawner is null");
            return;
        }

        bool hasPlacementDescription = !string.IsNullOrWhiteSpace(request?.scene?.placementText) ||
                                       !string.IsNullOrWhiteSpace(request?.placementText);

        if ((objectsToSpawn == null || objectsToSpawn.Length == 0) && !hasPlacementDescription) {
            Debug.LogWarning("[SceneBuild] No objects or placement description available in AI/fallback path");
            return;
        }

        objectsToSpawn ??= Array.Empty<SceneObjectData>();

        bool aiPlaced = false;
        bool aiAttempted = false;

        var ai = AIPlacementOrchestrator.EnsureInstance();
        if (ai != null && ai.UseAIPlacement) {
            aiAttempted = true;
            try {
                string environmentId = ResolveActiveEnvironmentId(request);

                string placementText = request?.scene?.placementText ?? request?.placementText;
                var aiTask = ai.PlaceObjectsWithAI(objectsToSpawn, intensity, environmentId, placementText);
                var completed = await Task.WhenAny(aiTask, Task.Delay(TimeSpan.FromSeconds(15)));
                if (completed == aiTask) {
                    aiPlaced = aiTask.Result;
                } else {
                    Debug.LogWarning("[SceneBuild] AI placement timed out after 15s. AI-only mode will skip object spawning.");
                    aiPlaced = false;
                }
            } catch (Exception e) {
                Debug.LogWarning($"[SceneBuild] AI placement threw exception. AI-only mode will skip object spawning: {e.Message}");
                aiPlaced = false;
            }

            if (aiPlaced && CountSpawnedChildren() == 0) {
                Debug.LogWarning("[SceneBuild] AI placement reported success but hierarchy is empty. AI-only mode will not run fallback spawner.");
                aiPlaced = false;
            }
        }

        if (!aiPlaced) {
            if (!aiAttempted) {
                Debug.LogWarning("[SceneBuild] AI placement is disabled or unavailable; AI-only mode skips object spawning.");
                return;
            }

            Debug.LogWarning("[SceneBuild] AI placement did not produce spawnable objects; AI-only mode skips fallback spawning.");
            return;
        } else {
            Debug.Log($"[SceneBuild] AI placement succeeded with {CountSpawnedChildren()} hierarchy objects");
        }
    }

    static int CountSpawnedChildren() {
        var container = GameObject.Find("SpawnedObjects");
        return container != null ? container.transform.childCount : 0;
    }

    SceneObjectData[] InferObjectsFromPlacementDescription(string placementText) {
        if (string.IsNullOrWhiteSpace(placementText))
            return Array.Empty<SceneObjectData>();

        if (!AssetManifestLoader.TryLoadManifest(out var manifestError)) {
            Debug.LogWarning($"[SceneBuild] Could not infer fallback objects from text: {manifestError}");
            return Array.Empty<SceneObjectData>();
        }

        var assets = AssetManifestLoader.GetAllAssets();
        if (assets == null || assets.Length == 0)
            return Array.Empty<SceneObjectData>();

        string text = placementText.ToLowerInvariant();
        var textWords = ExtractWords(text);
        var scored = new List<(int score, AssetManifestLoader.AssetEntry asset)>();

        for (int i = 0; i < assets.Length; i++) {
            var asset = assets[i];
            if (asset == null || string.IsNullOrWhiteSpace(asset.id))
                continue;

            int score = 0;

            // Exact ID phrase match (highest priority)
            string idPhrase = asset.id.ToLowerInvariant().Replace("_", " ").Replace("-", " ");
            if (text.Contains(idPhrase))
                score += 40;
            else {
                // Fuzzy ID match: count word overlaps
                var idWords = ExtractWords(asset.id);
                int idMatches = CountWordOverlap(textWords, idWords);
                score += idMatches * 12;
            }

            // Category match (medium priority)
            if (!string.IsNullOrWhiteSpace(asset.category)) {
                string categoryLower = asset.category.ToLowerInvariant();
                if (text.Contains(categoryLower))
                    score += 8;
            }

            // Tag matches (fuzzy and exact)
            if (asset.tags != null) {
                for (int t = 0; t < asset.tags.Length; t++) {
                    string tag = asset.tags[t];
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    string normalizedTag = tag.Trim().ToLowerInvariant();
                    if (text.Contains(normalizedTag))
                        score += 12;  // Exact tag match
                    else {
                        // Fuzzy tag match: substring overlap or word similarity
                        if (textWords.Count > 0) {
                            foreach (var word in textWords) {
                                if (word.Length >= 3 && (normalizedTag.Contains(word) || word.Contains(normalizedTag))) {
                                    score += 6;  // Fuzzy match gets lower score
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Keep only high-confidence manifest matches; low scores create random objects.
            if (score >= 20) {
                scored.Add((score, asset));
                Debug.Log($"[InferObjects] '{asset.id}' scored {score}");
            }
        }

        // Sort manifest matches by score
        scored.Sort((a, b) => b.score.CompareTo(a.score));

        // Take up to 5 best manifest candidates
        int manifestCount = Mathf.Clamp(scored.Count, 0, 5);
        
        bool hasBounds = TryGetRenderableBounds(out var bounds);
        float floorY = hasBounds ? bounds.min.y + 0.05f : 0.05f;
        Vector3 center = hasBounds ? bounds.center : Vector3.zero;

        var inferred = new List<SceneObjectData>();

        // Add manifest matches
        for (int i = 0; i < manifestCount; i++) {
            var candidate = scored[i].asset;
            float angle = (Mathf.PI * 2f * i) / Mathf.Max(1, manifestCount);
            float radius = 0.8f + 0.35f * (i % 3);

            float x = center.x + Mathf.Cos(angle) * radius;
            float z = center.z + Mathf.Sin(angle) * radius;

            if (hasBounds) {
                x = Mathf.Clamp(x, bounds.min.x + 0.2f, bounds.max.x - 0.2f);
                z = Mathf.Clamp(z, bounds.min.z + 0.2f, bounds.max.z - 0.2f);
            }

            inferred.Add(new SceneObjectData {
                id = GeneratePlacementId(candidate.id, placementText, i),
                type = candidate.id,
                position = new PositionData {
                    x = x,
                    y = floorY,
                    z = z
                }
            });
        }

        // Extract keywords that might not have manifest matches (for Objaverse fallback)
        var missingKeywords = ExtractMissingObjectKeywords(text, scored);
        int maxMissingToAdd = Mathf.Max(0, 8 - inferred.Count);

        for (int i = 0; i < Mathf.Min(missingKeywords.Count, maxMissingToAdd); i++) {
            string keyword = missingKeywords[i];
            float angle = (Mathf.PI * 2f * (manifestCount + i)) / Mathf.Max(1, manifestCount + missingKeywords.Count);
            float radius = 1.2f + 0.4f * ((manifestCount + i) % 3);

            float x = center.x + Mathf.Cos(angle) * radius;
            float z = center.z + Mathf.Sin(angle) * radius;

            if (hasBounds) {
                x = Mathf.Clamp(x, bounds.min.x + 0.2f, bounds.max.x - 0.2f);
                z = Mathf.Clamp(z, bounds.min.z + 0.2f, bounds.max.z - 0.2f);
            }

            // Map keyword to semantic category/tags for Objaverse search
            var (category, tags) = MapKeywordToObjaverseQuery(keyword);

            inferred.Add(new SceneObjectData {
                id = GeneratePlacementId(keyword, placementText, manifestCount + i),
                type = keyword,  // Will be resolved via Objaverse search
                position = new PositionData {
                    x = x,
                    y = floorY,
                    z = z
                },
                category = category,  // For Objaverse search
                tags = tags           // For Objaverse search
            });

            Debug.Log($"[InferObjects] Added missing keyword '{keyword}' with category='{category}' tags={string.Join(",", tags)}");
        }

        if (inferred.Count == 0) {
            Debug.LogWarning($"[InferObjects] No assets inferred from description: '{placementText}'");
            return Array.Empty<SceneObjectData>();
        }

        Debug.Log($"[InferObjects] Inferred {inferred.Count} total objects ({manifestCount} from manifest, {inferred.Count - manifestCount} for Objaverse)");
        return inferred.ToArray();
    }

    static (string category, string[] tags) MapKeywordToObjaverseQuery(string keyword) {
        keyword = keyword.ToLowerInvariant();

        // Map keywords to Objaverse (LVIS) categories and tags
        if (keyword.Contains("bag") || keyword.Contains("emergency") || keyword.Contains("backpack") || keyword.Contains("luggage") || keyword.Contains("suitcase"))
            return ("bag", new[] { "bag", "luggage", "container", "portable" });
        
        if (keyword.Contains("fan") || keyword.Contains("vent") || keyword.Contains("ceiling"))
            return ("fan", new[] { "fan", "ventilation", "hvac", "appliance" });
        
        if (keyword.Contains("phone") || keyword.Contains("telephone") || keyword.Contains("mobile"))
            return ("phone", new[] { "phone", "telephone", "device", "electronics" });

        if (keyword.Contains("car") || keyword.Contains("vehicle") || keyword.Contains("truck") || keyword.Contains("bus"))
            return ("car", new[] { "car", "vehicle", "automobile", "transport" });
        
        if (keyword.Contains("door"))
            return ("door", new[] { "door", "entrance", "architecture", "furniture" });
        
        if (keyword.Contains("window"))
            return ("window", new[] { "window", "glass", "architecture", "furniture" });
        
        if (keyword.Contains("wall"))
            return ("wall", new[] { "wall", "surface", "architecture", "structure" });
        
        if (keyword.Contains("bench") || keyword.Contains("seat") || keyword.Contains("couch") || keyword.Contains("sofa"))
            return ("furniture", new[] { "bench", "seating", "furniture", "indoor" });
        
        if (keyword.Contains("light") || keyword.Contains("lamp"))
            return ("light", new[] { "light", "lamp", "furniture", "lighting" });
        
        if (keyword.Contains("box") || keyword.Contains("container"))
            return ("container", new[] { "box", "container", "storage", "object" });
        
        if (keyword.Contains("sign") || keyword.Contains("alarm") || keyword.Contains("fire") || keyword.Contains("exit"))
            return ("sign", new[] { "sign", "warning", "indoor", "object" });

        // Default fallback: use keyword itself as both category and tag
        return (keyword, new[] { keyword });
    }

    static List<string> ExtractMissingObjectKeywords(string text, List<(int score, AssetManifestLoader.AssetEntry asset)> matchedAssets) {
        var keywords = new List<string>();
        
        // Keywords commonly found in exposure therapy scenes that might not be in manifest
        var commonKeywords = new[] {
            "bag", "backpack", "emergency bag", "red bag",
            "fan", "vent", "vent fan", "ceiling fan",
            "phone", "telephone",
            "alarm", "fire", "exit sign",
            "car", "vehicle", "truck", "bus",
            "bench", "seat", "couch", "sofa",
            "light", "lamp",
            "box", "container",
            "luggage", "suitcase"
        };

        var matchedIds = new HashSet<string>();
        foreach (var (_, asset) in matchedAssets) {
            matchedIds.Add(asset.id.ToLowerInvariant());
        }

        foreach (var keyword in commonKeywords) {
            if (text.Contains(keyword.ToLowerInvariant())) {
                // Check if this keyword is already well-matched by an asset
                bool alreadyMatched = false;
                foreach (var id in matchedIds) {
                    if (id.Contains(keyword.Replace(" ", "_")) || keyword.Contains(id.Replace("_", " "))) {
                        alreadyMatched = true;
                        break;
                    }
                }

                if (!alreadyMatched) {
                    // Avoid duplicates like "bag" + "emergency bag"; keep the more specific phrase.
                    bool duplicate = false;
                    for (int k = 0; k < keywords.Count; k++) {
                        string existing = keywords[k];
                        if (existing.Contains(keyword) || keyword.Contains(existing)) {
                            if (keyword.Length > existing.Length)
                                keywords[k] = keyword;
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        keywords.Add(keyword);

                    Debug.Log($"[InferObjects] Found unmatched keyword: '{keyword}'");
                }
            }
        }

        return keywords;
    }

    static List<string> ExtractWords(string text) {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return words;

        var normalized = text.ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        var parts = normalized.Split(new[] { ' ', ',', '.', ':', ';', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts) {
            if (part.Length > 0)
                words.Add(part);
        }

        return words;
    }

    static int CountWordOverlap(List<string> sourceWords, List<string> targetWords) {
        if (sourceWords == null || sourceWords.Count == 0 || targetWords == null || targetWords.Count == 0)
            return 0;

        int matches = 0;
        foreach (var target in targetWords) {
            foreach (var source in sourceWords) {
                if (target.Equals(source) || (target.Length > 2 && source.Contains(target)) || (source.Length > 2 && target.Contains(source))) {
                    matches++;
                    break;
                }
            }
        }

        return matches;
    }

    string ResolveActiveEnvironmentId(BuildRequest request) {
        string fromRequestScene = request?.scene?.environment;
        if (!string.IsNullOrWhiteSpace(fromRequestScene))
            return fromRequestScene;

        string fromRequest = pendingTherapistRequest?.scene?.environment;
        if (!string.IsNullOrWhiteSpace(fromRequest))
            return fromRequest;

        if (registry?.environments != null) {
            foreach (var candidate in registry.environments) {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.environmentId))
                    continue;

                if (GameObject.Find($"ENV_{candidate.environmentId}") != null)
                    return candidate.environmentId;
            }
        }

        return SceneManager.GetActiveScene().name;
    }

    SceneObjectData[] MapObjectsToEnvironmentSpace(EnvironmentRegistry.EnvironmentEntry entry, SceneObjectData[] objects) {
        if (objects == null || objects.Length == 0 || entry == null || string.IsNullOrEmpty(entry.environmentId))
            return objects;

        var envRoot = FindEnvironmentAnchor(entry)?.gameObject;
        if (envRoot != null) {
            GetRenderableBounds(envRoot, out var envBounds);
            var t = envRoot.transform;
            for (int i = 0; i < objects.Length; i++) {
                var obj = objects[i];
                if (obj?.position == null)
                    continue;

                var local = new Vector3(obj.position.x, obj.position.y, obj.position.z);
                var world = t.TransformPoint(local);
                world.y = Mathf.Max(world.y, envBounds.min.y + 0.05f);

                obj.position.x = world.x;
                obj.position.y = world.y;
                obj.position.z = world.z;
            }

            Debug.Log($"[SceneBuild] Mapped {objects.Length} objects into environment root '{envRoot.name}' space");
            return objects;
        }

        // Scene-based environments may not have ENV_ root; map around scene content bounds instead.
        if (!TryGetRenderableBounds(out var bounds))
            return objects;

        var anchor = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        var expanded = bounds;
        expanded.Expand(1f);

        const float padding = 0.25f;
        for (int i = 0; i < objects.Length; i++) {
            var obj = objects[i];
            if (obj?.position == null)
                continue;

            var input = new Vector3(obj.position.x, obj.position.y, obj.position.z);
            var world = expanded.Contains(input) ? input : anchor + input;

            world.x = Mathf.Clamp(world.x, bounds.min.x + padding, bounds.max.x - padding);
            world.y = Mathf.Max(bounds.min.y + 0.05f, Mathf.Clamp(world.y, bounds.min.y + 0.02f, bounds.max.y - 0.02f));
            world.z = Mathf.Clamp(world.z, bounds.min.z + padding, bounds.max.z - padding);

            obj.position.x = world.x;
            obj.position.y = world.y;
            obj.position.z = world.z;
        }

        Debug.Log($"[SceneBuild] Mapped {objects.Length} objects into scene bounds for '{entry.environmentId}'");
        return objects;
    }

    Transform FindEnvironmentAnchor(EnvironmentRegistry.EnvironmentEntry entry) {
        if (entry == null || string.IsNullOrEmpty(entry.environmentId))
            return null;

        var directRoot = GameObject.Find($"ENV_{entry.environmentId}");
        if (directRoot != null)
            return directRoot.transform;

        string normalized = NormalizeEnvironmentId(entry.environmentId);
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        Transform best = null;
        float bestScore = float.NegativeInfinity;

        foreach (var root in roots) {
            if (root == null)
                continue;

            string rootName = NormalizeEnvironmentId(root.name);
            float score = 0f;

            if (rootName == normalized)
                score += 100f;
            if (rootName.Contains(normalized) || normalized.Contains(rootName))
                score += 50f;
            if (root.GetComponentsInChildren<Renderer>(includeInactive: false).Length > 0)
                score += 10f;

            if (score > bestScore) {
                bestScore = score;
                best = root.transform;
            }
        }

        return best;
    }

    static bool GetRenderableBounds(GameObject root, out Bounds bounds) {
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

    IEnumerator FinalizeBuildAfterSceneLoad(EnvironmentRegistry.EnvironmentEntry entry, BuildRequest request) {
        int waitFrames = 240;
        while (waitFrames-- > 0) {
            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.name == entry.sceneName)
                break;

            yield return null;
        }

        // Give one extra frame for scene activation and object initialization.
        yield return null;

        var result = FinalizeBuildInCurrentScene(entry, request);
        if (result.status != "ok") {
            Debug.LogError($"[SceneBuild] Deferred build failed: {result.error}");
        }
    }

    static ObjectSpawner EnsureObjectSpawner() {
        if (ObjectSpawner.Instance != null)
            return ObjectSpawner.Instance;

        var go = new GameObject("ObjectSpawnerRuntime");
        DontDestroyOnLoad(go);
        var created = go.AddComponent<ObjectSpawner>();
        Debug.Log("[SceneBuild] Created fallback ObjectSpawnerRuntime");
        return created;
    }

    /// <summary>
    /// Validates the entire build request before attempting to build the scene.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    string ValidateRequest(BuildRequest request) {
        if (request == null)
            return "BuildRequest is null";

        if (request.scene == null)
            return "Missing 'scene' field in request";

        if (string.IsNullOrEmpty(request.scene.environment))
            return "Missing or empty 'scene.environment' field";

        // Validate camera if provided
        if (request.scene.camera != null) {
            var cam = request.scene.camera;
            if (cam.startPosition == null)
                return "Missing 'scene.camera.startPosition' field";

            if (float.IsNaN(cam.startPosition.x) || float.IsNaN(cam.startPosition.y) || float.IsNaN(cam.startPosition.z))
                return "Invalid camera position: contains NaN values";
        }

        // Validate objects if provided
        if (request.scene.sceneObjects != null && request.scene.sceneObjects.Length > 0) {
            for (int i = 0; i < request.scene.sceneObjects.Length; i++) {
                var obj = request.scene.sceneObjects[i];
                if (obj == null)
                    return $"Scene object at index {i} is null";

                if (string.IsNullOrEmpty(obj.type))
                    return $"Scene object at index {i} is missing 'type' field";

                if (obj.position == null)
                    Debug.LogWarning($"[SceneBuild] Scene object at index {i} is missing 'position' field; default placement will be used");
                else if (float.IsNaN(obj.position.x) || float.IsNaN(obj.position.y) || float.IsNaN(obj.position.z))
                    return $"Scene object at index {i} has invalid position with NaN values";
            }
        }

        // Validate intensity if provided
        if (request.intensity.HasValue) {
            if (float.IsNaN(request.intensity.Value))
                return "Invalid intensity: NaN value";
        }

        return null; // Valid
    }

    static bool IsRelaxationSceneActive() {
        var active = SceneManager.GetActiveScene();
        return active.IsValid() && active.name == "RelaxationScene";
    }

    SceneObjectData[] BuildObjectsToSpawn(SceneData scene) {
        var generated = BuildObjectsFromTriggers(scene?.sceneTriggers);

        NormalizeSceneObjectIds(scene?.sceneObjects, scene?.placementText);

        if (scene?.sceneObjects == null || scene.sceneObjects.Length == 0)
            return generated.Count > 0 ? generated.ToArray() : scene?.sceneObjects;

        if (generated.Count == 0)
            return scene.sceneObjects;

        var combined = new List<SceneObjectData>(scene.sceneObjects.Length + generated.Count);
        combined.AddRange(scene.sceneObjects);
        combined.AddRange(generated);
        return combined.ToArray();
    }

    static void NormalizeSceneObjectIds(SceneObjectData[] objects, string placementText) {
        if (objects == null || objects.Length == 0)
            return;

        for (int i = 0; i < objects.Length; i++) {
            var obj = objects[i];
            if (obj == null || string.IsNullOrEmpty(obj.type))
                continue;

            if (string.IsNullOrWhiteSpace(obj.id))
                obj.id = GeneratePlacementId(obj.type, placementText, i);
        }
    }

    static string GeneratePlacementId(string objectType, string placementText, int index) {
        string baseText = $"{objectType}|{placementText}|{index}";
        uint hash = 2166136261;

        for (int i = 0; i < baseText.Length; i++) {
            hash ^= baseText[i];
            hash *= 16777619;
        }

        string typePart = NormalizeObjectType(objectType);
        return $"{typePart}_{hash:x8}";
    }

    static string NormalizeObjectType(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return "object";

        return value.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
    }

    List<SceneObjectData> BuildObjectsFromTriggers(TriggerData[] triggers) {
        var generated = new List<SceneObjectData>();
        if (triggers == null || triggers.Length == 0)
            return generated;

        int index = 0;
        foreach (var trigger in triggers) {
            if (trigger == null || string.IsNullOrEmpty(trigger.type))
                continue;

            if (!trigger.type.Equals("spider", System.StringComparison.OrdinalIgnoreCase))
                continue;

            int count = Mathf.Clamp(trigger.count ?? 1, 1, 50);
            for (int i = 0; i < count; i++) {
                generated.Add(new SceneObjectData {
                    id = string.IsNullOrEmpty(trigger.id) ? $"trigger_spider_{index++}" : $"{trigger.id}_{i}",
                    // Match manifest id for deterministic local resolution.
                    type = "spider_myoldone",
                    position = BuildTriggerPosition(trigger)
                });
            }
        }

        return generated;
    }

    static PositionData BuildTriggerPosition(TriggerData trigger) {
        if (trigger?.position != null)
            return trigger.position;

        if (trigger?.spawnArea != null) {
            float minX = trigger.spawnArea.minX;
            float maxX = trigger.spawnArea.maxX;
            float minZ = trigger.spawnArea.minZ;
            float maxZ = trigger.spawnArea.maxZ;

            if (maxX < minX) (minX, maxX) = (maxX, minX);
            if (maxZ < minZ) (minZ, maxZ) = (maxZ, minZ);

            return new PositionData {
                x = UnityEngine.Random.Range(minX, maxX),
                y = 0f,
                z = UnityEngine.Random.Range(minZ, maxZ)
            };
        }

        return new PositionData { x = 0f, y = 0f, z = 0f };
    }

    bool LoadEnvironment(EnvironmentRegistry.EnvironmentEntry entry,
                         SceneData scene,
                         out string error) {
        error = null;

        if (!string.IsNullOrEmpty(entry.sceneName)) {
            // Load as a separate Unity scene
            SceneManager.LoadScene(entry.sceneName);
            StartCoroutine(FramePlayerToSceneContentNextFrame(entry.environmentId));
            Debug.Log($"[SceneBuild] Loaded scene: {entry.sceneName}");
            return true;
        } else if (entry.prefab != null) {
            // Instantiate as prefab in current scene
            ClearExistingEnvironmentInstances();

            // Use non-generic Instantiate to avoid hard cast exceptions on invalid references.
            var instance = Instantiate((UnityEngine.Object)entry.prefab);
            var go = instance as GameObject;
            if (go == null) {
                if (instance != null) Destroy(instance);
                error = $"Environment '{entry.environmentId}' prefab is not a GameObject reference";
                Debug.LogError($"[SceneBuild] {error}");
                return false;
            }

            PrepareEnvironmentTransform(go);
            go.name = $"ENV_{entry.environmentId}";
            Debug.Log($"[SceneBuild] Instantiated prefab: {entry.prefab.name}");
            return true;
        }

        error = $"Environment '{entry.environmentId}' has no sceneName or prefab mapping";
        Debug.LogError($"[SceneBuild] {error}");
        return false;
    }

    IEnumerator FramePlayerToSceneContentNextFrame(string environmentId) {
        // Let scene activation and camera tagging complete before framing.
        yield return null;
        yield return null;
        FramePlayerToSceneContent(environmentId);
    }

    void FramePlayerToSceneContent(string environmentId) {
        if (!TryGetRenderableBounds(out var bounds)) {
            Debug.LogWarning($"[SceneBuild] Could not compute render bounds for '{environmentId}'");
            return;
        }

        var cam = EnsureRenderableCamera(bounds, environmentId);
        if (cam == null) {
            Debug.LogWarning("[SceneBuild] No main camera found after scene load");
            return;
        }

        var center = bounds.center;
        var distance = Mathf.Max(2.5f, bounds.extents.magnitude * 0.75f);
        var desiredCameraPos = new Vector3(
            center.x,
            Mathf.Max(bounds.min.y + 1.6f, center.y),
            center.z - distance
        );

        // Move the rig root so the actual runtime camera is guaranteed to see scene geometry.
        var rigRoot = cam.transform.root;
        var delta = desiredCameraPos - cam.transform.position;
        rigRoot.position += delta;

        var toCenter = center - cam.transform.position;
        toCenter.y = 0f;
        if (toCenter.sqrMagnitude > 0.0001f) {
            var targetYaw = Quaternion.LookRotation(toCenter.normalized, Vector3.up).eulerAngles.y;
            var rootEuler = rigRoot.eulerAngles;
            rootEuler.y += Mathf.DeltaAngle(cam.transform.eulerAngles.y, targetYaw);
            rigRoot.eulerAngles = rootEuler;
        }

        Debug.Log($"[SceneBuild] Framed '{environmentId}' camera at {cam.transform.position}");
    }

    Camera EnsureRenderableCamera(Bounds bounds, string environmentId) {
        var cam = Camera.main;
        if (cam == null) {
            var cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            if (cameras != null) {
                for (int i = 0; i < cameras.Length; i++) {
                    if (cameras[i] == null) continue;
                    if (!cameras[i].gameObject.activeInHierarchy) continue;
                    cam = cameras[i];
                    break;
                }
            }
        }

        if (cam == null) {
            var fallback = new GameObject("SceneBuildFallbackCamera");
            cam = fallback.AddComponent<Camera>();
            fallback.tag = "MainCamera";
            Debug.LogWarning($"[SceneBuild] Created fallback camera for '{environmentId}'");
        }

        // Force baseline camera render state so Game view cannot stay blank.
        cam.enabled = true;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.cullingMask = ~0;
        cam.nearClipPlane = 0.03f;
        cam.farClipPlane = Mathf.Max(1000f, bounds.extents.magnitude * 8f);
        cam.fieldOfView = Mathf.Clamp(cam.fieldOfView, 55f, 75f);
        cam.useOcclusionCulling = false;

        // In-editor fallback: disable tracked pose driver so HMD tracking does not pin to invalid pose.
        var trackedPose = cam.GetComponent("TrackedPoseDriver") as Behaviour;
        if (trackedPose != null)
            trackedPose.enabled = false;

        return cam;
    }

    bool TryGetRenderableBounds(out Bounds bounds) {
        bounds = default;
        bool found = false;

        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (var root in roots) {
            // Skip player rig and UI-only roots when estimating world content bounds.
            if (root.name.Contains("XR") || root.name.Contains("Camera") || root.name == "VR Player")
                continue;

            var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
            foreach (var renderer in renderers) {
                if (!renderer.enabled) continue;

                if (!found) {
                    bounds = renderer.bounds;
                    found = true;
                } else {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
        }

        return found;
    }

    void PrepareEnvironmentTransform(GameObject go) {
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers == null || renderers.Length == 0) {
            Debug.LogWarning($"[SceneBuild] Instantiated environment '{go.name}' has no renderers");
            return;
        }

        var bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        // Center on X/Z and place model on the floor (Y=0).
        var offset = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z + 2.5f);
        go.transform.position += offset;
    }

    void ClearExistingEnvironmentInstances() {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (var root in roots) {
            if (root.name.StartsWith("ENV_"))
                Destroy(root);
        }
    }

    void SetupCamera(CameraData cameraData) {
        var cam = Camera.main;
        if (cam == null) {
            Debug.LogWarning("[SceneBuild] No main camera found");
            return;
        }

        // Validate coordinates
        if (float.IsNaN(cameraData.startPosition.x) ||
            float.IsNaN(cameraData.startPosition.y) ||
            float.IsNaN(cameraData.startPosition.z)) {
            Debug.LogWarning("[SceneBuild] Invalid camera coordinates — using defaults");
            return;
        }

        cam.transform.position = new Vector3(
            cameraData.startPosition.x,
            cameraData.startPosition.y,
            cameraData.startPosition.z
        );

        Debug.Log($"[SceneBuild] Camera set to " +
                  $"({cameraData.startPosition.x}, " +
                  $"{cameraData.startPosition.y}, " +
                  $"{cameraData.startPosition.z})");
    }

    EnvironmentRegistry.EnvironmentEntry FindEnvironmentEntry(string env) {
        if (registry == null || string.IsNullOrEmpty(env)) return null;

        var direct = registry.Find(env);
        if (direct != null) return direct;

        string normalized = NormalizeEnvironmentId(env);
        foreach (var candidate in registry.environments) {
            if (NormalizeEnvironmentId(candidate.environmentId) == normalized)
                return candidate;
        }

        return null;
    }

    static string NormalizeEnvironmentId(string value) {
        return value?.ToLowerInvariant().Replace("-", "").Replace("_", "") ?? "";
    }
}