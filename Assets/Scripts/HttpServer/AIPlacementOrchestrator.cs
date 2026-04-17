using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class AIPlacementOrchestrator : MonoBehaviour {

    public static AIPlacementOrchestrator Instance { get; private set; }

    [Header("AI Placement")]
    [SerializeField] bool useAIPlacement = true;
    [SerializeField] string openRouterUrl = "https://openrouter.ai/api/v1/chat/completions";
    [SerializeField] string openRouterModel = "openai/gpt-4o";
    [SerializeField] int openRouterMaxTokens = 1200;

    [Header("LLM Auth")]
    [SerializeField] bool includeOpenAIApiKeyHeader = true;
    [SerializeField] string openAIApiKey = "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";
    [SerializeField] string openAIApiKeyEnvVar = "OPENROUTER_API_KEY";

    readonly Dictionary<string, SceneLayoutContext> sceneLayoutCache = new Dictionary<string, SceneLayoutContext>();
    SceneLayoutContext activeSceneLayout;
    string activePlacementText;
    string activeEnvironmentId;
    Transform activeEnvironmentRoot;

    static readonly HttpClient http = new HttpClient {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public bool UseAIPlacement => useAIPlacement;

    void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public static AIPlacementOrchestrator EnsureInstance() {
        if (Instance != null)
            return Instance;

        var go = new GameObject("AIPlacementOrchestrator");
        return go.AddComponent<AIPlacementOrchestrator>();
    }

    public async Task<bool> PlaceObjectsWithAI(SceneObjectData[] objects, float intensity, string environmentId = null, string placementText = null) {
        if (!useAIPlacement)
            return false;

        if (objects == null)
            objects = Array.Empty<SceneObjectData>();

        var scanner = SceneScanner.EnsureInstance();
        Debug.Log("[AIPlacement] Scanning scene for screenshots and inventory...");

        string[] screenshots = await scanner.CaptureSceneViews();
        SceneInventory inventory = scanner.GetSceneInventory();
        activeSceneLayout = ResolveSceneLayoutContext(environmentId);
        activePlacementText = placementText;
        activeEnvironmentId = environmentId;
        activeEnvironmentRoot = ResolveEnvironmentRoot(environmentId);
        string baseEnvironmentJson = LoadBaseEnvironmentJson(environmentId);
        string baseEnvironmentDetails = BuildEnvironmentObjectDetails(baseEnvironmentJson);
        string baseEnvironmentObjectIds = BuildEnvironmentObjectIdList(baseEnvironmentJson);
        string localPreloadedAssetJson = LoadLocalPreloadedAssetJson();

        string prompt = BuildPlacementPrompt(
            placementText,
            baseEnvironmentJson,
            baseEnvironmentDetails,
            baseEnvironmentObjectIds,
            localPreloadedAssetJson,
            activeSceneLayout,
            inventory,
            intensity,
            objects);

        Debug.Log($"[AIPlacement] Prompt size (chars) = {prompt.Length}");

        string responseJson;
        try {
            responseJson = await CallOpenRouterAsync(prompt);
        } catch (Exception e) {
            Debug.LogWarning($"[AIPlacement] OpenRouter request failed: {e.Message}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(responseJson)) {
            Debug.LogWarning("[AIPlacement] Empty response from LLM");
            return false;
        }

        var placements = ParsePlacements(responseJson);
        if (placements == null || placements.Length == 0) {
            Debug.LogWarning("[AIPlacement] No placements returned by AI endpoint");
            return false;
        }

        Debug.Log($"[AIPlacement] Received {placements.Length} placements from AI");

        EnsureSpawnedObjectsContainer();
        SmartPlacer.EnsureInstance().ClearRegisteredBounds();

        int placedCount = 0;
        for (int i = 0; i < placements.Length; i++) {
            if (await PlaceSingleObject(placements[i]))
                placedCount++;
        }

        if (placedCount == 0) {
            Debug.LogWarning("[AIPlacement] AI returned placements but none could be instantiated, falling back");
            return false;
        }

        Debug.Log($"[AIPlacement] Successfully instantiated {placedCount}/{placements.Length} AI placements");
        return true;
    }

    async Task<string> CallOpenRouterAsync(string prompt) {
        string apiKey = ResolveOpenAIApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing OpenRouter API key. Set openAIApiKey or OPENROUTER_API_KEY.");

        int requestedMaxTokens = Mathf.Clamp(openRouterMaxTokens, 128, 4000);
        var firstAttempt = await SendOpenRouterRequestAsync(prompt, apiKey, requestedMaxTokens);
        if (firstAttempt.ok)
            return ExtractOpenRouterContent(firstAttempt.response);

        int affordableMaxTokens = TryExtractAffordableMaxTokens(firstAttempt.response);
        if (firstAttempt.statusCode == 402 && affordableMaxTokens > 0 && affordableMaxTokens < requestedMaxTokens) {
            int retryMaxTokens = Mathf.Max(128, affordableMaxTokens - 16);
            Debug.LogWarning($"[AIPlacement] OpenRouter credits limited this request. Retrying with max_tokens={retryMaxTokens}.");

            var retryAttempt = await SendOpenRouterRequestAsync(prompt, apiKey, retryMaxTokens);
            if (retryAttempt.ok)
                return ExtractOpenRouterContent(retryAttempt.response);

            throw new InvalidOperationException($"OpenRouter returned {retryAttempt.statusCode}: {retryAttempt.response}");
        }

        throw new InvalidOperationException($"OpenRouter returned {firstAttempt.statusCode}: {firstAttempt.response}");
    }

    async Task<(bool ok, int statusCode, string response)> SendOpenRouterRequestAsync(string prompt, string apiKey, int maxTokens) {
        var requestBody = new {
            model = string.IsNullOrWhiteSpace(openRouterModel) ? "openai/gpt-4o" : openRouterModel,
            messages = new[] {
                new {
                    role = "system",
                    content = BuildSystemPrompt()
                },
                new {
                    role = "user",
                    content = prompt
                }
            },
            max_tokens = maxTokens,
            temperature = 0.2f,
            response_format = new { type = "json_object" }
        };

        string json = JsonConvert.SerializeObject(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, string.IsNullOrWhiteSpace(openRouterUrl) ? "https://openrouter.ai/api/v1/chat/completions" : openRouterUrl) {
            Content = content
        };

        if (includeOpenAIApiKeyHeader)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "http://localhost");
        request.Headers.TryAddWithoutValidation("X-Title", "VR Exposure Therapy");

        using var response = await http.SendAsync(request);
        string responseStr = await response.Content.ReadAsStringAsync();

        return (response.IsSuccessStatusCode, (int)response.StatusCode, responseStr);
    }

    static int TryExtractAffordableMaxTokens(string responseStr) {
        if (string.IsNullOrWhiteSpace(responseStr))
            return -1;

        try {
            var match = Regex.Match(responseStr, @"can only afford\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
                return parsed;
        } catch {
            // Ignore parse issues and fall through.
        }

        return -1;
    }

    static string ExtractOpenRouterContent(string responseStr) {
        if (string.IsNullOrWhiteSpace(responseStr))
            return "{}";

        var wrapper = JsonConvert.DeserializeObject<OpenRouterResponse>(responseStr);
        string content = wrapper?.choices?[0]?.message?.content ?? "{}";

        return content
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();
    }

        string BuildSystemPrompt() {
                return @"
You are a 3D scene layout expert for VR exposure therapy.
Your job is to return object placements for a Unity scene based on therapist text and room context.

Rules:
- Read the selected base-environment JSON carefully.
- Read BASE ENVIRONMENT OBJECT DETAILS and BASE ENVIRONMENT OBJECT IDS.
- Read LOCAL PRELOADED ASSET DESCRIPTORS before deciding downloads.
- Extract concrete object mentions from therapist text.
- Exclude objects that are already part of the base environment object IDs/details.
- Exclude objects already available locally in LOCAL PRELOADED ASSET DESCRIPTORS from download candidates.
- Use local assets first. Objaverse is fallback only for objects not available locally.
- Return only NEW add-on objects that should be spawned on top of the base environment.
- If REQUESTED OBJECTS TO PLACE is empty, infer object types from therapist description.
- If REQUESTED OBJECTS TO PLACE is not empty, still add inferred objects from description when they are explicit and not in base environment.
- Treat the selected environment as local room space, not raw world space.
- Use room dimensions, room center, and object anchors to compute positions.
- Keep all placements inside the room volume; never place objects outside a wall plane.
- For wall placements, place the object just inside the wall and face it into the room.
- Scale objects so they stay visible relative to the room size, especially when the room is scaled up.
- Return ONLY valid JSON.
- Do not include explanations outside JSON.
- Prefer placements that align with existing scene objects, spawn points, and scene layout.
- Include an optional downloadCandidates list for inferred objects that are not local and not in base scene.
- Return position, rotation, scale, anchoredTo, surface, and reasoning for each object.

Output format:
{
    ""downloadCandidates"": [
        {
            ""type"": ""hanging_web_cluster"",
            ""reason"": ""Mentioned in therapist text and not available locally or in base scene""
        }
    ],
    ""placements"": [
        {
            ""type"": ""spider_small"",
            ""position"": {""x"": -3.8, ""y"": 1.5, ""z"": 2.0},
            ""rotation"": {""x"": 0, ""y"": 90, ""z"": 0},
            ""scale"": {""x"": 1, ""y"": 1, ""z"": 1},
            ""anchoredTo"": ""left_wall"",
            ""surface"": ""wall"",
            ""reasoning"": ""Wall surface to the left, visible from player""
        }
    ]
}";
        }

    string BuildPlacementPrompt(string therapistDescription, string baseEnvironmentJson, string baseEnvironmentDetails, string baseEnvironmentObjectIds, string localPreloadedAssetJson, SceneLayoutContext sceneLayout, SceneInventory inventory, float intensity, SceneObjectData[] objectsToPlace) {
        string environmentJson = BuildCompactEnvironmentContext(baseEnvironmentJson);
        string environmentDetails = TruncateForPrompt(string.IsNullOrWhiteSpace(baseEnvironmentDetails) ? "No explicit environment object details were found." : baseEnvironmentDetails, 900);
        string environmentObjectIds = BuildCompactEnvironmentObjectIds(baseEnvironmentObjectIds);
        string localAssetsJson = BuildCompactLocalAssetCatalog(localPreloadedAssetJson);
        string sceneLayoutJson = sceneLayout != null ? JsonConvert.SerializeObject(sceneLayout, Formatting.None) : "null";
        string inventoryJson = BuildCompactSceneInventoryJson(inventory);
        string requestedObjectsJson = objectsToPlace != null ? JsonConvert.SerializeObject(objectsToPlace, Formatting.None) : "null";

        return $@"
THERAPIST DESCRIPTION:
{therapistDescription ?? string.Empty}

THERAPY INTENSITY:
{Mathf.Clamp01(intensity):0.00}

SELECTED BASE ENVIRONMENT JSON:
{environmentJson}

BASE ENVIRONMENT OBJECT DETAILS:
{environmentDetails}

BASE ENVIRONMENT OBJECT IDS:
{environmentObjectIds}

LOCAL PRELOADED ASSET DESCRIPTORS:
{localAssetsJson}

PLACEMENT CONSTRAINTS:
- Use the environment's local coordinate space.
- Keep each object inside the selected room.
- For a wall request, place the object slightly inside the wall surface.
- Return scale values that are visible at the room's current scale.
- Do not place objects already listed in BASE ENVIRONMENT OBJECT IDS.
- Prefer matching LOCAL PRELOADED ASSET DESCRIPTORS for inferred objects.
- Only include items in downloadCandidates when there is no local match and object is not in base environment.
- Objaverse is fallback only for downloadCandidates.

SCENE LAYOUT CONTEXT:
{sceneLayoutJson}

CURRENT SCENE INVENTORY:
{inventoryJson}

REQUESTED OBJECTS TO PLACE:
{requestedObjectsJson}

Always infer additional objects from THERAPIST DESCRIPTION.
Then remove objects that are part of BASE ENVIRONMENT OBJECT IDS.
When REQUESTED OBJECTS TO PLACE is empty, output all inferred NEW objects.
When REQUESTED OBJECTS TO PLACE is non-empty, merge requested and inferred NEW objects, then de-duplicate by semantic type.

Return placements for all NEW objects that should be added to the scene based on therapist description, base environment, and room geometry.";
    }

    static string BuildCompactEnvironmentContext(string baseEnvironmentJson) {
        if (string.IsNullOrWhiteSpace(baseEnvironmentJson))
            return "{}";

        try {
            var src = JObject.Parse(baseEnvironmentJson);
            var compact = new JObject {
                ["environmentId"] = src["environmentId"],
                ["displayName"] = src["displayName"],
                ["description"] = src["description"],
                ["sceneLayout"] = src["sceneLayout"]
            };

            if (src["spawnPoints"] is JArray spawnPoints) {
                var compactSpawn = new JArray();
                int limit = Mathf.Min(10, spawnPoints.Count);
                for (int i = 0; i < limit; i++) {
                    if (spawnPoints[i] is JObject sp) {
                        compactSpawn.Add(new JObject {
                            ["id"] = sp["id"],
                            ["type"] = sp["type"],
                            ["position"] = sp["position"]
                        });
                    }
                }

                compact["spawnPoints"] = compactSpawn;
            }

            if (src["objects"] is JArray objects) {
                var compactObjects = new JArray();
                int limit = Mathf.Min(20, objects.Count);
                for (int i = 0; i < limit; i++) {
                    if (objects[i] is JObject obj) {
                        compactObjects.Add(new JObject {
                            ["id"] = obj["id"],
                            ["name"] = obj["name"],
                            ["type"] = obj["type"],
                            ["role"] = obj["role"],
                            ["position"] = obj["position"],
                            ["scale"] = obj["scale"]
                        });
                    }
                }

                compact["objects"] = compactObjects;
                compact["objectsTotal"] = objects.Count;
            }

            return compact.ToString(Formatting.None);
        } catch {
            return TruncateForPrompt(baseEnvironmentJson, 3000);
        }
    }

    static string BuildCompactEnvironmentObjectIds(string baseEnvironmentObjectIds) {
        if (string.IsNullOrWhiteSpace(baseEnvironmentObjectIds))
            return "[]";

        try {
            var ids = JArray.Parse(baseEnvironmentObjectIds);
            var compact = new JArray();
            int limit = Mathf.Min(60, ids.Count);
            for (int i = 0; i < limit; i++)
                compact.Add(ids[i]);

            return compact.ToString(Formatting.None);
        } catch {
            return TruncateForPrompt(baseEnvironmentObjectIds, 1200);
        }
    }

    static string BuildCompactLocalAssetCatalog(string localPreloadedAssetJson) {
        if (string.IsNullOrWhiteSpace(localPreloadedAssetJson))
            return "[]";

        try {
            var src = JArray.Parse(localPreloadedAssetJson);
            var compact = new JArray();
            int limit = Mathf.Min(30, src.Count);

            for (int i = 0; i < limit; i++) {
                if (src[i] is not JObject obj)
                    continue;

                compact.Add(new JObject {
                    ["id"] = obj["id"],
                    ["category"] = obj["category"],
                    ["tags"] = obj["tags"]
                });
            }

            return compact.ToString(Formatting.None);
        } catch {
            return TruncateForPrompt(localPreloadedAssetJson, 2200);
        }
    }

    static string BuildCompactSceneInventoryJson(SceneInventory inventory) {
        if (inventory == null)
            return "null";

        var compact = new JObject {
            ["roomBounds"] = inventory.roomBounds != null ? JObject.FromObject(inventory.roomBounds) : null
        };

        var existing = new JArray();
        if (inventory.existingObjects != null) {
            int limit = Mathf.Min(10, inventory.existingObjects.Count);
            for (int i = 0; i < limit; i++) {
                var item = inventory.existingObjects[i];
                if (item == null)
                    continue;

                existing.Add(new JObject {
                    ["name"] = item.name,
                    ["position"] = item.position != null ? JObject.FromObject(item.position) : null,
                    ["bounds"] = item.bounds != null ? JObject.FromObject(item.bounds) : null
                });
            }

            compact["existingObjectsTotal"] = inventory.existingObjects.Count;
        }

        compact["existingObjects"] = existing;
        return compact.ToString(Formatting.None);
    }

    static string TruncateForPrompt(string value, int maxChars) {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value ?? string.Empty;

        return value.Substring(0, maxChars) + "\n... [truncated for prompt size]";
    }

    AIPlacementResult[] ParsePlacements(string json) {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<AIPlacementResult>();

        try {
            var direct = JsonConvert.DeserializeObject<AIPlacementResult[]>(json);
            if (direct != null && direct.Length > 0)
                return direct;
        } catch {
            // Try wrapped response formats below.
        }

        try {
            var wrapper = JsonConvert.DeserializeObject<AIPlacementEnvelope>(json);
            if (wrapper == null)
                return Array.Empty<AIPlacementResult>();

            if (wrapper.objects != null && wrapper.objects.Length > 0)
                return wrapper.objects;
            if (wrapper.placements != null && wrapper.placements.Length > 0)
                return wrapper.placements;
            if (wrapper.layout != null && wrapper.layout.Length > 0)
                return wrapper.layout;

            return Array.Empty<AIPlacementResult>();
        } catch (Exception e) {
            Debug.LogWarning($"[AIPlacement] Failed to parse placement response: {e.Message}");
            return Array.Empty<AIPlacementResult>();
        }
    }

    async Task<bool> PlaceSingleObject(AIPlacementResult placement) {
        string placementType = ResolvePlacementType(placement);
        if (placement == null || string.IsNullOrEmpty(placementType))
            return false;

        placementType = NormalizePlacementTypeForResolution(placementType);

        string resolvedId = string.IsNullOrWhiteSpace(placement.id)
            ? GeneratePlacementId(placementType, activePlacementText, placement.position)
            : placement.id;

        GameObject go = null;
        var resolution = LocalAssetResolver.Resolve(resolvedId, placementType);
        if (!resolution.found) {
            // Retry with semantic id in case model-generated id is hash-like and not helpful.
            var semanticId = NormalizeType(placementType);
            if (!string.IsNullOrWhiteSpace(semanticId) && !semanticId.Equals(resolvedId, StringComparison.OrdinalIgnoreCase))
                resolution = LocalAssetResolver.Resolve(semanticId, placementType);
        }

        if (resolution.found && resolution.asset != null && !string.IsNullOrEmpty(resolution.prefabPath)) {
            var prefab = LoadPrefab(resolution.prefabPath, placementType, resolvedId);
            if (prefab != null) {
                var container = EnsureSpawnedObjectsContainer(activeEnvironmentRoot);
                go = Instantiate(prefab, container, worldPositionStays: false);
            } else {
                Debug.LogWarning($"[AIPlacement] Prefab not loadable for '{placementType}' ({resolution.prefabPath}), trying Objaverse fallback");
            }
        }

        if (go == null) {
            go = await TryLoadFromObjaverse(placementType, resolvedId);
            if (go == null) {
                Debug.LogWarning($"[AIPlacement] No local or Objaverse asset found for '{placementType}', skipping. Local resolver reason: {resolution?.reason}");
                return false;
            }

            var container = EnsureSpawnedObjectsContainer(activeEnvironmentRoot);
            go.transform.SetParent(container, worldPositionStays: false);
        }

        go.name = $"{placementType}_{resolvedId}";
        TryAssignSpawnedTag(go);

        Vector3 targetPosition = placement.position != null
            ? new Vector3(placement.position.x, placement.position.y, placement.position.z)
            : Vector3.zero;

        if (activeSceneLayout != null)
            targetPosition = ClampToSceneLayout(targetPosition, activeSceneLayout);

        targetPosition = KeepInsideRoom(targetPosition, placement.surface);

        Vector3 targetRotation = placement.rotation != null
            ? new Vector3(placement.rotation.x, placement.rotation.y, placement.rotation.z)
            : Vector3.zero;

        go.transform.localPosition = targetPosition;
        go.transform.localRotation = Quaternion.Euler(targetRotation);

        if (placement.scale != null) {
            go.transform.localScale = new Vector3(
                Mathf.Max(0.02f, placement.scale.x),
                Mathf.Max(0.02f, placement.scale.y),
                Mathf.Max(0.02f, placement.scale.z));
        }

        await SmartPlacer.EnsureInstance().ResolveOverlaps(go);

        Debug.Log($"[AIPlacement] Placed {placementType} at ({targetPosition.x:F2}, {targetPosition.y:F2}, {targetPosition.z:F2}) — {placement.reasoning}");
        return true;
    }

    async Task<GameObject> TryLoadFromObjaverse(string placementType, string resolvedId) {
        var resolver = EnsureObjaverseResolver();
        if (resolver == null)
            return null;

        string semanticId = NormalizeType(placementType);
        if (string.IsNullOrWhiteSpace(semanticId))
            semanticId = string.IsNullOrWhiteSpace(resolvedId) ? "unknown_object" : resolvedId;

        var tagList = BuildObjaverseTags(placementType);
        string category = placementType.Replace("_", " ").Trim();
        if (string.IsNullOrWhiteSpace(category))
            category = semanticId;

        var requestAsset = new SceneAsset {
            id = semanticId,
            category = category,
            tags = tagList,
            preferredStyle = string.Empty
        };

        Debug.Log($"[AIPlacement] Trying Objaverse for type='{placementType}' id='{requestAsset.id}' (resolvedId='{resolvedId}')");

        return await resolver.TryResolve(requestAsset);
    }

    static ObjaverseResolver EnsureObjaverseResolver() {
        if (ObjaverseResolver.Instance != null)
            return ObjaverseResolver.Instance;

        var host = GameObject.Find("HttpServerRuntime");
        if (host == null)
            host = EnsureInstance().gameObject;

        var resolver = host.GetComponent<ObjaverseResolver>();
        if (resolver == null)
            resolver = host.AddComponent<ObjaverseResolver>();

        return resolver;
    }

    static string ResolvePlacementType(AIPlacementResult placement) {
        if (placement == null)
            return null;

        if (!string.IsNullOrWhiteSpace(placement.type))
            return placement.type.Trim();

        return null;
    }

    static string NormalizePlacementTypeForResolution(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        string normalized = value.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");

        // Common singularization for model outputs such as "cars", "chairs", "tables".
        if (normalized.EndsWith("s") && normalized.Length > 3 && !normalized.EndsWith("ss"))
            normalized = normalized.Substring(0, normalized.Length - 1);

        switch (normalized) {
            case "automobile":
            case "vehicle":
                return "car";
            default:
                return normalized;
        }
    }

    static string[] BuildObjaverseTags(string placementType) {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string normalized = NormalizePlacementTypeForResolution(placementType) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalized)) {
            set.Add(normalized);
            set.Add(normalized.Replace("_", " "));
        }

        string[] tokens = normalized.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
            set.Add(tokens[i]);

        if (set.Count == 0)
            set.Add("object");

        return new List<string>(set).ToArray();
    }

    static Transform EnsureSpawnedObjectsContainer(Transform parent = null) {
        var existing = GameObject.Find("SpawnedObjects");
        if (existing != null) {
            if (parent != null && existing.transform.parent != parent)
                existing.transform.SetParent(parent, worldPositionStays: false);

            if (parent != null) {
                existing.transform.localPosition = Vector3.zero;
                existing.transform.localRotation = Quaternion.identity;
                existing.transform.localScale = Vector3.one;
            }

            return existing.transform;
        }

        var created = new GameObject("SpawnedObjects");
        if (parent != null) {
            created.transform.SetParent(parent, worldPositionStays: false);
            created.transform.localPosition = Vector3.zero;
            created.transform.localRotation = Quaternion.identity;
            created.transform.localScale = Vector3.one;
        }

        return created.transform;
    }

    static Vector3 KeepInsideRoom(Vector3 targetPosition, string surface) {
        if (string.IsNullOrWhiteSpace(surface))
            return targetPosition;

        string normalizedSurface = surface.Trim().ToLowerInvariant();
        const float inset = 0.08f;

        if (normalizedSurface.Contains("wall")) {
            if (Mathf.Abs(targetPosition.x) >= Mathf.Abs(targetPosition.z))
                targetPosition.x += targetPosition.x >= 0f ? -inset : inset;
            else
                targetPosition.z += targetPosition.z >= 0f ? -inset : inset;
        }

        if (normalizedSurface.Contains("ceiling"))
            targetPosition.y -= inset;

        if (normalizedSurface.Contains("floor") || normalizedSurface.Contains("ground"))
            targetPosition.y += inset;

        return targetPosition;
    }

    Transform ResolveEnvironmentRoot(string environmentId) {
        if (string.IsNullOrWhiteSpace(environmentId))
            return null;

        var directRoot = GameObject.Find($"ENV_{environmentId}");
        if (directRoot != null)
            return directRoot.transform;

        string normalizedId = NormalizeEnvironmentId(environmentId);
        var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        Transform best = null;
        float bestScore = float.NegativeInfinity;

        for (int i = 0; i < roots.Length; i++) {
            var root = roots[i];
            if (root == null)
                continue;

            string rootName = NormalizeEnvironmentId(root.name);
            float score = 0f;

            if (rootName == normalizedId)
                score += 100f;
            if (rootName.Contains(normalizedId) || normalizedId.Contains(rootName))
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

    static GameObject LoadPrefab(string addressablePath, string placementType = null, string resolvedId = null) {
        if (string.IsNullOrEmpty(addressablePath))
            return null;

        var pathVariants = GetResourcePathVariants(addressablePath);
        for (int i = 0; i < pathVariants.Length; i++) {
            var prefab = Resources.Load<GameObject>(pathVariants[i]);
            if (prefab != null)
                return prefab;
        }

#if UNITY_EDITOR
        var descriptorPath = TryResolveActualObjectPathFromDescriptors(placementType, resolvedId, addressablePath);
        if (!string.IsNullOrWhiteSpace(descriptorPath)) {
            var descriptorAsset = AssetDatabase.LoadAssetAtPath<GameObject>(descriptorPath);
            if (descriptorAsset != null)
                return descriptorAsset;
        }

        string basePath = "Assets/" + addressablePath;
        string[] editorCandidatePaths = {
            basePath + ".prefab",
            basePath + ".fbx",
            basePath + ".FBX"
        };

        for (int i = 0; i < editorCandidatePaths.Length; i++) {
            var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(editorCandidatePaths[i]);
            if (loaded != null)
                return loaded;
        }
#endif

        return null;
    }

    static string[] GetResourcePathVariants(string addressablePath) {
        var variants = new List<string>();

        if (string.IsNullOrEmpty(addressablePath))
            return variants.ToArray();

        variants.Add(addressablePath);

        if (addressablePath.StartsWith("PreloadedAssets/", StringComparison.OrdinalIgnoreCase))
            variants.Add(addressablePath.Substring("PreloadedAssets/".Length));
        else
            variants.Add($"PreloadedAssets/{addressablePath}");

        int slash = addressablePath.LastIndexOf('/');
        if (slash >= 0 && slash < addressablePath.Length - 1)
            variants.Add(addressablePath.Substring(slash + 1));

        return variants.ToArray();
    }

    static string TryResolveActualObjectPathFromDescriptors(string placementType, string resolvedId, string addressablePath) {
        try {
            string preloadedRoot = Path.Combine(Application.dataPath, "PreloadedAssets");
            if (!Directory.Exists(preloadedRoot))
                return null;

            string normalizedType = NormalizeType(placementType);
            string normalizedId = NormalizeType(resolvedId);

            var files = Directory.GetFiles(preloadedRoot, "*.json", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++) {
                string json = File.ReadAllText(files[i]);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                JObject doc;
                try {
                    doc = JObject.Parse(json);
                } catch {
                    continue;
                }

                string id = doc["id"]?.ToString();
                string key = doc["addressableKey"]?.ToString();
                string actual = doc["actualObjectPath"]?.ToString();

                bool idMatch = !string.IsNullOrWhiteSpace(id) &&
                               (NormalizeType(id) == normalizedType || NormalizeType(id) == normalizedId);
                bool keyMatch = !string.IsNullOrWhiteSpace(key) &&
                                key.Equals(addressablePath, StringComparison.OrdinalIgnoreCase);

                if ((idMatch || keyMatch) && !string.IsNullOrWhiteSpace(actual))
                    return actual.Replace('\\', '/');
            }
        } catch {
            // Non-fatal: just continue with other fallback loading strategies.
        }

        return null;
    }

    static string BuildEnvironmentObjectDetails(string baseEnvironmentJson) {
        if (string.IsNullOrWhiteSpace(baseEnvironmentJson))
            return null;

        try {
            var doc = JObject.Parse(baseEnvironmentJson);
            var objects = doc["objects"] as JArray;
            if (objects == null || objects.Count == 0)
                return "No object list was found in the environment JSON.";

            var sb = new StringBuilder();
            sb.AppendLine("Environment object inventory (id | name | type | role | position | scale):");

            for (int i = 0; i < objects.Count; i++) {
                if (objects[i] is not JObject obj)
                    continue;

                string id = obj["id"]?.ToString() ?? $"object_{i + 1}";
                string name = obj["name"]?.ToString() ?? "unnamed";
                string type = obj["type"]?.ToString() ?? "unknown";
                string role = obj["role"]?.ToString() ?? "unspecified";

                var p = ParseVector(obj["position"]);
                var s = ParseVector(obj["scale"]);

                sb.AppendLine($"- {id} | {name} | {type} | {role} | pos=({p.x:0.##}, {p.y:0.##}, {p.z:0.##}) | scale=({s.x:0.##}, {s.y:0.##}, {s.z:0.##})");
            }

            return sb.ToString().Trim();
        } catch {
            return "Environment object details were unavailable due to JSON parse failure.";
        }
    }

    static string BuildEnvironmentObjectIdList(string baseEnvironmentJson) {
        if (string.IsNullOrWhiteSpace(baseEnvironmentJson))
            return "[]";

        try {
            var doc = JObject.Parse(baseEnvironmentJson);
            var objects = doc["objects"] as JArray;
            if (objects == null || objects.Count == 0)
                return "[]";

            var ids = new List<string>();
            for (int i = 0; i < objects.Count; i++) {
                if (objects[i] is not JObject obj)
                    continue;

                string id = obj["id"]?.ToString();
                string name = obj["name"]?.ToString();
                string type = obj["type"]?.ToString();

                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id.Trim());
                if (!string.IsNullOrWhiteSpace(name))
                    ids.Add(name.Trim());
                if (!string.IsNullOrWhiteSpace(type))
                    ids.Add(type.Trim());
            }

            return JsonConvert.SerializeObject(ids, Formatting.Indented);
        } catch {
            return "[]";
        }
    }

    string ResolveOpenAIApiKey() {
        if (!string.IsNullOrWhiteSpace(openAIApiKey))
            return openAIApiKey.Trim();

        if (!string.IsNullOrWhiteSpace(openAIApiKeyEnvVar)) {
            var envValue = Environment.GetEnvironmentVariable(openAIApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(envValue))
                return envValue.Trim();
        }

        return null;
    }

    SceneLayoutContext ResolveSceneLayoutContext(string environmentId) {
        if (string.IsNullOrWhiteSpace(environmentId))
            return null;

        string normalizedId = NormalizeEnvironmentId(environmentId);
        if (sceneLayoutCache.TryGetValue(normalizedId, out var cached))
            return cached;

        string root = Path.Combine(Application.dataPath, "BaseEnvironments");
        if (!Directory.Exists(root))
            return null;

        string[] configFiles;
        try {
            configFiles = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
        } catch {
            return null;
        }

        for (int i = 0; i < configFiles.Length; i++) {
            try {
                string json = File.ReadAllText(configFiles[i]);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var doc = JObject.Parse(json);
                string fileEnvId = doc["environmentId"]?.ToString();
                if (NormalizeEnvironmentId(fileEnvId) != normalizedId)
                    continue;

                var context = BuildSceneLayoutContext(doc);
                if (context != null) {
                    sceneLayoutCache[normalizedId] = context;
                    return context;
                }
            } catch {
                // Ignore malformed config files and continue searching.
            }
        }

        return null;
    }

    string LoadBaseEnvironmentJson(string environmentId) {
        if (string.IsNullOrWhiteSpace(environmentId))
            return null;

        string root = Path.Combine(Application.dataPath, "BaseEnvironments");
        if (!Directory.Exists(root))
            return null;

        string normalizedId = NormalizeEnvironmentId(environmentId);

        try {
            var configFiles = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            for (int i = 0; i < configFiles.Length; i++) {
                string json = File.ReadAllText(configFiles[i]);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var doc = JObject.Parse(json);
                string fileEnvId = doc["environmentId"]?.ToString();
                if (NormalizeEnvironmentId(fileEnvId) != normalizedId)
                    continue;

                return json;
            }
        } catch (Exception e) {
            Debug.LogWarning($"[AIPlacement] Failed to load base environment JSON for '{environmentId}': {e.Message}");
        }

        return null;
    }

    static string LoadLocalPreloadedAssetJson() {
        string preloadedRoot = Path.Combine(Application.dataPath, "PreloadedAssets");
        if (!Directory.Exists(preloadedRoot))
            return "[]";

        try {
            var files = Directory.GetFiles(preloadedRoot, "*.json", SearchOption.TopDirectoryOnly);
            var entries = new JArray();

            for (int i = 0; i < files.Length; i++) {
                string json = File.ReadAllText(files[i]);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                try {
                    entries.Add(JObject.Parse(json));
                } catch {
                    // Ignore malformed local descriptor files.
                }
            }

            return entries.ToString(Formatting.Indented);
        } catch {
            return "[]";
        }
    }

    static SceneLayoutContext BuildSceneLayoutContext(JObject doc) {
        if (doc == null)
            return null;

        var referenceObjects = ParseSceneReferenceObjects(
            doc["objects"] as JArray ??
            doc["sceneObjects"] as JArray ??
            doc["referenceObjects"] as JArray);

        var explicitLayout = doc["sceneLayout"] ?? doc["sceneTransform"];
        if (explicitLayout is JObject layoutObj) {
            var explicitContext = new SceneLayoutContext {
                position = ParseVector(layoutObj["position"]),
                dimensions = ParseVector(layoutObj["dimensions"]),
                scale = ParseVector(layoutObj["scale"]),
                objects = referenceObjects
            };

            if (IsValidDimensions(explicitContext.dimensions))
                return explicitContext;
        }

        var spawnPoints = doc["spawnPoints"] as JArray;
        if (spawnPoints == null || spawnPoints.Count == 0)
            return referenceObjects != null && referenceObjects.Length > 0
                ? BuildSceneLayoutFromObjects(referenceObjects)
                : null;

        bool found = false;
        float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f, minZ = 0f, maxZ = 0f;

        for (int i = 0; i < spawnPoints.Count; i++) {
            var posToken = spawnPoints[i]?["position"];
            var pos = ParseVector(posToken);
            if (!HasNumericVector(posToken))
                continue;

            if (!found) {
                minX = maxX = pos.x;
                minY = maxY = pos.y;
                minZ = maxZ = pos.z;
                found = true;
            } else {
                minX = Mathf.Min(minX, pos.x);
                maxX = Mathf.Max(maxX, pos.x);
                minY = Mathf.Min(minY, pos.y);
                maxY = Mathf.Max(maxY, pos.y);
                minZ = Mathf.Min(minZ, pos.z);
                maxZ = Mathf.Max(maxZ, pos.z);
            }
        }

        if (!found)
            return null;

        return new SceneLayoutContext {
            position = new PositionData {
                x = (minX + maxX) * 0.5f,
                y = (minY + maxY) * 0.5f,
                z = (minZ + maxZ) * 0.5f
            },
            dimensions = new PositionData {
                x = Mathf.Max(0.1f, maxX - minX),
                y = Mathf.Max(0.1f, maxY - minY),
                z = Mathf.Max(0.1f, maxZ - minZ)
            },
            scale = new PositionData {
                x = 1f,
                y = 1f,
                z = 1f
            },
            objects = referenceObjects
        };
    }

    static SceneLayoutContext BuildSceneLayoutFromObjects(SceneReferenceObject[] objects) {
        if (objects == null || objects.Length == 0)
            return null;

        bool found = false;
        float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f, minZ = 0f, maxZ = 0f;

        for (int i = 0; i < objects.Length; i++) {
            var obj = objects[i];
            if (obj == null || obj.position == null)
                continue;

            float extentX = obj.scale != null ? Mathf.Abs(obj.scale.x) * 0.5f : 0f;
            float extentY = obj.scale != null ? Mathf.Abs(obj.scale.y) * 0.5f : 0f;
            float extentZ = obj.scale != null ? Mathf.Abs(obj.scale.z) * 0.5f : 0f;

            float objMinX = obj.position.x - extentX;
            float objMaxX = obj.position.x + extentX;
            float objMinY = obj.position.y - extentY;
            float objMaxY = obj.position.y + extentY;
            float objMinZ = obj.position.z - extentZ;
            float objMaxZ = obj.position.z + extentZ;

            if (!found) {
                minX = objMinX; maxX = objMaxX;
                minY = objMinY; maxY = objMaxY;
                minZ = objMinZ; maxZ = objMaxZ;
                found = true;
            } else {
                minX = Mathf.Min(minX, objMinX);
                maxX = Mathf.Max(maxX, objMaxX);
                minY = Mathf.Min(minY, objMinY);
                maxY = Mathf.Max(maxY, objMaxY);
                minZ = Mathf.Min(minZ, objMinZ);
                maxZ = Mathf.Max(maxZ, objMaxZ);
            }
        }

        if (!found)
            return null;

        return new SceneLayoutContext {
            position = new PositionData {
                x = (minX + maxX) * 0.5f,
                y = (minY + maxY) * 0.5f,
                z = (minZ + maxZ) * 0.5f
            },
            dimensions = new PositionData {
                x = Mathf.Max(0.1f, maxX - minX),
                y = Mathf.Max(0.1f, maxY - minY),
                z = Mathf.Max(0.1f, maxZ - minZ)
            },
            scale = new PositionData {
                x = 1f,
                y = 1f,
                z = 1f
            },
            objects = objects
        };
    }

    static SceneReferenceObject[] ParseSceneReferenceObjects(JArray objects) {
        if (objects == null || objects.Count == 0)
            return null;

        var result = new List<SceneReferenceObject>();
        for (int i = 0; i < objects.Count; i++) {
            if (objects[i] is not JObject obj)
                continue;

            result.Add(new SceneReferenceObject {
                id = obj["id"]?.ToString(),
                name = obj["name"]?.ToString(),
                type = obj["type"]?.ToString(),
                position = ParseVector(obj["position"]),
                scale = ParseVector(obj["scale"]),
                role = obj["role"]?.ToString()
            });
        }

        return result.Count > 0 ? result.ToArray() : null;
    }

    static bool IsValidDimensions(PositionData dimensions) {
        if (dimensions == null)
            return false;

        return dimensions.x > 0f && dimensions.y > 0f && dimensions.z > 0f;
    }

    static bool HasNumericVector(JToken token) {
        if (token == null)
            return false;

        return token["x"] != null && token["y"] != null && token["z"] != null;
    }

    static PositionData ParseVector(JToken token) {
        return new PositionData {
            x = token?["x"]?.Value<float>() ?? 0f,
            y = token?["y"]?.Value<float>() ?? 0f,
            z = token?["z"]?.Value<float>() ?? 0f
        };
    }

    static Vector3 ClampToSceneLayout(Vector3 candidate, SceneLayoutContext layout) {
        if (layout?.position == null || layout.dimensions == null)
            return candidate;

        float halfX = layout.dimensions.x * 0.5f;
        float halfY = layout.dimensions.y * 0.5f;
        float halfZ = layout.dimensions.z * 0.5f;

        float minX = layout.position.x - halfX;
        float maxX = layout.position.x + halfX;
        float minY = layout.position.y - halfY;
        float maxY = layout.position.y + halfY;
        float minZ = layout.position.z - halfZ;
        float maxZ = layout.position.z + halfZ;

        return new Vector3(
            Mathf.Clamp(candidate.x, minX, maxX),
            Mathf.Clamp(candidate.y, minY, maxY),
            Mathf.Clamp(candidate.z, minZ, maxZ)
        );
    }

    static string NormalizeEnvironmentId(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value.ToLowerInvariant()
                    .Replace("-", "")
                    .Replace("_", "")
                    .Replace(" ", "");
    }

    static string GeneratePlacementId(string objectType, string placementText, PositionData position) {
        string baseText = $"{objectType}|{placementText}|{position?.x:F2}|{position?.y:F2}|{position?.z:F2}";
        uint hash = 2166136261;

        for (int i = 0; i < baseText.Length; i++) {
            hash ^= baseText[i];
            hash *= 16777619;
        }

        string typePart = string.IsNullOrWhiteSpace(objectType) ? "object" : NormalizeType(objectType);
        return $"{typePart}_{hash:x8}";
    }

    static string NormalizeType(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return "object";

        return value.Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
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

[Serializable]
public class AIPlacementResult {
    public string id;
    public string type;

    [JsonProperty("assetId")]
    string AssetIdAlias {
        set {
            if (string.IsNullOrWhiteSpace(type))
                type = value;
        }
    }

    [JsonProperty("objectType")]
    string ObjectTypeAlias {
        set {
            if (string.IsNullOrWhiteSpace(type))
                type = value;
        }
    }

    [JsonProperty("name")]
    string NameAlias {
        set {
            if (string.IsNullOrWhiteSpace(type))
                type = value;
        }
    }

    public PositionData position;
    public PositionData rotation;
    public PositionData scale;
    public string surface;
    public string reasoning;
}

[Serializable]
public class AIPlacementRequest {
    public string[] screenshots;
    public SceneObjectData[] objectsToPlace;
    public SceneInventory sceneInventory;
    public float intensity;
    public string environmentId;
    public SceneLayoutContext sceneLayout;
    public string placementText;
}

[Serializable]
public class OpenRouterResponse {
    public OpenRouterChoice[] choices;
}

[Serializable]
public class OpenRouterChoice {
    public OpenRouterMessage message;
}

[Serializable]
public class OpenRouterMessage {
    public string content;
}

[Serializable]
public class SceneLayoutContext {
    public PositionData position;
    public PositionData dimensions;
    public PositionData scale;
    public SceneReferenceObject[] objects;
}

[Serializable]
public class SceneReferenceObject {
    public string id;
    public string name;
    public string type;
    public PositionData position;
    public PositionData scale;
    public string role;
}

[Serializable]
public class AIPlacementEnvelope {
    public AIPlacementResult[] objects;
    public AIPlacementResult[] placements;
    public AIPlacementResult[] layout;
}