using System;
using System.Threading.Tasks;
using UnityEngine;

public class ObjaverseSmokeTest : MonoBehaviour {

    [SerializeField] bool runOnStart = false;
    [SerializeField] bool placeResultInScene = true;

    [Header("Test Asset")]
    [SerializeField] string testId = "smoke_objaverse_chair";
    [SerializeField] string category = "furniture";
    [SerializeField] string[] tags = { "chair", "wooden", "indoor" };
    [SerializeField] string preferredStyle = "realistic";
    [SerializeField] Vector3 position = new Vector3(0f, 0f, 2f);
    [SerializeField] Vector3 rotationEuler = Vector3.zero;
    [SerializeField] Vector3 scale = Vector3.one;

    async void Start() {
        if (!runOnStart) {
            return;
        }

        await RunSmokeTest();
    }

    [ContextMenu("Run Objaverse Smoke Test")]
    public void RunFromContextMenu() {
        _ = RunSmokeTest();
    }

    async Task RunSmokeTest() {
        if (ObjaverseResolver.Instance == null) {
            Debug.LogError("[ObjaverseSmokeTest] ObjaverseResolver.Instance is missing. Add ObjaverseResolver to a scene object.");
            return;
        }

        if (LocalAssetRegistry.Instance != null) {
            while (!LocalAssetRegistry.Instance.IsReady) {
                await Task.Yield();
            }
        }

        SceneAsset asset = BuildTestAsset();
        Debug.Log($"[ObjaverseSmokeTest] Starting test for '{asset.id}' with query parts: {asset.category} | {string.Join(", ", asset.tags)} | {asset.preferredStyle}");

        GameObject resolved = await ObjaverseResolver.Instance.TryResolve(asset);
        if (resolved == null) {
            Debug.LogError("[ObjaverseSmokeTest] Objaverse stage returned null. Verify Objaverse API base URL and endpoint response format.");
            return;
        }

        if (placeResultInScene) {
            if (ScenePlacer.Instance != null) {
                ScenePlacer.Instance.Place(resolved, asset);
            } else {
                resolved.transform.position = asset.position;
                resolved.transform.rotation = Quaternion.Euler(asset.rotation);
                resolved.transform.localScale = asset.scale;
            }
        }

        Debug.Log("[ObjaverseSmokeTest] Success: Objaverse resolver returned an object.");
    }

    SceneAsset BuildTestAsset() {
        return new SceneAsset {
            id = $"{testId}_{Guid.NewGuid():N}",
            category = category,
            tags = tags ?? Array.Empty<string>(),
            preferredStyle = preferredStyle,
            position = position,
            rotation = rotationEuler,
            scale = scale
        };
    }
}
