using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class AIPlacementOrchestrator : MonoBehaviour {

    public static AIPlacementOrchestrator Instance { get; private set; }

    [Header("AI Placement")]
    [SerializeField] bool useAIPlacement = true;
    [SerializeField] string openRouterUrl = "https://openrouter.ai/api/v1/chat/completions";
    [SerializeField] string openRouterModel = "openai/gpt-4o";

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

        string prompt = BuildPlacementPrompt(
            placementText,
            baseEnvironmentJson,
            baseEnvironmentDetails,
            baseEnvironmentObjectIds,
            activeSceneLayout,
            inventory,
            intensity,
            objects);

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
            max_tokens = 2000,
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

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter returned {(int)response.StatusCode}: {responseStr}");

        return ExtractOpenRouterContent(responseStr);
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
- Extract concrete object mentions from therapist text.
- Exclude objects that are already part of the base environment object IDs/details.
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
- Return position, rotation, scale, anchoredTo, surface, and reasoning for each object.

Output format:
{
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

    string BuildPlacementPrompt(string therapistDescription, string baseEnvironmentJson, string baseEnvironmentDetails, string baseEnvironmentObjectIds, SceneLayoutContext sceneLayout, SceneInventory inventory, float intensity, SceneObjectData[] objectsToPlace) {
        string environmentJson = string.IsNullOrWhiteSpace(baseEnvironmentJson) ? "{}" : baseEnvironmentJson;
        string environmentDetails = string.IsNullOrWhiteSpace(baseEnvironmentDetails) ? "No explicit environment object details were found." : baseEnvironmentDetails;
        string environmentObjectIds = string.IsNullOrWhiteSpace(baseEnvironmentObjectIds) ? "[]" : baseEnvironmentObjectIds;
        string sceneLayoutJson = sceneLayout != null ? JsonConvert.SerializeObject(sceneLayout, Formatting.Indented) : "null";
        string inventoryJson = inventory != null ? JsonConvert.SerializeObject(inventory, Formatting.Indented) : "null";
        string requestedObjectsJson = objectsToPlace != null ? JsonConvert.SerializeObject(objectsToPlace, Formatting.Indented) : "null";

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

PLACEMENT CONSTRAINTS:
- Use the environment's local coordinate space.
- Keep each object inside the selected room.
- For a wall request, place the object slightly inside the wall surface.
- Return scale values that are visible at the room's current scale.
- Do not place objects already listed in BASE ENVIRONMENT OBJECT IDS.

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

        string resolvedId = string.IsNullOrWhiteSpace(placement.id)
            ? GeneratePlacementId(placementType, activePlacementText, placement.position)
            : placement.id;

        GameObject go;
        var resolution = LocalAssetResolver.Resolve(resolvedId, placementType);
        if (resolution.found && resolution.asset != null && !string.IsNullOrEmpty(resolution.prefabPath)) {
            var prefab = LoadPrefab(resolution.prefabPath);
            if (prefab == null) {
                Debug.LogWarning($"[AIPlacement] Prefab not loadable for '{placementType}' ({resolution.prefabPath})");
                return false;
            }

            var container = EnsureSpawnedObjectsContainer(activeEnvironmentRoot);
            go = Instantiate(prefab, container, worldPositionStays: false);
        } else {
            go = await TryLoadFromObjaverse(placementType, resolvedId);
            if (go == null) {
                Debug.LogWarning($"[AIPlacement] No local or Objaverse asset found for '{placementType}', skipping");
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

        var requestAsset = new SceneAsset {
            id = string.IsNullOrWhiteSpace(resolvedId) ? NormalizeType(placementType) : resolvedId,
            category = placementType,
            tags = new[] { placementType },
            preferredStyle = string.Empty
        };

        Debug.Log($"[AIPlacement] Trying Objaverse for type='{placementType}' id='{requestAsset.id}'");

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

    static GameObject LoadPrefab(string addressablePath) {
        if (string.IsNullOrEmpty(addressablePath))
            return null;

        var paths = new List<string> {
            addressablePath
        };

        if (!addressablePath.StartsWith("PreloadedAssets/", StringComparison.OrdinalIgnoreCase))
            paths.Add($"PreloadedAssets/{addressablePath}");

        if (addressablePath.StartsWith("PreloadedAssets/", StringComparison.OrdinalIgnoreCase)) {
            paths.Add(addressablePath.Substring("PreloadedAssets/".Length));
        }

        int slash = addressablePath.LastIndexOf('/');
        if (slash >= 0 && slash < addressablePath.Length - 1)
            paths.Add(addressablePath.Substring(slash + 1));

        for (int i = 0; i < paths.Count; i++) {
            var prefab = Resources.Load<GameObject>(paths[i]);
            if (prefab != null)
                return prefab;
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