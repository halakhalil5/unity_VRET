using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class GLBLoader : MonoBehaviour {

    public static GLBLoader Instance { get; private set; }

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public async Task<GameObject> LoadFromUrl(string url) {
        try {
            var req = UnityWebRequest.Get(url);
            var op = req.SendWebRequest();
            while (!op.isDone) {
                await Task.Yield();
            }

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success) {
#else
            if (req.isNetworkError || req.isHttpError) {
#endif
                Debug.LogWarning($"[GLBLoader] Failed to load: {url}");
                return null;
            }

            var loaded = await TryLoadWithGltfFast(url);
            if (loaded != null) {
                return loaded;
            }

            Debug.LogWarning("[GLBLoader] GLTFast runtime import unavailable. Returning a fallback placeholder object.");
            return CreateFallbackObject();
        } catch (System.Exception e) {
            Debug.LogWarning($"[GLBLoader] Exception: {e.Message}");
            return null;
        }
    }

    async Task<GameObject> TryLoadWithGltfFast(string url) {
        Type gltfImportType = FindType("GLTFast.GltfImport");
        if (gltfImportType == null) {
            return null;
        }

        if (!TryCreateGltfImport(gltfImportType, out var gltf)) {
            Debug.LogWarning("[GLBLoader] GLTFast found, but no compatible constructor could be used to create GltfImport.");
            return null;
        }

        MethodInfo loadMethod = FindMethod(gltfImportType, "Load", typeof(string));
        if (loadMethod == null) {
            Debug.LogWarning("[GLBLoader] GLTFast found, but no compatible Load(string) method was found.");
            return null;
        }

        bool loadOk = await InvokeBoolTaskMethod(gltf, loadMethod, url);
        if (!loadOk) {
            Debug.LogWarning($"[GLBLoader] GLTFast failed to parse GLB: {url}");
            return null;
        }

        var container = new GameObject("GLB_Container");
        MethodInfo instantiateMethod = FindMethod(gltfImportType, "InstantiateMainSceneAsync", typeof(Transform));
        if (instantiateMethod == null) {
            Debug.LogWarning("[GLBLoader] GLTFast found, but no compatible InstantiateMainSceneAsync(Transform) method was found.");
            Destroy(container);
            return null;
        }

        bool instantiateOk = await InvokeBoolTaskMethod(gltf, instantiateMethod, container.transform);
        if (!instantiateOk) {
            Debug.LogWarning("[GLBLoader] GLTFast could not instantiate scene.");
            Destroy(container);
            return null;
        }

        return container;
    }

    static bool TryCreateGltfImport(Type gltfImportType, out object gltfImport) {
        gltfImport = null;

        try {
            gltfImport = Activator.CreateInstance(gltfImportType);
            if (gltfImport != null) {
                return true;
            }
        } catch {
            // Fall through and probe constructors below.
        }

        var constructors = gltfImportType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(c => c.GetParameters().Length)
            .ToArray();

        for (int i = 0; i < constructors.Length; i++) {
            var ctor = constructors[i];
            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];
            bool canInvoke = true;

            for (int p = 0; p < parameters.Length; p++) {
                var parameter = parameters[p];
                if (parameter.HasDefaultValue) {
                    args[p] = parameter.DefaultValue;
                    continue;
                }

                if (parameter.ParameterType.IsValueType) {
                    args[p] = Activator.CreateInstance(parameter.ParameterType);
                    continue;
                }

                // Reference types/interfaces can be passed as null for optional runtime services.
                args[p] = null;
            }

            if (!canInvoke) {
                continue;
            }

            try {
                gltfImport = ctor.Invoke(args);
                if (gltfImport != null) {
                    return true;
                }
            } catch {
                // Try next constructor signature.
            }
        }

        return false;
    }

    static Type FindType(string fullName) {
        var type = Type.GetType(fullName);
        if (type != null) {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            type = assembly.GetType(fullName);
            if (type != null) {
                return type;
            }
        }

        return null;
    }

    static MethodInfo FindMethod(Type type, string name, Type firstParameterType) {
        return type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => {
                if (m.Name != name) {
                    return false;
                }

                var p = m.GetParameters();
                return p.Length > 0 && p[0].ParameterType == firstParameterType;
            });
    }

    static async Task<bool> InvokeBoolTaskMethod(object target, MethodInfo method, object firstArg) {
        object[] args = BuildInvocationArgs(method, firstArg);
        object invocationResult = method.Invoke(target, args);

        if (invocationResult is Task task) {
            await task;

            var taskType = task.GetType();
            if (taskType.IsGenericType) {
                var resultProp = taskType.GetProperty("Result");
                if (resultProp != null && resultProp.PropertyType == typeof(bool)) {
                    return (bool)resultProp.GetValue(task);
                }
            }

            return true;
        }

        return invocationResult is bool b && b;
    }

    static object[] BuildInvocationArgs(MethodInfo method, object firstArg) {
        var parameters = method.GetParameters();
        var args = new object[parameters.Length];

        if (parameters.Length > 0) {
            args[0] = firstArg;
        }

        for (int i = 1; i < parameters.Length; i++) {
            if (parameters[i].HasDefaultValue) {
                args[i] = parameters[i].DefaultValue;
            } else if (parameters[i].ParameterType.IsValueType) {
                args[i] = Activator.CreateInstance(parameters[i].ParameterType);
            } else {
                args[i] = null;
            }
        }

        return args;
    }

    static GameObject CreateFallbackObject() {
        var container = new GameObject("GLB_Container_Fallback");
        var mesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
        mesh.transform.SetParent(container.transform, false);
        return container;
    }
}