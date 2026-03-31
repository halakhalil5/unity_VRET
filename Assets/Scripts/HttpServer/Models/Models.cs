using System;
using Newtonsoft.Json;

// ── Request Models ────────────────────────────────

[Serializable]
public class BuildRequest {
    public SceneData scene;
    public string    timestamp;
    public bool      requestPreview;
}

[Serializable]
public class SceneData {
    public string            environment;
    public SceneObjectData[] sceneObjects;
    public TriggerData[]     sceneTriggers;
    public CameraData        camera;

    // Backward-compatible aliases accepted by incoming JSON payloads.
    [JsonProperty("objects")]
    SceneObjectData[] ObjectsAlias {
        set => sceneObjects = value;
    }

    [JsonProperty("triggers")]
    TriggerData[] TriggersAlias {
        set => sceneTriggers = value;
    }
}

[Serializable]
public class SceneObjectData {
    public string       id;
    public string       type;
    public PositionData position;
}

[Serializable]
public class TriggerData {
    public string       id;
    public string       type;
    public PositionData position;
}

[Serializable]
public class CameraData {
    public PositionData startPosition;
    public bool         allowMovement;
}

[Serializable]
public class PositionData {
    public float x;
    public float y;
    public float z;
}

// ── Response Models ───────────────────────────────

[Serializable]
public class BuildResponse {
    public string status;
    public string previewURL;
    public string error;
}

[Serializable]
public class StatusResponse {
    public string   status;
    public string   version;
    public string[] capabilities;
}
