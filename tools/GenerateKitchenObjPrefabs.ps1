# Generates missing KitchenObj logic prefabs, wires KitchenObjSo, updates DefaultNetworkPrefabs.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $root "Assets"))) { $root = $PSScriptRoot }
$prefabDir = Join-Path $root "Assets\Resources\Prefab\KitchenObj"
$soDir = Join-Path $root "Assets\Resources\So\KitchenObj"
$netList = Join-Path $root "Assets\DefaultNetworkPrefabs.asset"

# visualName -> visual prefab guid (KitchenObjectsVisuals)
$visuals = @{
    "Tomato_Visual"            = "81e9fe7b3cd084242a088e945f531f38"
    "TomatoSlices_Visual"      = "fb403d1efda6baf4190c507841617408"
    "Bread_Visual"             = "bc49891f572f1164e9938f845ec37ab0"
    "Cabbage_Visual"           = "259702638197d9246bfc6f2b0fe88c09"
    "CabbageSliced_Visual"     = "44b7e90f95929c04c8394768d8bd1d14"
    "CheeseBlock_Visual"       = "44325c67017631342b5f02792113e57b"
    "CheeseSlices_Visual"      = "17cb28a1a37fd7f44ae6e1ad2aa9da78"
    "MeatPattyUncooked_Visual" = "21b2fcdae015b8c4c83ff4b68b31b06f"
    "MeatPattyCooked_Visual"   = "e879ff1414d1dd64c9ad7b04bffdfaef"
    "MeatPattyBurned_Visual"   = "0df961a7e3870b44f9bbce7bdf930613"
    "Plate_Visual"             = "c0c9afd619b40914a8f625c0535e2284"
}

# name, enumValue, visualKey, soAssetName (optional, default=name)
$items = @(
    @{ Name = "ChickenRaw"; Enum = 11; Visual = "MeatPattyUncooked_Visual" }
    @{ Name = "ChickenCooked"; Enum = 12; Visual = "MeatPattyCooked_Visual" }
    @{ Name = "ChickenBurned"; Enum = 13; Visual = "MeatPattyBurned_Visual" }
    @{ Name = "Fish"; Enum = 14; Visual = "Cabbage_Visual" }
    @{ Name = "FishSlices"; Enum = 15; Visual = "TomatoSlices_Visual" }
    @{ Name = "Shrimp"; Enum = 16; Visual = "CheeseBlock_Visual" }
    @{ Name = "ShrimpSlices"; Enum = 17; Visual = "CheeseSlices_Visual" }
    @{ Name = "RiceBall"; Enum = 18; Visual = "Bread_Visual" }
    @{ Name = "Nori"; Enum = 19; Visual = "CheeseSlices_Visual" }
    @{ Name = "Cream"; Enum = 20; Visual = "CheeseBlock_Visual" }
    @{ Name = "PizzaDough"; Enum = 21; Visual = "Bread_Visual" }
    @{ Name = "PizzaUnbaked"; Enum = 22; Visual = "Bread_Visual" }
    @{ Name = "PizzaBaked"; Enum = 23; Visual = "Bread_Visual" }
    @{ Name = "ChickenBurger"; Enum = 24; Visual = "Bread_Visual" }
    @{ Name = "BeefBurger"; Enum = 25; Visual = "Bread_Visual" }
    @{ Name = "FishSushi"; Enum = 26; Visual = "CabbageSliced_Visual" }
    @{ Name = "ShrimpSushi"; Enum = 27; Visual = "CabbageSliced_Visual" }
    @{ Name = "TomatoSalad"; Enum = 28; Visual = "TomatoSlices_Visual" }
    @{ Name = "CabbageSalad"; Enum = 29; Visual = "CabbageSliced_Visual" }
    @{ Name = "TomatoCabbageSalad"; Enum = 30; Visual = "CabbageSliced_Visual" }
    @{ Name = "TomatoShake"; Enum = 31; Visual = "Tomato_Visual" }
    @{ Name = "CabbageShake"; Enum = 32; Visual = "Cabbage_Visual" }
    @{ Name = "TomatoCabbageShake"; Enum = 33; Visual = "Cabbage_Visual" }
)

function New-GuidHex {
    [guid]::NewGuid().ToString("N")
}

function New-PrefabYaml([string]$name, [int]$enumValue, [string]$visualGuid, [string]$visualDisplayName, [uint32]$netHash) {
@"
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &7347640811211430993
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  serializedVersion: 6
  m_Component:
  - component: {fileID: 7347640811211430992}
  - component: {fileID: -6451538418681678889}
  - component: {fileID: 3791774424439512234}
  - component: {fileID: 793269313709611719}
  m_Layer: 0
  m_Name: $name
  m_TagString: Untagged
  m_Icon: {fileID: 0}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
--- !u!4 &7347640811211430992
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 7347640811211430993}
  serializedVersion: 2
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_ConstrainProportionsScale: 0
  m_Children:
  - {fileID: 5841766198852696428}
  m_Father: {fileID: 0}
  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}
