using System;

[Serializable]
public class PlacementRules {
    public string type;
    public string[] allowedSurfaces;
    public string preferredSurface;
    public float? minDistanceFromPlayer;
    public float? maxDistanceFromPlayer;
    public HeightRange heightRange;
    public bool mustBeInCorner;
    public bool mustFacePlayer;
    public float? minDistanceFromWall;
    public string clusterWith;
    public int? maxClusterSize;
}

[Serializable]
public class HeightRange {
    public float min;
    public float max;
}

public enum PlacementType {
    Floor,
    Wall,
    Ceiling,
    Ground,
    OnObject
}
