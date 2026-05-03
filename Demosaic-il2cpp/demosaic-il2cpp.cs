using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
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
        Transparent
    }

    [BepInPlugin("demosaic", "Demosaic", "1.4.1")]
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
        internal KeyCode ForceScanHotkeyValue => forceScanHotkey.Value;
        internal ConfigEntry<float> periodicScanInterval;
        internal ConfigEntry<float> sceneLoadScanDelay;
        internal ConfigEntry<int> scanBatchSize;

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
        private ConfigEntry<string> methodPatchTargetAssemblies;

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

        private readonly HashSet<Renderer> processedRenderers = new HashSet<Renderer>();
        private Material transparentMaterial;

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
            mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger);

            lifecycleManager = AddComponent<PluginLifecycleManager>();
            Logger.LogInfo($"Demosaic加载成功！去除方式: {removeMode.Value}");

            try
            {
                harmony = new Harmony("demosaic");
                harmony.PatchAll(typeof(DemosaicPlugin).Assembly);
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
            removeMode = Config.Bind("Remove", "Mode", RemoveMode.Disable, "去除方式：Disable=禁用GameObject，Transparent=替换为透明材质");
            forceScanHotkey = Config.Bind("General", "ForceScanHotkey", KeyCode.F10, "按下此快捷键可强制重新扫描");
            exportSceneKey = Config.Bind("General", "ExportSceneKey", KeyCode.F11, "按下此键将场景中所有渲染器信息导出到日志");

            periodicScanInterval = Config.Bind("Scan", "PeriodicScanInterval", 10f, "定期场景扫描的间隔（秒）。设置为0可禁用。");
            sceneLoadScanDelay = Config.Bind("Scan", "SceneLoadScanDelay", 1.5f, "新场景加载后延迟扫描的时间（秒）。");
            scanBatchSize = Config.Bind("Scan", "ScanBatchSize", 500, "全场景扫描时每帧处理的对象数量，防止卡顿。");

            objectKeywords = Config.Bind("Detection", "ObjectNameKeywords", "mosaic,censored,pixelated", "游戏对象名称的关键词，逗号分隔");
            materialKeywords = Config.Bind("Detection", "MaterialNameKeywords", "mosaic,censored,pixel", "材质名称的关键词，逗号分隔");
            textureKeywords = Config.Bind("Detection", "TextureKeywords", "mosaic", "纹理名称的关键词，逗号分隔");
            shaderKeywords = Config.Bind("Detection", "ShaderNameKeywords", "mosaic,pixelate,censor,moza", "着色器名称的关键词，逗号分隔");
            meshKeywords = Config.Bind("Detection", "MeshNameKeywords", "censor,mosaic,moza", "网格名称的关键词，逗号分隔");
            componentNameKeywords = Config.Bind("Detection", "ComponentNameKeywords", "", "组件名称的关键词，逗号分隔");
            shaderPropertyKeywords = Config.Bind("Detection", "ShaderPropertyKeywords", "_PixelSize,_BlockSize,_MosaicFactor", "着色器属性名称的关键词，逗号分隔");
            exclusionKeywords = Config.Bind("Detection", "ExclusionKeywords", "", "白名单关键词（最高优先级），逗号分隔");

            disableMethods = Config.Bind("Advanced", "DisableMethods", false, "是否启用方法名拦截（反射扫描，谨慎使用）");
            methodDisableKeywords = Config.Bind("Advanced", "MethodDisableKeywords", "censor,mosaic", "方法名拦截关键词");
            methodPatchTargetAssemblies = Config.Bind("Advanced", "MethodPatchTargetAssemblies", "Assembly-CSharp", "需要进行方法扫描的目标程序集名称，逗号分隔");

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
                shaderPropertyKeywordList
            );

            methodDisableKeywordList = ParseKeywordString(methodDisableKeywords.Value);
        }

        private void CreateTransparentMaterial()
        {
            var shader = Shader.Find("Standard");
            if (shader != null)
            {
                transparentMaterial = new Material(shader);
                transparentMaterial.SetFloat("_Mode", 3);
                transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                transparentMaterial.SetInt("_ZWrite", 0);
                transparentMaterial.DisableKeyword("_ALPHATEST_ON");
                transparentMaterial.EnableKeyword("_ALPHABLEND_ON");
                transparentMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                transparentMaterial.renderQueue = 3000;
                transparentMaterial.color = Color.clear;
            }
            else
            {
                Logger.LogError("未能找到 Standard 着色器。透明模式将无法正常工作。");
            }
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
            processedRenderers.Clear();
            mosaicDetector.ClearCache();
        }

        internal bool ProcessRenderer(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled || processedRenderers.Contains(renderer)) return false;

            if (mosaicDetector.IsMosaic(renderer))
            {
                mosaicProcessor.Process(renderer);
                processedRenderers.Add(renderer);
                return true;
            }
            return false;
        }

        public void ProcessNewGameObject(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return;

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                ProcessRenderer(renderer);
            }
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e.ChangedSetting.Definition.Key.Contains("Keywords"))
            {
                ReloadAllKeywords();
                mosaicDetector.ClearCache();
            }
            else if (e.ChangedSetting.Definition.Key == removeMode.Definition.Key)
            {
                mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger);
                Logger.LogInfo($"处理模式已更新为: {removeMode.Value}。");
            }
        }

        private void PatchMethodsByName(Harmony harmonyInstance)
        {
            if (methodDisableKeywordList == null || methodDisableKeywordList.Count == 0) return;

            Logger.LogInfo("开始动态扫描并禁用匹配关键词的方法...");
            int patchedCount = 0;

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
                            if (method.IsSpecialName || method.IsGenericMethod || method.IsAbstract) continue;

                            if (methodDisableKeywordList.Any(keyword =>
                                    method.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                try
                                {
                                    harmonyInstance.Patch(method,
                                        new HarmonyMethod(typeof(GenericDisablePatch), nameof(GenericDisablePatch.Prefix)));
                                    patchedCount++;
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            Logger.LogInfo($"动态方法扫描完成，共禁用了 {patchedCount} 个方法。");
        }

        public override bool Unload()
        {
            harmony?.UnpatchSelf();
            Config.SettingChanged -= OnSettingChanged;
            if (lifecycleManager != null)
                GameObject.Destroy(lifecycleManager.gameObject);

            if (transparentMaterial != null)
                UnityEngine.Object.Destroy(transparentMaterial);

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
        private readonly ManualLogSource logger;

        private Dictionary<int, bool> materialCache = new Dictionary<int, bool>();
        private Dictionary<int, bool> shaderCache = new Dictionary<int, bool>();
        private Dictionary<string, bool> componentTypeCache = new Dictionary<string, bool>();

        public MosaicDetector(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public void UpdateKeywords(
            List<string> objKeywords, List<string> matKeywords, List<string> texKeywords,
            List<string> shadKeywords, List<string> mshKeywords,
            List<string> exclKeywords, List<string> compKeywords,
            List<string> shadPropKeywords)
        {
            objectKeywordList = objKeywords;
            materialKeywordList = matKeywords;
            textureKeywordList = texKeywords;
            shaderKeywordList = shadKeywords;
            meshKeywordList = mshKeywords;
            exclusionKeywordList = exclKeywords;
            componentNameKeywordList = compKeywords;
            shaderPropertyKeywordList = shadPropKeywords;
        }

        public void ClearCache()
        {
            materialCache.Clear();
            shaderCache.Clear();
            componentTypeCache.Clear();
        }

        public bool IsMosaic(Renderer renderer)
        {
            var go = renderer.gameObject;

            if (NameContainsKeyword(go.name, exclusionKeywordList)) return false;
            if (CheckAndLog(go.name, objectKeywordList, "对象名检测")) return true;

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

            Mesh mesh = (renderer is SkinnedMeshRenderer smr)
                ? smr.sharedMesh
                : (renderer.GetComponent<MeshFilter>()?.sharedMesh);
            if (mesh != null && CheckAndLog(mesh.name, meshKeywordList, "网格名检测")) return true;

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
        private readonly Material transparentMaterial;
        private readonly ManualLogSource logger;

        public MosaicProcessor(RemoveMode removeMode, Material transparentMaterial, ManualLogSource logger)
        {
            this.removeMode = removeMode;
            this.transparentMaterial = transparentMaterial;
            this.logger = logger;
        }

        public void Process(Renderer renderer)
        {
            if (removeMode == RemoveMode.Transparent)
            {
                if (transparentMaterial != null)
                {
                    var sharedMats = renderer.sharedMaterials;
                    var newMaterials = new Il2CppReferenceArray<Material>(sharedMats.Length);
                    for (int i = 0; i < newMaterials.Length; i++)
                        newMaterials[i] = transparentMaterial;
                    renderer.sharedMaterials = newMaterials;
                    logger.LogInfo($"已去除马赛克 (透明模式): {renderer.name}");
                    return;
                }
                else
                {
                    logger.LogWarning($"透明材质缺失，降级为 Disable 模式: {renderer.name}");
                }
            }

            renderer.gameObject.SetActive(false);
            logger.LogInfo($"已去除马赛克 (禁用模式): {renderer.name}");
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
                if (_delayScanTimer >= DemosaicPlugin.Instance.sceneLoadScanDelay.Value)
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
            if (Input.GetKeyDown(DemosaicPlugin.Instance.ForceScanHotkeyValue))
            {
                DemosaicPlugin.Logger.LogInfo("快捷键被按下，强制重新执行全场景扫描...");
                DemosaicPlugin.Instance.NotifySceneUnloaded();
                StartBatchScan();
            }

            // 4. 新增：导出场景资源热键
            if (Input.GetKeyDown(DemosaicPlugin.Instance.exportSceneKey.Value))
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
            _batchRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            _currentBatchIndex = 0;
            _isBatchScanning = true;
            DemosaicPlugin.Logger.LogDebug($"开始分批扫描 {_batchRenderers.Length} 个渲染器...");
        }

        private void ProcessBatchScan()
        {
            var renderers = _batchRenderers;
            if (!_isBatchScanning || renderers == null) return;

            int batchSize = DemosaicPlugin.Instance.scanBatchSize.Value;
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
            }
        }

        // ==================== 导出功能实现 ====================
        private void StartExport()
        {
            if (_isExporting) return;
            _exportRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            _exportCurrentIndex = 0;
            _isExporting = true;
            DemosaicPlugin.Logger.LogInfo($"准备导出 {_exportRenderers.Length} 个渲染器信息...");
        }

        private void ProcessExportBatch()
        {
            var renderers = _exportRenderers;
            if (!_isExporting || renderers == null) return;

            int batchSize = DemosaicPlugin.Instance.scanBatchSize.Value;
            int processed = 0;

            while (_exportCurrentIndex < renderers.Length && processed < batchSize)
            {
                var renderer = renderers[_exportCurrentIndex];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    var go = renderer.gameObject;
                    string meshName = "N/A";
                    if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                        meshName = smr.sharedMesh.name;
                    else if (renderer is MeshRenderer)
                    {
                        var mf = renderer.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                            meshName = mf.sharedMesh.name;
                    }

                    var mats = renderer.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var mat = mats[i];
                        if (mat != null)
                        {
                            DemosaicPlugin.Logger.LogInfo(
                                $"[Demosaic Export] GO: {go.name} | Material: {mat.name} | Shader: {(mat.shader != null ? mat.shader.name : "N/A")} | Mesh: {meshName}");
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

    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object) })]
    class Object_Instantiate_Patch
    {
        static void Postfix(UnityEngine.Object __result)
        {
            if (DemosaicPlugin.Instance == null) return;
            try { if (__result is GameObject go) DemosaicPlugin.Instance.ProcessNewGameObject(go); }
            catch (Exception ex) { DemosaicPlugin.Logger.LogError($"处理实例化对象错误: {ex}"); }
        }
    }

    public static class GenericDisablePatch
    {
        public static bool Prefix() => false;
    }
}