using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Component = UnityEngine.Component;

namespace DemosaicPlugin
{
    public enum RemoveMode
    {
        Disable,
        Transparent,
        Smart
    }

    [BepInPlugin("demosaic", "Demosaic", "1.5.0")]
    public class DemosaicPlugin : BasePlugin
    {
        public static DemosaicPlugin Instance { get; private set; }
        internal static ManualLogSource Logger { get; private set; }

        private Harmony harmony;
        private MosaicDetector mosaicDetector;
        private MosaicProcessor mosaicProcessor;
        private PluginLifecycleManager lifecycleManager;

        // 配置项
        private ConfigEntry<bool> enablePlugin;
        private ConfigEntry<RemoveMode> removeMode;
        private ConfigEntry<KeyCode> forceScanHotkey;
        private ConfigEntry<bool> includeInactiveObjects;
        private ConfigEntry<bool> detectParentObjectNames;
        private ConfigEntry<bool> logProcessedObjects;
        internal KeyCode ForceScanHotkeyValue => forceScanHotkey.Value;
        internal ConfigEntry<float> periodicScanInterval;
        internal ConfigEntry<float> sceneLoadScanDelay;
        internal ConfigEntry<int> scanBatchSize;
        internal int ScanBatchSizeValue => Math.Max(1, scanBatchSize.Value);
        internal float SceneLoadScanDelayValue => Math.Max(0f, sceneLoadScanDelay.Value);
        internal bool IncludeInactiveObjectsValue => includeInactiveObjects.Value;

        private ConfigEntry<string> objectKeywords;
        private ConfigEntry<string> materialKeywords;
        private ConfigEntry<string> textureKeywords;
        private ConfigEntry<string> shaderKeywords;
        private ConfigEntry<string> meshKeywords;
        private ConfigEntry<string> exclusionKeywords;
        private ConfigEntry<string> componentNameKeywords;
        private ConfigEntry<string> shaderPropertyKeywords;

        private ConfigEntry<bool> disableMethods;
        private ConfigEntry<string> methodDisableKeywords;
        private ConfigEntry<string> methodExcludeKeywords;
        private ConfigEntry<string> methodPatchTargetAssemblies;

        // Camera 后处理检测
        private ConfigEntry<bool> enableCameraEffectDetection;
        private ConfigEntry<string> cameraEffectKeywords;

        // Decal/Projector 检测
        private ConfigEntry<bool> enableDecalDetection;

        // 材质 setter Hook
        private ConfigEntry<bool> enableMaterialSetterHook;

        // 新增：导出场景键
        internal ConfigEntry<KeyCode> exportSceneKey;

        // 缓存
        private List<string> objectKeywordList;
        private List<string> materialKeywordList;
        private List<string> textureKeywordList;
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private List<string> componentNameKeywordList;
        private List<string> shaderPropertyKeywordList;
        private List<string> methodDisableKeywordList;
        private List<string> methodExcludeKeywordList;
        private List<string> cameraEffectKeywordList;

        private readonly HashSet<int> processedRendererIds = new HashSet<int>();
        private readonly HashSet<int> processedCameraEffectIds = new HashSet<int>();
        private readonly HashSet<int> processedDecalIds = new HashSet<int>();
        private static bool _isProcessingMaterial = false; // 防止材质 setter hook 递归
        private static int _lastMaterialHookFrame = -1;
        private static readonly HashSet<int> _materialHookFrameDedup = new HashSet<int>();
        private Material transparentMaterial;
        private Shader transparentShader;
        private bool transparentShaderIsURP;

        public override void Load()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { }

            Instance = this;
            Logger = Log;

            SetupConfiguration();

            if (!enablePlugin.Value) return;

            ClassInjector.RegisterTypeInIl2Cpp<PluginLifecycleManager>();

            mosaicDetector = new MosaicDetector(Logger);
            ReloadAllKeywords();

            CreateTransparentMaterial();
            mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger, logProcessedObjects.Value, materialKeywordList, shaderKeywordList, mosaicDetector);

            lifecycleManager = AddComponent<PluginLifecycleManager>();
            Logger.LogInfo($"Demosaic加载成功！去除方式: {removeMode.Value}");

