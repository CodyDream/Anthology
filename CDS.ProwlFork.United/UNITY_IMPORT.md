# Unity Import / Shader Pipeline

User guide for the Unity asset import and shader transpilation pipeline built on
`CDS.ProwlFork.United` (this repo) and the Prowl editor fork. The goal is to bring
Unity source assets into Prowl without a Unity Editor dependency: read the
archive, parse its YAML, convert what can be converted automatically, and turn
ShaderLab/HLSL/compute shaders into Slang and then into importable Prowl shaders.

> **Both forks are personal.** The Anthology side lives on branch
> `cds-united-library` of `CodyDream/Anthology`; the Prowl side lives on branch
> `unity-import` of `CodyDream/NexusForge-Prowl`. There is no PR to either
> upstream. Treat these as private integrations, not supported upstream features.

## What the library does

`CDS.ProwlFork.United` is an engine-agnostic set of readers and a transpiler:

- **`.unitypackage` reader** (`UnityPackageReader`, `UnityPackageEntry`): a
  `.unitypackage` is a gzip-compressed tar archive. Each asset is a directory
  named after its GUID hash, normally containing `pathname` (the UTF-8 Unity
  project path), `asset` (raw bytes) and `asset.meta` (optional). Directories
  without a `pathname` member are ignored. The reader preserves asset and meta
  bytes verbatim.
