using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

public class PictureRoomImageOrchestrator : MonoBehaviour {

    static readonly string[] PreferredPhotoObjectNames = {
        "photo1", "photo2", "photo3", "photo4", "photo5", "photo6", "photo7"
    };

    public static PictureRoomImageOrchestrator Instance { get; private set; }

    [Header("Picture Room Image Generation")]
    [SerializeField] bool enablePictureRoomGeneration = true;
    [SerializeField] string imageApiBaseUrl = "http://127.0.0.1:8000";
    [SerializeField] string generatePath = "/generate";
    [SerializeField, Range(1, 100)] int numInferenceSteps = 20;
    [SerializeField, Range(1f, 20f)] float guidanceScale = 7.5f;
    [SerializeField, Min(1)] int expectedPromptCount = 7;
    [SerializeField] bool autoDiscoverRenderers = true;
    [SerializeField] Renderer[] pictureRenderers;
    [SerializeField] string downloadFolderName = "GeneratedPictureRoomImages";

    static readonly HttpClient http = new HttpClient {
        Timeout = TimeSpan.FromSeconds(300)
    };

    readonly List<Texture2D> runtimeTextures = new List<Texture2D>();
    readonly List<Texture2D> preparedTextures = new List<Texture2D>();
    readonly List<string> preparedImagePaths = new List<string>();

    bool isPreparing;
    bool isGeneratingDisplay;
    string preparedPromptSignature;
    string inFlightPreloadSignature;
    string inFlightDisplaySignature;
    Task<bool> activePreloadTask;
    Task activeDisplayTask;

    public bool IsPreparing => isPreparing;
    public int PreparedImageCount => preparedTextures.Count;
    public int ExpectedPromptCount => expectedPromptCount;

    void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public static PictureRoomImageOrchestrator EnsureInstance() {
        if (Instance != null)
            return Instance;

        var go = new GameObject("PictureRoomImageOrchestrator");
        return go.AddComponent<PictureRoomImageOrchestrator>();
    }

    public void GenerateAndDisplayForPictureRoom(string environmentId, string[] prompts) {
        if (!enablePictureRoomGeneration)
            return;

        if (!IsPictureRoomEnvironment(environmentId))
            return;

        if (prompts == null || prompts.Length == 0) {
            Debug.LogWarning("[PictureRoomImages] Picture room selected but prompt list is empty");
            return;
        }

        string signature = BuildPromptSignature(prompts);

        if (isPreparing && inFlightPreloadSignature == signature) {
            Debug.Log("[PictureRoomImages] Preload already in progress for this prompt set; skipping duplicate display generation trigger");
            return;
        }

        if (isGeneratingDisplay && inFlightDisplaySignature == signature) {
            Debug.Log("[PictureRoomImages] Display generation already in progress for this prompt set; skipping duplicate trigger");
            return;
        }

        if (preparedTextures.Count >= expectedPromptCount && preparedPromptSignature == signature) {
            ApplyPreparedImagesToRenderers();
            return;
        }

        inFlightDisplaySignature = signature;
        activeDisplayTask = GenerateAndDisplayAsync(prompts, signature);
    }

    public Task<bool> PreloadImagesForPictureRoom(string environmentId, string[] prompts) {
        if (!enablePictureRoomGeneration)
            return Task.FromResult(false);

        if (!IsPictureRoomEnvironment(environmentId))
            return Task.FromResult(false);

        if (prompts == null || prompts.Length < expectedPromptCount) {
            Debug.LogWarning($"[PictureRoomImages] Cannot preload images: expected {expectedPromptCount} prompts but got {prompts?.Length ?? 0}");
            return Task.FromResult(false);
        }

        string signature = BuildPromptSignature(prompts);

        if (isPreparing && activePreloadTask != null && !activePreloadTask.IsCompleted && inFlightPreloadSignature == signature) {
            Debug.Log("[PictureRoomImages] Reusing in-flight preload for identical Picture Room prompts");
            return activePreloadTask;
        }

        if (preparedTextures.Count >= expectedPromptCount && preparedPromptSignature == signature) {
            Debug.Log("[PictureRoomImages] Picture Room prompts already preloaded; skipping regeneration");
            return Task.FromResult(true);
        }

        inFlightPreloadSignature = signature;
        activePreloadTask = PreloadImagesInternal(prompts, signature);
        return activePreloadTask;
    }

