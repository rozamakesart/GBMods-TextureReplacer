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
        internal static bool IsNormal     => LogLevel >= PluginLogLevel.Normal;
        internal static bool IsDiagnostic => LogLevel >= PluginLogLevel.Diagnostic;

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

            var pngFiles = Directory.GetFiles(folder, "*.png");
            TextureReplacementPlugin.Log.LogInfo(
                $"[LOADER] Found {pngFiles.Length} PNG(s) in {folder}");

            if (pngFiles.Length == 0)
            {
                TextureReplacementPlugin.Log.LogWarning(
                    "[LOADER] No PNGs found — nothing will be replaced.");
                return;
            }

            foreach (string file in pngFiles)
            {
                try
                {
                    string assetName = Path.GetFileNameWithoutExtension(file);
                    Texture2D tex = LoadWithImageSharp(file, assetName);
                    if (tex != null)
                    {
                        TextureRegistry.Register(assetName, tex);
                        TextureReplacementPlugin.Log.LogInfo(
                            $"[LOADER] Registered '{assetName}' ({tex.width}x{tex.height})");
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

            // GameObject load — scan children for embedded textures
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
