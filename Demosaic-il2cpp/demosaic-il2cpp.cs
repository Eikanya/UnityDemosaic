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
    /// <summary>
    /// 去除马赛克的处理模式
    /// </summary>
    public enum RemoveMode
    {
        Disable,     // 禁用对象 (推荐：性能最好，不破坏游戏原本的依赖和逻辑)
        Transparent  // 替换为透明材质 (安全，视觉上消失)
    }

    // =================================================================================
    // 主插件类 (IL2CPP)
    // =================================================================================
    [BepInPlugin("demosaic", "Demosaic", "1.3.0")] 
    public class DemosaicPlugin : BasePlugin
    {
        public static DemosaicPlugin Instance { get; private set; }
        internal static ManualLogSource Logger { get; private set; }

        private Harmony harmony;
        private MosaicDetector mosaicDetector;
        private MosaicProcessor mosaicProcessor;
        private PluginLifecycleManager lifecycleManager;

        // --- 配置项 ---
        private ConfigEntry<bool> enablePlugin;
        private ConfigEntry<RemoveMode> removeMode;
        private ConfigEntry<KeyCode> forceScanHotkey;
        internal KeyCode ForceScanHotkeyValue => forceScanHotkey.Value;

        // 新增：与 Mono 版对齐的扫描配置
        internal ConfigEntry<float> periodicScanInterval;
        internal ConfigEntry<float> sceneLoadScanDelay;
        internal ConfigEntry<int> scanBatchSize;

        private ConfigEntry<string> keywords;
        private ConfigEntry<string> shaderKeywords;
        private ConfigEntry<string> meshKeywords;
        private ConfigEntry<string> exclusionKeywords;
        private ConfigEntry<string> componentNameKeywords;
        private ConfigEntry<string> shaderPropertyKeywords;
        private ConfigEntry<string> methodDisableKeywords;
        private ConfigEntry<string> methodPatchTargetAssemblies;

        // --- 优化缓存 ---
        private List<string> keywordList;
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private List<string> componentNameKeywordList;
        private List<string> shaderPropertyKeywordList;
        private List<string> methodDisableKeywordList;
        
        // 记录已处理的渲染器，防止重复替换
        private readonly HashSet<Renderer> processedRenderers = new HashSet<Renderer>();
        private Material transparentMaterial;

        // 默认关键词常量
        private const string DefaultKeywords = "mosaic,censor,pixelated";
        private const string DefaultShaderKeywords = "mosaic,censor,pixelate";
        private const string DefaultMeshKeywords = "mosaic,censor";
        private const string DefaultExclusionKeywords = "";
        private const string DefaultComponentNameKeywords = "Mosaic,CensorEffect";
        private const string DefaultShaderPropertyKeywords = "_PixelSize,_BlockSize,_MosaicFactor";

        public override void Load()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (Exception ex) { Log.LogWarning(string.Format("无法设置控制台输出编码为UTF-8: {0}", ex.Message)); }

            Instance = this;
            Logger = Log;

            SetupConfiguration();

            if (!enablePlugin.Value) return;

            // IL2CPP 特有：注册 MonoBehaviour 到 Unity 生命周期
            ClassInjector.RegisterTypeInIl2Cpp<PluginLifecycleManager>();

            mosaicDetector = new MosaicDetector(Logger);
            ReloadAllKeywords();

            CreateTransparentMaterial();
            mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger);

            // 挂载生命周期管理器
            lifecycleManager = AddComponent<PluginLifecycleManager>();
            Logger.LogInfo(string.Format("Demosaic加载成功！去除方式: {0}", removeMode.Value));

            try
            {
                harmony = new Harmony("demosaic");
                harmony.PatchAll(typeof(DemosaicPlugin).Assembly);
                Logger.LogInfo("Harmony 补丁应用成功，已启用实时对象拦截。");

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

            // 同步补全 Mono 版的高级扫描配置
            periodicScanInterval = Config.Bind("Scan", "PeriodicScanInterval", 10f, "定期场景扫描的间隔（秒）。设置为0可禁用。");
            sceneLoadScanDelay = Config.Bind("Scan", "SceneLoadScanDelay", 1.5f, "新场景加载后延迟扫描的时间（秒）。");
            scanBatchSize = Config.Bind("Scan", "ScanBatchSize", 500, "全场景扫描时每帧处理的对象数量，防止卡顿。");
            
            keywords = Config.Bind("Detection", "Keywords", DefaultKeywords, "对象/材质/纹理名称的关键词，逗号分隔");
            shaderKeywords = Config.Bind("Detection", "ShaderKeywords", DefaultShaderKeywords, "着色器的关键词，逗号分隔");
            meshKeywords = Config.Bind("Detection", "MeshKeywords", DefaultMeshKeywords, "网格的关键词，逗号分隔");
            componentNameKeywords = Config.Bind("Detection", "ComponentNameKeywords", DefaultComponentNameKeywords, "组件名称的关键词，逗号分隔");
            shaderPropertyKeywords = Config.Bind("Detection", "ShaderPropertyKeywords", DefaultShaderPropertyKeywords, "着色器属性的关键词，逗号分隔");
            exclusionKeywords = Config.Bind("Detection", "ExclusionKeywords", DefaultExclusionKeywords, "白名单关键词（最高优先级），逗号分隔");

            methodDisableKeywords = Config.Bind("Advanced", "MethodDisableKeywords", "censor,mosaic", "方法名拦截关键词");
            methodPatchTargetAssemblies = Config.Bind("Advanced", "MethodPatchTargetAssemblies", "Assembly-CSharp", "需要进行方法扫描的目标程序集名称");

            Config.SettingChanged += OnSettingChanged;
            
            try
            {
                var configFileType = typeof(ConfigEntryBase).Assembly.GetType("BepInEx.Configuration.ConfigFile");
                var saveProp = configFileType?.GetProperty("SaveOnConfigChanged", BindingFlags.Public | BindingFlags.Instance);
                if (saveProp != null && saveProp.CanWrite) saveProp.SetValue(Config, false);
            }
            catch { /* ignore */ }
        }

        private void ReloadAllKeywords()
        {
            keywordList = ParseKeywordString(keywords.Value);
            shaderKeywordList = ParseKeywordString(shaderKeywords.Value);
            meshKeywordList = ParseKeywordString(meshKeywords.Value);
            exclusionKeywordList = ParseKeywordString(exclusionKeywords.Value);
            componentNameKeywordList = ParseKeywordString(componentNameKeywords.Value);
            shaderPropertyKeywordList = ParseKeywordString(shaderPropertyKeywords.Value);

            mosaicDetector.UpdateKeywords(keywordList, shaderKeywordList, meshKeywordList, exclusionKeywordList, componentNameKeywordList, shaderPropertyKeywordList);
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

        // 生命周期与缓存管理
        internal void NotifySceneUnloaded()
        {
            processedRenderers.Clear();
            mosaicDetector.ClearCache();
        }

        internal bool ProcessRenderer(Renderer renderer)
        {
            // 快速跳过已处理或已禁用的渲染器
            if (renderer == null || !renderer.enabled || processedRenderers.Contains(renderer)) return false;

            if (mosaicDetector.IsMosaic(renderer))
            {
                mosaicProcessor.Process(renderer);
                processedRenderers.Add(renderer); // 记录进安全名单
                return true;
            }
            return false;
        }

        public void ProcessNewGameObject(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return;
            
            // 获取所有子渲染器
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                ProcessRenderer(renderer);
            }
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            var key = e.ChangedSetting.Definition.Key;
            if (key.Contains("Keywords"))
            {
                ReloadAllKeywords();
                mosaicDetector.ClearCache(); // 配置更改时强制清理缓存
            }
            else if (key == removeMode.Definition.Key)
            {
                mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger);
                Logger.LogInfo(string.Format("处理模式已更新为: {0}。", removeMode.Value));
            }
        }

        private void PatchMethodsByName(Harmony harmonyInstance)
        {
            if (methodDisableKeywordList == null || methodDisableKeywordList.Count == 0) return;

            Logger.LogInfo("开始动态扫描并禁用匹配关键词的方法...");
            int patchedCount = 0;

            var targetAssemblyNames = new HashSet<string>(
                methodPatchTargetAssemblies.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
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
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (method.IsSpecialName || method.IsGenericMethod || method.IsAbstract) continue;

                            if (methodDisableKeywordList.Any(keyword => method.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                try
                                {
                                    harmonyInstance.Patch(method, new HarmonyMethod(typeof(GenericDisablePatch), nameof(GenericDisablePatch.Prefix)));
                                    patchedCount++;
                                }
                                catch { /* ignore */ }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            if (patchedCount > 0)
                Logger.LogInfo(string.Format("动态方法扫描完成，共禁用了 {0} 个方法。", patchedCount));
        }

        public override bool Unload()
        {
            harmony?.UnpatchSelf();
            Config.SettingChanged -= OnSettingChanged;
            if (lifecycleManager != null) GameObject.Destroy(lifecycleManager.gameObject);
            Logger.LogInfo("Demosaic 插件已卸载。");
            return base.Unload();
        }
    }

    #region Helper Components and Patches

    // =================================================================================
    // 马赛克检测器 (IL2CPP 三级缓存极速版)
    // =================================================================================
    public class MosaicDetector
    {
        private List<string> keywordList;
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private List<string> componentNameKeywordList;
        private List<string> shaderPropertyKeywordList;
        private readonly ManualLogSource logger;

        // --- 核心三级缓存字典 ---
        // 解决 IL2CPP 跨语言封送读取 Name 造成的性能灾难
        private Dictionary<int, bool> materialCache = new Dictionary<int, bool>();
        private Dictionary<int, bool> shaderCache = new Dictionary<int, bool>();
        private Dictionary<string, bool> componentTypeCache = new Dictionary<string, bool>();

        public MosaicDetector(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public void UpdateKeywords(List<string> keywords, List<string> shaderKeywords, List<string> meshKeywords, List<string> exclusionKeywords, List<string> componentKeywords, List<string> shaderPropertyKeywords)
        {
            keywordList = keywords;
            shaderKeywordList = shaderKeywords;
            meshKeywordList = meshKeywords;
            exclusionKeywordList = exclusionKeywords;
            componentNameKeywordList = componentKeywords;
            shaderPropertyKeywordList = shaderPropertyKeywords;
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

            // 1. 白名单拦截
            if (NameContainsKeyword(go.name, exclusionKeywordList)) return false;
            
            // 2. 对象名检测
            if (CheckAndLog(go.name, keywordList, "对象名检测")) return true;

            // 3. 材质与着色器检测 (加入缓存机制)
            // [安全] IL2CPP 中使用 sharedMaterials 可防止材质隐式克隆
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

            // 4. 网格名检测
            Mesh mesh = (renderer is SkinnedMeshRenderer smr)
                ? smr.sharedMesh
                : (renderer.GetComponent<MeshFilter>() is { } meshFilter ? meshFilter.sharedMesh : null);
            if (mesh != null && CheckAndLog(mesh.name, meshKeywordList, "网格名检测")) return true;

            // 5. 组件名检测 (加入类型缓存机制)
            if (CheckComponents(go)) return CheckAndLog(go.name, new List<string> { "组件名检测" }, "组件名检测");

            return false;
        }

        private bool CheckMaterialIsMosaic(Material mat)
        {
            if (NameContainsKeyword(mat.name, keywordList)) return true;

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

            // 检查纹理名
            if (keywordList != null && keywordList.Count > 0)
            {
                var texturePropertyIDs = mat.GetTexturePropertyNameIDs();
                foreach (var propID in texturePropertyIDs)
                {
                    var texture = mat.GetTexture(propID);
                    if (texture != null && NameContainsKeyword(texture.name, keywordList)) return true;
                }
            }
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
                    if (NameContainsKeyword(shader.GetPropertyName(i), shaderPropertyKeywordList)) return true;
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
                if (component == null) continue;
                
                // IL2CPP 中获取类名封送开销较大，必须进行字典缓存
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
                logger.LogDebug(string.Format("[{0}] 命中: {1}", category, name));
                return true;
            }
            return false;
        }
    }

    // =================================================================================
    // 马赛克处理器 (已移除污染游戏全局资产的错误逻辑)
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
                    // IL2CPP 特有的对象数组创建方式
                    var sharedMats = renderer.sharedMaterials;
                    var newMaterials = new Il2CppReferenceArray<Material>(sharedMats.Length);
                    for (int i = 0; i < newMaterials.Length; i++)
                    {
                        newMaterials[i] = transparentMaterial;
                    }
                    
                    // [修复] 原版中修改已有材质的做法已被移除。
                    // 直接对 sharedMaterials 赋值会替换物体上的材质指针，而不会破坏原有的全局材质文件
                    renderer.sharedMaterials = newMaterials;
                    logger.LogInfo("已去除马赛克 (透明模式): " + renderer.name);
                    return;
                }
                else
                {
                    logger.LogWarning("透明材质缺失，降级为 Disable 模式: " + renderer.name);
                }
            }

            // 安全的禁用处理
            renderer.gameObject.SetActive(false);
            logger.LogInfo("已去除马赛克 (禁用模式): " + renderer.name);
        }
    }

    // =================================================================================
    // 生命周期与分批扫描器管理器 (基于 Update 状态机，防止卡死主线程)
    // =================================================================================
    public class PluginLifecycleManager : MonoBehaviour
    {
        public PluginLifecycleManager(IntPtr ptr) : base(ptr) { }

        private Action<Scene, LoadSceneMode> _onSceneLoadedAction;
        private Action<Scene> _onSceneUnloadedAction;

        // 延迟扫描参数
        private bool _delayScanQueued = false;
        private float _delayScanTimer = 0f;
        
        // 周期扫描参数
        private float _periodicScanTimer = 0f;

        // 分批扫描状态机
        private bool _isBatchScanning = false;
        private int _currentBatchIndex = 0;
        private Il2CppArrayBase<Renderer> _batchRenderers;

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

            // 1. 处理延迟扫描
            if (_delayScanQueued)
            {
                _delayScanTimer += Time.deltaTime;
                if (_delayScanTimer >= DemosaicPlugin.Instance.sceneLoadScanDelay.Value)
                {
                    _delayScanQueued = false;
                    StartBatchScan();
                }
            }

            // 2. 处理后台周期扫描
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

            // 3. 处理热键触发扫描
            if (Input.GetKeyDown(DemosaicPlugin.Instance.ForceScanHotkeyValue))
            {
                DemosaicPlugin.Logger.LogInfo("快捷键被按下，强制重新执行全场景扫描...");
                DemosaicPlugin.Instance.NotifySceneUnloaded(); // 清理名单以便全部重查
                StartBatchScan();
            }

            // 4. 执行分批扫描逻辑 (替代 Coroutine 防止 IL2CPP 下的支持问题)
            ProcessBatchScan();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _delayScanQueued = true;
            _delayScanTimer = 0f;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            // [极其重要] 通知主插件清空缓存字典
            if (DemosaicPlugin.Instance != null)
            {
                DemosaicPlugin.Instance.NotifySceneUnloaded();
            }
            
            // 中断正在进行的扫描
            _isBatchScanning = false;
            _batchRenderers = null;
        }

        private void StartBatchScan()
        {
            if (_isBatchScanning) return; // 如果上一波还没扫完，直接跳过
            
            _batchRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            _currentBatchIndex = 0;
            _isBatchScanning = true;
            
            DemosaicPlugin.Logger.LogDebug(string.Format("开始分批扫描 {0} 个渲染器...", _batchRenderers.Length));
        }

        private void ProcessBatchScan()
        {
            if (!_isBatchScanning || _batchRenderers == null) return;

            int processedThisFrame = 0;
            int batchSize = DemosaicPlugin.Instance.scanBatchSize.Value;

            while (_currentBatchIndex < _batchRenderers.Length && processedThisFrame < batchSize)
            {
                var renderer = _batchRenderers[_currentBatchIndex];
                
                if (renderer != null)
                {
                    DemosaicPlugin.Instance.ProcessRenderer(renderer);
                }
                
                _currentBatchIndex++;
                processedThisFrame++;
            }

            // 扫描结束
            if (_currentBatchIndex >= _batchRenderers.Length)
            {
                _isBatchScanning = false;
                _batchRenderers = null;
            }
        }
    }

    [HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive))]
    class GameObject_SetActive_Patch
    {
        static void Postfix(GameObject __instance, bool value)
        {
            if (!value || DemosaicPlugin.Instance == null) return;
            try
            {
                DemosaicPlugin.Instance.ProcessNewGameObject(__instance);
            }
            catch (Exception ex) { DemosaicPlugin.Logger.LogError(string.Format("处理新GameObject '{0}' 错误: {1}", __instance.name, ex)); }
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object) })]
    class Object_Instantiate_Patch
    {
        static void Postfix(UnityEngine.Object __result)
        {
            if (DemosaicPlugin.Instance == null) return;
            try
            {
                if (__result is GameObject go) DemosaicPlugin.Instance.ProcessNewGameObject(go);
            }
            catch (Exception ex) { DemosaicPlugin.Logger.LogError(string.Format("处理实例化对象错误: {0}", ex)); }
        }
    }

    public static class GenericDisablePatch
    {
        public static bool Prefix() => false;
    }

    [HarmonyPatch]
    public static class MosaicControllerUpdatePatch
    {
        static MethodBase TargetMethod()
        {    
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (assembly == null) return null;

            var mosaicControllerType = assembly.GetType("MosaicController");
            if (mosaicControllerType == null) return null;

            var updateMethod = mosaicControllerType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance); 
            return updateMethod;
        }

        static bool Prefix(MethodBase __originalMethod) => false; 
    }
    #endregion
}