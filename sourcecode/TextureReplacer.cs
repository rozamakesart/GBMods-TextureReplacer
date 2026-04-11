using System;
using System.IO;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// ImageSharp namespaces
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace TextureReplacer
{
    // =========================================================
    // Log level
    // =========================================================

    /// <summary>
    /// Controls how much the plugin logs to the BepInEx console.
    /// </summary>
    public enum PluginLogLevel
    {
        /// <summary>Only errors are logged. Recommended for normal use.</summary>
        None,

        /// <summary>Logs texture registrations at startup and successful replacements at runtime.</summary>
        Normal,

        /// <summary>
        /// Logs every registry lookup (hits and misses) and every asset intercepted by get_asset.
        /// Very spammy — use only when a texture is not being replaced and you need to find out why.
        /// </summary>
        Diagnostic
    }

    // =========================================================
    // Plugin entry point
    // =========================================================

    [BepInPlugin("TextureReplacer", "Texture Replacer", "1.7.0")]
    public class TextureReplacementPlugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        internal static Harmony Harmony;

        public static PluginLogLevel LogLevel { get; private set; }

        // Convenience helpers used throughout the plugin
        internal static bool IsNormal             => LogLevel >= PluginLogLevel.Normal;
        internal static bool IsDiagnostic         => LogLevel >= PluginLogLevel.Diagnostic;
        internal static bool SpriteReplacementEnabled { get; private set; }

        public override void Load()
        {
            Log = base.Log;

            var cfgLogLevel = Config.Bind(
                "General",
                "LogLevel",
                PluginLogLevel.None,
                "Controls how much the plugin logs.\n" +
                "  None       - Only errors. Recommended for normal use.\n" +
                "  Normal     - Logs texture registrations at startup and replacements at runtime.\n" +
                "  Diagnostic - Logs every registry lookup and every intercepted asset.\n" +
                "               Very spammy — only use when a texture is not being replaced.\n" +
                "Default: None"
            );
            LogLevel = cfgLogLevel.Value;

            var cfgScanner = Config.Bind(
                "General",
                "EnableF5Scanner",
                false,
                "Enable the F5 hotkey to manually re-scan all active renderers and apply replacements.\n" +
                "Useful if a texture loads after the initial scene load and isn't caught by get_asset.\n" +
                "Default: false"
            );

            var cfgSprite = Config.Bind(
                "General",
                "EnableSpriteReplacement",
                false,
                "Enable replacement of Sprite assets and UI.Image components.\n" +
                "Only standalone sprites (whose texture rect covers the full texture) are\n" +
                "replaced safely. Atlas-packed sprites are skipped automatically.\n" +
                "Default: false"
            );
            SpriteReplacementEnabled = cfgSprite.Value;

            // Initialize Harmony and apply patches
            Harmony = new Harmony("TextureReplacer");
            Harmony.PatchAll(typeof(GetAssetPatches));

            // Load replacement textures on first scene load
            SceneManager.add_sceneLoaded((UnityAction<Scene, LoadSceneMode>)OnFirstSceneLoaded);

            // Only register and instantiate the scanner if the user has enabled it
            if (cfgScanner.Value)
            {
                try { ClassInjector.RegisterTypeInIl2Cpp<TextureScanner>(); }
                catch { Log.LogWarning("TextureScanner already registered"); }
                CreatePersistentComponent<TextureScanner>();
            }

            Log.LogInfo("Texture Replacer plugin loaded.");
        }

        private static bool _texturesLoaded;
        private static void OnFirstSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_texturesLoaded) return;
            _texturesLoaded = true;
            TextureLoader.LoadAllReplacements();
        }

        private static void CreatePersistentComponent<T>() where T : MonoBehaviour
        {
            var go = new GameObject("TextureReplacementScanner");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<T>();
        }
    }

    // =========================================================
    // Optional F5 scanner
    // =========================================================

    public class TextureScanner : MonoBehaviour
    {
        public TextureScanner(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F5)) return;

            if (TextureReplacementPlugin.IsNormal)
                TextureReplacementPlugin.Log.LogInfo("[SCANNER] F5 manual refresh triggered.");

            int swapCount = 0;
            var allRenderers = GameObject.FindObjectsOfType<Renderer>(true);

            foreach (var renderer in allRenderers)
            {
                if (renderer == null) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    foreach (var slot in MaterialTextureUtils.Slots)
                    {
                        if (!mat.HasProperty(slot)) continue;
                        var tex = mat.GetTexture(slot);
                        if (tex == null) continue;

                        string normName = TextureNameUtil.Normalize(tex.name);
                        if (TextureRegistry.TryGet(normName, out var replacement))
                        {
                            if (tex.GetInstanceID() != replacement.GetInstanceID())
                            {
                                mat.SetTexture(slot, replacement);
                                swapCount++;
                            }
                        }
                    }
                }
            }

            // UI.Image sprite sweep
            if (TextureReplacementPlugin.SpriteReplacementEnabled)
            {
                var allImages = GameObject.FindObjectsOfType<UnityEngine.UI.Image>(true);
                foreach (var img in allImages)
                {
                    if (img == null) continue;
                    int imgSwaps = SpriteUtils.ReplaceImageSprite(img, "[SCANNER]");
                    swapCount += imgSwaps;
                }
            }

            if (TextureReplacementPlugin.IsNormal)
                TextureReplacementPlugin.Log.LogInfo(
                    swapCount > 0
                        ? $"[SCANNER] Applied {swapCount} replacement(s)."
                        : "[SCANNER] No replacements needed.");
        }
    }

    // =========================================================
    // Shared material slot definitions and renderer scanning
    // =========================================================

    internal static class MaterialTextureUtils
    {
        /// <summary>
        /// All material texture slot names to check. Add new shader slots here.
        /// </summary>
        internal static readonly string[] Slots =
        {
            "_MainTex", "_BaseMap", "_BaseColorMap",
            "_Albedo", "_DiffuseTexture", "_Texture", "_SkinTex"
        };

        /// <summary>
        /// Scans all renderers on a GameObject and its children,
        /// replacing any registered textures found in their materials.
        /// Returns the number of replacements made.
        /// </summary>
        public static int ScanAndReplace(GameObject go, string logPrefix)
        {
            int count = 0;

            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    foreach (var slot in Slots)
                    {
                        if (!mat.HasProperty(slot)) continue;
                        var tex = mat.GetTexture(slot);
                        if (tex == null) continue;

                        string normName = TextureNameUtil.Normalize(tex.name);
                        if (TextureRegistry.TryGet(normName, out var replacement))
                        {
                            mat.SetTexture(slot, replacement);
                            count++;
                            if (TextureReplacementPlugin.IsNormal)
                                TextureReplacementPlugin.Log.LogInfo(
                                    $"{logPrefix} Replaced '{normName}' in " +
                                    $"'{go.name}/{renderer.name}' slot '{slot}'");
                        }
                    }
                }
            }

            return count;
        }
    }

    // =========================================================
    // Sprite utilities
    // =========================================================

    internal static class SpriteUtils
    {
        /// <summary>
        /// Attempts to rebuild <paramref name="original"/> using a registered
        /// replacement texture. Returns null if:
        ///   - sprite replacement is disabled in config
        ///   - no replacement texture is registered for this sprite
        ///   - the sprite is atlas-packed (rect does not cover the full texture)
        /// </summary>
        public static Sprite RebuildSprite(Sprite original, string logPrefix)
        {
            if (!TextureReplacementPlugin.SpriteReplacementEnabled) return null;
            if (original == null)                                   return null;

            if (original.texture == null)                                   return null;
            string normName = TextureNameUtil.Normalize(original.texture.name);
            if (string.IsNullOrEmpty(normName))                             return null;
            if (!TextureRegistry.TryGet(normName, out var replacement))     return null;

            // Atlas-safety guard: skip sprites that cover only a sub-region
            // of their texture, as replacing the texture would break UV mapping
            // for every other sprite sharing that atlas.
            var rect = original.rect;
            if (rect.width  != original.texture.width ||
                rect.height != original.texture.height)
            {
                TextureReplacementPlugin.Log.LogWarning(
                    $"{logPrefix} Skipped atlas-packed sprite '{original.name}' " +
                    $"(rect {rect.width}x{rect.height} != " +
                    $"texture {original.texture.width}x{original.texture.height}). " +
                    "Supply a full replacement atlas to replace this sprite.");
                return null;
            }

            var newSprite = Sprite.Create(
                replacement,
                new Rect(0f, 0f, replacement.width, replacement.height),
                new Vector2(original.pivot.x / original.rect.width,
                            original.pivot.y / original.rect.height),
                original.pixelsPerUnit,
                0,
                SpriteMeshType.FullRect,
                original.border
            );
            newSprite.name      = original.name;
            newSprite.hideFlags = HideFlags.HideAndDontSave;

            if (TextureReplacementPlugin.IsNormal)
                TextureReplacementPlugin.Log.LogInfo(
                    $"{logPrefix} Rebuilt sprite '{original.name}' " +
                    $"with replacement texture '{normName}'");

            return newSprite;
        }

        /// <summary>
        /// Scans a single UI.Image and replaces its sprite if a registered
        /// texture matches. Returns 1 if a replacement was made, 0 otherwise.
        /// </summary>
        public static int ReplaceImageSprite(UnityEngine.UI.Image img, string logPrefix)
        {
            if (img?.sprite == null) return 0;
            var rebuilt = RebuildSprite(img.sprite, logPrefix);
            if (rebuilt == null) return 0;
            img.sprite = rebuilt;
            return 1;
        }

        /// <summary>
        /// Walks all UI.Image components on <paramref name="go"/> and its children,
        /// replacing sprites whose backing texture is registered.
        /// Returns the number of replacements made.
        /// </summary>
        public static int ScanAndReplaceImages(GameObject go, string logPrefix)
        {
            int count = 0;
            foreach (var img in go.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                count += ReplaceImageSprite(img, logPrefix);
            return count;
        }
    }

    // =========================================================
    // Texture registry
    // =========================================================

    internal static class TextureRegistry
    {
        // HideAndDontSave on each texture already prevents Unity from unloading them,
        // so no separate root list is needed.
        private static readonly Dictionary<string, Texture2D> _replacements = new();

        public static void Register(string name, Texture2D tex)
        {
            _replacements[name] = tex;
        }

        public static bool Contains(string name) =>
            _replacements.ContainsKey(name);

        public static bool TryGet(string name, out Texture2D tex)
        {
            bool found = _replacements.TryGetValue(name, out tex);
            if (TextureReplacementPlugin.IsDiagnostic)
                TextureReplacementPlugin.Log.LogInfo(
                    found
                        ? $"[REGISTRY HIT]  '{name}'"
                        : $"[REGISTRY MISS] '{name}'");
            return found;
        }

        public static void DumpAll()
        {
            TextureReplacementPlugin.Log.LogInfo(
                $"[REGISTRY] {_replacements.Count} replacement(s) registered:");
            foreach (var kvp in _replacements)
                TextureReplacementPlugin.Log.LogInfo(
                    $"  '{kvp.Key}' ({kvp.Value.width}x{kvp.Value.height})");
        }
    }

    // =========================================================
    // Texture loader
    // =========================================================

    internal static class TextureLoader
    {
        public static void LoadAllReplacements()
        {
            string folder = Path.Combine(Paths.PluginPath, "TextureReplacer");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                TextureReplacementPlugin.Log.LogWarning(
                    $"[LOADER] Texture folder not found — created it at: {folder}\n" +
                    "Place replacement PNGs here, named to match the in-game texture asset name.");
                return;
            }

            // Collect top-level PNGs first so they take priority over subfolders,
            // then append any PNGs found inside subdirectories.
            var topLevelFiles  = Directory.GetFiles(folder, "*.png",
                                     SearchOption.TopDirectoryOnly);
            var subFolderFiles = Directory.GetFiles(folder, "*.png",
                                     SearchOption.AllDirectories);

            // Build a depth-ordered, deduplicated file list.
            // Top-level entries come first; subfolder entries are appended only
            // if their path wasn't already covered by the top-level scan.
            var topLevelSet   = new System.Collections.Generic.HashSet<string>(topLevelFiles);
            var orderedFiles  = new System.Collections.Generic.List<string>(topLevelFiles);
            foreach (string f in subFolderFiles)
                if (!topLevelSet.Contains(f))
                    orderedFiles.Add(f);

            int totalCount = orderedFiles.Count;
            TextureReplacementPlugin.Log.LogInfo(
                $"[LOADER] Found {totalCount} PNG(s) under {folder} " +
                $"({topLevelFiles.Length} top-level, " +
                $"{totalCount - topLevelFiles.Length} in subfolders)");

            if (totalCount == 0)
            {
                TextureReplacementPlugin.Log.LogWarning(
                    "[LOADER] No PNGs found — nothing will be replaced.");
                return;
            }

            foreach (string file in orderedFiles)
            {
                try
                {
                    string assetName = Path.GetFileNameWithoutExtension(file);

                    // Deduplication guard: the first file registered for a given
                    // asset name wins (top-level beats subfolders; among subfolders,
                    // the first one in directory-enumeration order wins).
                    if (TextureRegistry.Contains(assetName))
                    {
                        TextureReplacementPlugin.Log.LogWarning(
                            $"[LOADER] Duplicate skipped: '{assetName}' " +
                            $"('{file}' — already registered from an earlier path).");
                        continue;
                    }

                    Texture2D tex = LoadWithImageSharp(file, assetName);
                    if (tex != null)
                    {
                        TextureRegistry.Register(assetName, tex);
                        if (TextureReplacementPlugin.IsNormal)
                            TextureReplacementPlugin.Log.LogInfo(
                                $"[LOADER] Registered '{assetName}' ({tex.width}x{tex.height}) " +
                                $"from '{Path.GetRelativePath(folder, file)}'");
                    }
                    else
                    {
                        TextureReplacementPlugin.Log.LogError(
                            $"[LOADER] Failed to load '{assetName}' — loader returned null.");
                    }
                }
                catch (Exception ex)
                {
                    TextureReplacementPlugin.Log.LogError(
                        $"[LOADER] Exception loading '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            if (TextureReplacementPlugin.IsNormal)
                TextureRegistry.DumpAll();
        }

        private static Texture2D LoadWithImageSharp(string path, string name)
        {
            using var image = ISImage.Load<Rgba32>(path);
            image.Mutate(x => x.Flip(FlipMode.Vertical));

            byte[] pixelBytes = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixelBytes);

            var tex = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false);
            tex.name = name;
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.LoadRawTextureData((Il2CppStructArray<byte>)pixelBytes);
            tex.Apply(true, true);
            return tex;
        }
    }

    // =========================================================
    // Utility
    // =========================================================

    internal static class TextureNameUtil
    {
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);

            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);

            return name.Replace(" (Instance)", "");
        }
    }

    // =========================================================
    // get_asset patch — intercepts all completed AssetBundleRequests
    // =========================================================

    [HarmonyPatch]
    internal static class GetAssetPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssetBundleRequest), "get_asset")]
        private static void GetAssetPostfix(ref Object __result)
        {
            if (__result == null) return;

            // Direct Texture2D load
            var tex = __result.TryCast<Texture2D>();
            if (tex != null)
            {
                string normName = TextureNameUtil.Normalize(tex.name);

                if (TextureReplacementPlugin.IsDiagnostic)
                    TextureReplacementPlugin.Log.LogInfo(
                        $"[GET_ASSET] Intercepted texture '{normName}'");

                if (TextureRegistry.TryGet(normName, out var replacement))
                {
                    __result = replacement;
                    if (TextureReplacementPlugin.IsNormal)
                        TextureReplacementPlugin.Log.LogInfo(
                            $"[GET_ASSET] Replaced '{normName}'");
                }
                return;
            }

            // Sprite load
            if (TextureReplacementPlugin.SpriteReplacementEnabled)
            {
                var sprite = __result.TryCast<Sprite>();
                if (sprite != null)
                {
                    if (TextureReplacementPlugin.IsDiagnostic)
                        TextureReplacementPlugin.Log.LogInfo(
                            $"[GET_ASSET] Intercepted sprite '{sprite.name}'");

                    var rebuilt = SpriteUtils.RebuildSprite(sprite, "[GET_ASSET]");
                    if (rebuilt != null)
                        __result = rebuilt;
                    return;
                }
            }

            // GameObject load — scan children for embedded textures and UI.Images
            var go = __result.TryCast<GameObject>();
            if (go != null)
            {
                if (TextureReplacementPlugin.IsDiagnostic)
                    TextureReplacementPlugin.Log.LogInfo(
                        $"[GET_ASSET] Intercepted GameObject '{go.name}' — scanning children");

                int count = MaterialTextureUtils.ScanAndReplace(go, "[GET_ASSET]");
                if (count > 0 && TextureReplacementPlugin.IsNormal)
                    TextureReplacementPlugin.Log.LogInfo(
                        $"[GET_ASSET] Replaced {count} texture(s) in '{go.name}'");

                if (TextureReplacementPlugin.SpriteReplacementEnabled)
                {
                    int spriteCount = SpriteUtils.ScanAndReplaceImages(go, "[GET_ASSET]");
                    if (spriteCount > 0 && TextureReplacementPlugin.IsNormal)
                        TextureReplacementPlugin.Log.LogInfo(
                            $"[GET_ASSET] Replaced {spriteCount} sprite(s) in '{go.name}'");
                }
            }
        }
    }
}

// Stub attributes required to avoid CS0656 on nullable reference type annotations
// emitted by the compiler. These are no-ops at runtime.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class NullableAttribute : Attribute
    {
        public NullableAttribute(byte flag) { }
        public NullableAttribute(byte[] flags) { }
    }

    [AttributeUsage(AttributeTargets.All)]
    public sealed class NullableContextAttribute : Attribute
    {
        public NullableContextAttribute(byte flag) { }
    }
}
