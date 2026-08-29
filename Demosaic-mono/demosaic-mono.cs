using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using BepInEx.Unity.Mono;
using UnityEngine;
using System.Collections;
using UnityEngine.SceneManagement;
using System.Reflection;
using System.Linq;
using System;
using BepInEx.Logging;
using System.Collections.Generic;

namespace DemosaicPlugin
{
    public enum RemoveMode
    {
        Disable,     // 禁用对象 (推荐：性能最好，不破坏游戏原本的依赖和逻辑)
        Destroy,     // 物理销毁对象 (危险：可能导致其他引用该对象的脚本报错 NullReference)
        Transparent, // 替换为透明材质 (安全，但对象仍在渲染管线中，有微小的性能开销)
        Smart        // 智能模式：仅替换马赛克材质槽为透明，保留同对象上的非马赛克材质
    }

    [BepInPlugin("demosaic", "Demosaic", "1.5.0")]
    public class DemosaicPlugin : BaseUnityPlugin
    {
        public static DemosaicPlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;
        private MosaicDetector _detector;
        private MosaicProcessor _processor;

        // 配置项
        private ConfigEntry<bool> _enablePlugin;
        private ConfigEntry<RemoveMode> _removeMode;
        private ConfigEntry<KeyCode> _manualScanKey;
        private ConfigEntry<KeyCode> _exportSceneKey;
        private ConfigEntry<bool> _includeInactiveObjects;
        private ConfigEntry<bool> _detectParentObjectNames;
        private ConfigEntry<bool> _logProcessedObjects;
        private ConfigEntry<float> _periodicScanInterval;
        private ConfigEntry<float> _sceneLoadScanDelay;
        private ConfigEntry<int> _scanBatchSize;
        private ConfigEntry<string> _objectNameKeywords;
        private ConfigEntry<string> _materialNameKeywords;
        private ConfigEntry<string> _shaderNameKeywords;
        private ConfigEntry<string> _meshNameKeywords;
        private ConfigEntry<string> _textureKeywords;
        private ConfigEntry<string> _componentNameKeywords;
        private ConfigEntry<string> _shaderPropertyKeywords;
        private ConfigEntry<string> _exclusionKeywords;
        private ConfigEntry<bool> _disableMethods;
        private ConfigEntry<string> _methodDisableKeywords;
        private ConfigEntry<string> _methodExcludeKeywords;
        private ConfigEntry<string> _assemblyNamesToPatch;

        // Camera 后处理检测
        private ConfigEntry<bool> _enableCameraEffectDetection;
        private ConfigEntry<string> _cameraEffectKeywords;

        // Decal/Projector 检测
        private ConfigEntry<bool> _enableDecalDetection;

        // 材质 setter Hook
        private ConfigEntry<bool> _enableMaterialSetterHook;

        // GC 优化复用
        private WaitForSeconds _periodicWait;
        private WaitForSeconds _delayWait;
        private List<Renderer> _rendererBuffer = new List<Renderer>();
        private HashSet<int> _processedRendererIds = new HashSet<int>();
        private HashSet<int> _processedCameraEffectIds = new HashSet<int>();
        private HashSet<int> _processedDecalIds = new HashSet<int>();
        internal static bool IsProcessingMaterial = false; // 防止材质 setter hook 递归
        private static int _lastMaterialHookFrame = -1;
        private static readonly HashSet<int> _materialHookFrameDedup = new HashSet<int>();
        private Coroutine _periodicScanCoroutine;
        private Coroutine _activeScanCoroutine;

        private void Awake()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (Exception ex) { Logger.LogWarning($"无法设置控制台输出编码为UTF-8: {ex.Message}"); }

            Instance = this;
            Log = Logger;
            Log.LogInfo("Demosaic 正在加载...");

            LoadConfig();

            if (!_enablePlugin.Value)
            {
                Log.LogInfo("插件已在配置文件中被禁用，停止初始化。");
                return;
            }

            // 复用 WaitForSeconds
            _periodicWait = new WaitForSeconds(Math.Max(0f, _periodicScanInterval.Value));
            _delayWait = new WaitForSeconds(Math.Max(0f, _sceneLoadScanDelay.Value));

            // 初始化检测器与处理器
            ReloadDetector();
            _processor = new MosaicProcessor(_removeMode.Value, _logProcessedObjects.Value, GetKeywordArray(_materialNameKeywords), GetKeywordArray(_shaderNameKeywords), _detector);

            // Harmony 补丁
            _harmony = new Harmony("demosaic.mono");
            ApplyHarmonyPatches();

            // 高级方法拦截（需手动开启）
            if (_disableMethods.Value)
                PatchMethodsByName();

            // 场景生命周期事件
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            // 启动周期扫描（如果间隔 > 0）
            if (_periodicScanInterval.Value > 0)
                _periodicScanCoroutine = StartCoroutine(PeriodicScan());

            // 首次场景扫描（如果当前已有场景）
            if (SceneManager.GetActiveScene().isLoaded)
                _activeScanCoroutine = StartCoroutine(DelayedScan());

            // 监听配置变更，实现关键词热重载
            Config.SettingChanged += OnSettingChanged;

            Log.LogInfo("Demosaic 加载成功！");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            Config.SettingChanged -= OnSettingChanged;
            StopAllCoroutines();
            _processor?.Dispose();
            Instance = null;
            Log.LogInfo("Demosaic 已卸载。");
        }

