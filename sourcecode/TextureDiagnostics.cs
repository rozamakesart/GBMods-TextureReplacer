using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

// =========================================================
// Texture Diagnostics Plugin
//
// A standalone observer plugin. It does NOT replace any textures.
// Drop it alongside (or without) the main TextureReplacer plugin.
//
// Hotkeys:
//   F6  — toggle diagnostic logging on/off
//   F7  — dump all renderers in the scene and their texture names
//
// Configuration:
//   Set TargetTexture to the normalized texture name you want to track
//   (filename without extension, no path, no " (Instance)" suffix).
// =========================================================
namespace TextureDiagnostics
{
    [BepInPlugin("com.yourname.texturediagnostics", "Texture Diagnostics", "1.0.0")]
    public class TextureDiagnosticsPlugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        internal static Harmony Harmony;

        /// <summary>
        /// The normalized texture name to track through the pipeline.
        /// Change this to whichever texture you're hunting.
        /// </summary>
        public static string TargetTexture = "tex_chr_npc_M_000_o";

        /// <summary>
        /// Whether verbose diagnostic logging is currently active.
        /// Toggle at runtime with F6.
        /// </summary>
        public static bool IsEnabled = true;

        public override void Load()
        {
            Log = base.Log;

            try { ClassInjector.RegisterTypeInIl2Cpp<DiagnosticMonitor>(); }
            catch { Log.LogWarning("DiagnosticMonitor already registered"); }

            Harmony = new Harmony("com.yourname.texturediagnostics");
            Harmony.PatchAll(typeof(DiagAssetBundlePatches));
            Harmony.PatchAll(typeof(DiagResourcePatches));
            Harmony.PatchAll(typeof(DiagMaterialPatches));
            Harmony.PatchAll(typeof(DiagMaterialPropertyBlockPatches));

            var go = new GameObject("TextureDiagnosticsMonitor");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<DiagnosticMonitor>();

            Log.LogInfo($"Texture Diagnostics plugin loaded. Tracking: '{TargetTexture}'");
            Log.LogInfo("  F6 = toggle logging | F7 = full scene texture dump");
        }
    }

    // =========================================================
    // Runtime monitor: hotkeys + scene dump
    // =========================================================
    public class DiagnosticMonitor : MonoBehaviour
    {
        public DiagnosticMonitor(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            // F6: toggle diagnostic logging
            if (Input.GetKeyDown(KeyCode.F6))
            {
                TextureDiagnosticsPlugin.IsEnabled = !TextureDiagnosticsPlugin.IsEnabled;
                TextureDiagnosticsPlugin.Log.LogWarning(
                    $"[DIAG] Logging {(TextureDiagnosticsPlugin.IsEnabled ? "ENABLED" : "DISABLED")} for target '{TextureDiagnosticsPlugin.TargetTexture}'");
            }

            // F7: full scene texture dump
            if (Input.GetKeyDown(KeyCode.F7))
                DumpSceneTextures();
        }

        /// <summary>
        /// Walks every Renderer in the scene and logs every texture it finds,
        /// with a highlighted warning for any that match the target.
        /// </summary>
        private static void DumpSceneTextures()
        {
            var log = TextureDiagnosticsPlugin.Log;
            string target = TextureDiagnosticsPlugin.TargetTexture;

            var allRenderers = GameObject.FindObjectsOfType<Renderer>(true);
            log.LogInfo($"[DIAG DUMP] Scanning {allRenderers.Length} renderers in scene...");

            string[] slots = { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_DiffuseTexture", "_Texture", "_SkinTex" };
            int total = 0;

            foreach (var renderer in allRenderers)
            {
                if (renderer == null) continue;

                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;

                    foreach (var slot in slots)
                    {
                        if (!mat.HasProperty(slot)) continue;

                        var tex = mat.GetTexture(slot);
                        if (tex == null) continue;

                        string normName = DiagTextureNameUtil.Normalize(tex.name);
                        total++;

                        if (normName == target)
                        {
                            // Highlight the texture we're hunting
                            log.LogWarning($"[DIAG DUMP] *** TARGET FOUND ***");
                            log.LogWarning($"[DIAG DUMP]   GameObject : {renderer.gameObject.name}");
                            log.LogWarning($"[DIAG DUMP]   Renderer   : {renderer.GetType().Name}");
                            log.LogWarning($"[DIAG DUMP]   Material   : {mat.name}");
                            log.LogWarning($"[DIAG DUMP]   Slot       : {slot}");
                            log.LogWarning($"[DIAG DUMP]   Tex name   : {normName}  (raw: {tex.name})");
                            log.LogWarning($"[DIAG DUMP]   InstanceID : {tex.GetInstanceID()}");
                        }
                        else
                        {
                            log.LogInfo($"[DIAG DUMP] {renderer.gameObject.name} | {mat.name} | {slot} => {normName}");
                        }
                    }
                }
            }

            log.LogInfo($"[DIAG DUMP] Done. {total} texture slots scanned.");
        }
    }

    // =========================================================
    // Utility (self-contained copy — no dependency on TextureReplacer)
    // =========================================================
    internal static class DiagTextureNameUtil
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
    // Observer patches — read-only, never modify __result or ref args
    // =========================================================

    /// <summary>
    /// Logs when the target texture is requested from an AssetBundle,
    /// and what was actually returned (postfix), so you can see whether
    /// another patch (e.g. the TextureReplacer) intercepted it first.
    /// </summary>
    [HarmonyPatch]
    internal static class DiagAssetBundlePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAsset), new[] { typeof(string), typeof(Il2CppSystem.Type) })]
        private static void LoadAssetPrefix(string name, Il2CppSystem.Type type)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled) return;
            if (type != Il2CppType.Of<Texture2D>()) return;

            string normName = DiagTextureNameUtil.Normalize(name);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] AssetBundle.LoadAsset requested: {normName}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Stack: {System.Environment.StackTrace}");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadAsset), new[] { typeof(string), typeof(Il2CppSystem.Type) })]
        private static void LoadAssetPostfix(string name, Il2CppSystem.Type type, Object __result)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled) return;
            if (type != Il2CppType.Of<Texture2D>()) return;

            string normName = DiagTextureNameUtil.Normalize(name);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            if (__result == null)
                TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] AssetBundle.LoadAsset returned NULL for {normName}");
            else
                TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] AssetBundle.LoadAsset returned: {__result.name} (ID: {__result.GetInstanceID()})");
        }
    }

    /// <summary>
    /// Logs when the target texture is requested via Resources.Load,
    /// and what was actually returned after all patches ran.
    /// </summary>
    [HarmonyPatch]
    internal static class DiagResourcePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Il2CppSystem.Type) })]
        private static void ResourcesLoadPrefix(string path, Il2CppSystem.Type systemTypeInstance)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled) return;
            if (systemTypeInstance != Il2CppType.Of<Texture2D>()) return;

            string normName = DiagTextureNameUtil.Normalize(path);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] Resources.Load requested: {normName}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Stack: {System.Environment.StackTrace}");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Resources), nameof(Resources.Load), new[] { typeof(string), typeof(Il2CppSystem.Type) })]
        private static void ResourcesLoadPostfix(string path, Il2CppSystem.Type systemTypeInstance, Object __result)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled) return;
            if (systemTypeInstance != Il2CppType.Of<Texture2D>()) return;

            string normName = DiagTextureNameUtil.Normalize(path);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            if (__result == null)
                TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] Resources.Load returned NULL for {normName}");
            else
                TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] Resources.Load returned: {__result.name} (ID: {__result.GetInstanceID()})");
        }
    }

    /// <summary>
    /// Logs every Material.SetTexture and mainTexture assignment involving
    /// the target, including what value arrived (before any prefix patches
    /// from other plugins may have modified it — this plugin runs last
    /// because it's a separate Harmony instance loaded after).
    /// </summary>
    [HarmonyPatch]
    internal static class DiagMaterialPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Material), nameof(Material.SetTexture), new[] { typeof(string), typeof(Texture) })]
        private static void SetTexturePrefix(Material __instance, string name, Texture value)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled || value == null) return;
            string normName = DiagTextureNameUtil.Normalize(value.name);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] Material.SetTexture('{name}') => {normName}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Material: {__instance.name} | TexID: {value.GetInstanceID()}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Stack: {System.Environment.StackTrace}");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Material), nameof(Material.SetTexture), new[] { typeof(int), typeof(Texture) })]
        private static void SetTextureByIDPrefix(Material __instance, int nameID, Texture value)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled || value == null) return;
            string normName = DiagTextureNameUtil.Normalize(value.name);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] Material.SetTexture(ID:{nameID}) => {normName}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Material: {__instance.name} | TexID: {value.GetInstanceID()}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Stack: {System.Environment.StackTrace}");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Material), nameof(Material.mainTexture), MethodType.Setter)]
        private static void MainTextureSetterPrefix(Material __instance, Texture value)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled || value == null) return;
            string normName = DiagTextureNameUtil.Normalize(value.name);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] Material.mainTexture setter => {normName}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Material: {__instance.name} | TexID: {value.GetInstanceID()}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Stack: {System.Environment.StackTrace}");
        }
    }

    /// <summary>
    /// Logs MaterialPropertyBlock.SetTexture calls involving the target texture.
    /// </summary>
    [HarmonyPatch]
    internal static class DiagMaterialPropertyBlockPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaterialPropertyBlock), nameof(MaterialPropertyBlock.SetTexture), new[] { typeof(string), typeof(Texture) })]
        private static void SetTexturePrefix(string name, Texture value)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled || value == null) return;
            string normName = DiagTextureNameUtil.Normalize(value.name);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] MaterialPropertyBlock.SetTexture('{name}') => {normName}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   TexID: {value.GetInstanceID()}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Stack: {System.Environment.StackTrace}");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaterialPropertyBlock), nameof(MaterialPropertyBlock.SetTexture), new[] { typeof(int), typeof(Texture) })]
        private static void SetTextureByIDPrefix(int nameID, Texture value)
        {
            if (!TextureDiagnosticsPlugin.IsEnabled || value == null) return;
            string normName = DiagTextureNameUtil.Normalize(value.name);
            if (normName != TextureDiagnosticsPlugin.TargetTexture) return;

            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG] MaterialPropertyBlock.SetTexture(ID:{nameID}) => {normName}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   TexID: {value.GetInstanceID()}");
            TextureDiagnosticsPlugin.Log.LogWarning($"[DIAG]   Stack: {System.Environment.StackTrace}");
        }
    }
}
