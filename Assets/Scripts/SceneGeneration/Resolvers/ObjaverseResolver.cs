using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class ObjaverseResolver : MonoBehaviour {

    public static ObjaverseResolver Instance { get; private set; }

    [SerializeField] string objaverseApiBase = "http://127.0.0.1:5001";

    static readonly HttpClient http = new HttpClient();

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        if (!string.IsNullOrWhiteSpace(objaverseApiBase) && objaverseApiBase.Contains("localhost")) {
            objaverseApiBase = objaverseApiBase.Replace("localhost", "127.0.0.1");
        }
        DontDestroyOnLoad(gameObject);
    }

    public async Task<GameObject> TryResolve(SceneAsset asset) {
        string glbUrl = await SearchForAsset(asset);
        if (glbUrl == null) return null;

        var loader = EnsureGlbLoader();
        if (loader == null) {
            Debug.LogWarning("[ObjaverseResolver] GLBLoader is unavailable");
            return null;
        }

        var go = await loader.LoadFromUrl(glbUrl);
        if (go == null) return null;

        await TryCacheLocally(go, asset);

        return go;
    }

    static GLBLoader EnsureGlbLoader() {
        if (GLBLoader.Instance != null)
            return GLBLoader.Instance;

        var host = GameObject.Find("HttpServerRuntime");
        if (host == null)
            host = new GameObject("ObjaverseRuntime");

        var loader = host.GetComponent<GLBLoader>();
        if (loader == null)
            loader = host.AddComponent<GLBLoader>();

        return loader;
    }

    async Task TryCacheLocally(GameObject go, SceneAsset asset) {
        if (go == null || asset == null)
            return;

        var updater = ManifestUpdater.Instance;
        var registry = LocalAssetRegistry.Instance;
        if (updater == null || registry == null) {
            Debug.Log("[ObjaverseResolver] Manifest cache services not available; using downloaded object without local registration");
            return;
        }

        try {
            string savedPath = await SaveGLBLocally(go, asset);
            var bounds = MeasureBounds(go);
            await updater.RegisterNewAsset(asset, savedPath, bounds);
        } catch (System.Exception e) {
            Debug.LogWarning($"[ObjaverseResolver] Could not cache downloaded asset: {e.Message}");
        }
    }

    async Task<string> SearchForAsset(SceneAsset asset) {
        string url = $"{objaverseApiBase}/search";
        var request = new ObjaverseSearchRequest {
            category = asset.category ?? string.Empty,
            tags = asset.tags ?? new string[0]
        };
        Debug.Log($"[ObjaverseResolver] Searching Objaverse for id='{asset.id}' category='{request.category}' tags=[{string.Join(",", request.tags)}] via {url}");
        var body = JsonUtility.ToJson(request);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var response = await http.PostAsync(url, content);
            var json     = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) {
                Debug.LogWarning($"[ObjaverseResolver] Search HTTP {(int)response.StatusCode}: {json}");
                return null;
            }
            var results  = JsonUtility.FromJson<ObjaverseSearchResult>(json);

            if (results == null || !results.found || string.IsNullOrEmpty(results.glbUrl)) {
                Debug.LogWarning($"[ObjaverseResolver] No match found for '{asset.id}'. Response: {json}");
                return null;
            }
            Debug.Log($"[ObjaverseResolver] Resolved '{asset.id}' -> {results.glbUrl}");
            return results.glbUrl;
        } catch (System.Exception e) {
            Debug.LogWarning($"[ObjaverseResolver] Search failed: {e.Message}");
            return null;
        }
    }

    async Task<string> SaveGLBLocally(GameObject go, SceneAsset asset) {
        // save bytes to persistent storage — implement based on your GLB loader
        string path = System.IO.Path.Combine(
            Application.persistentDataPath, "CachedAssets", asset.id + ".glb");
        await Task.CompletedTask; // replace with actual save logic
        return path;
    }

    BoundsData MeasureBounds(GameObject go) {
        var renderer = go.GetComponentInChildren<Renderer>();
        if (renderer == null) return new BoundsData { x = 1, y = 1, z = 1 };
        var b = renderer.bounds;
        return new BoundsData { x = b.size.x, y = b.size.y, z = b.size.z };
    }
}

[System.Serializable]
public class ObjaverseSearchResult {
    public bool found;
    public string uid;
    public string glbUrl;
    public string reason;
}

[System.Serializable]
public class ObjaverseSearchRequest {
    public string category;
    public string[] tags;
}