using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class EnvironmentRegistryAutoSetup {

    const string RegistryPath = "Assets/Resources/EnvironmentRegistry.asset";
    const string LivingRoomScenePath = "Assets/BaseEnvironments/LivingRoom/LivingRoom.unity";
    const string LivingRoomSceneName = "LivingRoom";
    const string CaveScenePath = "Assets/BaseEnvironments/Cave/Cave.unity";
    const string CaveSceneName = "Cave";

    [InitializeOnLoadMethod]
    static void EnsureRegistry() {
        EditorApplication.delayCall += EnsureRegistryDelayed;
    }

    static void EnsureRegistryDelayed() {
        var registry = AssetDatabase.LoadAssetAtPath<EnvironmentRegistry>(RegistryPath);
        if (registry == null) {
            registry = ScriptableObject.CreateInstance<EnvironmentRegistry>();
            registry.environments = new List<EnvironmentRegistry.EnvironmentEntry>();
            AssetDatabase.CreateAsset(registry, RegistryPath);
            AssetDatabase.SaveAssets();
        }

        var livingRoomScene = AssetDatabase.LoadMainAssetAtPath(LivingRoomScenePath);
        if (livingRoomScene == null) {
            Debug.LogWarning("[SceneBuild] Living room scene not found at Assets/BaseEnvironments/LivingRoom/LivingRoom.unity");
            return;
        }

        var caveScene = AssetDatabase.LoadMainAssetAtPath(CaveScenePath);
        if (caveScene == null) {
            Debug.LogWarning("[SceneBuild] Cave scene not found at Assets/BaseEnvironments/Cave/Cave.unity");
            return;
        }

        if (registry.environments == null)
            registry.environments = new List<EnvironmentRegistry.EnvironmentEntry>();

        var entry = registry.environments.Find(e => e.environmentId == "living_room");
        if (entry == null) {
            entry = new EnvironmentRegistry.EnvironmentEntry {
                environmentId = "living_room",
                sceneName = LivingRoomSceneName,
                prefab = null
            };
            registry.environments.Add(entry);
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuild] Added living_room mapping to EnvironmentRegistry.asset");
        } else {
            // Always enforce scene mapping to keep therapist flow scene-based.
            entry.sceneName = LivingRoomSceneName;
            entry.prefab = null;
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuild] Enforced living_room mapping in EnvironmentRegistry.asset");
        }

        var caveEntry = registry.environments.Find(e => e.environmentId == "cave");
        if (caveEntry == null) {
            caveEntry = new EnvironmentRegistry.EnvironmentEntry {
                environmentId = "cave",
                sceneName = CaveSceneName,
                prefab = null
            };
            registry.environments.Add(caveEntry);
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuild] Added cave mapping to EnvironmentRegistry.asset");
        } else {
            caveEntry.sceneName = CaveSceneName;
            caveEntry.prefab = null;
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuild] Enforced cave mapping in EnvironmentRegistry.asset");
        }
    }
}
