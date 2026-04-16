using UnityEngine;

public static class PlacementRulesResolver {

    public static PlacementType Resolve(PlacementRules rules) {
        if (rules == null)
            return PlacementType.Floor;

        if (TryParseType(rules.preferredSurface, out var preferred))
            return preferred;

        if (TryParseType(rules.type, out var fromType))
            return fromType;

        return PlacementType.Floor;
    }

    public static Vector3 ApplyHeightConstraint(Vector3 requestedPos, PlacementRules rules) {
        if (rules?.heightRange == null)
            return requestedPos;

        requestedPos.y = Mathf.Clamp(requestedPos.y, rules.heightRange.min, rules.heightRange.max);
        return requestedPos;
    }

    public static bool ValidateDistance(Vector3 requestedPos, PlacementRules rules) {
        if (rules == null)
            return true;

        var camera = Camera.main;
        if (camera == null)
            return true;

        var playerPos = camera.transform.position;
        float distance = Vector3.Distance(requestedPos, playerPos);

        if (rules.minDistanceFromPlayer.HasValue && distance < rules.minDistanceFromPlayer.Value)
            return false;

        if (rules.maxDistanceFromPlayer.HasValue && distance > rules.maxDistanceFromPlayer.Value)
            return false;

        return true;
    }

    public static Vector3 EnforceDistance(Vector3 requestedPos, PlacementRules rules) {
        if (rules == null)
            return requestedPos;

        var camera = Camera.main;
        if (camera == null)
            return requestedPos;

        var playerPos = camera.transform.position;
        var delta = requestedPos - playerPos;

        if (delta.sqrMagnitude < 0.0001f)
            delta = camera.transform.forward;

        var direction = delta.normalized;
        var distance = delta.magnitude;

        if (rules.minDistanceFromPlayer.HasValue && distance < rules.minDistanceFromPlayer.Value) {
            distance = rules.minDistanceFromPlayer.Value;
        }

        if (rules.maxDistanceFromPlayer.HasValue && distance > rules.maxDistanceFromPlayer.Value) {
            distance = rules.maxDistanceFromPlayer.Value;
        }

        return playerPos + direction * distance;
    }

    static bool TryParseType(string value, out PlacementType type) {
        type = PlacementType.Floor;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim().ToLowerInvariant();
        switch (normalized) {
            case "floor":
                type = PlacementType.Floor;
                return true;
            case "ground":
                type = PlacementType.Ground;
                return true;
            case "wall":
                type = PlacementType.Wall;
                return true;
            case "ceiling":
                type = PlacementType.Ceiling;
                return true;
            case "onobject":
            case "on_object":
                type = PlacementType.OnObject;
                return true;
            default:
                return false;
        }
    }
}
