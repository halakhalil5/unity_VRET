using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class EnvironmentRegistryAutoSetup {

    const string RegistryPath = "Assets/Resources/EnvironmentRegistry.asset";

    struct EnvironmentMapping {
        public readonly string environmentId;
        public readonly string sceneName;
        public readonly string scenePath;

        public EnvironmentMapping(string environmentId, string sceneName, string scenePath) {
            this.environmentId = environmentId;
            this.sceneName = sceneName;
            this.scenePath = scenePath;
        }
    }

    static readonly EnvironmentMapping[] EnvironmentMappings = {
        new EnvironmentMapping("airplane", "Airplane", "Assets/BaseEnvironments/Airplane/Airplane.unity"),
        new EnvironmentMapping("airport", "Airport", "Assets/BaseEnvironments/Airport/Airport.unity"),
        new EnvironmentMapping("building", "Building", "Assets/BaseEnvironments/Building/Building.unity"),
        new EnvironmentMapping("cliff", "cliff", "Assets/BaseEnvironments/Cliff/cliff.unity"),
        new EnvironmentMapping("elevator", "Elevator", "Assets/BaseEnvironments/Elevator/Elevator.unity"),
        new EnvironmentMapping("house", "room", "Assets/BaseEnvironments/House/room.unity"),
        new EnvironmentMapping("living_room", "LivingRoom", "Assets/BaseEnvironments/LivingRoom/LivingRoom.unity"),
        new EnvironmentMapping("picture_room", "PictureRoom", "Assets/BaseEnvironments/PictureRoom/PictureRoom.unity"),
    };

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

        if (registry.environments == null)
            registry.environments = new List<EnvironmentRegistry.EnvironmentEntry>();

        var registryChanged = false;

        foreach (var mapping in EnvironmentMappings) {
            var scene = AssetDatabase.LoadMainAssetAtPath(mapping.scenePath);
            if (scene == null) {
                Debug.LogWarning($"[SceneBuild] Scene not found at {mapping.scenePath}");
                continue;
            }

            var entry = registry.environments.Find(e => e.environmentId == mapping.environmentId);
            if (entry == null) {
                entry = new EnvironmentRegistry.EnvironmentEntry {
                    environmentId = mapping.environmentId,
                    sceneName = mapping.sceneName,
                    prefab = null
                };
                registry.environments.Add(entry);
                registryChanged = true;
                Debug.Log($"[SceneBuild] Added {mapping.environmentId} mapping to EnvironmentRegistry.asset");
            } else if (entry.sceneName != mapping.sceneName || entry.prefab != null) {
                entry.sceneName = mapping.sceneName;
                entry.prefab = null;
                registryChanged = true;
                Debug.Log($"[SceneBuild] Enforced {mapping.environmentId} mapping in EnvironmentRegistry.asset");
            }
        }

        if (registryChanged) {
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
        }
    }
}
