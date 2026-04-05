# Texture Replacer
A BepInEx plugin for Unity IL2CPP games (specifically Story of Seasons Grand Bazaar) that replaces in-game textures at runtime by intercepting the game's texture loading and rendering pipeline.

---

## How It Works

The plugin reads `.png` files from a `TextureReplacer` folder, loads them as `Texture2D` objects, and registers them in a central registry keyed by filename (without extension). Whenever the game tries to use a texture with a matching name, the plugin swaps it out for the custom one.

---

## Installation

1. Install [BepInEx](https://github.com/BepInEx/BepInEx) for your game.
2. Drop `TextureReplacer.dll` into `BepInEx/plugins/`.
3. Launch the game once to auto-create the `BepInEx/plugins/TextureReplacer/` folder.
4. Place your replacement `.png` files into that folder.
5. Each file must be named **exactly** after the texture it replaces (e.g. `tex_chr_npc_M_000_o.png`).

---

## Texture Naming

Texture names are normalized before matching, so you don't need to worry about paths, extensions, or Unity's ` (Instance)` suffix. The rules are:

- Everything before and including the last `/` is stripped (path prefix removed)
- Everything from the last `.` onward is stripped (extension removed)
- The suffix ` (Instance)` is removed if present

So a texture the game refers to as `characters/skins/tex_chr_npc_M_000_o.png (Instance)` will match a file named `tex_chr_npc_M_000_o.png`.
NOTE: This only works if the texture is indeed used 

---

## Interception Points

The plugin hooks into the following function of the Unity Asset Pipeline:

`AssetBundleRequest.get_asset(string, Type)`

---

## Fallback Scanner

A persistent `MonoBehaviour` survives scene loads via `DontDestroyOnLoad` and provides a manual fallback for anything that might load outside the patched method. It has to be activated via config and should mostly be used for debugging purposes, since .get_asset() should be able to cover all bases.

| Key | Action |
|---|---|
| `F5` | Manually scans every `Renderer` in the scene and swaps any matching textures on their materials |

---

## Dependencies

- [BepInEx 6 (IL2CPP) & IL2CPPInterop Bleeding Edge version #738](https://builds.bepinex.dev/projects/bepinex_be)
- [HarmonyX](https://github.com/BepInEx/HarmonyX) (not necessary to download)
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) (library provided in release builds)

This project uses Six Labors ImageSharp, licensed under the Apache License 2.0.

---

## Notes

- Textures loaded via ImageSharp are flipped vertically on load, since Unity's UV origin differs from most standard image formats. This should not cause issues if you use the ingame textures though.
- The registry holds strong `Object` references to all loaded textures to prevent Unity's garbage collector from unloading them.
- This plugin should be game-agnostic, but has only been tested with Story of Seasons Grand Bazaar.
   
---

# Texture Diagnostics
A standalone BepInEx observer plugin for tracing how a specific texture moves through Unity's rendering pipeline. It works alongside the Texture Replacer plugin but has no dependency on it — it can be used entirely on its own.

> **This plugin never modifies game state.** All patches are read-only observers.

---

## How It Works

The plugin hooks into the same texture loading and assignment methods as the replacer, but only to log what is happening — it never modifies `ref` parameters or return values. Load method hooks use both a Prefix and Postfix so you can see both that a texture was requested *and* what was actually returned after all other patches (including the replacer) have run.

---

## Installation

1. Install [BepInEx](https://github.com/BepInEx/BepInEx) for your game.
2. Drop `TextureDiagnostics.dll` into `BepInEx/plugins/`.
3. Set the target texture name in the plugin source before building (see [Configuration](#configuration)).

---

## Configuration

Open `TextureDiagnostics.cs` and set `TargetTexture` to the normalized name of the texture you want to track:

```csharp
public static string TargetTexture = "tex_chr_npc_M_000_o";
```

The name follows the same normalization rules as the replacer — filename only, no path, no extension, no ` (Instance)` suffix.

Logging is **enabled by default** on load. Toggle it at runtime with `F6`.

---

## Hotkeys

| Key | Action |
|---|---|
| `F6` | Toggle diagnostic logging on/off |
| `F7` | Dump every texture on every `Renderer` in the current scene to the log, with the target texture highlighted |

---

## Observed Methods

| Hook | What it reports |
|---|---|
| `AssetBundle.LoadAsset` (Prefix) | Logs when the target is requested from an asset bundle, with a full stack trace |
| `AssetBundle.LoadAsset` (Postfix) | Logs what was actually returned — useful for confirming whether another patch intercepted it first |
| `Resources.Load` (Prefix) | Logs when the target is requested via the Resources system, with a full stack trace |
| `Resources.Load` (Postfix) | Logs what was actually returned |
| `Material.SetTexture` (by name & ID) | Logs every material texture assignment involving the target |
| `Material.mainTexture` setter | Logs main texture property assignments involving the target |
| `MaterialPropertyBlock.SetTexture` (by name & ID) | Logs per-renderer texture overrides involving the target |

All log entries include the texture's instance ID, so you can tell at a glance whether the object in memory has changed between the request and the return.

---

## Scene Dump (F7)

Pressing `F7` walks every `Renderer` in the scene across the common texture slots (`_MainTex`, `_BaseMap`, `_BaseColorMap`, `_Albedo`, `_DiffuseTexture`, `_Texture`, `_SkinTex`) and logs each one. The target texture is printed as a `LogWarning` so it stands out in the log:

```
[DIAG DUMP] *** TARGET FOUND ***
[DIAG DUMP]   GameObject : NPC_Male_000
[DIAG DUMP]   Renderer   : SkinnedMeshRenderer
[DIAG DUMP]   Material   : mat_chr_npc_M_000
[DIAG DUMP]   Slot       : _MainTex
[DIAG DUMP]   Tex name   : tex_chr_npc_M_000_o  (raw: tex_chr_npc_M_000_o)
[DIAG DUMP]   InstanceID : 12345678
```

---

## Dependencies

- [BepInEx 6 (IL2CPP) & IL2CPPInterop Bleeding Edge version #738](https://builds.bepinex.dev/projects/bepinex_be)
- [HarmonyX](https://github.com/BepInEx/HarmonyX) (not required to download)

> Unlike the Texture Replacer, this plugin does **not** require ImageSharp.

---

## Notes

- This plugin uses its own `Harmony` instance (`com.yourname.texturediagnostics`), completely separate from the replacer. Both can be active at the same time without conflict.
- `DiagTextureNameUtil` is a self-contained copy of the name normalization logic, so this plugin compiles independently with no cross-assembly dependency on the replacer.
- Because this plugin's Harmony instance is registered after the replacer's, its Prefix patches observe the `ref` value *as modified by the replacer* — meaning you see the final value going into the method, not the original. The Postfix patches on load methods give you the returned result after all patches have run, which is usually the most useful signal.

