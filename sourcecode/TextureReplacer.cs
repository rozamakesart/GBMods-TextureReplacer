using System;
using System.IO;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime;
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
    [BepInPlugin("com.yourname.TextureReplacer", "Texture Replacement", "1.6.0")]
    public class TextureReplacerPlugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        internal static Harmony Harmony;

        public override void Load()
        {
            Log = base.Log;

            // Register our MonoBehaviour type with IL2CPP
            try { ClassInjector.RegisterTypeInIl2Cpp<TextureScanner>(); }
            catch { Log.LogWarning("TextureScanner already registered"); }

            // Initialize Harmony and apply all patches
            Harmony = new Harmony("com.yourname.TextureReplacer");

            // Asset loading interception
            Harmony.PatchAll(typeof(AssetBundlePatches));
            Harmony.PatchAll(typeof(ResourcePatches));

            // Material assignment interception
            Harmony.PatchAll(typeof(MaterialPatches));

            // MaterialPropertyBlock interception (catches per-renderer overrides)
            Harmony.PatchAll(typeof(MaterialPropertyBlockPatches));

            // Game-specific hooks
            Harmony.PatchAll(typeof(ResourceManagerPatches));
            Harmony.PatchAll(typeof(FadePatches));

            // Load textures on first scene load
            SceneManager.add_sceneLoaded((UnityAction<Scene, LoadSceneMode>)OnFirstSceneLoaded);

            Log.LogInfo("Texture Replacement plugin loaded.");

            // Add the persistent scanner/hotkey handler
            AddUnityComponent<TextureScanner>();
        }

        private static bool _texturesLoaded = false;
        private static void OnFirstSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_texturesLoaded) return;
            _texturesLoaded = true;
            TextureLoader.LoadAllReplacements();
        }

        private static void AddUnityComponent<T>() where T : MonoBehaviour
        {
            var go = new GameObject("TextureReplacerScanner");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<T>();
        }
    }

    // =========================================================
    // The Scanner (Handles Hotkeys and Post-Load Fallback)
    // =========================================================
    public class TextureScanner : MonoBehaviour
    {
        public TextureScanner(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F5))
            {
                TextureReplacerPlugin.Log.LogInfo("[HOTKEY] F5 Manual Refresh.");
                PerformGlobalSwap();
            }
        }

        public System.Collections.IEnumerator DelayedSwap(float delay)
        {
            yield return new WaitForSeconds(delay);
            PerformGlobalSwap();
        }

        public void PerformGlobalSwap()
        {
            int swapCount = 0;
            var allRenderers = GameObject.FindObjectsOfType<Renderer>(true);

            TextureReplacerPlugin.Log.LogInfo($"[SWAP] Scanning {allRenderers.Length} renderers...");

            foreach (var renderer in allRenderers)
            {
                if (renderer == null) continue;

                var mats = renderer.sharedMaterials;
                foreach (var mat in mats)
                {
                    if (mat == null) continue;

                    string[] slots = { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_DiffuseTexture", "_Texture", "_SkinTex" };
                    foreach (var slot in slots)
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

            if (swapCount > 0)
                TextureReplacerPlugin.Log.LogInfo($"[SWAP] Applied {swapCount} textures.");
            else
                TextureReplacerPlugin.Log.LogInfo($"[SWAP] No textures needed replacement.");
        }
    }

    // =========================================================
    // Core Logic & Registry
    // =========================================================
    internal static class TextureRegistry
    {
        private static readonly List<Object> _roots = new();
        private static readonly Dictionary<string, Texture2D> _replacements = new();

        public static void Register(string name, Texture2D tex)
        {
            _replacements[name] = tex;
            if (!_roots.Contains(tex)) _roots.Add(tex);
        }

        public static bool TryGet(string name, out Texture2D tex) => _replacements.TryGetValue(name, out tex);

        public static int Count => _replacements.Count;
    }

    internal static class TextureLoader
    {
        public static void LoadAllReplacements()
        {
            string folder = Path.Combine(Paths.PluginPath, "TextureReplacer");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                TextureReplacerPlugin.Log.LogInfo($"Created texture folder: {folder}");
                return;
            }

            var pngFiles = Directory.GetFiles(folder, "*.png");
            TextureReplacerPlugin.Log.LogInfo($"Loading {pngFiles.Length} textures from {folder}");

            foreach (string file in pngFiles)
            {
                try
                {
                    string assetName = Path.GetFileNameWithoutExtension(file);
                    Texture2D tex = LoadWithImageSharp(file, assetName);
                    if (tex != null)
                    {
                        TextureRegistry.Register(assetName, tex);
                        TextureReplacerPlugin.Log.LogInfo($"Registered: {assetName}");
                    }
                }
                catch (Exception ex)
                {
                    TextureReplacerPlugin.Log.LogError($"Failed to load {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            TextureReplacerPlugin.Log.LogInfo($"Loaded {TextureRegistry.Count} replacement textures");
        }

        private static Texture2D LoadWithImageSharp(string path, string name)
        {
            using (Image<Rgba32> image = ISImage.Load<Rgba32>(path))
            {
                image.Mutate(x => x.Flip(FlipMode.Vertical));
                byte[] pixelBytes = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixelBytes);

                var tex = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false);
                tex.name = name;
                tex.hideFlags = HideFlags.HideAndDontSave;

                var il2cppBytes = (Il2CppStructArray<byte>)pixelBytes;
                tex.LoadRawTextureData(il2cppBytes);
                tex.Apply(true, true);
                return tex;
            }
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
    // AssetBundle.LoadAsset Patches
    // =========================================================
    [HarmonyPatch]
    internal static class AssetBundlePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAsset), new[] { typeof(string), typeof(Il2CppSystem.Type) })]
        private static bool LoadAssetPrefix(string name, Il2CppSystem.Type type, ref Object __result)
        {
            if (type != Il2CppType.Of<Texture2D>()) return true;

            string normName = TextureNameUtil.Normalize(name);

            if (TextureRegistry.TryGet(normName, out var replacement))
            {
                __result = replacement;
                TextureReplacerPlugin.Log.LogInfo($"[ASSETBUNDLE] Replaced {normName}");
                return false;
            }

            return true;
        }
    }

    // =========================================================
    // Resources.Load Patches
    // =========================================================
    [HarmonyPatch]
    internal static class ResourcePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Il2CppSystem.Type) })]
        private static bool ResourcesLoadPrefix(string path, Il2CppSystem.Type systemTypeInstance, ref Object __result)
        {
            if (systemTypeInstance != Il2CppType.Of<Texture2D>()) return true;

            string normName = TextureNameUtil.Normalize(path);

            if (TextureRegistry.TryGet(normName, out var replacement))
            {
                __result = replacement;
                TextureReplacerPlugin.Log.LogInfo($"[RESOURCES] Replaced {normName}");
                return false;
            }

            return true;
        }
    }

    // =========================================================
    // Material.SetTexture Patches
    // =========================================================
    [HarmonyPatch]
    internal static class MaterialPatches
    {
        [HarmonyPatch(typeof(Material), nameof(Material.SetTexture), new[] { typeof(string), typeof(Texture) })]
        [HarmonyPrefix]
        private static void SetTexturePrefix(Material __instance, string name, ref Texture value)
        {
            if (value == null) return;
            string normName = TextureNameUtil.Normalize(value.name);
            if (TextureRegistry.TryGet(normName, out var replacement))
            {
                value = replacement;
                TextureReplacerPlugin.Log.LogInfo($"[MATERIAL.SET] Replaced {normName} in {__instance.name} (slot: {name})");
            }
        }

        [HarmonyPatch(typeof(Material), nameof(Material.SetTexture), new[] { typeof(int), typeof(Texture) })]
        [HarmonyPrefix]
        private static void SetTextureByIDPrefix(Material __instance, int nameID, ref Texture value)
        {
            if (value == null) return;
            string normName = TextureNameUtil.Normalize(value.name);
            if (TextureRegistry.TryGet(normName, out var replacement))
            {
                value = replacement;
                TextureReplacerPlugin.Log.LogInfo($"[MATERIAL.SET] Replaced {normName} in {__instance.name} (propertyID: {nameID})");
            }
        }

        [HarmonyPatch(typeof(Material), nameof(Material.mainTexture), MethodType.Setter)]
        [HarmonyPrefix]
        private static void MainTextureSetterPrefix(Material __instance, ref Texture value)
        {
            if (value == null) return;
            string normName = TextureNameUtil.Normalize(value.name);
            if (TextureRegistry.TryGet(normName, out var replacement))
            {
                value = replacement;
                TextureReplacerPlugin.Log.LogInfo($"[MATERIAL.MAINTEX] Replaced {normName} in {__instance.name}");
            }
        }
    }

    // =========================================================
    // MaterialPropertyBlock Patches (catches per-renderer overrides)
    // =========================================================
    [HarmonyPatch]
    internal static class MaterialPropertyBlockPatches
    {
        [HarmonyPatch(typeof(MaterialPropertyBlock), nameof(MaterialPropertyBlock.SetTexture), new[] { typeof(string), typeof(Texture) })]
        [HarmonyPrefix]
        private static void SetTexturePrefix(string name, ref Texture value)
        {
            if (value == null) return;
            string normName = TextureNameUtil.Normalize(value.name);
            if (TextureRegistry.TryGet(normName, out var replacement))
            {
                value = replacement;
                TextureReplacerPlugin.Log.LogInfo($"[MPB.SET] Replaced {normName} (slot: {name})");
            }
        }

        [HarmonyPatch(typeof(MaterialPropertyBlock), nameof(MaterialPropertyBlock.SetTexture), new[] { typeof(int), typeof(Texture) })]
        [HarmonyPrefix]
        private static void SetTextureByIDPrefix(int nameID, ref Texture value)
        {
            if (value == null) return;
            string normName = TextureNameUtil.Normalize(value.name);
            if (TextureRegistry.TryGet(normName, out var replacement))
            {
                value = replacement;
                TextureReplacerPlugin.Log.LogInfo($"[MPB.SET] Replaced {normName} (propertyID: {nameID})");
            }
        }
    }

    // =========================================================
    // FadeManager Patches
    // =========================================================
    [HarmonyPatch]
    internal static class FadePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BokuMono.FadeManager), "FadeOutAsync")]
        private static void FadeOutAsyncPostfix()
        {
            TextureReplacerPlugin.Log.LogInfo("[FADE] FadeOut detected. Triggering texture refresh...");
            var scanner = Object.FindObjectOfType<TextureScanner>();
            if (scanner != null)
                scanner.StartCoroutine(scanner.DelayedSwap(0.1f));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BokuMono.FadeManager), "FadeOut")]
        private static void FadeOutPostfix()
        {
            TextureReplacerPlugin.Log.LogInfo("[FADE] FadeOut detected. Triggering texture refresh...");
            var scanner = Object.FindObjectOfType<TextureScanner>();
            if (scanner != null)
                scanner.StartCoroutine(scanner.DelayedSwap(0.1f));
        }
    }

    // =========================================================
    // ResourceManager Patches
    // =========================================================
    [HarmonyPatch(typeof(BokuMono.ResourceManager), "LoadPrefab", new[] { typeof(string), typeof(Il2CppSystem.Action<GameObject>) })]
    internal static class ResourceManagerPatches
    {
        [HarmonyPrefix]
        private static void LoadPrefabPrefix(string __0, ref Il2CppSystem.Action<GameObject> __1)
        {
            if (string.IsNullOrEmpty(__0) || __1 == null) return;

            var originalCallback = __1;

            Action<GameObject> wrapper = (GameObject prefab) =>
            {
                if (prefab != null)
                    ProcessPrefabTextures(prefab);
                originalCallback.Invoke(prefab);
            };

            __1 = wrapper;
        }

        private static void ProcessPrefabTextures(GameObject prefab)
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                var mats = renderer.sharedMaterials;
                foreach (var mat in mats)
                {
                    if (mat == null) continue;

                    string[] slots = { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_DiffuseTexture", "_Texture", "_SkinTex" };
                    foreach (var slot in slots)
                    {
                        if (!mat.HasProperty(slot)) continue;

                        var tex = mat.GetTexture(slot);
                        if (tex == null) continue;

                        string normName = TextureNameUtil.Normalize(tex.name);

                        if (TextureRegistry.TryGet(normName, out var replacement))
                        {
                            mat.SetTexture(slot, replacement);
                            TextureReplacerPlugin.Log.LogInfo($"[PREFAB] Injected {normName} into {prefab.name} (slot: {slot})");
                        }
                    }
                }
            }
        }
    }
}

// CS0656 Fix
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