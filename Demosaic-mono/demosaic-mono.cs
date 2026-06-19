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

    [BepInPlugin("demosaic", "Demosaic", "1.4.0")]
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
        private ConfigEntry<string> _assemblyNamesToPatch;

        // GC 优化复用
        private WaitForSeconds _periodicWait;
        private WaitForSeconds _delayWait;
        private List<Renderer> _rendererBuffer = new List<Renderer>();
        private HashSet<int> _processedRendererIds = new HashSet<int>();
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
            _harmony = new Harmony("com.yourname.demosaic.harmony");
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
        }

        private void LoadConfig()
        {
            _enablePlugin = Config.Bind("1. 通用", "EnablePlugin", true, "启用或禁用整个插件。");
            _removeMode = Config.Bind("1. 通用", "RemoveMode", RemoveMode.Smart, "移除方式：Disable (禁用), Destroy (销毁), Transparent (透明), Smart (仅替换马赛克材质槽)");
            _manualScanKey = Config.Bind("1. 通用", "ManualScanKey", KeyCode.F10, "按下此键可手动扫描场景。");
            _includeInactiveObjects = Config.Bind("1. 通用", "IncludeInactiveObjects", true, "扫描时是否包含未激活对象。Unity 2022+ 会优先使用更快的 FindObjectsByType。");
            _detectParentObjectNames = Config.Bind("1. 通用", "DetectParentObjectNames", true, "检测 Renderer 的父节点名称，适合 MosaicRoot/Quad 这类层级。");
            _logProcessedObjects = Config.Bind("1. 通用", "LogProcessedObjects", false, "是否为每个被处理对象写 Info 日志。大量对象会明显影响性能。");

            _periodicScanInterval = Config.Bind("2. 扫描", "PeriodicScanInterval", 10f, "周期扫描间隔（秒），0 则禁用。");
            _sceneLoadScanDelay = Config.Bind("2. 扫描", "SceneLoadScanDelay", 1.5f, "场景加载后延迟扫描的时间（秒）。");
            _scanBatchSize = Config.Bind("2. 扫描", "ScanBatchSize", 500, "每帧最多处理的对象数量，防止卡顿。");

            _objectNameKeywords = Config.Bind("3. 关键词", "ObjectNameKeywords", "moza,mosaic,mozic,mazic", "对象名关键词。");
            _materialNameKeywords = Config.Bind("3. 关键词", "MaterialNameKeywords", "moza,mosaic,mozic,mazic", "材质名关键词。");
            _shaderNameKeywords = Config.Bind("3. 关键词", "ShaderNameKeywords", "moza,mosaic,censorb,mozic,mazic", "着色器名关键词。");
            _meshNameKeywords = Config.Bind("3. 关键词", "MeshNameKeywords", "moza,mosaic,censorb,mozic,mazic", "网格名关键词。");
            _textureKeywords = Config.Bind("3. 关键词", "TextureKeywords", "mosaic", "纹理名关键词。");
            _componentNameKeywords = Config.Bind("3. 关键词", "ComponentNameKeywords", "", "组件名关键词。");
            _shaderPropertyKeywords = Config.Bind("3. 关键词", "ShaderPropertyKeywords", "moza,mosaic", "着色器属性名关键词。");
            _exclusionKeywords = Config.Bind("3. 关键词", "ExclusionKeywords", "", "白名单关键词，命中后不会处理该对象。");

            _disableMethods = Config.Bind("4. 高级", "DisableMethods", false, "启用按名称拦截方法（慎用）。");
            _methodDisableKeywords = Config.Bind("4. 高级", "MethodDisableKeywords", "moza,mosaic", "要拦截的方法名的关键词。");
            _assemblyNamesToPatch = Config.Bind("4. 高级", "AssemblyNamesToPatch", "Assembly-CSharp", "要扫描的程序集名称，逗号分隔。");
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
            }
            catch (Exception e)
            {
                Log.LogError("应用 Harmony 补丁时出错: " + e);
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

            var assemblyNames = _assemblyNamesToPatch.Value.Split(new[] { ',' })
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => assemblyNames.Contains(asm.GetName().Name))
                .ToList();

            if (!assemblies.Any()) return;

            var emptyPrefix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(EmptyPatch));
            int patchedCount = 0;

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

                            if (keywords.Any(keyword => method.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                try
                                {
                                    _harmony.Patch(method, prefix: emptyPrefix);
                                    patchedCount++;
                                    Log.LogDebug($"已拦截方法: {type.FullName}.{method.Name}");
                                }
                                catch (Exception) { /* 忽略 */ }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }
            Log.LogInfo($"方法拦截完成，共拦截 {patchedCount} 个方法。");
        }

        private static bool EmptyPatch() => false;

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
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    shader = Shader.Find("HDRP/Lit");
                    if (shader == null)
                        shader = Shader.Find("HD Render Pipeline/Lit");
                }
                if (shader == null)
                    shader = Shader.Find("Standard");

                if (shader != null)
                {
                    _transparentMaterial = new Material(shader);
                    // Check if it's URP/HDRP or Standard to set properties
                    bool isURP = shader.name.Contains("Universal Render Pipeline");
                    if (isURP)
                    {
                        _transparentMaterial.SetFloat("_Surface", 1);
                        _transparentMaterial.SetFloat("_Blend", 0);
                        _transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        _transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        _transparentMaterial.SetInt("_ZWrite", 0);
                        _transparentMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    }
                    else
                    {
                        _transparentMaterial.SetFloat("_Mode", 3);
                        _transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        _transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        _transparentMaterial.SetInt("_ZWrite", 0);
                        _transparentMaterial.DisableKeyword("_ALPHATEST_ON");
                        _transparentMaterial.EnableKeyword("_ALPHABLEND_ON");
                        _transparentMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    }
                    _transparentMaterial.renderQueue = 3000;
                    _transparentMaterial.color = Color.clear;
                }
                else
                {
                    DemosaicPlugin.Log.LogError("找不到 URP Lit、HDRP Lit 或 Standard 着色器，透明模式将不可用。");
                }
            }
        }

        public void Process(GameObject go)
        {
            if (go == null) return;

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
                        int matCount = renderer.sharedMaterials.Length;
                        renderer.sharedMaterials = GetTransparentMaterials(matCount);
                    }
                    else
                    {
                        DemosaicPlugin.Log.LogWarning($"无法对 {go.name} 应用透明模式，降级为禁用对象。");
                        go.SetActive(false);
                    }
                    break;
                case RemoveMode.Smart:
                    if (renderer != null && _transparentMaterial != null)
                    {
                        ProcessSmart(renderer);
                    }
                    else
                    {
                        DemosaicPlugin.Log.LogWarning($"无法对 {go.name} 应用智能模式，降级为禁用对象。");
                        go.SetActive(false);
                    }
                    break;
            }
        }

        public void Process(Renderer renderer)
        {
            if (renderer == null) return;

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

        /// <summary>
        /// Smart 模式：仅替换匹配马赛克关键词的材质槽，保留同对象上的非马赛克材质。
        /// 解决马赛克网格与正常网格共用同一 GameObject 的问题。
        /// </summary>
        private void ProcessSmart(Renderer renderer)
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

            // 优先从共享材质缓存查询
            if (_detector != null && _detector.IsMaterialCached(mat, out bool isMosaic))
            {
                return isMosaic;
            }

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
        private static MethodInfo _keyboardCurrentMethod;
        private static MethodInfo _keyIsPressedMethod;
        private static object _keyboardInstance;
        private static bool _newInputInitialized = false;
        private static readonly Dictionary<KeyCode, object[]> _keyArgsCache = new Dictionary<KeyCode, object[]>();

        public static bool GetKeyDown(KeyCode key)
        {
            if (_legacyInputAvailable)
            {
                try
                {
                    return Input.GetKeyDown(key);
                }
                catch (InvalidOperationException)
                {
                    _legacyInputAvailable = false;
                    if (!_warned)
                    {
                        DemosaicPlugin.Log.LogWarning("检测到游戏使用新版 Input System，旧版 Input 已禁用。尝试初始化新输入系统支持...");
                        _warned = true;
                    }
                }
            }

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
                Type keyboardType = null;
                Type keyEnumType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name;
                    if (asmName.Contains("InputSystem") || asmName.Contains("Unity.InputSystem"))
                    {
                        if (keyboardType == null) keyboardType = asm.GetType("UnityEngine.InputSystem.Keyboard");
                        if (keyEnumType == null) keyEnumType = asm.GetType("UnityEngine.InputSystem.Key");
                    }
                }

                if (keyboardType == null)
                    keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (keyEnumType == null)
                    keyEnumType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");

                if (keyboardType == null)
                {
                    DemosaicPlugin.Log.LogWarning("未能找到 InputSystem.Keyboard 类型，快捷键功能将不可用。");
                    return;
                }

                _keyboardCurrentMethod = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetMethod;

                if (keyEnumType != null)
                {
                    _keyIsPressedMethod = keyboardType.GetMethod("IsKeyPressed", new[] { keyEnumType });
                    if (_keyIsPressedMethod == null)
                        _keyIsPressedMethod = keyboardType.GetMethod("wasKeyPressedThisFrame", new[] { keyEnumType });
                }

                if (_keyboardCurrentMethod != null && _keyIsPressedMethod != null)
                    DemosaicPlugin.Log.LogInfo("新版 Input System 键盘支持初始化成功。");
                else
                    DemosaicPlugin.Log.LogWarning($"Input System Keyboard 方法未找到，快捷键不可用。");
            }
            catch (Exception ex)
            {
                DemosaicPlugin.Log.LogWarning($"初始化新版 Input System 失败: {ex.Message}。快捷键功能将不可用。");
            }
        }

        private static object MapKeyCodeToKey(KeyCode keyCode)
        {
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
