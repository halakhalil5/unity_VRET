using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class ManifestUpdater : MonoBehaviour {

    public static ManifestUpdater Instance { get; private set; }

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public async Task RegisterNewAsset(SceneAsset original, string savedPath, BoundsData bounds) {
        var registry = LocalAssetRegistry.Instance;
        var manifest = registry.GetManifest();
        var lookup   = registry.GetLookup();

        var newEntry = new LocalAssetEntry {
            id             = original.id,
            addressableKey = savedPath,
            category       = original.category,
            tags           = original.tags,
            style          = original.preferredStyle,
            bounds         = bounds
        };

        var list = manifest.assets.ToList();
        list.Add(newEntry);
        manifest.assets = list.ToArray();
        lookup[newEntry.id] = newEntry;

        string json = JsonUtility.ToJson(manifest, true);
        string path = Path.Combine(Application.streamingAssetsPath, "AssetManifest.json");
        await File.WriteAllTextAsync(path, json);

        Debug.Log($"[ManifestUpdater] Registered new asset: {original.id}");
    }
}