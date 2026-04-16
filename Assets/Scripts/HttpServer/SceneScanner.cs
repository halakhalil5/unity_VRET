using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class SceneScanner : MonoBehaviour {

    public static SceneScanner Instance { get; private set; }

    [SerializeField] Camera scanCamera;
    [SerializeField] int captureWidth = 1024;
    [SerializeField] int captureHeight = 768;
    [SerializeField] float topDownHeight = 8f;
    [SerializeField] Vector3 playerViewOffset = new Vector3(0f, 1.6f, -3f);

    void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public static SceneScanner EnsureInstance() {
        if (Instance != null)
            return Instance;

        var go = new GameObject("SceneScanner");
        return go.AddComponent<SceneScanner>();
    }

    public async Task<string[]> CaptureSceneViews() {
        var screenshots = new string[2];
        Vector3 center = ResolveRoomCenter();

        screenshots[0] = CaptureFromAngle(
            center + new Vector3(0f, topDownHeight, 0f),
            new Vector3(90f, 0f, 0f),
            "scan_topdown"
        );

        screenshots[1] = CaptureFromAngle(
            center + playerViewOffset,
            new Vector3(10f, 0f, 0f),
            "scan_playerview"
        );

        await Task.Yield();
        return screenshots;
    }

    string CaptureFromAngle(Vector3 pos, Vector3 euler, string name) {
        var source = scanCamera != null ? scanCamera : (Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>());
        if (source == null) {
            Debug.LogWarning($"[SceneScanner] No source camera available for '{name}', returning empty image");
            return "";
        }

        var rt = new RenderTexture(captureWidth, captureHeight, 24);
        var cam = Instantiate(source);
        cam.name = $"{name}_camera";
        cam.transform.position = pos;
        cam.transform.eulerAngles = euler;
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
        tex.Apply();

        cam.targetTexture = null;
        RenderTexture.active = null;
        Destroy(cam.gameObject);
        Destroy(rt);

        byte[] bytes = tex.EncodeToPNG();
        string base64 = System.Convert.ToBase64String(bytes);
        Destroy(tex);

        return base64;
    }

    public SceneInventory GetSceneInventory() {
        var inventory = new SceneInventory {
            existingObjects = new List<ExistingObject>(),
            roomBounds = BuildRoomBounds()
        };

        var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        foreach (var renderer in renderers) {
            if (renderer == null || !renderer.enabled || renderer.transform == null)
                continue;

            var go = renderer.transform.root != null ? renderer.transform.root.gameObject : renderer.gameObject;
            if (go == null)
                continue;

            if (HasTag(go, "SpawnedObject") || go.name == "SpawnedObjects")
                continue;

            inventory.existingObjects.Add(new ExistingObject {
                name = go.name,
                position = ToPositionData(go.transform.position),
                bounds = ToPositionData(renderer.bounds.size)
            });
        }

        return inventory;
    }

    Vector3 ResolveRoomCenter() {
        if (TryGetRenderableBounds(out var bounds))
            return bounds.center;

        return Vector3.zero;
    }

    RoomBounds BuildRoomBounds() {
        if (TryGetRenderableBounds(out var bounds)) {
            return new RoomBounds {
                minX = bounds.min.x,
                maxX = bounds.max.x,
                minZ = bounds.min.z,
                maxZ = bounds.max.z,
                floorY = bounds.min.y,
                ceilingY = bounds.max.y
            };
        }

        return new RoomBounds {
            minX = -4f,
            maxX = 4f,
            minZ = -4f,
            maxZ = 4f,
            floorY = 0f,
            ceilingY = 3f
        };
    }

    bool TryGetRenderableBounds(out Bounds bounds) {
        bounds = default;
        var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        if (renderers == null || renderers.Length == 0)
            return false;

        bool found = false;
        for (int i = 0; i < renderers.Length; i++) {
            var renderer = renderers[i];
            if (renderer == null || !renderer.enabled)
                continue;

            var rootName = renderer.transform.root != null ? renderer.transform.root.name : renderer.name;
            if (rootName.Contains("XR") || rootName.Contains("Camera") || rootName == "VR Player")
                continue;

            if (!found) {
                bounds = renderer.bounds;
                found = true;
            } else {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    static PositionData ToPositionData(Vector3 value) {
        return new PositionData {
            x = value.x,
            y = value.y,
            z = value.z
        };
    }

    static bool HasTag(GameObject go, string tag) {
        if (go == null || string.IsNullOrWhiteSpace(tag))
            return false;

        try {
            return go.CompareTag(tag);
        } catch (UnityException) {
            // Missing tags should not break scene scanning.
            return false;
        }
    }
}

[System.Serializable]
public class SceneInventory {
    public List<ExistingObject> existingObjects;
    public RoomBounds roomBounds;
}

[System.Serializable]
public class ExistingObject {
    public string name;
    public PositionData position;
    public PositionData bounds;
}

[System.Serializable]
public class RoomBounds {
    public float minX;
    public float maxX;
    public float minZ;
    public float maxZ;
    public float floorY;
    public float ceilingY;
}