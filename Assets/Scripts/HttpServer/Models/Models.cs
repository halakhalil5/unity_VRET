using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ── Request Models ────────────────────────────────

[Serializable]
public class BuildRequest {
    public SceneData scene;
    public float?    intensity;        // 0.0 to 1.0 exposure intensity
    public string    timestamp;
    public bool      requestPreview;
    public string    placementText;

    // Backward-compatible aliases accepted by incoming JSON payloads where fields are top-level.
    [JsonProperty("environment")]
    string EnvironmentAlias {
        set {
            EnsureScene();
            scene.environment = value;
        }
    }

    [JsonProperty("objects")]
    SceneObjectData[] ObjectsAlias {
        set {
            EnsureScene();
            scene.sceneObjects = value;
        }
    }

    [JsonProperty("triggers")]
    TriggerData[] TriggersAlias {
        set {
            EnsureScene();
            scene.sceneTriggers = value;
        }
    }

    [JsonProperty("picturePrompts")]
    JToken PicturePromptsAlias {
        set {
            EnsureScene();
            scene.picturePrompts = PromptPayloadParser.ExtractPrompts(value);
        }
    }

    [JsonProperty("prompts")]
    JToken PromptsAlias {
        set {
            EnsureScene();
            scene.picturePrompts = PromptPayloadParser.ExtractPrompts(value);
        }
    }

    [JsonProperty("pictures")]
    JToken PicturesAlias {
        set {
            EnsureScene();
            scene.picturePrompts = PromptPayloadParser.ExtractPrompts(value);
        }
    }

    [JsonProperty("picture1")]
    string Picture1Alias {
        set {
            EnsureScene();
            scene.SetPicturePrompt(0, value);
        }
    }

    [JsonProperty("picture2")]
    string Picture2Alias {
        set {
            EnsureScene();
            scene.SetPicturePrompt(1, value);
        }
    }

    [JsonProperty("picture3")]
    string Picture3Alias {
        set {
            EnsureScene();
            scene.SetPicturePrompt(2, value);
        }
    }

    [JsonProperty("picture4")]
    string Picture4Alias {
        set {
            EnsureScene();
            scene.SetPicturePrompt(3, value);
        }
    }

    [JsonProperty("picture5")]
    string Picture5Alias {
        set {
            EnsureScene();
            scene.SetPicturePrompt(4, value);
        }
    }

    [JsonProperty("picture6")]
    string Picture6Alias {
        set {
            EnsureScene();
            scene.SetPicturePrompt(5, value);
        }
    }

    [JsonProperty("picture7")]
    string Picture7Alias {
        set {
            EnsureScene();
            scene.SetPicturePrompt(6, value);
        }
    }

    [JsonProperty("prompt")]
    string PromptAlias {
        set => placementText = value;
    }

    [JsonProperty("text")]
    string TextAlias {
        set => placementText = value;
    }

    [JsonProperty("instructions")]
    string InstructionsAlias {
        set => placementText = value;
    }

    [JsonProperty("description")]
    string DescriptionAlias {
        set => placementText = value;
    }

    void EnsureScene() {
        if (scene == null)
            scene = new SceneData();
    }
}

[Serializable]
public class SceneData {
    public string            environment;
    public SceneObjectData[] sceneObjects;
    public TriggerData[]     sceneTriggers;
    public CameraData        camera;
    public string            placementText;
    [JsonIgnore]
    public string[]          picturePrompts;

    // Backward-compatible aliases accepted by incoming JSON payloads.
    [JsonProperty("objects")]
    SceneObjectData[] ObjectsAlias {
        set => sceneObjects = value;
    }

    [JsonProperty("triggers")]
    TriggerData[] TriggersAlias {
        set => sceneTriggers = value;
    }

    [JsonProperty("picturePrompts")]
    JToken PicturePromptsPayload {
        set => picturePrompts = PromptPayloadParser.ExtractPrompts(value);
    }

    [JsonProperty("prompts")]
    JToken PromptsAlias {
        set => picturePrompts = PromptPayloadParser.ExtractPrompts(value);
    }

    [JsonProperty("pictures")]
    JToken PicturesAlias {
        set => picturePrompts = PromptPayloadParser.ExtractPrompts(value);
    }

    [JsonProperty("picture1")]
    string Picture1Alias {
        set => SetPicturePrompt(0, value);
    }

    [JsonProperty("picture2")]
    string Picture2Alias {
        set => SetPicturePrompt(1, value);
    }

    [JsonProperty("picture3")]
    string Picture3Alias {
        set => SetPicturePrompt(2, value);
    }

    [JsonProperty("picture4")]
    string Picture4Alias {
        set => SetPicturePrompt(3, value);
    }

    [JsonProperty("picture5")]
    string Picture5Alias {
        set => SetPicturePrompt(4, value);
    }

    [JsonProperty("picture6")]
    string Picture6Alias {
        set => SetPicturePrompt(5, value);
    }

    [JsonProperty("picture7")]
    string Picture7Alias {
        set => SetPicturePrompt(6, value);
    }

    [JsonProperty("prompt")]
    string PromptAlias {
        set => placementText = value;
    }

    [JsonProperty("text")]
    string TextAlias {
        set => placementText = value;
    }

    [JsonProperty("instructions")]
    string InstructionsAlias {
        set => placementText = value;
    }

    [JsonProperty("description")]
    string DescriptionAlias {
        set => placementText = value;
    }

    public void SetPicturePrompt(int index, string value) {
        if (index < 0)
            return;

        if (picturePrompts == null || picturePrompts.Length < 7)
            picturePrompts = new string[7];

        picturePrompts[index] = value;
    }
}

static class PromptPayloadParser {
    public static string[] ExtractPrompts(JToken token) {
        if (token == null || token.Type == JTokenType.Null)
            return null;

        if (token.Type == JTokenType.String)
            return new[] { token.Value<string>() };

        if (token.Type != JTokenType.Array)
            return null;

        var prompts = new System.Collections.Generic.List<string>();
        foreach (var item in token) {
            if (item == null || item.Type == JTokenType.Null)
                continue;

            if (item.Type == JTokenType.String) {
                string text = item.Value<string>();
                if (!string.IsNullOrWhiteSpace(text))
                    prompts.Add(text);
                continue;
            }

            if (item.Type == JTokenType.Object) {
                string text = item["prompt"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(text))
                    prompts.Add(text);
            }
        }

        return prompts.Count > 0 ? prompts.ToArray() : null;
    }
}

[Serializable]
public class SceneObjectData {
    public string       id;
    public string       type;
    public PositionData position;
    public string       category;  // For Objaverse search
    public string[]     tags;      // For Objaverse search
}

[Serializable]
public class TriggerData {
    public string       id;
    public string       type;
    public PositionData position;
    public int?         count;
    public SpawnAreaData spawnArea;
    public ScaleRangeData scale;
    public string       behavior;
}

[Serializable]
public class SpawnAreaData {
    public float minX;
    public float maxX;
    public float minZ;
    public float maxZ;
}

[Serializable]
public class ScaleRangeData {
    public float min;
    public float max;
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
    public string message;
}

[Serializable]
public class StatusResponse {
    public string   status;
    public string   version;
    public string[] capabilities;
}
