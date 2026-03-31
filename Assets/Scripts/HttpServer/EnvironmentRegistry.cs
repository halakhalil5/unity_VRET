using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "EnvironmentRegistry",
                 menuName  = "VRTherapy/EnvironmentRegistry")]
public class EnvironmentRegistry : ScriptableObject {

    [System.Serializable]
    public class EnvironmentEntry {
        public string environmentId;   // matches JSON string e.g. "indoor-room"
        public string sceneName;       // Unity scene name to load
        public GameObject prefab;      // OR a prefab if not using separate scenes
    }

    public List<EnvironmentEntry> environments;

    static EnvironmentRegistry _instance;
    public static EnvironmentRegistry Instance {
        get {
            if (_instance == null)
                _instance = Resources.Load<EnvironmentRegistry>("EnvironmentRegistry");
            return _instance;
        }
    }

    public EnvironmentEntry Find(string environmentId) {
        return environments.Find(e => e.environmentId == environmentId);
    }

    public bool IsValid(string environmentId) {
        return Find(environmentId) != null;
    }
}
