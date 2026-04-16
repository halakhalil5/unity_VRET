$ErrorActionPreference = 'Stop'

$root = 'Assets'
$manifestPath = Join-Path $root 'StreamingAssets/AssetManifest.json'
$preloadedDir = Join-Path $root 'PreloadedAssets'
$baseEnvRoot = Join-Path $root 'BaseEnvironments'

function Normalize-Key([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return '' }
    return ($s.ToLowerInvariant() -replace '[^a-z0-9]', '')
}

function Safe-Float([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return 0.0 }
    return [double]::Parse($v, [Globalization.CultureInfo]::InvariantCulture)
}

function Parse-VectorBlock([string]$block, [string]$header) {
    $inlineRx = [regex]::new([regex]::Escape($header) + ':\s*\{\s*x:\s*([^,\}\r\n]+),\s*y:\s*([^,\}\r\n]+),\s*z:\s*([^\}\r\n]+)\s*\}', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $inline = $inlineRx.Match($block)
    if ($inline.Success) {
        return @{
            x = (Safe-Float $inline.Groups[1].Value)
            y = (Safe-Float $inline.Groups[2].Value)
            z = (Safe-Float $inline.Groups[3].Value)
        }
    }

    $multilineRx = [regex]::new([regex]::Escape($header) + ':\s*\r?\n\s*x:\s*([^\r\n]+)\r?\n\s*y:\s*([^\r\n]+)\r?\n\s*z:\s*([^\r\n]+)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $multiline = $multilineRx.Match($block)
    if ($multiline.Success) {
        return @{
            x = (Safe-Float $multiline.Groups[1].Value)
            y = (Safe-Float $multiline.Groups[2].Value)
            z = (Safe-Float $multiline.Groups[3].Value)
        }
    }

    return @{ x = 0.0; y = 0.0; z = 0.0 }
}

function Get-PathForTransform($tid, $trByIdRef, $goByIdRef) {
    if (-not $trByIdRef.ContainsKey($tid)) { return '' }

    $parts = New-Object System.Collections.Generic.List[string]
    $cur = $tid
    $safety = 0

    while ($trByIdRef.ContainsKey($cur) -and $safety -lt 2048) {
        $node = $trByIdRef[$cur]
        $goId = $node.goId
        $name = if ($goByIdRef.ContainsKey($goId)) { $goByIdRef[$goId].name } else { "GO_$goId" }
        $parts.Insert(0, $name)

        if ([string]::IsNullOrWhiteSpace($node.parentTrId) -or $node.parentTrId -eq '0') {
            break
        }

        $cur = $node.parentTrId
        $safety++
    }

    return ($parts -join '/')
}

function Resolve-ActualAssetPath([string]$addressableKey, [string]$fallbackName) {
    $leaf = Split-Path -Leaf $addressableKey
    if ([string]::IsNullOrWhiteSpace($leaf)) {
        $leaf = $fallbackName
    }

    $candidates = Get-ChildItem -Path 'Assets' -Recurse -File | Where-Object {
        $_.Extension -notin '.meta', '.json' -and $_.BaseName -ieq $leaf
    }

    if (-not $candidates -or $candidates.Count -eq 0) {
        return ''
    }

    function To-UnityRelativePath([string]$fullPath) {
        if ([string]::IsNullOrWhiteSpace($fullPath)) { return '' }

        $normalized = $fullPath.Replace('/', '\\')
        $assetsMarker = '\Assets\'
        $idx = $normalized.IndexOf($assetsMarker, [System.StringComparison]::OrdinalIgnoreCase)
        if ($idx -ge 0) {
            return (('Assets' + $normalized.Substring($idx + $assetsMarker.Length - 1)) -replace '\\', '/')
        }

        return ($fullPath -replace '\\', '/')
    }

    $pref = $candidates | Where-Object { $_.Extension -ieq '.prefab' } | Select-Object -First 1
    if ($pref) {
        return To-UnityRelativePath $pref.FullName
    }

    $fbx = $candidates | Where-Object { $_.Extension -ieq '.fbx' } | Select-Object -First 1
    if ($fbx) {
        return To-UnityRelativePath $fbx.FullName
    }

    return To-UnityRelativePath (($candidates | Select-Object -First 1).FullName)
}

$manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
$entries = @($manifest.assets)
$entryByNorm = @{}

foreach ($e in $entries) {
    $keys = @(
        (Normalize-Key $e.id),
        (Normalize-Key $e.addressableKey),
        (Normalize-Key (Split-Path -Leaf $e.addressableKey))
    ) | Where-Object { $_ -ne '' } | Select-Object -Unique

    foreach ($k in $keys) {
        if (-not $entryByNorm.ContainsKey($k)) { $entryByNorm[$k] = @() }
        $entryByNorm[$k] += $e
    }
}

$manualMap = @{
    'elderfemale' = 'elder_female_a'
    'littleboy' = 'little_boy_b'
    'policefemale' = 'police_female_a'
    'workermaleconstructorb' = 'worker_constructor_male'
    'dpropplantpot' = 'plant_pot'
    'toolhandsaw' = 'hand_saw'
}

$preloadedJsonFiles = Get-ChildItem -Path $preloadedDir -File -Filter '*.json'
foreach ($f in $preloadedJsonFiles) {
    $base = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    $norm = Normalize-Key $base
    $targetNorm = if ($manualMap.ContainsKey($norm)) { Normalize-Key $manualMap[$norm] } else { $norm }

    $candidates = @()
    if ($entryByNorm.ContainsKey($targetNorm)) {
        $candidates += $entryByNorm[$targetNorm]
    }

    foreach ($e in $entries) {
        $idNorm = Normalize-Key $e.id
        $addrNorm = Normalize-Key $e.addressableKey
        $leafNorm = Normalize-Key (Split-Path -Leaf $e.addressableKey)

        if ($idNorm -like "*$targetNorm*" -or $addrNorm -like "*$targetNorm*" -or $leafNorm -like "*$targetNorm*") {
            $candidates += $e
        }
    }

    $picked = $candidates | Select-Object -Unique | Select-Object -First 1

    $category = if ($picked) { $picked.category } else { 'unknown' }
    $tags = if ($picked -and $picked.tags) { @($picked.tags) } else { @($base.ToLowerInvariant()) }
    $style = if ($picked) { $picked.style } else { 'unknown' }
    $bounds = if ($picked) { $picked.bounds } else { @{ x = 1.0; y = 1.0; z = 1.0 } }
    $addressableKey = if ($picked) { $picked.addressableKey } else { "PreloadedAssets/$base" }
    $assetId = if ($picked) { $picked.id } else { $base.ToLowerInvariant() }

    $actualObjectPath = Resolve-ActualAssetPath $addressableKey $base
    $description = "Local preloaded $category asset '$base' from $addressableKey (asset path: $actualObjectPath). Use this when therapist description requests matching terms: " + (($tags -join ', ')) + '. Prefer this local asset before Objaverse.'

    $obj = [ordered]@{
        id = $assetId
        actualObjectPath = $actualObjectPath
        sourceType = 'preloaded_local'
        addressableKey = $addressableKey
        category = $category
        tags = $tags
        style = $style
        bounds = [ordered]@{
            x = [double]$bounds.x
            y = [double]$bounds.y
            z = [double]$bounds.z
        }
        description = $description
        llmHints = [ordered]@{
            synonyms = $tags
            excludeIfAlreadyInBaseScene = $true
            fallbackOrder = @('local', 'objaverse', 'skip')
        }
    }

    ($obj | ConvertTo-Json -Depth 16) | Set-Content -Path $f.FullName -Encoding UTF8
}

$configFiles = Get-ChildItem -Path $baseEnvRoot -Recurse -File -Filter '*.json' | Where-Object { $_.Name -notlike '*.meta' }
foreach ($cfg in $configFiles) {
    $dir = Split-Path -Parent $cfg.FullName
    $scene = Get-ChildItem -Path $dir -File -Filter '*.unity' | Select-Object -First 1
    if (-not $scene) { continue }

    $sceneText = Get-Content -Raw -Path $scene.FullName

    $goById = @{}
    $goMatches = [regex]::Matches($sceneText, '--- !u!1 &(\d+)\r?\nGameObject:\r?\n(.*?)(?=\r?\n--- !u!|\z)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    foreach ($m in $goMatches) {
        $id = $m.Groups[1].Value
        $block = $m.Groups[2].Value
        $nameMatch = [regex]::Match($block, 'm_Name:\s*(.*)')
        $name = if ($nameMatch.Success) { $nameMatch.Groups[1].Value.Trim() } else { "GameObject_$id" }
        $goById[$id] = @{ name = $name }
    }

    $trById = @{}
    $trMatches = [regex]::Matches($sceneText, '--- !u!4 &(\d+)\r?\nTransform:\r?\n(.*?)(?=\r?\n--- !u!|\z)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    foreach ($m in $trMatches) {
        $trId = $m.Groups[1].Value
        $block = $m.Groups[2].Value

        $goMatch = [regex]::Match($block, 'm_GameObject:\s*\{fileID:\s*(\d+)\}')
        if (-not $goMatch.Success) { continue }
        $goId = $goMatch.Groups[1].Value

        $parentMatch = [regex]::Match($block, 'm_Father:\s*\{fileID:\s*(-?\d+)\}')
        $parentTr = if ($parentMatch.Success) { $parentMatch.Groups[1].Value } else { '0' }

        $localPos = Parse-VectorBlock $block 'm_LocalPosition'
        $localScale = Parse-VectorBlock $block 'm_LocalScale'

        $trById[$trId] = @{
            trId = $trId
            goId = $goId
            parentTrId = $parentTr
            localPos = $localPos
            localScale = $localScale
        }
    }

    $objects = New-Object System.Collections.Generic.List[object]
    foreach ($trId in ($trById.Keys | Sort-Object { [long]$_ })) {
        $tr = $trById[$trId]
        $goId = $tr.goId
        $name = if ($goById.ContainsKey($goId)) { $goById[$goId].name } else { "GameObject_$goId" }
        $scenePath = Get-PathForTransform $trId $trById $goById

        $id = (($scenePath -replace '[^A-Za-z0-9]+', '_').Trim('_')).ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($id)) { $id = "go_$goId" }

        $objects.Add([ordered]@{
            id = $id
            name = $name
            type = 'scene_object'
            role = 'base_scene_existing'
            scenePath = $scenePath
            transformFileId = [long]$trId
            position = [ordered]@{
                x = [double]$tr.localPos.x
                y = [double]$tr.localPos.y
                z = [double]$tr.localPos.z
            }
            scale = [ordered]@{
                x = [double]$tr.localScale.x
                y = [double]$tr.localScale.y
                z = [double]$tr.localScale.z
            }
        })
    }

    $cfgObj = Get-Content -Raw -Path $cfg.FullName | ConvertFrom-Json
    $cfgObj | Add-Member -NotePropertyName objects -NotePropertyValue $objects -Force
    ($cfgObj | ConvertTo-Json -Depth 100) | Set-Content -Path $cfg.FullName -Encoding UTF8
}

Write-Output 'Updated preloaded descriptors and base environment objects from scene transforms.'
