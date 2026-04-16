using System.Threading.Tasks;
using UnityEngine;

public class LocalResolver : MonoBehaviour {

    public static LocalResolver Instance { get; private set; }

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public async Task<GameObject> TryResolve(SceneAsset asset) {
        var registry = LocalAssetRegistry.Instance;
        var match    = registry.FindBestMatch(asset);

        if (match == null) {
            Debug.Log($"[LocalResolver] No match for {asset.id}");
            return null;
        }

        if (!registry.BoundsFit(match.entry, asset)) {
            Debug.Log($"[LocalResolver] Match found but bounds don't fit for {asset.id}");
            return null;
        }

        Debug.Log($"[LocalResolver] Matched {asset.id} → {match.entry.id} (score:{match.score})");
        return await LoadLocalPrefab(match.entry.addressableKey);
    }

    async Task<GameObject> LoadLocalPrefab(string key) {
        var req = Resources.LoadAsync<GameObject>(key);
        while (!req.isDone) {
            await Task.Yield();
        }

        var prefab = req.asset as GameObject;
        if (prefab == null) {
            Debug.LogWarning($"[LocalResolver] Resources load failed: {key}");
            return null;
        }

        return Instantiate(prefab);
    }
}