    async Task<bool> PreloadImagesInternal(string[] prompts, string signature) {
        isPreparing = true;
        ClearRuntimeTextures();
        preparedTextures.Clear();
        preparedImagePaths.Clear();
        preparedPromptSignature = null;

        int loaded = 0;
        try {
            for (int i = 0; i < expectedPromptCount; i++) {
                string prompt = prompts[i];
                if (string.IsNullOrWhiteSpace(prompt)) {
                    Debug.LogWarning($"[PictureRoomImages] Prompt #{i + 1} is empty during preload");
                    return false;
                }

                byte[] pngBytes = await GenerateImageAsync(prompt);
                if (pngBytes == null || pngBytes.Length == 0) {
                    Debug.LogWarning($"[PictureRoomImages] Failed to generate image #{i + 1} during preload");
                    return false;
                }

                string imagePath = SaveDownloadedImage(i, pngBytes);
                preparedImagePaths.Add(imagePath);

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(pngBytes)) {
                    Destroy(texture);
                    Debug.LogWarning($"[PictureRoomImages] Failed to decode preloaded PNG #{i + 1}");
                    return false;
                }

                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                preparedTextures.Add(texture);
                runtimeTextures.Add(texture);
                loaded++;
            }

            Debug.Log($"[PictureRoomImages] Preloaded {loaded}/{expectedPromptCount} image(s) for Picture Room");
            bool success = loaded == expectedPromptCount;
            if (success)
                preparedPromptSignature = signature;

            return success;
        } finally {
            isPreparing = false;
        }
    }

    async Task GenerateAndDisplayAsync(string[] prompts, string signature) {
        var slots = ResolveRenderers();
        if (slots.Count == 0) {
            Debug.LogWarning("[PictureRoomImages] No picture renderers found. Assign Picture Renderers in the component or rename objects to include 'picture', 'photo', or 'frame'.");
            return;
        }

        if (prompts.Length < expectedPromptCount) {
            Debug.LogWarning($"[PictureRoomImages] Expected {expectedPromptCount} prompts for Picture Room but got {prompts.Length}");
        }

        int targetCount = Mathf.Min(expectedPromptCount, Mathf.Min(prompts.Length, slots.Count));
        Debug.Log($"[PictureRoomImages] Generating {targetCount} image(s) for Picture Room");

        isGeneratingDisplay = true;
        try {
            for (int i = 0; i < targetCount; i++) {
                string prompt = prompts[i];
                if (string.IsNullOrWhiteSpace(prompt)) {
                    Debug.LogWarning($"[PictureRoomImages] Prompt #{i + 1} is empty. Skipping.");
                    continue;
                }

                byte[] pngBytes = await GenerateImageAsync(prompt);
                if (pngBytes == null || pngBytes.Length == 0)
                    continue;

                ApplyPngToRenderer(slots[i], pngBytes, i);
            }
        } finally {
            isGeneratingDisplay = false;
            inFlightDisplaySignature = null;
        }
    }

    static string BuildPromptSignature(string[] prompts) {
        if (prompts == null || prompts.Length == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append(prompts.Length);

        for (int i = 0; i < prompts.Length; i++) {
            sb.Append('|');
            sb.Append((prompts[i] ?? string.Empty).Trim());
        }

        return sb.ToString();
    }

    void ApplyPreparedImagesToRenderers() {
        var slots = ResolveRenderers();
        if (slots.Count == 0) {
            Debug.LogWarning("[PictureRoomImages] No picture renderers found while applying preloaded images");
            return;
        }

        Debug.Log($"[PictureRoomImages] Applying {preparedTextures.Count} prepared textures to {slots.Count} renderers");

        int count = Mathf.Min(expectedPromptCount, Mathf.Min(preparedTextures.Count, slots.Count));
        for (int i = 0; i < count; i++) {
            Texture2D texture = LoadPreparedTexture(i);
            if (texture == null) {
                Debug.Log($"[PictureRoomImages] LoadPreparedTexture({i}) returned null, using preparedTextures[{i}]");
                texture = preparedTextures[i];
            }

            if (texture == null) {
                Debug.LogWarning($"[PictureRoomImages] No texture available for slot {i}");
                continue;
            }

            ApplyTextureToRenderer(slots[i], texture, i);
        }

        Debug.Log($"[PictureRoomImages] Applied {count} preloaded image(s) to Picture Room frames");
    }

    async Task<byte[]> GenerateImageAsync(string prompt) {
        string url = BuildGenerateUrl();

        var payload = new GenerateRequest {
            prompt = prompt,
            num_inference_steps = Mathf.Clamp(numInferenceSteps, 1, 100),
            guidance_scale = Mathf.Clamp(guidanceScale, 1f, 20f)
        };

        string json = JsonConvert.SerializeObject(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try {
            response = await http.PostAsync(url, content);
        } catch (Exception e) {
            Debug.LogWarning($"[PictureRoomImages] Could not reach image API at {url}: {e.Message}");
            return null;
        }

        if (!response.IsSuccessStatusCode) {
            string errorBody = await response.Content.ReadAsStringAsync();
            Debug.LogWarning($"[PictureRoomImages] Image API returned {(int)response.StatusCode}: {errorBody}");
            return null;
        }

        string mediaType = response.Content.Headers?.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("image")) {
            string body = await response.Content.ReadAsStringAsync();
            Debug.LogWarning($"[PictureRoomImages] Expected image response but got '{mediaType}'. Body: {body}");
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync();
    }

    void ApplyPngToRenderer(Renderer renderer, byte[] pngBytes, int index) {
        if (renderer == null)
            return;

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(pngBytes)) {
            Destroy(texture);
            Debug.LogWarning($"[PictureRoomImages] Failed to decode PNG for slot #{index + 1}");
            return;
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        runtimeTextures.Add(texture);

        SaveDownloadedImage(index, pngBytes);

        ApplyTextureToRenderer(renderer, texture, index);
    }

    Texture2D LoadPreparedTexture(int index) {
        if (index < 0 || index >= preparedImagePaths.Count)
            return null;

        string path = preparedImagePaths[index];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        byte[] bytes;
        try {
            bytes = File.ReadAllBytes(path);
        } catch (Exception e) {
            Debug.LogWarning($"[PictureRoomImages] Could not read downloaded image '{path}': {e.Message}");
            return null;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(bytes)) {
            Destroy(texture);
            return null;
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        runtimeTextures.Add(texture);
        return texture;
    }

    string SaveDownloadedImage(int index, byte[] pngBytes) {
        if (pngBytes == null || pngBytes.Length == 0)
            return null;

        string folder = Path.Combine(Application.persistentDataPath, downloadFolderName);
        try {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string fileName = $"photo_{index + 1:00}.png";
            string fullPath = Path.Combine(folder, fileName);
            File.WriteAllBytes(fullPath, pngBytes);
            Debug.Log($"[PictureRoomImages] Downloaded image saved: {fullPath}");
            return fullPath;
        } catch (Exception e) {
            Debug.LogWarning($"[PictureRoomImages] Failed to save downloaded image #{index + 1}: {e.Message}");
            return null;
        }
    }

    void ApplyTextureToRenderer(Renderer renderer, Texture2D texture, int index) {
        if (renderer == null || texture == null) {
            Debug.LogWarning($"[PictureRoomImages] Cannot apply texture #{index + 1}: renderer={renderer}, texture={texture}");
            return;
        }

        var materials = renderer.materials;
        if (materials == null || materials.Length == 0) {
            Debug.LogWarning($"[PictureRoomImages] Renderer '{renderer.name}' has no material");
            return;
        }

        int materialIndex = FindPhotoMaterialIndex(materials);
        if (materialIndex < 0)
            materialIndex = 0;

        var material = materials[materialIndex];
        if (material == null) {
            Debug.LogWarning($"[PictureRoomImages] Renderer '{renderer.name}' material slot #{materialIndex} is null");
            return;
        }

        // Try to set texture with multiple property names for compatibility
        if (material.HasProperty("_BaseMap")) {
            material.SetTexture("_BaseMap", texture);
            Debug.Log($"[PictureRoomImages] Set _BaseMap on material '{material.name}'");
        }
        if (material.HasProperty("_MainTex")) {
            material.SetTexture("_MainTex", texture);
            Debug.Log($"[PictureRoomImages] Set _MainTex on material '{material.name}'");
        }

        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);

        // IMPORTANT: Update the materials array and reassign to renderer
        materials[materialIndex] = material;
        renderer.materials = materials;

        Debug.Log($"[PictureRoomImages] Applied generated image #{index + 1} to renderer '{renderer.name}' ({renderer.gameObject.name}) material '{material.name}' (slot #{materialIndex}), texture size: {texture.width}x{texture.height}");
    }

    static int FindPhotoMaterialIndex(Material[] materials) {
        for (int i = 0; i < materials.Length; i++) {
            var mat = materials[i];
            if (mat == null)
                continue;

            string name = mat.name.ToLowerInvariant();
            if (name.StartsWith("photo_") || name.Contains("photo_"))
                return i;
        }

        return -1;
    }

    void ClearRuntimeTextures() {
        for (int i = 0; i < runtimeTextures.Count; i++) {
            if (runtimeTextures[i] != null)
                Destroy(runtimeTextures[i]);
        }

        runtimeTextures.Clear();
    }

    List<Renderer> ResolveRenderers() {
        var result = new List<Renderer>();

        if (pictureRenderers != null && pictureRenderers.Length > 0) {
            for (int i = 0; i < pictureRenderers.Length; i++) {
                if (pictureRenderers[i] != null)
                    result.Add(pictureRenderers[i]);
            }

            if (result.Count > 0) {
                Debug.Log($"[PictureRoomImages] Found {result.Count} picture renderers from component assignment");
                return result;
            }
        }

        if (!autoDiscoverRenderers) {
            Debug.LogWarning("[PictureRoomImages] Auto-discovery disabled and no picture renderers assigned");
            return result;
        }

        // Prefer deterministic picture-room slots: photo1..photo7.
        for (int i = 0; i < PreferredPhotoObjectNames.Length; i++) {
            var renderer = FindRendererForNamedPhotoObject(PreferredPhotoObjectNames[i]);
            if (renderer != null) {
                result.Add(renderer);
                Debug.Log($"[PictureRoomImages] Found photo renderer '{PreferredPhotoObjectNames[i]}' ({renderer.gameObject.name})");
            }
        }

        if (result.Count > 0) {
            Debug.Log($"[PictureRoomImages] Discovered {result.Count} preferred photo renderers");
            return result;
        }

        var allRenderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        for (int i = 0; i < allRenderers.Length; i++) {
            var renderer = allRenderers[i];
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;

            string name = renderer.name.ToLowerInvariant();
            if (name.Contains("picture") || name.Contains("photo") || name.Contains("frame"))
                result.Add(renderer);
        }

        result.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        Debug.Log($"[PictureRoomImages] Fallback: Found {result.Count} renderers with 'picture'/'photo'/'frame' in name");
        for (int i = 0; i < result.Count; i++) {
            Debug.Log($"  [{i}] {result[i].gameObject.name} ({result[i].name})");
        }
        return result;
    }

    static Renderer FindRendererForNamedPhotoObject(string objectName) {
        if (string.IsNullOrWhiteSpace(objectName))
            return null;

        // First try exact case match
        var root = GameObject.Find(objectName);
        
        // If not found, try case-insensitive search
        if (root == null) {
            var allObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            string lowerName = objectName.ToLowerInvariant();
            for (int i = 0; i < allObjects.Length; i++) {
                if (allObjects[i].name.ToLowerInvariant() == lowerName) {
                    root = allObjects[i];
                    break;
                }
            }
        }

        if (root == null)
            return null;

        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: false);
        if (renderers == null || renderers.Length == 0)
            return null;

        for (int i = 0; i < renderers.Length; i++) {
            var renderer = renderers[i];
            if (renderer == null)
                continue;

            if (FindPhotoMaterialIndex(renderer.sharedMaterials) >= 0)
                return renderer;
        }

        return renderers[0];
    }

    string BuildGenerateUrl() {
        string baseUrl = string.IsNullOrWhiteSpace(imageApiBaseUrl)
            ? "http://127.0.0.1:8000"
            : imageApiBaseUrl.TrimEnd('/');

        string path = string.IsNullOrWhiteSpace(generatePath) ? "/generate" : generatePath;
        if (!path.StartsWith("/"))
            path = "/" + path;

        return baseUrl + path;
    }

    static bool IsPictureRoomEnvironment(string environmentId) {
        return NormalizeEnvironmentId(environmentId) == "pictureroom";
    }

    static string NormalizeEnvironmentId(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value.ToLowerInvariant()
                    .Replace("-", "")
                    .Replace("_", "")
                    .Replace(" ", "");
    }

    void OnDestroy() {
        ClearRuntimeTextures();
        preparedTextures.Clear();
        preparedImagePaths.Clear();
    }

    [Serializable]
    class GenerateRequest {
        public string prompt;
        public int num_inference_steps;
        public float guidance_scale;
    }
}
