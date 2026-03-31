using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

public class SceneBuildController : MonoBehaviour {

    public static SceneBuildController Instance { get; private set; }

    [SerializeField] EnvironmentRegistry registry;

    BuildRequest pendingTherapistRequest;

    public bool HasPendingTherapistSelection => pendingTherapistRequest != null;
    public string PendingEnvironment => pendingTherapistRequest?.scene?.environment;

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        if (registry == null)
            registry = EnvironmentRegistry.Instance;
    }

    public BuildResponse BuildScene(BuildRequest request) {
        if (IsRelaxationSceneActive()) {
            pendingTherapistRequest = request;
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

        var request = pendingTherapistRequest;
        pendingTherapistRequest = null;
        return BuildSceneNow(request);
    }

    BuildResponse BuildSceneNow(BuildRequest request) {
        string env = request.scene.environment;

        Debug.Log($"[SceneBuild] Building environment: {env}");

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

        // Handle camera
        if (request.scene.camera != null)
            SetupCamera(request.scene.camera);

        // Spawn objects
        if (request.scene.sceneObjects != null && request.scene.sceneObjects.Length > 0)
            ObjectSpawner.Instance.SpawnObjects(request.scene.sceneObjects);
        // Capture preview
        string previewUrl = "";
        if (request.requestPreview)
            previewUrl = PreviewCaptureService.Instance.Capture();

        Debug.Log($"[SceneBuild] ✓ Scene built: {env}");

        return new BuildResponse {
            status     = "ok",
            previewURL = previewUrl
        };
    }

    static bool IsRelaxationSceneActive() {
        var active = SceneManager.GetActiveScene();
        return active.IsValid() && active.name == "RelaxationScene";
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
            var instance = Instantiate((Object)entry.prefab);
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