using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Resolves incoming scene object types to prefabs from AssetManifest.
/// Uses a scoring system: exact match (score +100) or tag overlap.
/// Returns best match or null if no prefab found.
/// </summary>
public class LocalAssetResolver {

    const int MinFuzzyMatchScore = 20;

    public class ResolutionResult {
        public bool found;
        public AssetManifestLoader.AssetEntry asset;
        public string prefabPath;  // Resources-relative path without extension
        public string reason;      // Why resolution failed
    }

    /// <summary>
    /// Matches a scene object type to a prefab.
    /// Returns null and logs warning if no match.
    /// </summary>
    public static ResolutionResult Resolve(string objectId, string objectType, string[] objectTags = null) {
        if (string.IsNullOrEmpty(objectType)) {
            Debug.LogWarning($"[LocalResolver] Object '{objectId}' has empty type");
            return new ResolutionResult { found = false, reason = "object type is empty" };
        }

        string normalizedObjectType = NormalizeTypeAlias(objectType);

        // Ensure manifest is loaded
        if (!AssetManifestLoader.TryLoadManifest(out var manifestError)) {
            Debug.LogWarning($"[LocalResolver] {manifestError}");
            return new ResolutionResult { found = false, reason = manifestError };
        }

        var allAssets = AssetManifestLoader.GetAllAssets();
        if (allAssets.Length == 0) {
            return new ResolutionResult { found = false, reason = "manifest is empty" };
        }

        // 1. Try exact match on asset ID using objectId first, then objectType.
        var asset = allAssets.FirstOrDefault(a =>
            !string.IsNullOrEmpty(objectId) &&
            a.id.Equals(objectId, StringComparison.OrdinalIgnoreCase));

        if (asset == null) {
            asset = allAssets.FirstOrDefault(a =>
                a.id.Equals(normalizedObjectType, StringComparison.OrdinalIgnoreCase));
        }
        if (asset != null) {
            Debug.Log($"[LocalResolver] Matched '{objectType}' ({normalizedObjectType}) → {asset.id} (exact match)");
            return new ResolutionResult {
                found = true,
                asset = asset,
                prefabPath = asset.addressableKey
            };
        }

        // Ambiguous request terms should prefer Objaverse instead of fuzzy local substitutions.
        if (ShouldPreferObjaverse(normalizedObjectType)) {
            Debug.LogWarning($"[LocalResolver] Skipping fuzzy local match for '{objectType}' to avoid incorrect substitution");
            return new ResolutionResult {
                found = false,
                reason = $"prefer Objaverse for ambiguous type '{objectType}'"
            };
        }

        // 2. Score by tag overlap + category
        var scored = new List<(int score, AssetManifestLoader.AssetEntry asset)>();

        foreach (var candidate in allAssets) {
            int score = 0;

            // Check type match in tags
            if (candidate.tags != null) {
                foreach (var tag in candidate.tags) {
                    if (tag.Equals(normalizedObjectType, StringComparison.OrdinalIgnoreCase))
                        score += 20;
                    else if (tag.Contains(normalizedObjectType, StringComparison.OrdinalIgnoreCase))
                        score += 10;
                }
            }

            // Check object tags against asset tags
            if (objectTags != null && objectTags.Length > 0 && candidate.tags != null) {
                foreach (var objTag in objectTags) {
                    if (string.IsNullOrEmpty(objTag)) continue;
                    foreach (var assetTag in candidate.tags) {
                        if (objTag.Equals(assetTag, StringComparison.OrdinalIgnoreCase)) {
                            score += 5;
                        }
                    }
                }
            }

            // Category bonus
            if (!string.IsNullOrEmpty(candidate.category)) {
                if (candidate.category.Contains(normalizedObjectType, StringComparison.OrdinalIgnoreCase))
                    score += 15;
            }

            if (score > 0)
                scored.Add((score, candidate));
        }

        if (scored.Count > 0) {
            scored = scored.OrderByDescending(x => x.score).ToList();
            var winner = scored[0];

            if (winner.score < MinFuzzyMatchScore) {
                Debug.LogWarning($"[LocalResolver] Best fuzzy match for '{objectType}' was too weak (score {winner.score} < {MinFuzzyMatchScore}); trying Objaverse fallback");
                return new ResolutionResult {
                    found = false,
                    reason = $"weak local fuzzy match ({winner.score}) for '{objectType}'"
                };
            }

            Debug.Log($"[LocalResolver] Matched '{objectType}' ({normalizedObjectType}) → {winner.asset.id} (score {winner.score})");
            return new ResolutionResult {
                found = true,
                asset = winner.asset,
                prefabPath = winner.asset.addressableKey
            };
        }

        Debug.LogWarning($"[LocalResolver] No match for '{objectType}' — will use placeholder");
        return new ResolutionResult {
            found = false,
            reason = $"no asset matched type '{objectType}'"
        };
    }

    static string NormalizeTypeAlias(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        string normalized = value.Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        normalized = string.Join(" ", normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        switch (normalized) {
            case "spider":
            case "small spider":
            case "spider small":
            case "big spider":
            case "large spider":
            case "spider big":
            case "spider large":
                return "spider_myoldone";
            default:
                return value;
        }
    }

    static bool ShouldPreferObjaverse(string normalizedType) {
        if (string.IsNullOrWhiteSpace(normalizedType))
            return false;

        string t = normalizedType.Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        return t == "bag"
            || t == "emergency bag"
            || t == "backpack"
            || t.Contains("bag")
            || t.Contains("backpack")
            || t == "fan"
            || t == "vent"
            || t == "vent fan"
            || t == "ceiling fan"
            || t.Contains("fan")
            || t.Contains("vent")
            || t == "door"
            || t == "window"
            || t == "wall"
            || t.Contains("door")
            || t.Contains("window")
            || t.Contains("wall")
            || t == "emergency";
    }
}
