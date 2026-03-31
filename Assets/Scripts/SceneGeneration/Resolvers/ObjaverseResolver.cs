using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class ObjaverseResolver : MonoBehaviour {

    public static ObjaverseResolver Instance { get; private set; }

    [SerializeField] string objaverseApiBase = "http://localhost:5001";

    static readonly HttpClient http = new HttpClient();

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    public async Task<GameObject> TryResolve(SceneAsset asset) {
        string glbUrl = await SearchForAsset(asset);
        if (glbUrl == null) return null;

        var go = await GLBLoader.Instance.LoadFromUrl(glbUrl);
        if (go == null) return null;

        // cache it locally for next time
        string savedPath = await SaveGLBLocally(go, asset);
        var bounds = MeasureBounds(go);
        await ManifestUpdater.Instance.RegisterNewAsset(asset, savedPath, bounds);

        return go;
    }

    async Task<string> SearchForAsset(SceneAsset asset) {
        string url = $"{objaverseApiBase}/search";
        var request = new ObjaverseSearchRequest {
            category = asset.category ?? string.Empty,
            tags = asset.tags ?? new string[0]
        };
        var body = JsonUtility.ToJson(request);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var response = await http.PostAsync(url, content);
            var json     = await response.Content.ReadAsStringAsync();
            var results  = JsonUtility.FromJson<ObjaverseSearchResult>(json);

            if (results == null || !results.found || string.IsNullOrEmpty(results.glbUrl)) return null;
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