        private void Update()
        {
            if (SafeInput.GetKeyDown(_manualScanKey.Value))
            {
                Log.LogInfo("手动扫描热键触发。");
                if (_activeScanCoroutine != null)
                    StopCoroutine(_activeScanCoroutine);
                _activeScanCoroutine = StartCoroutine(ScanSceneCoro());
            }

            if (SafeInput.GetKeyDown(_exportSceneKey.Value))
            {
                Log.LogInfo("开始将场景渲染器信息导出到日志...");
                StartCoroutine(ExportSceneCoro());
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 停止之前可能残留的扫描协程
            if (_activeScanCoroutine != null)
            {
                StopCoroutine(_activeScanCoroutine);
                _activeScanCoroutine = null;
            }
            Log.LogInfo($"场景已加载: {scene.name}，开始延迟扫描。");
            _activeScanCoroutine = StartCoroutine(DelayedScan());
        }

        private void OnSceneUnloaded(Scene scene)
        {
            // 停止正在进行的扫描协程，防止访问已销毁对象
            if (_activeScanCoroutine != null)
            {
                StopCoroutine(_activeScanCoroutine);
                _activeScanCoroutine = null;
            }
            _detector.ClearCache();
            _processedRendererIds.Clear();
            _processedCameraEffectIds.Clear();
            _processedDecalIds.Clear();
        }

        private void LoadConfig()
        {
            _enablePlugin = Config.Bind("1. 通用", "EnablePlugin", true, "启用或禁用整个插件。");
            _removeMode = Config.Bind("1. 通用", "RemoveMode", RemoveMode.Smart, "移除方式：Disable (禁用), Destroy (销毁), Transparent (透明), Smart (仅替换马赛克材质槽)");
            _manualScanKey = Config.Bind("1. 通用", "ManualScanKey", KeyCode.F10, "按下此键可手动扫描场景。");
            _exportSceneKey = Config.Bind("1. 通用", "ExportSceneKey", KeyCode.F11, "按下此键将场景渲染器信息导出到日志。");
            _includeInactiveObjects = Config.Bind("1. 通用", "IncludeInactiveObjects", true, "扫描时是否包含未激活对象。Unity 2022+ 会优先使用更快的 FindObjectsByType。");
            _detectParentObjectNames = Config.Bind("1. 通用", "DetectParentObjectNames", true, "检测 Renderer 的父节点名称，适合 MosaicRoot/Quad 这类层级。");
            _logProcessedObjects = Config.Bind("1. 通用", "LogProcessedObjects", false, "是否为每个被处理对象写 Info 日志。大量对象会明显影响性能。");

            _periodicScanInterval = Config.Bind("2. 扫描", "PeriodicScanInterval", 10f, "周期扫描间隔（秒），0 则禁用。");
            _sceneLoadScanDelay = Config.Bind("2. 扫描", "SceneLoadScanDelay", 1.5f, "场景加载后延迟扫描的时间（秒）。");
            _scanBatchSize = Config.Bind("2. 扫描", "ScanBatchSize", 500, "每帧最多处理的对象数量，防止卡顿。");

            _objectNameKeywords = Config.Bind("3. 关键词", "ObjectNameKeywords", "mosaic,censored,pixelated,mozic,mazic,mozaic,moza", "对象名关键词。");
            _materialNameKeywords = Config.Bind("3. 关键词", "MaterialNameKeywords", "mosaic,censored,pixel,mozic,mazic,moza", "材质名关键词。");
            _shaderNameKeywords = Config.Bind("3. 关键词", "ShaderNameKeywords", "mosaic,pixelate,censor,moza,mozic,mazic,mozaic", "着色器名关键词。");
            _meshNameKeywords = Config.Bind("3. 关键词", "MeshNameKeywords", "censor,mosaic,moza,mozic,mazic,mozaic", "网格名关键词。");
            _textureKeywords = Config.Bind("3. 关键词", "TextureKeywords", "mosaic", "纹理名关键词。");
            _componentNameKeywords = Config.Bind("3. 关键词", "ComponentNameKeywords", "", "组件名关键词。");
            _shaderPropertyKeywords = Config.Bind("3. 关键词", "ShaderPropertyKeywords", "_PixelSize,_BlockSize,_MosaicFactor", "着色器属性名关键词。");
            _exclusionKeywords = Config.Bind("3. 关键词", "ExclusionKeywords", "", "白名单关键词，命中后不会处理该对象。");

            _disableMethods = Config.Bind("4. 高级", "DisableMethods", false, "启用按名称拦截方法（慎用）。");
            _methodDisableKeywords = Config.Bind("4. 高级", "MethodDisableKeywords", "moza,mosaic", "要拦截的方法名的关键词。");
            _methodExcludeKeywords = Config.Bind("4. 高级", "MethodExcludeKeywords", "remove,destroy,clear,disable,hide,off,delete,undo,stop,cancel", "方法名排除词，同时命中排除词的方法不会被拦截。");
            _assemblyNamesToPatch = Config.Bind("4. 高级", "AssemblyNamesToPatch", "Assembly-CSharp", "要扫描的程序集名称，逗号分隔。");
            _enableCameraEffectDetection = Config.Bind("4. 高级", "EnableCameraEffectDetection", true, "是否启用 Camera 后处理组件检测，禁用匹配关键词的后处理效果。");
            _cameraEffectKeywords = Config.Bind("4. 高级", "CameraEffectKeywords", "mosaic,censor,pixelat,moza,mozic,mazic", "Camera 后处理组件名关键词，匹配的 MonoBehaviour 将被禁用。");
            _enableDecalDetection = Config.Bind("4. 高级", "EnableDecalDetection", true, "是否启用 Projector/DecalProjector 检测，禁用材质匹配马赛克关键词的投影/贴花。");
            _enableMaterialSetterHook = Config.Bind("4. 高级", "EnableMaterialSetterHook", false, "是否启用 Renderer 材质 setter Hook（实验性功能，默认关闭）。");
        }

        private void ReloadDetector()
        {
            _detector = new MosaicDetector(
                _objectNameKeywords.Value.Split(new[] { ',' }),
                _materialNameKeywords.Value.Split(new[] { ',' }),
                _shaderNameKeywords.Value.Split(new[] { ',' }),
                _meshNameKeywords.Value.Split(new[] { ',' }),
                _textureKeywords.Value.Split(new[] { ',' }),
                _componentNameKeywords.Value.Split(new[] { ',' }),
                _shaderPropertyKeywords.Value.Split(new[] { ',' }),
                _exclusionKeywords.Value.Split(new[] { ',' }),
                _detectParentObjectNames.Value
            );
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            // 若关键词相关配置变更，重新加载检测器并清空缓存
            var section = e.ChangedSetting.Definition.Section;
            if (section == "3. 关键词" ||
                e.ChangedSetting.Definition.Key.IndexOf("Keywords", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ReloadDetector();
                _detector.ClearCache();
                _processedRendererIds.Clear();
                // Smart 模式下关键词变更也需要重建处理器
                if (_removeMode.Value == RemoveMode.Smart)
                {
                    _processor?.Dispose();
                    _processor = new MosaicProcessor(_removeMode.Value, _logProcessedObjects.Value, GetKeywordArray(_materialNameKeywords), GetKeywordArray(_shaderNameKeywords), _detector);
                }
                Log.LogInfo("关键词配置已更新，下次扫描将生效。");
            }

            // 扫描间隔变更时，更新等待对象
            if (e.ChangedSetting.Definition.Key == "PeriodicScanInterval")
            {
                _periodicWait = new WaitForSeconds(Math.Max(0f, _periodicScanInterval.Value));
                // 重启周期协程
                if (_periodicScanCoroutine != null) StopCoroutine(_periodicScanCoroutine);
                if (_periodicScanInterval.Value > 0)
                    _periodicScanCoroutine = StartCoroutine(PeriodicScan());
            }
            if (e.ChangedSetting.Definition.Key == "SceneLoadScanDelay")
            {
                _delayWait = new WaitForSeconds(Math.Max(0f, _sceneLoadScanDelay.Value));
            }
            if (e.ChangedSetting.Definition.Key == "RemoveMode")
            {
                _processor?.Dispose();
                _processor = new MosaicProcessor(_removeMode.Value, _logProcessedObjects.Value, GetKeywordArray(_materialNameKeywords), GetKeywordArray(_shaderNameKeywords), _detector);
                _processedRendererIds.Clear();
                Log.LogInfo($"处理模式已更新为: {_removeMode.Value}。");
            }
            if (e.ChangedSetting.Definition.Key == "LogProcessedObjects")
            {
                _processor = new MosaicProcessor(_removeMode.Value, _logProcessedObjects.Value, GetKeywordArray(_materialNameKeywords), GetKeywordArray(_shaderNameKeywords), _detector);
            }
        }

        private static string[] GetKeywordArray(ConfigEntry<string> config)
        {
            return config.Value.Split(new[] { ',' })
                .Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        }

        private void ApplyHarmonyPatches()
        {
            try
            {
                var setActiveOriginal = AccessTools.Method(typeof(GameObject), "SetActive", new[] { typeof(bool) });
                if (setActiveOriginal != null)
                {
                    var setActivePostfix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(SetActivePatch));
                    _harmony.Patch(setActiveOriginal, postfix: setActivePostfix);
                }

                foreach (var instantiateOriginal in AccessTools.GetDeclaredMethods(typeof(UnityEngine.Object))
                    .Where(method => method.Name == "Instantiate" &&
                                     !method.IsGenericMethodDefinition &&
                                     !method.ContainsGenericParameters))
                {
                    var instantiatePostfix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(InstantiatePatch));
                    _harmony.Patch(instantiateOriginal, postfix: instantiatePostfix);
                }

                // Hook Renderer 材质 setter，实时捕获动态材质变更
                if (_enableMaterialSetterHook.Value)
                    ApplyMaterialSetterPatches();
            }
            catch (Exception e)
            {
                Log.LogError("应用 Harmony 补丁时出错: " + e);
            }
        }