--- !u!114 &-6451538418681678889
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 7347640811211430993}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: d5a57f767e5e46a458fc5d3c628d0cbb, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  GlobalObjectIdHash: $netHash
  InScenePlacedSourceGlobalObjectIdHash: 0
  AlwaysReplicateAsRoot: 0
  SynchronizeTransform: 1
  ActiveSceneSynchronization: 0
  SceneMigrationSynchronization: 1
  SpawnWithObservers: 1
  DontDestroyWithOwner: 0
  AutoObjectParentSync: 1
--- !u!114 &3791774424439512234
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 7347640811211430993}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: b2e40bbcaed9436da1994d19a4b4656e, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  objEnum: $enumValue
  <follower>k__BackingField: {fileID: 0}
--- !u!114 &793269313709611719
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 7347640811211430993}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: fba95087c8914d57b3bc033293854f75, type: 3}
  m_Name: 
  m_EditorClassIdentifier: 
  <Target>k__BackingField: {fileID: 0}
--- !u!1001 &7347640810055610348
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {fileID: 7347640811211430992}
    m_Modifications:
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_RootOrder
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalPosition.x
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalPosition.y
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalPosition.z
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalRotation.w
      value: 1
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalRotation.x
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalRotation.y
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalRotation.z
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalEulerAnglesHint.x
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalEulerAnglesHint.y
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226176, guid: $visualGuid,
        type: 3}
      propertyPath: m_LocalEulerAnglesHint.z
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 3812876574508226177, guid: $visualGuid,
        type: 3}
      propertyPath: m_Name
      value: $visualDisplayName
      objectReference: {fileID: 0}
    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {fileID: 100100000, guid: $visualGuid, type: 3}
--- !u!4 &5841766198852696428 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: 3812876574508226176, guid: $visualGuid,
    type: 3}
  m_PrefabInstance: {fileID: 7347640810055610348}
  m_PrefabAsset: {fileID: 0}
"@
}

$created = @()
foreach ($item in $items) {
    $name = $item.Name
    $prefabPath = Join-Path $prefabDir "$name.prefab"
    $metaPath = "$prefabPath.meta"
    $visualKey = $item.Visual
    if (-not $visuals.ContainsKey($visualKey)) { throw "Unknown visual $visualKey" }
    $vGuid = $visuals[$visualKey]

    if (Test-Path $prefabPath) {
        Write-Host "Skip existing prefab: $name"
        $guidLine = Select-String -Path $metaPath -Pattern '^guid:\s*(\w+)' | Select-Object -First 1
        $guid = $guidLine.Matches[0].Groups[1].Value
    }
    else {
        $guid = New-GuidHex
        # Stable-ish hash from name
        $hashBytes = [System.Text.Encoding]::UTF8.GetBytes("KitchenObj.$name")
        $sha = [System.Security.Cryptography.SHA1]::Create().ComputeHash($hashBytes)
        $netHash = [BitConverter]::ToUInt32($sha, 0)
        if ($netHash -eq 0) { $netHash = 1 }

        $yaml = New-PrefabYaml -name $name -enumValue $item.Enum -visualGuid $vGuid -visualDisplayName "$($name)_Visual" -netHash $netHash
        Set-Content -Path $prefabPath -Value $yaml -Encoding UTF8
        $meta = @"
fileFormatVersion: 2
guid: $guid
PrefabImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"@
        Set-Content -Path $metaPath -Value $meta -Encoding UTF8
        Write-Host "Created prefab: $name ($guid) visual=$visualKey"
        $created += $name
    }

    # Wire SO
    $soPath = Join-Path $soDir "$name.asset"
    if (-not (Test-Path $soPath)) {
        Write-Warning "Missing SO: $soPath"
        continue
    }
    $soText = Get-Content $soPath -Raw
    $newRef = "prefab: {fileID: 7347640811211430993, guid: $guid, type: 3}"
    if ($soText -match 'prefab:\s*\{fileID:[^}]+\}') {
        $soText = [regex]::Replace($soText, 'prefab:\s*\{fileID:[^}]+\}', $newRef, 1)
        Set-Content -Path $soPath -Value $soText.TrimEnd() -Encoding UTF8
        Write-Host "  Wired SO: $name.asset -> $guid"
    }

    # Network prefabs list
    $netText = Get-Content $netList -Raw
    if ($netText -notmatch $guid) {
        $entry = @"
  - Override: 0
    Prefab: {fileID: 7347640811211430993, guid: $guid, type: 3}
    SourcePrefabToOverride: {fileID: 0}
    SourceHashToOverride: 0
    OverridingTargetPrefab: {fileID: 0}
"@
        if ($netText -notmatch "`r?`n$") { $netText += "`n" }
        $netText = $netText.TrimEnd() + "`n" + $entry.TrimEnd() + "`n"
        Set-Content -Path $netList -Value $netText -Encoding UTF8
        Write-Host "  Added to DefaultNetworkPrefabs: $name"
    }
}

Write-Host ""
Write-Host "Done. New prefabs: $($created.Count)"
$created | ForEach-Object { Write-Host " - $_" }