- **Unity YAML parser** (`UnityYaml`, `UnityDocument`, `UnityRef`,
  `UnityYamlNode`): reads the subset of YAML emitted by Unity serialisation —
  document markers such as `--- !u!21 &2100000`, indentation-based maps/lists
  (including Unity's indentless list style), and multi-line flow maps like
  `{fileID: 1, guid: abc, type: 3}`. YAML-less `.meta` files become one implicit
  document (`ClassId`/`FileId` zero). Binary inputs and ShaderGraph JSON produce
  an empty result rather than throwing. `UnityRef` is the parsed
  `guid/fileID/type` reference used to reconnect assets.
- **Shader transpiler** (`UnityShaderTranspiler`, `ShaderLabParser`,
  `ShaderHlslTranslator`): translates ShaderLab structure — properties, tags,
  subshaders, passes and `CGPROGRAM`/`HLSLPROGRAM` blocks — and the safe subset
  of HLSL/Cg into Slang. Unity-specific constructs that cannot be mapped
  reliably are commented out or left in place and surfaced through
  `TranspiledShader.Warnings`; the original source is appended as line comments
  for review. `Transpile` handles `.shader`; `TranspileCompute` handles both
  `#pragma kernel` ShaderLab shaders and raw `.compute` HLSL. ShaderGraph JSON
  is deliberately not parsed.

## Where the Prowl-side glue lives

- `Prowl.Editor/AssetsDatabase/Importers/Unity*`
  - `UnityPackageImporter.cs` — the `[ImporterFor(".unitypackage")]` importer;
    extracts the archive, drives the conversions below and produces a
    `UnityImportReport` as the main asset.
  - `UnityImportAssets.cs` — `UnityImportReport` (counts: entries, raw imports,
    materials, ScriptableObjects, meta files, skipped) and
    `UnityScriptableObjectAsset`.
  - `UnityHierarchyConverter.cs` — builds real Prowl prefab/scene objects from
    Unity YAML documents; resolves local `fileID`s and `.meta` GUIDs.
  - `UnityFileId.cs` — fileID helpers.
- `Prowl.Editor/UnityShaders/`
  - `UnityShaderTranspile.cs` — folder walk, mirrored output tree and
    `TRANSPILE_REPORT.txt`; the per-file manual-work report.
  - `UnityShaderTranspileMenu.cs` — the **Tools/Unity Shader Transpiler** menu
    entries.
  - `UnityShaderTranspileCli.cs` — the `unity_shaders` CLI command.
  - `SlangCompilePipeline.cs` / `SlangCompileCli.cs` / `SlangGlslRewrite.cs` —
    second stage: compile Slang with the native compiler (`glsl_450`), rewrite
    GLSL into Prowl ShaderDef form, emit `.shader`/`.compute` and
    `SLANG_COMPILE_REPORT.txt`.
- `Prowl.UnityCompat/` — minimal Unity-shaped runtime types (`Object`,
  `MonoBehaviour`, `ScriptableObject`, `SerializeField`, `Vector3`, `Time`,
  `Debug`, `HideFlags`) plus hand-written compute drivers in
  `Prowl.UnityCompat/Drivers/` (`OceanFFTDriver`, `ShipWakeDriver`). These let
  converted scripts and compute shaders have a C# side to drive them.

## Commands

Run these from the Prowl project directory; `Library/prowl` is the editor CLI
launcher. All paths are relative to the project's `Assets` unless stated
otherwise.

Import a `.unitypackage` (extracts and converts in one step):

```bat
Library/prowl command asset import --path <file> --timeout 300
```

Transpile a folder of Unity `.shader`/`.compute` sources to `.slang`. `--output`
is optional; without it the sibling `Slang` folder is used:

```bat
Library/prowl command unity_shaders --source <folder> [--output <folder>]
```

Compile the generated `.slang` into Prowl `.shader`/`.compute` assets once that
command lands in the fork. It takes a `--source` folder of `.slang` files and an
optional `--output` (default: sibling `CompiledShaders` folder):

```bat
Library/prowl command unity_shaders_compile --source <folder> [--output <folder>]
```

Both transpile stages write a report next to the output:
`TRANSPILE_REPORT.txt` (properties, warnings, files needing manual C# driver
work, failures) and `SLANG_COMPILE_REPORT.txt` (entry points, compiler
warnings, files written/imported, failures). A bad file never aborts the run.

### Editor menu

Under **Tools/Unity Shader Transpiler**:

- **Transpile Shader Folder...** — pick a source folder, then an output folder
  (defaults to a sibling `Slang` folder).
- **Transpile Known External Folder** — convenience entry for this import
  task's hard-coded external shader folder.

## Conversion behaviour

- **Textures and models** (`.png`, `.jpg`, `.tga`, `.psd`, `.exr`, `.fbx`,
  `.obj`, `.gltf`, `.glb`, audio, etc.) are written out and handed to Prowl's
  *existing* importers — no duplicate format handling. Unity `.meta` files are
  archived under `UnityMeta/` as source data only; Prowl metadata is always
  generated by Prowl's backend, never copied from Unity.
- **`.mat`** files become Prowl **Lit** materials. Common URP and Built-in
  property names are mapped onto the Lit slots (`_BaseMap`/`_MainTex` →
  `base_color_map`, normals, occlusion, metallic/gloss, `_Color` →
  `base_color`). Unity smoothness is stored as inverse roughness, and URP's
  packed metallic/roughness map is bound to both slots with the right channels.
- **Simple ScriptableObjects** (YAML `.asset`, class 114/MonoBehaviour) become
  `UnityScriptableObjectAsset` objects preserving class/file IDs, the `m_Script`
  reference and top-level scalar fields and references. Binary assets and URP
  volume profiles are skipped.
- **Prefabs** become real Prowl prefab assets written as
  `*.converted.prefab` next to the untouched raw YAML.
- **Scenes** become real Prowl scene assets written as
  `*.converted.scene` next to the source YAML.

The main asset of an import is a `UnityImportReport` summarising counts and
skips. GUID references from Unity `.meta` files are bridged to Prowl GUIDs
through the imported asset paths so textures/materials reconnect correctly.

## Known limitations (from the audit)

These need a manual port; automatic conversion does not cover them:

- **ShaderGraph** — node data is not parsed. Export ShaderLab/HLSL source first;
  graphs are detected and reported as warnings.
- **URP volume profiles** and **terrain layers** — not translated (skipped
  with a warning and counted in the report).
- **Particle systems** — not converted; left as source.
- **Fullscreen water raymarch** and the **water clipmap** — shader-side work
  that needs manual porting.
- **Compute shaders need C# drivers.** The kernels alone do nothing; dispatch
  order and resource binding must be written in C#. See `OceanFFTDriver` and
  `ShipWakeDriver` in `Prowl.UnityCompat/Drivers/` for the hand-written
  examples (they own the kernel dispatch and cbuffer values).
- **Prowl has no `.slang` importer.** Generated `.slang` files are plain-text
  artifacts for review only. They must be compiled to Prowl `.shader` /
  `.compute` assets (via `unity_shaders_compile` when available) before the
  asset database can import them.

Always review `TRANSPILE_REPORT.txt` / `SLANG_COMPILE_REPORT.txt` and the
warnings embedded at the top of each generated file before using the output.
Properties are emitted as named globals — bind every one of them (texture
properties also need a sampler named `<Property>Sampler`), and clamp `Range`
values manually since the original min/max limits are not preserved.