            try
            {
                harmony = new Harmony("demosaic");
                harmony.PatchAll(typeof(DemosaicPlugin).Assembly);
                ApplyInstantiatePatches(harmony);
                ApplyMaterialSetterPatches(harmony);
                Logger.LogInfo("Harmony 补丁应用成功，已启用实时对象拦截。");

                if (disableMethods.Value)
                    PatchMethodsByName(harmony);
            }
            catch (Exception e)
            {
                Logger.LogError("Harmony 补丁应用失败: " + e);
            }
        }

        private void SetupConfiguration()
        {
            enablePlugin = Config.Bind("General", "Enable", true, "是否启用去马赛克插件");
            removeMode = Config.Bind("Remove", "Mode", RemoveMode.Smart, "去除方式：Disable=禁用GameObject，Transparent=替换全部材质为透明，Smart=仅替换马赛克材质槽为透明（保留同对象上的非马赛克材质）");
            forceScanHotkey = Config.Bind("General", "ForceScanHotkey", KeyCode.F10, "按下此快捷键可强制重新扫描");
            exportSceneKey = Config.Bind("General", "ExportSceneKey", KeyCode.F11, "按下此键将场景中所有渲染器信息导出到日志");
            includeInactiveObjects = Config.Bind("General", "IncludeInactiveObjects", true, "扫描时是否包含未激活对象。Unity 2022+ 会优先使用更快的 FindObjectsByType。");
            detectParentObjectNames = Config.Bind("General", "DetectParentObjectNames", true, "检测 Renderer 的父节点名称，适合 MosaicRoot/Quad 这类层级。");
            logProcessedObjects = Config.Bind("General", "LogProcessedObjects", false, "是否为每个被处理对象写 Info 日志。大量对象会明显影响性能。");

            periodicScanInterval = Config.Bind("Scan", "PeriodicScanInterval", 10f, "定期场景扫描的间隔（秒）。设置为0可禁用。");
            sceneLoadScanDelay = Config.Bind("Scan", "SceneLoadScanDelay", 1.5f, "新场景加载后延迟扫描的时间（秒）。");
            scanBatchSize = Config.Bind("Scan", "ScanBatchSize", 500, "全场景扫描时每帧处理的对象数量，防止卡顿。");

            objectKeywords = Config.Bind("Detection", "ObjectNameKeywords", "mosaic,censored,pixelated,mozic,mazic,mozaic", "游戏对象名称的关键词，逗号分隔");
            materialKeywords = Config.Bind("Detection", "MaterialNameKeywords", "mosaic,censored,pixel,mozic,mazic", "材质名称的关键词，逗号分隔");
            textureKeywords = Config.Bind("Detection", "TextureKeywords", "mosaic", "纹理名称的关键词，逗号分隔");
            shaderKeywords = Config.Bind("Detection", "ShaderNameKeywords", "mosaic,pixelate,censor,moza,mozic,mazic,mozaic", "着色器名称的关键词，逗号分隔");
            meshKeywords = Config.Bind("Detection", "MeshNameKeywords", "censor,mosaic,moza,mozic,mazic,mozaic", "网格名称的关键词，逗号分隔");
            componentNameKeywords = Config.Bind("Detection", "ComponentNameKeywords", "", "组件名称的关键词，逗号分隔");
            shaderPropertyKeywords = Config.Bind("Detection", "ShaderPropertyKeywords", "_PixelSize,_BlockSize,_MosaicFactor", "着色器属性名称的关键词，逗号分隔");
            exclusionKeywords = Config.Bind("Detection", "ExclusionKeywords", "", "白名单关键词（最高优先级），逗号分隔");

            disableMethods = Config.Bind("Advanced", "DisableMethods", false, "是否启用方法名拦截（反射扫描，谨慎使用）");
            methodDisableKeywords = Config.Bind("Advanced", "MethodDisableKeywords", "censor,mosaic", "方法名拦截关键词");
            methodExcludeKeywords = Config.Bind("Advanced", "MethodExcludeKeywords", "remove,destroy,clear,disable,hide,off,delete,undo,stop,cancel", "方法名排除词，同时命中排除词的方法不会被拦截（防止误杀去除马赛克的方法）");
            methodPatchTargetAssemblies = Config.Bind("Advanced", "MethodPatchTargetAssemblies", "Assembly-CSharp", "需要进行方法扫描的目标程序集名称，逗号分隔");

            enableCameraEffectDetection = Config.Bind("Advanced", "EnableCameraEffectDetection", true, "是否启用 Camera 后处理组件检测，禁用匹配关键词的后处理效果");
            cameraEffectKeywords = Config.Bind("Advanced", "CameraEffectKeywords", "mosaic,censor,pixelat,moza,mozic,mazic", "Camera 后处理组件名关键词，匹配的 MonoBehaviour 将被禁用");
            enableDecalDetection = Config.Bind("Advanced", "EnableDecalDetection", true, "是否启用 Projector/DecalProjector 检测，禁用材质匹配马赛克关键词的投影/贴花");
            enableMaterialSetterHook = Config.Bind("Advanced", "EnableMaterialSetterHook", true, "是否启用 Renderer 材质 setter Hook，实时捕获动态材质变更（如玩具激活时动态添加马赛克）");

            Config.SettingChanged += OnSettingChanged;
        }

        private void ReloadAllKeywords()
        {
            objectKeywordList = ParseKeywordString(objectKeywords.Value);
            materialKeywordList = ParseKeywordString(materialKeywords.Value);
            textureKeywordList = ParseKeywordString(textureKeywords.Value);
            shaderKeywordList = ParseKeywordString(shaderKeywords.Value);
            meshKeywordList = ParseKeywordString(meshKeywords.Value);
            exclusionKeywordList = ParseKeywordString(exclusionKeywords.Value);
            componentNameKeywordList = ParseKeywordString(componentNameKeywords.Value);
            shaderPropertyKeywordList = ParseKeywordString(shaderPropertyKeywords.Value);

            mosaicDetector.UpdateKeywords(
                objectKeywordList, materialKeywordList, textureKeywordList,
                shaderKeywordList, meshKeywordList,
                exclusionKeywordList, componentNameKeywordList,
                shaderPropertyKeywordList,
                detectParentObjectNames.Value
            );

            methodDisableKeywordList = ParseKeywordString(methodDisableKeywords.Value);
            methodExcludeKeywordList = ParseKeywordString(methodExcludeKeywords.Value);
            cameraEffectKeywordList = ParseKeywordString(cameraEffectKeywords.Value);
        }

        private void CreateTransparentMaterial()
        {
            // 按优先级尝试多种着色器，提升不同渲染管线和裁剪配置下的兼容性
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            transparentShaderIsURP = shader != null;

            if (shader == null)
            {
                shader = Shader.Find("HDRP/Lit");
                if (shader == null)
                    shader = Shader.Find("HD Render Pipeline/Lit");
            }

            // Built-in RP 常见着色器（部分游戏会裁剪 Standard）
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Standard (Specular setup)");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            if (shader != null)
            {
                transparentShader = shader;
                transparentMaterial = CreateTransparentMaterialFromShader(shader, transparentShaderIsURP);
                Logger.LogInfo($"透明材质创建成功，使用着色器: {shader.name}");
            }
            else
            {
                Logger.LogError("未能找到任何可用着色器，透明模式将无法正常工作。");
            }
        }

        private Material CreateTransparentMaterialFromShader(Shader shader, bool isURP)
        {
            var mat = new Material(shader);
            // 防止 Unity GC 回收
            mat.hideFlags = HideFlags.HideAndDontSave;

            string shaderName = shader.name;
            if (isURP)
            {
                mat.SetFloat("_Surface", 1);
                mat.SetFloat("_Blend", 0);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            else if (shaderName == "Sprites/Default")
            {
                // Sprites/Default 不支持 _Mode/_Surface 等属性，直接设置颜色 Alpha 为 0
                mat.color = Color.clear;
                mat.renderQueue = 3000;
                return mat;
            }
            else if (shaderName.Contains("Unlit"))
            {
                // Unlit 系列着色器无光照属性，仅设置混合模式
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
            }
            else
            {
                // Standard / Standard (Specular setup) / HDRP
                mat.SetFloat("_Mode", 3);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            mat.renderQueue = 3000;
            mat.color = Color.clear;
            return mat;
        }

        /// <summary>
        /// 确保透明材质可用。如果材质被 GC 回收则自动重建。
        /// </summary>
        internal Material EnsureTransparentMaterial()
        {
            if (transparentMaterial == null && transparentShader != null)
            {
                transparentMaterial = CreateTransparentMaterialFromShader(transparentShader, transparentShaderIsURP);
                Logger.LogWarning("透明材质被回收，已自动重建。");
            }
            return transparentMaterial;
        }

        private List<string> ParseKeywordString(string keywordString)
        {
            return keywordString.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        internal void NotifySceneUnloaded()
        {
            processedRendererIds.Clear();
            processedCameraEffectIds.Clear();
            processedDecalIds.Clear();
            mosaicDetector.ClearCache();
        }

        internal void RequestFullScan()
        {
            processedRendererIds.Clear();
            processedCameraEffectIds.Clear();
            processedDecalIds.Clear();
            mosaicDetector.ClearCache();
            lifecycleManager?.RestartBatchScan();
        }

        internal bool ProcessRenderer(Renderer renderer)
        {
            if (renderer == null) return false;

            int rendererId = renderer.GetInstanceID();
            if (processedRendererIds.Contains(rendererId)) return false;

            if (mosaicDetector.IsMosaic(renderer))
            {
                mosaicProcessor.Process(renderer);
                processedRendererIds.Add(rendererId);
                return true;
            }
            return false;
        }

        public void ProcessNewGameObject(GameObject go)
        {
            if (go == null) return;

            // 快速检查以避免对没有渲染器的GameObject层级分配数组
            var hasRenderer = go.GetComponentInChildren<Renderer>(true);
            if (hasRenderer == null) return;

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                ProcessRenderer(renderer);
            }
        }

        /// <summary>
        /// 扫描场景中所有 Camera 上的 MonoBehaviour 组件，禁用名称匹配关键词的后处理效果。
        /// </summary>
        internal void ProcessCameraEffects()
        {
            if (!enableCameraEffectDetection.Value || cameraEffectKeywordList == null || cameraEffectKeywordList.Count == 0)
                return;

            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            foreach (var cam in cameras)
            {
                if (cam == null) continue;
                var components = cam.gameObject.GetComponents<MonoBehaviour>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    int compId = comp.GetInstanceID();
                    if (processedCameraEffectIds.Contains(compId)) continue;

                    string typeName = comp.GetIl2CppType().Name;
                    bool match = false;
                    foreach (var keyword in cameraEffectKeywordList)
                    {
                        if (typeName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            match = true;
                            break;
                        }
                    }

                    if (match)
                    {
                        comp.enabled = false;
                        processedCameraEffectIds.Add(compId);
                        Logger.LogInfo($"已禁用 Camera 后处理组件: {typeName} (on {cam.gameObject.name})");
                    }
                }
            }
        }

        /// <summary>
        /// 扫描场景中的 Projector（Built-in RP）和 DecalProjector（URP）组件，
        /// 检查其材质是否匹配马赛克关键词，匹配则禁用。
        /// </summary>
        internal void ProcessDecalsAndProjectors()
        {
            if (!enableDecalDetection.Value) return;

            // 1. 检测 Built-in RP 的 Projector 组件
            try
            {
                var projectors = UnityEngine.Object.FindObjectsOfType<Projector>();
                foreach (var proj in projectors)
                {
                    if (proj == null) continue;
                    int projId = proj.GetInstanceID();
                    if (processedDecalIds.Contains(projId)) continue;

                    var mat = proj.material;
                    if (mat != null && mosaicDetector.CheckMaterialFull(mat))
                    {
                        proj.enabled = false;
                        processedDecalIds.Add(projId);
                        Logger.LogInfo($"已禁用 Projector: {proj.gameObject.name} (材质: {mat.name})");
                    }
                }
            }
            catch (Exception) { /* Projector 可能不存在于某些 URP/HDRP 环境 */ }

            // 2. 检测 URP DecalProjector（通过反射，因为编译时可能无引用）
            try
            {
                var decalType = Type.GetType("UnityEngine.Rendering.Universal.DecalProjector, Unity.RenderPipelines.Universal.Runtime");
                if (decalType == null)
                    decalType = Type.GetType("UnityEngine.Rendering.Universal.DecalProjector, Unity.RenderPipelines.Universal");

                if (decalType != null)
                {
                    var decals = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(decalType));
                    var matProp = decalType.GetProperty("material") ?? decalType.GetProperty("m_Material");

                    if (decals != null && matProp != null)
                    {
                        foreach (var decalObj in decals)
                        {
                            if (decalObj == null) continue;
                            int decalId = decalObj.GetInstanceID();
                            if (processedDecalIds.Contains(decalId)) continue;

                            var behaviour = decalObj.TryCast<MonoBehaviour>();
                            if (behaviour == null) continue;

                            var mat = matProp.GetValue(behaviour) as Material;
                            if (mat != null && mosaicDetector.CheckMaterialFull(mat))
                            {
                                behaviour.enabled = false;
                                processedDecalIds.Add(decalId);
                                Logger.LogInfo($"已禁用 DecalProjector: {behaviour.gameObject.name} (材质: {mat.name})");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug($"DecalProjector 检测跳过: {ex.Message}");
            }
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e.ChangedSetting.Definition.Key.Contains("Keywords"))
            {
                ReloadAllKeywords();
                // Smart 模式下关键词变更也需要重建处理器
                if (removeMode.Value == RemoveMode.Smart)
                    mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger, logProcessedObjects.Value, materialKeywordList, shaderKeywordList, mosaicDetector);
                RequestFullScan();
            }
            else if (e.ChangedSetting.Definition.Key == removeMode.Definition.Key)
            {
                mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger, logProcessedObjects.Value, materialKeywordList, shaderKeywordList, mosaicDetector);
                RequestFullScan();
                Logger.LogInfo($"处理模式已更新为: {removeMode.Value}。");
            }
            else if (e.ChangedSetting.Definition.Key == logProcessedObjects.Definition.Key)
            {
                mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger, logProcessedObjects.Value, materialKeywordList, shaderKeywordList, mosaicDetector);
            }
        }

        private void ApplyInstantiatePatches(Harmony harmonyInstance)
        {
            var postfix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(InstantiatePatch));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(UnityEngine.Object))
                .Where(method => method.Name == nameof(UnityEngine.Object.Instantiate) &&
                                 !method.IsGenericMethodDefinition &&
                                 !method.ContainsGenericParameters))
            {
                try
                {
                    harmonyInstance.Patch(method, postfix: postfix);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug($"跳过 Instantiate 重载补丁 {method}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Hook Renderer 的材质 setter，实时捕获动态材质变更（如玩具激活时动态添加马赛克）。
        /// </summary>
        private void ApplyMaterialSetterPatches(Harmony harmonyInstance)
        {
            var postfix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(MaterialSetterPostfix));
            int patched = 0;

            // Patch Renderer.set_material
            var setMaterial = AccessTools.PropertySetter(typeof(Renderer), "material");
            if (setMaterial != null)
            {
                try { harmonyInstance.Patch(setMaterial, postfix: postfix); patched++; }
                catch (Exception ex) { Logger.LogDebug($"Renderer.material setter patch 失败: {ex.Message}"); }
            }

            // Patch Renderer.set_sharedMaterial
            var setSharedMaterial = AccessTools.PropertySetter(typeof(Renderer), "sharedMaterial");
            if (setSharedMaterial != null)
            {
                try { harmonyInstance.Patch(setSharedMaterial, postfix: postfix); patched++; }
                catch (Exception ex) { Logger.LogDebug($"Renderer.sharedMaterial setter patch 失败: {ex.Message}"); }
            }

            // Patch Renderer.set_materials
            var setMaterials = AccessTools.PropertySetter(typeof(Renderer), "materials");
            if (setMaterials != null)
            {
                try { harmonyInstance.Patch(setMaterials, postfix: postfix); patched++; }
                catch (Exception ex) { Logger.LogDebug($"Renderer.materials setter patch 失败: {ex.Message}"); }
            }

            // Patch Renderer.set_sharedMaterials
            var setSharedMaterials = AccessTools.PropertySetter(typeof(Renderer), "sharedMaterials");
            if (setSharedMaterials != null)
            {
                try { harmonyInstance.Patch(setSharedMaterials, postfix: postfix); patched++; }
                catch (Exception ex) { Logger.LogDebug($"Renderer.sharedMaterials setter patch 失败: {ex.Message}"); }
            }

            if (patched > 0)
                Logger.LogInfo($"已 Hook {patched} 个 Renderer 材质 setter，可实时捕获动态材质变更。");
        }

        /// <summary>
        /// 材质 setter 的 Postfix：当游戏代码动态设置材质时，立即检测并处理马赛克。
        /// </summary>
        private static void MaterialSetterPostfix(Renderer __instance)
        {
            if (Instance == null || _isProcessingMaterial) return;
            if (__instance == null) return;
            if (!Instance.enableMaterialSetterHook.Value) return;

            try
            {
                int rendererId = __instance.GetInstanceID();

                // 帧内去重：同一帧内同一 Renderer 只处理一次
                int currentFrame = Time.frameCount;
                if (currentFrame != _lastMaterialHookFrame)
                {
                    _lastMaterialHookFrame = currentFrame;
                    _materialHookFrameDedup.Clear();
                }
                if (!_materialHookFrameDedup.Add(rendererId)) return;

                // 从已处理集中移除，允许重新检测（因为材质变了）
                Instance.processedRendererIds.Remove(rendererId);

                // 检测新材质是否为马赛克
                if (Instance.mosaicDetector.IsMosaic(__instance))
                {
                    _isProcessingMaterial = true;
                    Instance.mosaicProcessor.Process(__instance);
                    Instance.processedRendererIds.Add(rendererId);
                    _isProcessingMaterial = false;
                }
            }
            catch (Exception ex)
            {
                _isProcessingMaterial = false;
                Logger.LogDebug($"材质 setter hook 处理异常: {ex.Message}");
            }
        }

        private static void InstantiatePatch(UnityEngine.Object __result)
        {
            if (Instance == null) return;
            try
            {
                if (__result != null)
                {
                    var go = __result.TryCast<GameObject>();
                    if (go != null)
                    {
                        Instance.ProcessNewGameObject(go);
                    }
                    else
                    {
                        var comp = __result.TryCast<Component>();
                        if (comp != null)
                        {
                            var compGo = comp.gameObject;
                            if (compGo != null)
                            {
                                Instance.ProcessNewGameObject(compGo);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"处理实例化对象错误: {ex}");
            }
        }

        private void PatchMethodsByName(Harmony harmonyInstance)
        {
            if (methodDisableKeywordList == null || methodDisableKeywordList.Count == 0) return;

            Logger.LogInfo("开始动态扫描并禁用匹配关键词的方法...");
            int patchedCount = 0;
            int skippedByExclusion = 0;

            var targetAssemblyNames = new HashSet<string>(
                methodPatchTargetAssemblies.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var assembliesToScan = AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => !asm.IsDynamic && targetAssemblyNames.Contains(asm.GetName().Name))
                .ToList();

            foreach (var assembly in assembliesToScan)
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                               BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (method.IsSpecialName || method.IsGenericMethod || method.ContainsGenericParameters ||
                                method.IsAbstract || method.ReturnType != typeof(void))
                            {
                                continue;
                            }

                            string methodName = method.Name;

                            // 检查是否命中拦截关键词
                            bool matchesKeyword = methodDisableKeywordList.Any(keyword =>
                                methodName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!matchesKeyword) continue;

                            // 检查是否命中排除词（防止误杀 RemoveMosaic 等方法）
                            bool matchesExclusion = methodExcludeKeywordList != null &&
                                methodExcludeKeywordList.Any(excl =>
                                    methodName.IndexOf(excl, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (matchesExclusion)
                            {
                                skippedByExclusion++;
                                Logger.LogDebug($"排除跳过: {type.FullName}.{methodName}");
                                continue;
                            }

                            try
                            {
                                harmonyInstance.Patch(method,
                                    new HarmonyMethod(typeof(GenericDisablePatch), nameof(GenericDisablePatch.Prefix)));
                                patchedCount++;
                                Logger.LogDebug($"已拦截: {type.FullName}.{methodName}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogDebug($"拦截失败: {type.FullName}.{methodName} - {ex.Message}");
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            Logger.LogInfo($"动态方法扫描完成，共禁用 {patchedCount} 个方法，排除跳过 {skippedByExclusion} 个。");
        }

        public override bool Unload()
        {
            harmony?.UnpatchSelf();
            Config.SettingChanged -= OnSettingChanged;
            if (lifecycleManager != null)
                GameObject.Destroy(lifecycleManager.gameObject);

            if (transparentMaterial != null)
                UnityEngine.Object.Destroy(transparentMaterial);

            processedRendererIds.Clear();
            processedCameraEffectIds.Clear();
            processedDecalIds.Clear();

            Instance = null;
            Logger.LogInfo("Demosaic 插件已卸载。");
            return base.Unload();
        }
    }

    // =================================================================================
    // 马赛克检测器 (纹理检测已安全禁用，避免 AccessViolationException)
    // =================================================================================
    public class MosaicDetector
    {
        private List<string> objectKeywordList;
        private List<string> materialKeywordList;
        private List<string> textureKeywordList;        // 保留变量，但实际不再使用纹理检测
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private List<string> componentNameKeywordList;
        private List<string> shaderPropertyKeywordList;
        private bool detectParentObjectNames;
        private readonly ManualLogSource logger;

        private Dictionary<int, bool> materialCache = new Dictionary<int, bool>();
        private Dictionary<int, bool> shaderCache = new Dictionary<int, bool>();
        private Dictionary<string, bool> componentTypeCache = new Dictionary<string, bool>();
        private Dictionary<int, bool?> parentHierarchyCache = new Dictionary<int, bool?>();

        public MosaicDetector(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public void UpdateKeywords(
            List<string> objKeywords, List<string> matKeywords, List<string> texKeywords,
            List<string> shadKeywords, List<string> mshKeywords,
            List<string> exclKeywords, List<string> compKeywords,
            List<string> shadPropKeywords,
            bool detectParentObjectNames)
        {
            objectKeywordList = objKeywords;
            materialKeywordList = matKeywords;
            textureKeywordList = texKeywords;
            shaderKeywordList = shadKeywords;
            meshKeywordList = mshKeywords;
            exclusionKeywordList = exclKeywords;
            componentNameKeywordList = compKeywords;
            shaderPropertyKeywordList = shadPropKeywords;
            this.detectParentObjectNames = detectParentObjectNames;
        }

        public bool IsMaterialCached(Material mat, out bool isMosaic)
        {
            isMosaic = false;
            if (mat == null) return false;
            return materialCache.TryGetValue(mat.GetInstanceID(), out isMosaic);
        }

        /// <summary>
        /// 完整检测材质是否为马赛克（含材质名/Shader名/Shader属性），并缓存结果。
        /// 供 Processor 的 Smart 模式调用，确保判定维度与检测器一致。
        /// </summary>
        public bool CheckMaterialFull(Material mat)
        {
            if (mat == null) return false;
            int matId = mat.GetInstanceID();
            if (materialCache.TryGetValue(matId, out bool cached))
                return cached;
            bool result = CheckMaterialIsMosaic(mat);
            materialCache[matId] = result;
            return result;
        }

        public bool IsObjectNameOrParentMosaic(GameObject go)
        {
            if (go == null) return false;
            string goName = go.name;
            if (NameContainsKeyword(goName, exclusionKeywordList)) return false;
            if (NameContainsKeyword(goName, objectKeywordList)) return true;

            if (detectParentObjectNames)
            {
                var parent = go.transform.parent;
                while (parent != null)
                {
                    string parentName = parent.name;
                    if (NameContainsKeyword(parentName, exclusionKeywordList)) return false;
                    if (NameContainsKeyword(parentName, objectKeywordList)) return true;
                    parent = parent.parent;
                }
            }
            return false;
        }

        public void ClearCache()
        {
            materialCache.Clear();
            shaderCache.Clear();
            componentTypeCache.Clear();
            parentHierarchyCache.Clear();
        }

        public bool IsMosaic(Renderer renderer)
        {
            var go = renderer.gameObject;
            string goName = go.name;

            if (NameContainsKeyword(goName, exclusionKeywordList)) return false;
            if (CheckAndLog(goName, objectKeywordList, "对象名检测")) return true;

            if (detectParentObjectNames)
            {
                var parent = go.transform.parent;
                if (parent != null)
                {
                    int parentId = parent.GetInstanceID();
                    if (parentHierarchyCache.TryGetValue(parentId, out bool? cachedResult))
                    {
                        if (cachedResult.HasValue)
                        {
                            if (cachedResult.Value) return true;
                            return false;
                        }
                    }
                    else
                    {
                        bool? result = null;
                        var current = parent;
                        while (current != null)
                        {
                            string currentName = current.name;
                            if (NameContainsKeyword(currentName, exclusionKeywordList))
                            {
                                result = false;
                                break;
                            }
                            if (CheckAndLog(currentName, objectKeywordList, "父对象名检测"))
                            {
                                result = true;
                                break;
                            }
                            current = current.parent;
                        }
                        parentHierarchyCache[parentId] = result;
                        if (result.HasValue)
                        {
                            if (result.Value) return true;
                            return false;
                        }
                    }
                }
            }

            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null) continue;

                int matId = mat.GetInstanceID();
                if (materialCache.TryGetValue(matId, out bool isMatMosaic))
                {
                    if (isMatMosaic) return true;
                    continue;
                }

                bool isCurrentMatMosaic = CheckMaterialIsMosaic(mat);
                materialCache[matId] = isCurrentMatMosaic;
                if (isCurrentMatMosaic) return true;
            }

            if (meshKeywordList != null && meshKeywordList.Count > 0)
            {
                Mesh mesh = null;
                var smr = renderer.TryCast<SkinnedMeshRenderer>();
                if (smr != null)
                {
                    mesh = smr.sharedMesh;
                }
                else
                {
                    var mf = renderer.GetComponent<MeshFilter>();
                    if (mf != null)
                    {
                        mesh = mf.sharedMesh;
                    }
                }
                if (mesh != null && CheckAndLog(mesh.name, meshKeywordList, "网格名检测")) return true;
            }

            if (CheckComponents(go)) return true;

            return false;
        }

        private bool CheckMaterialIsMosaic(Material mat)
        {
            if (NameContainsKeyword(mat.name, materialKeywordList)) return true;

            if (mat.shader != null)
            {
                int shaderId = mat.shader.GetInstanceID();
                if (shaderCache.TryGetValue(shaderId, out bool isShaderMosaic))
                {
                    if (isShaderMosaic) return true;
                }
                else
                {
                    bool currentShaderMosaic = CheckShaderIsMosaic(mat.shader);
                    shaderCache[shaderId] = currentShaderMosaic;
                    if (currentShaderMosaic) return true;
                }
            }

            // 纹理检测已完全禁用，防止 AccessViolationException
            return false;
        }

        private bool CheckShaderIsMosaic(Shader shader)
        {
            if (NameContainsKeyword(shader.name, shaderKeywordList)) return true;

            if (shaderPropertyKeywordList != null && shaderPropertyKeywordList.Count > 0)
            {
                int propCount = shader.GetPropertyCount();
                for (int i = 0; i < propCount; i++)
                {
                    if (NameContainsKeyword(shader.GetPropertyName(i), shaderPropertyKeywordList))
                        return true;
                }
            }
            return false;
        }

        private bool CheckComponents(GameObject go)
        {
            if (componentNameKeywordList == null || componentNameKeywordList.Count == 0) return false;

            var components = go.GetComponents<Component>();
            foreach (var component in components)
            {
                if (component == null || component.gameObject == null) continue;

                string compName = component.GetIl2CppType().Name;
                if (componentTypeCache.TryGetValue(compName, out bool isMosaicComp))
                {
                    if (isMosaicComp) return true;
                    continue;
                }

                bool match = NameContainsKeyword(compName, componentNameKeywordList);
                componentTypeCache[compName] = match;
                if (match) return true;
            }
            return false;
        }

        private bool NameContainsKeyword(string name, List<string> keywords)
        {
            if (string.IsNullOrEmpty(name) || keywords == null || keywords.Count == 0) return false;
            foreach (var keyword in keywords)
            {
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private bool CheckAndLog(string name, List<string> keywords, string category)
        {
            if (NameContainsKeyword(name, keywords))
            {
                logger.LogDebug($"[{category}] 命中: {name}");
                return true;
            }
            return false;
        }
    }

    // =================================================================================
    // 马赛克处理器
    // =================================================================================
    public class MosaicProcessor
    {
        private readonly RemoveMode removeMode;
        private readonly ManualLogSource logger;
        private readonly bool logProcessedObjects;
        private readonly List<string> materialKeywords;
        private readonly List<string> shaderKeywords;
        private readonly MosaicDetector detector;
        private readonly Dictionary<int, Il2CppReferenceArray<Material>> transparentMaterialArrays = new Dictionary<int, Il2CppReferenceArray<Material>>();

        public MosaicProcessor(RemoveMode removeMode, Material transparentMaterial, ManualLogSource logger, bool logProcessedObjects,
            List<string> materialKeywords = null, List<string> shaderKeywords = null, MosaicDetector detector = null)
        {
            this.removeMode = removeMode;
            this.logger = logger;
            this.logProcessedObjects = logProcessedObjects;
            this.materialKeywords = materialKeywords;
            this.shaderKeywords = shaderKeywords;
            this.detector = detector;
        }

        private Material GetTransparentMaterial()
        {
            // 动态获取，支持自动重建被 GC 回收的材质
            if (DemosaicPlugin.Instance != null)
                return DemosaicPlugin.Instance.EnsureTransparentMaterial();
            return null;
        }

        public void Process(Renderer renderer)
        {
            var transMat = GetTransparentMaterial();

            // 禁用阴影，防止透明面片投射黑影块导致阴影区变色/暗色斑块
            try
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            catch { }

            if (removeMode == RemoveMode.Smart)
            {
                if (transMat != null)
                {
                    ProcessSmart(renderer, transMat);
                    return;
                }
                else
                {
                    logger.LogWarning($"透明材质缺失，Smart 模式降级为 Disable: {renderer.name}");
                }
            }
            else if (removeMode == RemoveMode.Transparent)
            {
                if (transMat != null)
                {
                    var sharedMats = renderer.sharedMaterials;
                    renderer.sharedMaterials = GetTransparentMaterialArray(sharedMats.Length, transMat);
                    if (logProcessedObjects)
                        logger.LogInfo($"已去除马赛克 (透明模式): {renderer.name}");
                    return;
                }
                else
                {
                    logger.LogWarning($"透明材质缺失，降级为 Disable 模式: {renderer.name}");
                }
            }

            renderer.gameObject.SetActive(false);
            if (logProcessedObjects)
                logger.LogInfo($"已去除马赛克 (禁用模式): {renderer.name}");
        }

        private void ProcessSmart(Renderer renderer, Material transMat)
        {
            var sharedMats = renderer.sharedMaterials;
            bool hasMosaicMat = false;

            for (int i = 0; i < sharedMats.Length; i++)
            {
                if (IsMosaicMaterial(sharedMats[i]))
                {
                    hasMosaicMat = true;
                    break;
                }
            }

            if (hasMosaicMat)
            {
                for (int i = 0; i < sharedMats.Length; i++)
                {
                    if (IsMosaicMaterial(sharedMats[i]))
                        sharedMats[i] = transMat;
                }
                renderer.sharedMaterials = sharedMats;
                if (logProcessedObjects)
                    logger.LogInfo($"已去除马赛克 (智能模式 - 仅替换马赛克材质槽): {renderer.name}");
            }
            else
            {
                // 只有当 GameObject 名字或父节点名字命中关键词时，才回退为全透明，防止网格名命中误伤整体皮肤
                if (detector != null && detector.IsObjectNameOrParentMosaic(renderer.gameObject))
                {
                    renderer.sharedMaterials = GetTransparentMaterialArray(sharedMats.Length, transMat);
                    if (logProcessedObjects)
                        logger.LogInfo($"已去除马赛克 (智能模式 - 全透明回退): {renderer.name}");
                }
                else
                {
                    if (logProcessedObjects)
                        logger.LogWarning($"跳过智能模式全透明回退以避免假阳性: {renderer.name} (仅网格名或组件名命中)");
                }
            }
        }

        private bool IsMosaicMaterial(Material mat)
        {
            if (mat == null) return false;

            // 委托给检测器的完整检测逻辑（材质名 + Shader名 + Shader属性），并自动缓存
            if (detector != null)
                return detector.CheckMaterialFull(mat);

            // 无检测器时的后备路径
            if (materialKeywords != null)
            {
                for (int i = 0; i < materialKeywords.Count; i++)
                {
                    if (mat.name.IndexOf(materialKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            if (shaderKeywords != null && mat.shader != null)
            {
                string shaderName = mat.shader.name;
                for (int i = 0; i < shaderKeywords.Count; i++)
                {
                    if (shaderName.IndexOf(shaderKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        private Il2CppReferenceArray<Material> GetTransparentMaterialArray(int materialCount, Material transMat)
        {
            if (!transparentMaterialArrays.TryGetValue(materialCount, out var materials))
            {
                materials = new Il2CppReferenceArray<Material>(materialCount);
                for (int i = 0; i < materials.Length; i++)
                    materials[i] = transMat;
                transparentMaterialArrays[materialCount] = materials;
            }
            return materials;
        }
    }

    // =================================================================================
    // 安全输入管理器（兼容新旧 Input System）
    // =================================================================================
    internal static class SafeInput
    {
        private static bool _legacyInputAvailable = true;
        private static bool _warned = false;
        private static System.Reflection.MethodInfo _keyboardCurrentMethod;
        private static System.Reflection.MethodInfo _keyIsPressedMethod;
        private static object _keyboardInstance;
        private static bool _newInputInitialized = false;
        private static readonly Dictionary<KeyCode, object[]> _keyArgsCache = new Dictionary<KeyCode, object[]>();

        /// <summary>
        /// 安全检测按键按下，兼容旧版 Input 和新版 Input System。
        /// </summary>
        public static bool GetKeyDown(KeyCode key)
        {
            // 优先尝试旧版 Input
            if (_legacyInputAvailable)
            {
                try
                {
                    return Input.GetKeyDown(key);
                }
                catch (Exception ex)
                {
                    // IL2CPP 环境下异常被 Il2CppException 包装，无法直接 catch InvalidOperationException
                    // 通过消息判断是否为 Input System 冲突
                    if (ex.Message.Contains("Input System") || ex.InnerException?.Message.Contains("Input System") == true)
                    {
                        _legacyInputAvailable = false;
                        if (!_warned)
                        {
                            DemosaicPlugin.Logger.LogWarning("检测到游戏使用新版 Input System，旧版 Input 已禁用。尝试初始化新输入系统支持...");
                            _warned = true;
                        }
                    }
                    else
                    {
                        // 其他异常直接抛出
                        throw;
                    }
                }
            }

            // 降级到新版 Input System（通过反射）
            return TryNewInputSystemKeyDown(key);
        }

        private static object[] GetKeyArgs(KeyCode key)
        {
            if (!_keyArgsCache.TryGetValue(key, out var args))
            {
                var keyEnum = MapKeyCodeToKey(key);
                if (keyEnum != null)
                {
                    args = new object[] { keyEnum };
                    _keyArgsCache[key] = args;
                }
            }
            return args;
        }

        private static bool TryNewInputSystemKeyDown(KeyCode key)
        {
            try
            {
                if (!_newInputInitialized)
                {
                    InitializeNewInputSystem();
                    _newInputInitialized = true;
                }

                if (_keyboardCurrentMethod == null || _keyIsPressedMethod == null) return false;

                if (_keyboardInstance == null)
                {
                    _keyboardInstance = _keyboardCurrentMethod.Invoke(null, null);
                    if (_keyboardInstance == null) return false;
                }

                var args = GetKeyArgs(key);
                if (args == null) return false;

                var result = _keyIsPressedMethod.Invoke(_keyboardInstance, args);
                return result is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static void InitializeNewInputSystem()
        {
            try
            {
                // 查找 UnityEngine.InputSystem.Keyboard 类型
                Type keyboardType = null;
                Type keyEnumType = null;

                // 优先通过 Il2CppInterop 查找（IL2CPP 环境下 InputSystem 是 Il2Cpp 类型）
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name;
                    if (asmName.Contains("InputSystem") || asmName.Contains("Unity.InputSystem"))
                    {
                        if (keyboardType == null) keyboardType = asm.GetType("UnityEngine.InputSystem.Keyboard");
                        if (keyEnumType == null) keyEnumType = asm.GetType("UnityEngine.InputSystem.Key");
                    }
                }

                // 备用：Type.GetType
                if (keyboardType == null)
                    keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (keyEnumType == null)
                    keyEnumType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");

                if (keyboardType == null)
                {
                    DemosaicPlugin.Logger.LogWarning("未能找到 InputSystem.Keyboard 类型，快捷键功能将不可用。");
                    return;
                }

                _keyboardCurrentMethod = keyboardType.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetMethod;

                // Keyboard 的 IsKeyPressed(Key) 方法
                if (keyEnumType != null)
                {
                    _keyIsPressedMethod = keyboardType.GetMethod("IsKeyPressed", new[] { keyEnumType });
                    if (_keyIsPressedMethod == null)
                    {
                        // 尝试 wasKeyPressedThisFrame（部分版本方法名不同）
                        _keyIsPressedMethod = keyboardType.GetMethod("wasKeyPressedThisFrame", new[] { keyEnumType });
                    }
                }

                if (_keyboardCurrentMethod != null && _keyIsPressedMethod != null)
                    DemosaicPlugin.Logger.LogInfo("新版 Input System 键盘支持初始化成功。");
                else
                    DemosaicPlugin.Logger.LogWarning($"Input System Keyboard 方法未找到 (current={_keyboardCurrentMethod != null}, isPressed={_keyIsPressedMethod != null})，快捷键不可用。");
            }
            catch (Exception ex)
            {
                DemosaicPlugin.Logger.LogWarning($"初始化新版 Input System 失败: {ex.Message}。快捷键功能将不可用。");
            }
        }

        private static object MapKeyCodeToKey(KeyCode keyCode)
        {
            // InputSystem.Key 枚举值名称与 KeyCode 大部分一致
            string keyName = keyCode.ToString();
            try
            {
                var keyEnumType = _keyIsPressedMethod?.GetParameters()[0].ParameterType;
                if (keyEnumType == null) return null;
                return Enum.Parse(keyEnumType, keyName, ignoreCase: true);
            }
            catch
            {
                return null;
            }
        }
    }

    // =================================================================================
    // 生命周期与分批扫描管理器（含导出功能）
    // =================================================================================
    public class PluginLifecycleManager : MonoBehaviour
    {
        public PluginLifecycleManager(IntPtr ptr) : base(ptr) { }

        private Action<Scene, LoadSceneMode> _onSceneLoadedAction;
        private Action<Scene> _onSceneUnloadedAction;

        private bool _delayScanQueued = false;
        private float _delayScanTimer = 0f;
        private float _periodicScanTimer = 0f;

        private bool _isBatchScanning = false;
        private int _currentBatchIndex = 0;
        private Il2CppArrayBase<Renderer> _batchRenderers;

        // 导出功能相关
        private bool _isExporting = false;
        private int _exportCurrentIndex = 0;
        private Il2CppArrayBase<Renderer> _exportRenderers;

        void Awake()
        {
            _onSceneLoadedAction = new Action<Scene, LoadSceneMode>(OnSceneLoaded);
            _onSceneUnloadedAction = new Action<Scene>(OnSceneUnloaded);
            SceneManager.sceneLoaded += _onSceneLoadedAction;
            SceneManager.sceneUnloaded += _onSceneUnloadedAction;
        }

        void OnDestroy()
        {
            if (_onSceneLoadedAction != null) SceneManager.sceneLoaded -= _onSceneLoadedAction;
            if (_onSceneUnloadedAction != null) SceneManager.sceneUnloaded -= _onSceneUnloadedAction;
        }

        void Update()
        {
            if (DemosaicPlugin.Instance == null) return;

            // 1. 延迟扫描
            if (_delayScanQueued)
            {
                _delayScanTimer += Time.deltaTime;
                if (_delayScanTimer >= DemosaicPlugin.Instance.SceneLoadScanDelayValue)
                {
                    _delayScanQueued = false;
                    StartBatchScan();
                }
            }

            // 2. 周期扫描
            float periodicInterval = DemosaicPlugin.Instance.periodicScanInterval.Value;
            if (periodicInterval > 0 && !_isBatchScanning)
            {
                _periodicScanTimer += Time.deltaTime;
                if (_periodicScanTimer >= periodicInterval)
                {
                    _periodicScanTimer = 0f;
                    StartBatchScan();
                }
            }

            // 3. 手动扫描热键
            if (SafeInput.GetKeyDown(DemosaicPlugin.Instance.ForceScanHotkeyValue))
            {
                DemosaicPlugin.Logger.LogInfo("快捷键被按下，强制重新执行全场景扫描...");
                DemosaicPlugin.Instance.RequestFullScan();
            }

            // 4. 新增：导出场景资源热键
            if (SafeInput.GetKeyDown(DemosaicPlugin.Instance.exportSceneKey.Value))
            {
                DemosaicPlugin.Logger.LogInfo("开始将场景渲染器信息导出到日志...");
                StartExport();
            }

            // 5. 执行分批扫描
            ProcessBatchScan();

            // 6. 执行分批导出
            ProcessExportBatch();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _delayScanQueued = true;
            _delayScanTimer = 0f;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            _isBatchScanning = false;
            _isExporting = false;       // 中断导出
            _exportRenderers = null;

            if (DemosaicPlugin.Instance != null)
                DemosaicPlugin.Instance.NotifySceneUnloaded();
        }

        private void StartBatchScan()
        {
            if (_isBatchScanning) return;
            _batchRenderers = SceneObjectFinder.FindRenderers(DemosaicPlugin.Instance.IncludeInactiveObjectsValue);
            _currentBatchIndex = 0;
            _isBatchScanning = true;
            DemosaicPlugin.Logger.LogDebug($"开始分批扫描 {_batchRenderers.Length} 个渲染器...");
        }

        internal void RestartBatchScan()
        {
            _isBatchScanning = false;
            _batchRenderers = null;
            StartBatchScan();
        }

        private void ProcessBatchScan()
        {
            var renderers = _batchRenderers;
            if (!_isBatchScanning || renderers == null) return;

            int batchSize = DemosaicPlugin.Instance.ScanBatchSizeValue;
            int processed = 0;
            while (_currentBatchIndex < renderers.Length && processed < batchSize)
            {
                var renderer = renderers[_currentBatchIndex];
                if (renderer != null)
                    DemosaicPlugin.Instance.ProcessRenderer(renderer);
                _currentBatchIndex++;
                processed++;
            }
            if (_currentBatchIndex >= renderers.Length)
            {
                _isBatchScanning = false;
                _batchRenderers = null;
                // 渲染器扫描完成后，执行 Camera 后处理组件检测
                DemosaicPlugin.Instance.ProcessCameraEffects();
                DemosaicPlugin.Instance.ProcessDecalsAndProjectors();
            }
        }

        // ==================== 导出功能实现 ====================
        private void StartExport()
        {
            if (_isExporting) return;
            _exportRenderers = SceneObjectFinder.FindRenderers(false);
            _exportCurrentIndex = 0;
            _isExporting = true;
            DemosaicPlugin.Logger.LogInfo($"准备导出 {_exportRenderers.Length} 个渲染器信息...");
        }

        private void ProcessExportBatch()
        {
            var renderers = _exportRenderers;
            if (!_isExporting || renderers == null) return;

            int batchSize = DemosaicPlugin.Instance.ScanBatchSizeValue;
            int processed = 0;

            while (_exportCurrentIndex < renderers.Length && processed < batchSize)
            {
                var renderer = renderers[_exportCurrentIndex];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    var go = renderer.gameObject;
                    string meshName = "N/A";

                    var smr = renderer.TryCast<SkinnedMeshRenderer>();
                    if (smr != null && smr.sharedMesh != null)
                    {
                        meshName = smr.sharedMesh.name;
                    }
                    else
                    {
                        var mr = renderer.TryCast<MeshRenderer>();
                        if (mr != null)
                        {
                            var mf = renderer.GetComponent<MeshFilter>();
                            if (mf != null && mf.sharedMesh != null)
                            {
                                meshName = mf.sharedMesh.name;
                            }
                        }
                    }

                    string goName = go.name;
                    var mats = renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var mat = mats[i];
                        if (mat != null)
                        {
                            string matName = mat.name;
                            string shaderName = (mat.shader != null) ? mat.shader.name : "N/A";
                            DemosaicPlugin.Logger.LogInfo(
                                $"[Demosaic Export] GO: {goName} | Material: {matName} | Shader: {shaderName} | Mesh: {meshName}");
                        }
                    }
                }
                _exportCurrentIndex++;
                processed++;
            }

            if (_exportCurrentIndex >= renderers.Length)
            {
                _isExporting = false;
                _exportRenderers = null;
                DemosaicPlugin.Logger.LogInfo("场景渲染器信息导出完成！");
            }
        }
    }

    internal static class SceneObjectFinder
    {
        private static MethodInfo findObjectsByTypeMethod;
        private static object includeInactive;
        private static object excludeInactive;
        private static object sortNone;
        private static bool initialized;

        public static Il2CppArrayBase<Renderer> FindRenderers(bool includeInactiveObjects)
        {
            Initialize();
            if (findObjectsByTypeMethod != null)
            {
                try
                {
                    var result = findObjectsByTypeMethod.Invoke(null, new[] { includeInactiveObjects ? includeInactive : excludeInactive, sortNone });
                    if (result is Il2CppArrayBase<Renderer> renderers)
                        return renderers;
                }
                catch
                {
                    findObjectsByTypeMethod = null;
                }
            }

            return UnityEngine.Object.FindObjectsOfType<Renderer>();
        }

        private static void Initialize()
        {
            if (initialized) return;
            initialized = true;

            var objectType = typeof(UnityEngine.Object);
            var inactiveType = objectType.Assembly.GetType("UnityEngine.FindObjectsInactive");
            var sortModeType = objectType.Assembly.GetType("UnityEngine.FindObjectsSortMode");
            if (inactiveType == null || sortModeType == null) return;

            includeInactive = Enum.Parse(inactiveType, "Include");
            excludeInactive = Enum.Parse(inactiveType, "Exclude");
            sortNone = Enum.Parse(sortModeType, "None");

            findObjectsByTypeMethod = objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "FindObjectsByType" &&
                                 method.IsGenericMethodDefinition &&
                                 method.GetParameters().Length == 2)
                .Select(method => method.MakeGenericMethod(typeof(Renderer)))
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters[0].ParameterType == inactiveType &&
                           parameters[1].ParameterType == sortModeType;
                });
        }
    }

    // =================================================================================
    // Harmony 实时拦截补丁
    // =================================================================================
    [HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive))]
    class GameObject_SetActive_Patch
    {
        static void Postfix(GameObject __instance, bool value)
        {
            if (!value || DemosaicPlugin.Instance == null) return;
            try { DemosaicPlugin.Instance.ProcessNewGameObject(__instance); }
            catch (Exception ex) { DemosaicPlugin.Logger.LogError($"处理新GameObject '{__instance.name}' 错误: {ex}"); }
        }
    }

    public static class GenericDisablePatch
    {
        /// <summary>
        /// 条件性 Prefix：仅当插件实例存在且启用时才拦截，否则放行原始方法。
        /// </summary>
        public static bool Prefix()
        {
            return DemosaicPlugin.Instance == null;
        }
    }
}