        /// <summary>
        /// Hook Renderer 的材质 setter，实时捕获动态材质变更（如玩具激活时动态添加马赛克）。
        /// </summary>
        private void ApplyMaterialSetterPatches()
        {
            if (!_enableMaterialSetterHook.Value) return;

            var postfix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(MaterialSetterPostfix));
            int patched = 0;

            var setMaterial = AccessTools.PropertySetter(typeof(Renderer), "material");
            if (setMaterial != null)
            {
                try { _harmony.Patch(setMaterial, postfix: postfix); patched++; }
                catch (Exception ex) { Log.LogDebug($"Renderer.material setter patch 失败: {ex.Message}"); }
            }

            var setSharedMaterial = AccessTools.PropertySetter(typeof(Renderer), "sharedMaterial");
            if (setSharedMaterial != null)
            {
                try { _harmony.Patch(setSharedMaterial, postfix: postfix); patched++; }
                catch (Exception ex) { Log.LogDebug($"Renderer.sharedMaterial setter patch 失败: {ex.Message}"); }
            }

            var setMaterials = AccessTools.PropertySetter(typeof(Renderer), "materials");
            if (setMaterials != null)
            {
                try { _harmony.Patch(setMaterials, postfix: postfix); patched++; }
                catch (Exception ex) { Log.LogDebug($"Renderer.materials setter patch 失败: {ex.Message}"); }
            }

            var setSharedMaterials = AccessTools.PropertySetter(typeof(Renderer), "sharedMaterials");
            if (setSharedMaterials != null)
            {
                try { _harmony.Patch(setSharedMaterials, postfix: postfix); patched++; }
                catch (Exception ex) { Log.LogDebug($"Renderer.sharedMaterials setter patch 失败: {ex.Message}"); }
            }

