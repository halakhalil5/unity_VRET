using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class EnvironmentRegistryAutoSetup {

    const string RegistryPath = "Assets/Resources/EnvironmentRegistry.asset";
    const string LivingRoomScenePath = "Assets/BaseEnvironments/LivingRoom/LivingRoom.unity";
    const string LivingRoomSceneName = "LivingRoom";

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
    }
}