            if (patched > 0)
                Log.LogInfo($"已 Hook {patched} 个 Renderer 材质 setter，可实时捕获动态材质变更。");
        }

        /// <summary>
        /// 材质 setter 的 Postfix：当游戏代码动态设置材质时，立即检测并处理马赛克。
        /// </summary>
        private static void MaterialSetterPostfix(Renderer __instance)
        {
            if (Instance == null || IsProcessingMaterial) return;
            if (__instance == null) return;
            if (!Instance._enableMaterialSetterHook.Value) return;

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

                // 检测新材质是否为马赛克
                if (Instance._detector.IsMosaic(__instance))
                {
                    IsProcessingMaterial = true;
                    try
                    {
                        Instance._processor.Process(__instance);
                        Instance._processedRendererIds.Add(rendererId);
                    }
                    finally
                    {
                        IsProcessingMaterial = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogDebug($"材质 setter hook 处理异常: {ex.Message}");
            }
        }

        private static void SetActivePatch(GameObject __instance, bool value)
        {
            if (value && Instance != null) Instance.ProcessObject(__instance);
        }

        private static void InstantiatePatch(UnityEngine.Object __result)
        {
            if (__result == null || Instance == null) return;
            if (__result is GameObject go)
            {
                Instance.ProcessObject(go);
            }
            else if (__result is Component comp)
            {
                var compGo = comp.gameObject;
                if (compGo != null)
                {
                    Instance.ProcessObject(compGo);
                }
            }
        }

        public void ProcessObject(GameObject go)
        {
            if (go == null) return;

            if (_detector.IsMosaic(go))
            {
                _processor.Process(go);
                return;
            }

            // 快速检查以避免对没有渲染器的GameObject层级分配数组/列表
            var hasRenderer = go.GetComponentInChildren<Renderer>(true);
            if (hasRenderer == null) return;

            _rendererBuffer.Clear();
            go.GetComponentsInChildren<Renderer>(true, _rendererBuffer);
            for (int i = 0; i < _rendererBuffer.Count; i++)
            {
                var renderer = _rendererBuffer[i];
                if (renderer != null && renderer.gameObject != go)
                {
                    ProcessRenderer(renderer);
                }
            }
            _rendererBuffer.Clear();
        }

        public bool ProcessRenderer(Renderer renderer)
        {
            if (renderer == null) return false;

            int rendererId = renderer.GetInstanceID();
            if (_processedRendererIds.Contains(rendererId)) return false;

            if (_detector.IsMosaic(renderer))
            {
                _processor.Process(renderer);
                _processedRendererIds.Add(rendererId);
                return true;
            }
            return false;
        }

        private void PatchMethodsByName()
        {
            var keywords = _methodDisableKeywords.Value.Split(new[] { ',' })
                .Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            if (keywords.Length == 0) return;

            var excludeKeywords = _methodExcludeKeywords.Value.Split(new[] { ',' })
                .Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();

            var assemblyNames = _assemblyNamesToPatch.Value.Split(new[] { ',' })
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => assemblyNames.Contains(asm.GetName().Name))
                .ToList();

            if (!assemblies.Any()) return;

            var conditionalPrefix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(ConditionalDisablePatch));
            int patchedCount = 0;
            int skippedByExclusion = 0;

            foreach (var assembly in assemblies)
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (method.IsSpecialName || method.IsGenericMethod || method.ContainsGenericParameters ||
                                method.IsAbstract || method.ReturnType != typeof(void))
                            {
                                continue;
                            }

                            string methodName = method.Name;

                            // 检查是否命中拦截关键词
                            bool matchesKeyword = keywords.Any(keyword =>
                                methodName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!matchesKeyword) continue;

                            // 检查是否命中排除词（防止误杀 RemoveMosaic 等方法）
                            bool matchesExclusion = excludeKeywords.Length > 0 &&
                                excludeKeywords.Any(excl =>
                                    methodName.IndexOf(excl, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (matchesExclusion)
                            {
                                skippedByExclusion++;
                                Log.LogDebug($"排除跳过: {type.FullName}.{methodName}");
                                continue;
                            }

                            try
                            {
                                _harmony.Patch(method, prefix: conditionalPrefix);
                                patchedCount++;
                                Log.LogDebug($"已拦截: {type.FullName}.{methodName}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
                            }
                            catch (Exception ex)
                            {
                                Log.LogDebug($"拦截失败: {type.FullName}.{methodName} - {ex.Message}");
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }
            Log.LogInfo($"方法拦截完成，共拦截 {patchedCount} 个方法，排除跳过 {skippedByExclusion} 个。");
        }

        /// <summary>
        /// 扫描场景中所有 Camera 上的 MonoBehaviour 组件，禁用名称匹配关键词的后处理效果。
        /// </summary>
        private void ProcessCameraEffects()
        {
            if (!_enableCameraEffectDetection.Value) return;
            var keywords = _cameraEffectKeywords.Value.Split(new[] { ',' })
                .Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            if (keywords.Length == 0) return;

            var cameras = FindObjectsOfType<Camera>(true);
            foreach (var cam in cameras)
            {
                if (cam == null) continue;
                var components = cam.GetComponents<MonoBehaviour>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    int compId = comp.GetInstanceID();
                    if (_processedCameraEffectIds.Contains(compId)) continue;

                    string typeName = comp.GetType().Name;
                    bool match = false;
                    for (int i = 0; i < keywords.Length; i++)
                    {
                        if (typeName.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            match = true;
                            break;
                        }
                    }

                    if (match)
                    {
                        comp.enabled = false;
                        _processedCameraEffectIds.Add(compId);
                        Log.LogInfo($"已禁用 Camera 后处理组件: {typeName} (on {cam.gameObject.name})");
                    }
                }
            }
        }

        /// <summary>
        /// 扫描场景中的 Projector（Built-in RP）和 DecalProjector（URP）组件，
        /// 检查其材质是否匹配马赛克关键词，匹配则禁用。
        /// </summary>
        private void ProcessDecalsAndProjectors()
        {
            if (!_enableDecalDetection.Value) return;

            // 1. 检测 Built-in RP 的 Projector 组件
            try
            {
                var projectors = FindObjectsOfType<Projector>(true);
                foreach (var proj in projectors)
                {
                    if (proj == null) continue;
                    int projId = proj.GetInstanceID();
                    if (_processedDecalIds.Contains(projId)) continue;

                    var mat = proj.material;
                    if (mat != null && _detector.CheckMaterialFull(mat))
                    {
                        proj.enabled = false;
                        _processedDecalIds.Add(projId);
                        Log.LogInfo($"已禁用 Projector: {proj.gameObject.name} (材质: {mat.name})");
                    }
                }
            }
            catch (Exception) { /* Projector 可能不存在于某些 URP/HDRP 环境 */ }

            // 2. 检测 URP DecalProjector（通过反射，因为编译时可能无引用）
            try
            {
                var decalType = Type.GetType("UnityEngine.Rendering.Universal.DecalProjector, Unity.RenderPipelines.Universal.Runtime")
                    ?? Type.GetType("UnityEngine.Rendering.Universal.DecalProjector, Unity.RenderPipelines.Universal");

                if (decalType != null)
                {
                    var decals = FindObjectsOfType(decalType, true);
                    var matProp = decalType.GetProperty("material") ?? decalType.GetProperty("m_Material");

                    if (decals != null && matProp != null)
                    {
                        foreach (var decalObj in decals)
                        {
                            if (decalObj == null) continue;
                            var behaviour = decalObj as MonoBehaviour;
                            if (behaviour == null) continue;

                            int decalId = behaviour.GetInstanceID();
                            if (_processedDecalIds.Contains(decalId)) continue;

                            var mat = matProp.GetValue(behaviour) as Material;
                            if (mat != null && _detector.CheckMaterialFull(mat))
                            {
                                behaviour.enabled = false;
                                _processedDecalIds.Add(decalId);
                                Log.LogInfo($"已禁用 DecalProjector: {behaviour.gameObject.name} (材质: {mat.name})");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogDebug($"DecalProjector 检测跳过: {ex.Message}");
            }
        }

        /// <summary>
        /// 条件性 Prefix：仅当插件实例存在时拦截，否则放行原始方法。
        /// </summary>
        private static bool ConditionalDisablePatch()
        {
            return Instance == null;
        }

        private IEnumerator DelayedScan()
        {
            yield return _delayWait;
            yield return StartCoroutine(ScanSceneCoro());
        }

        private IEnumerator PeriodicScan()
        {
            while (true)
            {
                yield return _periodicWait;
                yield return StartCoroutine(ScanSceneCoro());
            }
        }

        private IEnumerator ScanSceneCoro()
        {
            int batchSize = Math.Max(1, _scanBatchSize.Value);
            var renderers = SceneObjectFinder.FindRenderers(_includeInactiveObjects.Value);
            if (renderers == null || renderers.Length == 0)
            {
                ProcessCameraEffects();
                ProcessDecalsAndProjectors();
                yield break;
            }
            int processedCount = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                ProcessRenderer(renderers[i]);

                processedCount++;
                if (processedCount % batchSize == 0)
                {
                    yield return null;
                }
            }

            // 渲染器扫描完成后，执行 Camera 后处理组件检测
            ProcessCameraEffects();
            ProcessDecalsAndProjectors();
        }

        /// <summary>
        /// 将场景中所有渲染器信息导出到日志（用于调试分析）。
        /// </summary>
        private IEnumerator ExportSceneCoro()
        {
            int batchSize = Math.Max(1, _scanBatchSize.Value);
            var renderers = SceneObjectFinder.FindRenderers(false);
            if (renderers == null || renderers.Length == 0)
            {
                Log.LogInfo("场景中未找到任何渲染器。");
                yield break;
            }
            Log.LogInfo($"准备导出 {renderers.Length} 个渲染器信息...");
            int processedCount = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    var go = renderer.gameObject;
                    string meshName = "N/A";

                    var smr = renderer as SkinnedMeshRenderer;
                    if (smr != null && smr.sharedMesh != null)
                    {
                        meshName = smr.sharedMesh.name;
                    }
                    else
                    {
                        var mf = renderer.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                            meshName = mf.sharedMesh.name;
                    }

                    string goName = go.name;
                    var mats = renderer.sharedMaterials;
                    for (int j = 0; j < mats.Length; j++)
                    {
                        var mat = mats[j];
                        if (mat != null)
                        {
                            string matName = mat.name;
                            string shaderName = (mat.shader != null) ? mat.shader.name : "N/A";
                            Log.LogInfo($"[Demosaic Export] GO: {goName} | Material: {matName} | Shader: {shaderName} | Mesh: {meshName}");
                        }
                    }
                }

                processedCount++;
                if (processedCount % batchSize == 0)
                    yield return null;
            }

            Log.LogInfo("场景渲染器信息导出完成！");
        }
    }

    // =================================================================================
    // 马赛克检测器
    // =================================================================================
    public class MosaicDetector
    {
        private string[] _objectNameKeywords;
        private string[] _materialNameKeywords;
        private string[] _shaderNameKeywords;
        private string[] _meshNameKeywords;
        private string[] _textureKeywords;
        private string[] _componentNameKeywords;
        private string[] _shaderPropertyKeywords;
        private string[] _exclusionKeywords;
        private bool _detectParentObjectNames;

        private Dictionary<int, bool> _materialCache = new Dictionary<int, bool>();
        private Dictionary<int, bool> _shaderCache = new Dictionary<int, bool>();
        private Dictionary<Type, bool> _componentTypeCache = new Dictionary<Type, bool>();
        private Dictionary<int, bool?> _parentHierarchyCache = new Dictionary<int, bool?>();
        private List<Component> _componentBuffer = new List<Component>();

        public MosaicDetector(string[] obj, string[] mat, string[] shader, string[] mesh, string[] tex, string[] comp, string[] prop, string[] exclusion, bool detectParentObjectNames)
        {
            _objectNameKeywords = obj.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _materialNameKeywords = mat.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _shaderNameKeywords = shader.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _meshNameKeywords = mesh.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _textureKeywords = tex.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _componentNameKeywords = comp.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _shaderPropertyKeywords = prop.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _exclusionKeywords = exclusion.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _detectParentObjectNames = detectParentObjectNames;
        }

        public bool IsMaterialCached(Material mat, out bool isMosaic)
        {
            isMosaic = false;
            if (mat == null) return false;
            return _materialCache.TryGetValue(mat.GetInstanceID(), out isMosaic);
        }

        /// <summary>
        /// 完整检测材质是否为马赛克（含材质名/Shader名/Shader属性/纹理名），并缓存结果。
        /// 供 Processor 的 Smart 模式调用，确保判定维度与检测器一致。
        /// </summary>
        public bool CheckMaterialFull(Material mat)
        {
            if (mat == null) return false;
            int matId = mat.GetInstanceID();
            if (_materialCache.TryGetValue(matId, out bool cached))
                return cached;
            bool result = CheckMaterialIsMosaic(mat);
            _materialCache[matId] = result;
            return result;
        }

        public bool IsObjectNameOrParentMosaic(GameObject go)
        {
            if (go == null) return false;
            string goName = go.name;
            if (ContainsAny(goName, _exclusionKeywords)) return false;
            if (ContainsAny(goName, _objectNameKeywords)) return true;

            if (_detectParentObjectNames)
            {
                var parent = go.transform.parent;
                while (parent != null)
                {
                    string parentName = parent.name;
                    if (ContainsAny(parentName, _exclusionKeywords)) return false;
                    if (ContainsAny(parentName, _objectNameKeywords)) return true;
                    parent = parent.parent;
                }
            }
            return false;
        }

        public void ClearCache()
        {
            _materialCache.Clear();
            _shaderCache.Clear();
            _componentTypeCache.Clear();
            _parentHierarchyCache.Clear();
        }

        public bool IsMosaic(GameObject go)
        {
            if (go == null) return false;

            if (ContainsAny(go.name, _exclusionKeywords)) return false;
            if (ContainsAny(go.name, _objectNameKeywords)) return true;

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                var sharedMats = renderer.sharedMaterials;
                if (sharedMats != null)
                {
                    for (int i = 0; i < sharedMats.Length; i++)
                    {
                        var mat = sharedMats[i];
                        if (mat == null) continue;

                        int matId = mat.GetInstanceID();
                        if (_materialCache.TryGetValue(matId, out bool isMatMosaic))
                        {
                            if (isMatMosaic) return true;
                            continue;
                        }

                        bool isCurrentMatMosaic = CheckMaterialIsMosaic(mat);
                        _materialCache[matId] = isCurrentMatMosaic;
                        if (isCurrentMatMosaic) return true;
                    }
                }
            }

            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                if (ContainsAny(meshFilter.sharedMesh.name, _meshNameKeywords)) return true;
            }
            var skinnedMeshRenderer = go.GetComponent<SkinnedMeshRenderer>();
            if (skinnedMeshRenderer != null && skinnedMeshRenderer.sharedMesh != null)
            {
                if (ContainsAny(skinnedMeshRenderer.sharedMesh.name, _meshNameKeywords)) return true;
            }

            if (_componentNameKeywords.Length > 0)
            {
                _componentBuffer.Clear();
                go.GetComponents<Component>(_componentBuffer);
                for (int i = 0; i < _componentBuffer.Count; i++)
                {
                    var component = _componentBuffer[i];
                    if (component == null) continue;

                    Type compType = component.GetType();
                    if (_componentTypeCache.TryGetValue(compType, out bool isMosaicComp))
                    {
                        if (isMosaicComp) return true;
                        continue;
                    }

                    bool match = ContainsAny(compType.Name, _componentNameKeywords);
                    _componentTypeCache[compType] = match;
                    if (match) return true;
                }
                _componentBuffer.Clear();
            }

            return false;
        }

        public bool IsMosaic(Renderer renderer)
        {
            if (renderer == null) return false;

            var go = renderer.gameObject;
            if (go == null) return false;

            string goName = go.name;
            if (ContainsAny(goName, _exclusionKeywords)) return false;
            if (ContainsAny(goName, _objectNameKeywords)) return true;

            if (_detectParentObjectNames)
            {
                var parent = go.transform.parent;
                if (parent != null)
                {
                    int parentId = parent.GetInstanceID();
                    if (_parentHierarchyCache.TryGetValue(parentId, out bool? cachedResult))
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
                            if (ContainsAny(currentName, _exclusionKeywords))
                            {
                                result = false;
                                break;
                            }
                            if (ContainsAny(currentName, _objectNameKeywords))
                            {
                                result = true;
                                break;
                            }
                            current = current.parent;
                        }
                        _parentHierarchyCache[parentId] = result;
                        if (result.HasValue)
                        {
                            if (result.Value) return true;
                            return false;
                        }
                    }
                }
            }

            var sharedMats = renderer.sharedMaterials;
            if (sharedMats != null)
            {
                for (int i = 0; i < sharedMats.Length; i++)
                {
                    var mat = sharedMats[i];
                    if (mat == null) continue;

                    int matId = mat.GetInstanceID();
                    if (_materialCache.TryGetValue(matId, out bool isMatMosaic))
                    {
                        if (isMatMosaic) return true;
                        continue;
                    }

                    bool isCurrentMatMosaic = CheckMaterialIsMosaic(mat);
                    _materialCache[matId] = isCurrentMatMosaic;
                    if (isCurrentMatMosaic) return true;
                }
            }

            if (_meshNameKeywords != null && _meshNameKeywords.Length > 0)
            {
                Mesh mesh = null;
                var skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
                if (skinnedMeshRenderer != null)
                    mesh = skinnedMeshRenderer.sharedMesh;
                else
                    mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;

                if (mesh != null && ContainsAny(mesh.name, _meshNameKeywords)) return true;
            }
            return CheckComponents(go);
        }

        private bool CheckComponents(GameObject go)
        {
            if (_componentNameKeywords.Length == 0) return false;

            _componentBuffer.Clear();
            go.GetComponents<Component>(_componentBuffer);
            for (int i = 0; i < _componentBuffer.Count; i++)
            {
                var component = _componentBuffer[i];
                if (component == null) continue;

                Type compType = component.GetType();
                if (_componentTypeCache.TryGetValue(compType, out bool isMosaicComp))
                {
                    if (isMosaicComp) return true;
                    continue;
                }

                bool match = ContainsAny(compType.Name, _componentNameKeywords);
                _componentTypeCache[compType] = match;
                if (match) return true;
            }
            _componentBuffer.Clear();
            return false;
        }

        private bool CheckMaterialIsMosaic(Material mat)
        {
            if (ContainsAny(mat.name, _materialNameKeywords)) return true;

            if (mat.shader != null)
            {
                int shaderId = mat.shader.GetInstanceID();
                if (_shaderCache.TryGetValue(shaderId, out bool isShaderMosaic))
                {
                    if (isShaderMosaic) return true;
                }
                else
                {
                    bool currentShaderMosaic = CheckShaderIsMosaic(mat.shader);
                    _shaderCache[shaderId] = currentShaderMosaic;
                    if (currentShaderMosaic) return true;
                }
            }

            if (_textureKeywords.Length > 0)
            {
                try
                {
                    int[] texturePropertyIDs = mat.GetTexturePropertyNameIDs();
                    for (int i = 0; i < texturePropertyIDs.Length; i++)
                    {
                        var texture = mat.GetTexture(texturePropertyIDs[i]);
                        if (texture != null && ContainsAny(texture.name, _textureKeywords)) return true;
                    }
                }
                catch (Exception ex)
                {
                    // 部分 Unity 版本可能不支持该 API，静默降级
                    DemosaicPlugin.Log.LogDebug($"纹理检测 API 不可用: {ex.Message}");
                }
            }
            return false;
        }

        private bool CheckShaderIsMosaic(Shader shader)
        {
            if (ContainsAny(shader.name, _shaderNameKeywords)) return true;

            if (_shaderPropertyKeywords.Length > 0)
            {
                int propCount = shader.GetPropertyCount();
                for (int i = 0; i < propCount; i++)
                {
                    var propName = shader.GetPropertyName(i);
                    if (ContainsAny(propName, _shaderPropertyKeywords)) return true;
                }
            }
            return false;
        }

        private bool ContainsAny(string target, string[] keywords)
        {
            if (string.IsNullOrEmpty(target) || keywords.Length == 0) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (target.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }

    // =================================================================================
    // 马赛克处理器
    // =================================================================================
    public class MosaicProcessor
    {
        private readonly RemoveMode _removeMode;
        private readonly bool _logProcessedObjects;
        private Material _transparentMaterial;
        private readonly string[] _materialKeywords;
        private readonly string[] _shaderKeywords;
        private readonly MosaicDetector _detector;
        private readonly Dictionary<int, Material[]> _transparentMaterialArrays = new Dictionary<int, Material[]>();

        public MosaicProcessor(RemoveMode removeMode, bool logProcessedObjects, string[] materialKeywords = null, string[] shaderKeywords = null, MosaicDetector detector = null)
        {
            _removeMode = removeMode;
            _logProcessedObjects = logProcessedObjects;
            _materialKeywords = materialKeywords;
            _shaderKeywords = shaderKeywords;
            _detector = detector;

            if (_removeMode == RemoveMode.Transparent || _removeMode == RemoveMode.Smart)
            {
                // 按优先级尝试多种着色器，提升不同渲染管线和裁剪配置下的兼容性
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
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
                    _transparentMaterial = new Material(shader);
                    // 防止 Unity GC 回收
                    _transparentMaterial.hideFlags = HideFlags.HideAndDontSave;

                    string shaderName = shader.name;
                    bool isURP = shaderName.Contains("Universal Render Pipeline");
                    if (isURP)
                    {
                        _transparentMaterial.SetFloat("_Surface", 1);
                        _transparentMaterial.SetFloat("_Blend", 0);
                        _transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        _transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        _transparentMaterial.SetInt("_ZWrite", 0);
                        _transparentMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    }
                    else if (shaderName == "Sprites/Default")
                    {
                        // Sprites/Default 不支持 _Mode/_Surface 等属性，直接设置颜色 Alpha 为 0
                        _transparentMaterial.color = Color.clear;
                        _transparentMaterial.renderQueue = 3000;
                    }
                    else if (shaderName.Contains("Unlit"))
                    {
                        // Unlit 系列着色器无光照属性，仅设置混合模式
                        _transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        _transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        _transparentMaterial.SetInt("_ZWrite", 0);
                        _transparentMaterial.renderQueue = 3000;
                        _transparentMaterial.color = Color.clear;
                    }
                    else
                    {
                        // Standard / Standard (Specular setup) / HDRP
                        _transparentMaterial.SetFloat("_Mode", 3);
                        _transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        _transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        _transparentMaterial.SetInt("_ZWrite", 0);
                        _transparentMaterial.DisableKeyword("_ALPHATEST_ON");
                        _transparentMaterial.EnableKeyword("_ALPHABLEND_ON");
                        _transparentMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        _transparentMaterial.renderQueue = 3000;
                        _transparentMaterial.color = Color.clear;
                    }

                    DemosaicPlugin.Log.LogInfo($"透明材质创建成功，使用着色器: {shader.name}");
                }
                else
                {
                    DemosaicPlugin.Log.LogError("找不到任何可用着色器，透明模式将不可用。");
                }
            }
        }

        public void Process(GameObject go)
        {
            if (go == null) return;

            try
            {
                DemosaicPlugin.IsProcessingMaterial = true;

                if (_logProcessedObjects)
                    DemosaicPlugin.Log.LogInfo($"去除马赛克 ({_removeMode})：{go.name}");

                // 禁用阴影，防止透明面片投射黑影块导致阴影区变色/暗色斑块
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    try
                    {
                        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        renderer.receiveShadows = false;
                    }
                    catch { }
                }

                switch (_removeMode)
                {
                    case RemoveMode.Disable:
                        go.SetActive(false);
                        break;
                    case RemoveMode.Destroy:
                        UnityEngine.Object.Destroy(go);
                        break;
                    case RemoveMode.Transparent:
                        if (renderer != null && _transparentMaterial != null)
                        {
                            var mats = renderer.sharedMaterials;
                            int matCount = mats != null ? mats.Length : 0;
                            if (matCount > 0)
                                renderer.sharedMaterials = GetTransparentMaterials(matCount);
                        }
                        else
                        {
                            DemosaicPlugin.Log.LogWarning($"无法对 {go.name} 应用透明模式，降级为禁用对象。");
                            go.SetActive(false);
                        }
                        break;
                    case RemoveMode.Smart:
                        if (_transparentMaterial != null)
                        {
                            if (renderer != null)
                            {
                                // 直接对该 Renderer 做智能处理
                                ProcessSmart(renderer);
                            }
                            else
                            {
                                // 空父节点被对象名命中，遍历子 Renderer 逐个做材质槽替换，而非禁用整棵树
                                var childRenderers = go.GetComponentsInChildren<Renderer>(true);
                                for (int i = 0; i < childRenderers.Length; i++)
                                {
                                    ProcessSmart(childRenderers[i]);
                                }
                            }
                        }
                        else
                        {
                            DemosaicPlugin.Log.LogWarning($"无法对 {go.name} 应用智能模式，降级为禁用对象。");
                            go.SetActive(false);
                        }
                        break;
                }
            }
            finally
            {
                DemosaicPlugin.IsProcessingMaterial = false;
            }
        }

        public void Process(Renderer renderer)
        {
            if (renderer == null) return;

            try
            {
                DemosaicPlugin.IsProcessingMaterial = true;

                // 禁用阴影，防止透明面片投射黑影块导致阴影区变色/暗色斑块
                try
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
                catch { }

                if (_removeMode == RemoveMode.Smart && _transparentMaterial != null)
                {
                    if (_logProcessedObjects)
                        DemosaicPlugin.Log.LogInfo($"去除马赛克 ({_removeMode})：{renderer.name}");
                    ProcessSmart(renderer);
                    return;
                }
                Process(renderer.gameObject);
            }
            finally
            {
                DemosaicPlugin.IsProcessingMaterial = false;
            }
        }

        /// <summary>
        /// Smart 模式：仅替换匹配马赛克关键词的材质槽，保留同对象上的非马赛克材质。
        /// 解决马赛克网格与正常网格共用同一 GameObject 的问题。
        /// </summary>
        private void ProcessSmart(Renderer renderer)
        {
            var sharedMats = renderer.sharedMaterials;
            if (sharedMats == null || sharedMats.Length == 0)
            {
                if (_detector != null && _detector.IsObjectNameOrParentMosaic(renderer.gameObject))
                {
                    renderer.gameObject.SetActive(false);
                }
                return;
            }

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
                // 仅替换匹配马赛克关键词的材质槽
                for (int i = 0; i < sharedMats.Length; i++)
                {
                    if (IsMosaicMaterial(sharedMats[i]))
                        sharedMats[i] = _transparentMaterial;
                }
                renderer.sharedMaterials = sharedMats;
                if (_logProcessedObjects)
                    DemosaicPlugin.Log.LogInfo($"已去除马赛克 (智能模式 - 仅替换马赛克材质槽): {renderer.name}");
            }
            else
            {
                // 只有当 GameObject 名字或父节点名字命中关键词时，才回退为全透明，防止网格名命中误伤整体皮肤
                if (_detector != null && _detector.IsObjectNameOrParentMosaic(renderer.gameObject))
                {
                    renderer.sharedMaterials = GetTransparentMaterials(sharedMats.Length);
                    if (_logProcessedObjects)
                        DemosaicPlugin.Log.LogInfo($"已去除马赛克 (智能模式 - 全透明回退): {renderer.name}");
                }
                else
                {
                    if (_logProcessedObjects)
                        DemosaicPlugin.Log.LogWarning($"跳过智能模式全透明回退以避免假阳性: {renderer.name} (仅网格名或组件名命中)");
                }
            }
        }

        private bool IsMosaicMaterial(Material mat)
        {
            if (mat == null) return false;

            // 委托给检测器的完整检测逻辑（材质名 + Shader名 + Shader属性 + 纹理名），并自动缓存
            if (_detector != null)
                return _detector.CheckMaterialFull(mat);

            // 无检测器时的后备路径
            if (_materialKeywords != null)
            {
                for (int i = 0; i < _materialKeywords.Length; i++)
                {
                    if (mat.name.IndexOf(_materialKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            if (_shaderKeywords != null && mat.shader != null)
            {
                string shaderName = mat.shader.name;
                for (int i = 0; i < _shaderKeywords.Length; i++)
                {
                    if (shaderName.IndexOf(_shaderKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        private Material[] GetTransparentMaterials(int materialCount)
        {
            if (!_transparentMaterialArrays.TryGetValue(materialCount, out var materials))
            {
                materials = new Material[materialCount];
                for (int i = 0; i < materialCount; i++) materials[i] = _transparentMaterial;
                _transparentMaterialArrays[materialCount] = materials;
            }
            return materials;
        }

        public void Dispose()
        {
            if (_transparentMaterial != null)
                UnityEngine.Object.Destroy(_transparentMaterial);
        }
    }

    // =================================================================================
    // 安全输入管理器（兼容新旧 Input System）
    // =================================================================================
    internal static class SafeInput
    {
        private static bool _legacyInputAvailable = true;
        private static bool _warned = false;
        private static PropertyInfo _keyboardCurrentProp;
        private static MethodInfo _keyboardIndexer;
        private static PropertyInfo _keyWasPressedProp;
        private static PropertyInfo _keyIsPressedProp;
        private static bool _newInputInitialized = false;
        private static Type _keyEnumType;
        private static readonly Dictionary<KeyCode, object> _keyEnumCache = new Dictionary<KeyCode, object>();

        public static bool GetKeyDown(KeyCode key)
        {
            if (_legacyInputAvailable)
            {
                try
                {
                    return Input.GetKeyDown(key);
                }
                catch (Exception ex)
                {
                    if (ex is InvalidOperationException ||
                        ex.Message.Contains("Input System") ||
                        ex.InnerException?.Message.Contains("Input System") == true)
                    {
                        _legacyInputAvailable = false;
                        if (!_warned)
                        {
                            DemosaicPlugin.Log.LogWarning("检测到游戏使用新版 Input System，旧版 Input 已禁用。尝试初始化新输入系统支持...");
                            _warned = true;
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            return TryNewInputSystemKeyDown(key);
        }

        private static object GetKeyEnumValue(KeyCode key)
        {
            if (_keyEnumType == null) return null;
            if (!_keyEnumCache.TryGetValue(key, out var keyObj))
            {
                try
                {
                    keyObj = Enum.Parse(_keyEnumType, key.ToString(), ignoreCase: true);
                }
                catch
                {
                    keyObj = null;
                }
                _keyEnumCache[key] = keyObj;
            }
            return keyObj;
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

                if (_keyboardCurrentProp == null || _keyboardIndexer == null || (_keyWasPressedProp == null && _keyIsPressedProp == null))
                    return false;

                var keyboardInstance = _keyboardCurrentProp.GetValue(null);
                if (keyboardInstance == null) return false;

                var keyEnum = GetKeyEnumValue(key);
                if (keyEnum == null) return false;

                var keyControl = _keyboardIndexer.Invoke(keyboardInstance, new[] { keyEnum });
                if (keyControl == null) return false;

                if (_keyWasPressedProp != null)
                {
                    var result = _keyWasPressedProp.GetValue(keyControl);
                    if (result is bool b) return b;
                }
                else if (_keyIsPressedProp != null)
                {
                    var result = _keyIsPressedProp.GetValue(keyControl);
                    if (result is bool b) return b;
                }

                return false;
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
                Type keyboardType = null;
                _keyEnumType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name;
                    if (asmName.Contains("InputSystem") || asmName.Contains("Unity.InputSystem"))
                    {
                        if (keyboardType == null) keyboardType = asm.GetType("UnityEngine.InputSystem.Keyboard");
                        if (_keyEnumType == null) _keyEnumType = asm.GetType("UnityEngine.InputSystem.Key");
                    }
                }

                if (keyboardType == null)
                    keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (_keyEnumType == null)
                    _keyEnumType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");

                if (keyboardType == null || _keyEnumType == null)
                {
                    DemosaicPlugin.Log.LogWarning("未能找到 InputSystem.Keyboard 或 Key 类型，快捷键功能将不可用。");
                    return;
                }

                _keyboardCurrentProp = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _keyboardIndexer = keyboardType.GetProperty("Item", new[] { _keyEnumType })?.GetGetMethod()
                                  ?? keyboardType.GetMethod("get_Item", new[] { _keyEnumType });

                // 查找 ButtonControl / KeyControl 的 wasPressedThisFrame / isPressed 属性
                Type buttonControlType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name;
                    if (asmName.Contains("InputSystem") || asmName.Contains("Unity.InputSystem"))
                    {
                        buttonControlType = asm.GetType("UnityEngine.InputSystem.Controls.ButtonControl")
                                         ?? asm.GetType("UnityEngine.InputSystem.Controls.KeyControl");
                        if (buttonControlType != null) break;
                    }
                }
                if (buttonControlType == null)
                    buttonControlType = Type.GetType("UnityEngine.InputSystem.Controls.ButtonControl, Unity.InputSystem")
                                     ?? Type.GetType("UnityEngine.InputSystem.Controls.KeyControl, Unity.InputSystem");

                if (buttonControlType != null)
                {
                    _keyWasPressedProp = buttonControlType.GetProperty("wasPressedThisFrame");
                    _keyIsPressedProp = buttonControlType.GetProperty("isPressed");
                }

                if (_keyboardCurrentProp != null && _keyboardIndexer != null && (_keyWasPressedProp != null || _keyIsPressedProp != null))
                    DemosaicPlugin.Log.LogInfo("新版 Input System 键盘支持初始化成功。");
                else
                    DemosaicPlugin.Log.LogWarning($"Input System Keyboard 初始化部分缺失 (current={_keyboardCurrentProp != null}, indexer={_keyboardIndexer != null}, wasPressed={_keyWasPressedProp != null})，快捷键可能不可用。");
            }
            catch (Exception ex)
            {
                DemosaicPlugin.Log.LogWarning($"初始化新版 Input System 失败: {ex.Message}。快捷键功能将不可用。");
            }
        }
    }

    internal static class SceneObjectFinder
    {
        private static MethodInfo _genericFindObjectsByTypeMethod;
        private static MethodInfo _findObjectsByTypeMethod;
        private static object _includeInactive;
        private static object _excludeInactive;
        private static object _sortNone;
        private static bool _initialized;

        public static Renderer[] FindRenderers(bool includeInactive)
        {
            Initialize();
            if (_genericFindObjectsByTypeMethod != null)
            {
                try
                {
                    var result = _genericFindObjectsByTypeMethod.Invoke(null, new[] { includeInactive ? _includeInactive : _excludeInactive, _sortNone });
                    if (result is Renderer[] genericRenderers)
                        return genericRenderers;
                }
                catch
                {
                    _genericFindObjectsByTypeMethod = null;
                }
            }

            if (_findObjectsByTypeMethod != null)
            {
                try
                {
                    var result = _findObjectsByTypeMethod.Invoke(null, new[] { typeof(Renderer), includeInactive ? _includeInactive : _excludeInactive, _sortNone });
                    if (result is Renderer[] renderers)
                        return renderers;
                    if (result is UnityEngine.Object[] objects)
                    {
                        var converted = new Renderer[objects.Length];
                        int count = 0;
                        for (int i = 0; i < objects.Length; i++)
                        {
                            if (objects[i] is Renderer renderer)
                                converted[count++] = renderer;
                        }
                        if (count == converted.Length)
                            return converted;
                        Array.Resize(ref converted, count);
                        return converted;
                    }
                }
                catch
                {
                    _findObjectsByTypeMethod = null;
                }
            }

            return UnityEngine.Object.FindObjectsOfType<Renderer>();
        }

        private static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            var objectType = typeof(UnityEngine.Object);
            var inactiveType = objectType.Assembly.GetType("UnityEngine.FindObjectsInactive");
            var sortModeType = objectType.Assembly.GetType("UnityEngine.FindObjectsSortMode");
            if (inactiveType == null || sortModeType == null) return;

            _includeInactive = Enum.Parse(inactiveType, "Include");
            _excludeInactive = Enum.Parse(inactiveType, "Exclude");
            _sortNone = Enum.Parse(sortModeType, "None");
            _genericFindObjectsByTypeMethod = objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
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
            _findObjectsByTypeMethod = objectType.GetMethod(
                "FindObjectsByType",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Type), inactiveType, sortModeType },
                null);
        }
    }
}
