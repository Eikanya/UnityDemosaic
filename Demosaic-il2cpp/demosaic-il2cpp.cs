using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DemosaicPlugin
{
    public enum RemoveMode
    {
        Disable,
        Transparent
    }

    // =================================================================================
    // 主插件类
    // =================================================================================
    [BepInPlugin("demosaic", "Demosaic", "1.0.0")]
    public class DemosaicPlugin : BasePlugin
    {
        // 配置项
        private ConfigEntry<bool> enablePlugin;
        private ConfigEntry<string> keywords;
        private ConfigEntry<RemoveMode> removeMode;
        private ConfigEntry<string> shaderKeywords;
        private ConfigEntry<string> meshKeywords;
        private ConfigEntry<string> exclusionKeywords;
        private ConfigEntry<string> methodDisableKeywords;
        private ConfigEntry<string> methodPatchTargetAssemblies;
        private ConfigEntry<KeyCode> forceScanHotkey;
        private ConfigEntry<float> periodicScanInterval;
        internal KeyCode ForceScanHotkeyValue => forceScanHotkey.Value;
        internal float PeriodicScanIntervalValue => periodicScanInterval.Value;

        // 优化字段
        private List<string> keywordList;
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private List<string> methodDisableKeywordList;
        private readonly HashSet<Renderer> processedRenderers = new HashSet<Renderer>();
        private Material transparentMaterial;
        private Harmony harmony;
        private MosaicDetector mosaicDetector;
        private MosaicProcessor mosaicProcessor;
        private PluginLifecycleManager lifecycleManager;

        // 单例，方便补丁代码访问
        public static DemosaicPlugin Instance { get; private set; }

        // 静态日志实例，方便在任何地方（尤其是静态补丁中）访问
        internal static ManualLogSource Logger { get; private set; }

        // 关键词常量
        private const string DefaultKeywords = "mosaic,censor";
        private const string DefaultShaderKeywords = "mosaic,censor";
        private const string DefaultMeshKeywords = "mosaic,censor";
        private const string DefaultExclusionKeywords = ""; // 默认留空，让用户按需添加

        public override void Load()
        {
            Instance = this;
            Logger = Log; // 从 BasePlugin 获取实例日志记录器并赋值给静态成员

            SetupConfiguration();

            if (!enablePlugin.Value) return;

            // 注册自定义 MonoBehaviour 以便在 IL2CPP 中使用
            ClassInjector.RegisterTypeInIl2Cpp<PluginLifecycleManager>();

            // 初始化负责检测和处理的核心服务
            mosaicDetector = new MosaicDetector(Logger);
            ReloadAllKeywords();

            // 创建共享的透明材质
            CreateTransparentMaterial();
            // 根据当前配置创建处理器
            mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger);

            // 使用 BepInEx 辅助方法添加组件，它会自动处理 GameObject 创建、IL2CPP 类型注册和持久化
            lifecycleManager = AddComponent<PluginLifecycleManager>();
            Logger.LogInfo(string.Format("Demosaic 插件加载成功！去除方式: {0}, 强制刷新快捷键: {1}", removeMode.Value, forceScanHotkey.Value));

            // 应用 Harmony 补丁
            try
            {
                harmony = new Harmony("demosaic");
                harmony.PatchAll(typeof(DemosaicPlugin).Assembly);
                Logger.LogInfo("Harmony 补丁应用成功，已启用实时对象处理。");

                // 动态地按名称查找并修补方法
                PatchMethodsByName(harmony);
            }
            catch (System.Exception e)
            {
                Logger.LogError("Harmony 补丁应用失败: " + e);
            }
        }

        private void SetupConfiguration()
        {
            // 初始化配置
            enablePlugin = Config.Bind("General", "Enable", true, "是否启用去马赛克插件");
            periodicScanInterval = Config.Bind("General", "PeriodicScanInterval", 0f, "周期性扫描的间隔时间（秒）。设为0则禁用。这是一个“最终兜底”选项，用于处理在同一场景内动态加载且无法被实时补丁捕获的内容。注意：频繁扫描可能会对性能产生影响。");
            forceScanHotkey = Config.Bind("General", "ForceScanHotkey", KeyCode.F10, "按下此快捷键可强制重新扫描并移除所有马赛克");
            keywords = Config.Bind("Detection", "Keywords", DefaultKeywords, "检测马赛克的关键词，逗号分隔");
            shaderKeywords = Config.Bind("Detection", "ShaderKeywords", DefaultShaderKeywords, "检测马赛克着色器的关键词，逗号分隔");
            meshKeywords = Config.Bind("Detection", "MeshKeywords", DefaultMeshKeywords, "检测马赛克网格的关键词，逗号分隔");
            exclusionKeywords = Config.Bind("Detection", "ExclusionKeywords", DefaultExclusionKeywords, "如果对象名称包含这些关键词，则会跳过检测（最高优先级），逗号分隔");

            methodDisableKeywords = Config.Bind("Advanced", "MethodDisableKeywords", "censor", "如果方法名包含这些关键词，则会尝试禁用该方法（高级功能，需要重启游戏生效），逗号分隔");
            methodPatchTargetAssemblies = Config.Bind("Advanced", "MethodPatchTargetAssemblies", "Assembly-CSharp", "需要进行方法扫描的目标程序集名称，逗号分隔。留空则扫描所有程序集（可能很慢）。");

            removeMode = Config.Bind("Remove", "Mode", RemoveMode.Disable, "去除方式：Disable=禁用Renderer，Transparent=替换为透明材质");

            // 监听配置文件更改事件，实现热重载
            Config.SettingChanged += OnSettingChanged;

            // 强制保存一次配置，确保文件在首次加载时生成
            Config.Save();
        }

        private void ReloadAllKeywords()
        {
            Logger.LogInfo("正在加载/重载所有关键词列表...");
            keywordList = ParseKeywordString(keywords.Value);
            shaderKeywordList = ParseKeywordString(shaderKeywords.Value);
            meshKeywordList = ParseKeywordString(meshKeywords.Value);
            exclusionKeywordList = ParseKeywordString(exclusionKeywords.Value);
            mosaicDetector.UpdateKeywords(keywordList, shaderKeywordList, meshKeywordList, exclusionKeywordList);

            // 此列表在启动时加载；更改需要重启游戏才能生效
            methodDisableKeywordList = ParseKeywordString(methodDisableKeywords.Value);
        }

        private void CreateTransparentMaterial()
        {
            var shader = Shader.Find("Unlit/Transparent");
            if (shader != null)
            {
                transparentMaterial = new Material(shader) { color = Color.clear };
            }
            else
            {
                Logger.LogError("找不到 Unlit/Transparent shader。透明模式可能无法正常工作。");
            }
        }

        private List<string> ParseKeywordString(string keywordString)
        {
            return keywordString.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        /// <summary>
        /// 启动或重启扫描协程
        /// </summary>
        internal void StartScan()
        {
            if (lifecycleManager != null)
            {
                lifecycleManager.StartScanCoroutine();
            }
        }

        /// <summary>
        /// 检查并处理单个Renderer，返回是否进行了处理。
        /// </summary>
        internal bool ProcessRenderer(Renderer renderer)
        {
            // 跳过无效、已禁用或已处理的Renderer
            if (renderer == null || !renderer.enabled || processedRenderers.Contains(renderer)) return false;

            if (mosaicDetector.IsMosaic(renderer))
            {
                mosaicProcessor.Process(renderer);
                processedRenderers.Add(renderer); 
                return true;
            }
            return false;
        }

        internal void ClearProcessedRenderers()
        {
            processedRenderers.Clear();
        }

        /// <summary>
        /// 公开一个方法，供Harmony补丁调用，用于处理新激活或实例化的游戏对象
        /// </summary>
        public void ProcessNewGameObject(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return;

            // 查找新对象及其所有子对象中的Renderer
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                ProcessRenderer(renderer);
            }
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            var key = e.ChangedSetting.Definition.Key;
            if (key == keywords.Definition.Key || key == shaderKeywords.Definition.Key || key == meshKeywords.Definition.Key || key == exclusionKeywords.Definition.Key)
            {
                ReloadAllKeywords();
            }
            else if (key == removeMode.Definition.Key)
            {
                mosaicProcessor = new MosaicProcessor(removeMode.Value, transparentMaterial, Logger);
                Logger.LogInfo(string.Format("处理模式已更新为: {0}。", removeMode.Value));
            }
        }

        private void PatchMethodsByName(Harmony harmonyInstance)
        {
            if (methodDisableKeywordList == null || methodDisableKeywordList.Count == 0)
            {
                return; // 没有配置关键词，跳过扫描
            }

            Logger.LogInfo("开始动态扫描并禁用匹配关键词的方法...");
            int patchedCount = 0;

            var targetAssemblyNames = new HashSet<string>(
                methodPatchTargetAssemblies.Value.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)
                                                  .Select(s => s.Trim()),
                System.StringComparer.OrdinalIgnoreCase);

            var allAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            var assembliesToScan = allAssemblies
                .Where(asm =>
                {
                    if (asm.IsDynamic) return false;
                    // 如果用户没有指定程序集，则扫描所有非系统程序集
                    if (targetAssemblyNames.Count == 0)
                    {
                        try
                        {
                            return !string.IsNullOrEmpty(asm.Location) && !asm.Location.Contains("System") && !asm.Location.Contains("mscorlib");
                        }
                        catch { return false; }
                    }
                    // 否则，只扫描用户指定的程序集
                    return targetAssemblyNames.Contains(asm.GetName().Name);
                })
                .ToList();

            Logger.LogInfo(string.Format("将要扫描 {0} 个程序集进行方法注入。", assembliesToScan.Count));

            foreach (var assembly in assembliesToScan)
            {
                try
                {
                    var types = assembly.GetTypes();
                    foreach (var type in types)
                    {
                        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                        foreach (var method in methods)
                        {
                            if (method.IsSpecialName || method.IsGenericMethod || method.IsAbstract) continue;

                            if (methodDisableKeywordList.Any(keyword => method.Name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                try
                                {
                                    harmonyInstance.Patch(method, new HarmonyMethod(typeof(GenericDisablePatch), nameof(GenericDisablePatch.Prefix)));
                                    Logger.LogDebug(string.Format("已禁用方法: {0} in type {1}", method.Name, type.FullName));
                                    patchedCount++;
                                }
                                catch (Exception patchEx)
                                {
                                    
                                    Logger.LogWarning(string.Format("修补方法 {0} 时失败: {1}", method.Name, patchEx.Message));
                                }
                            }
                        }
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException ex)
                {
                    Logger.LogWarning(string.Format("无法完全加载程序集 {0} 中的类型，已跳过。错误: {1}", assembly.FullName, ex.Message));
                }
                catch (System.Exception e)
                {
                    Logger.LogWarning(string.Format("扫描程序集 {0} 时发生未知错误，已跳过。错误: {1}", assembly.FullName, e.Message));
                }
            }

            if (patchedCount > 0)
                Logger.LogInfo(string.Format("动态方法扫描完成，共禁用了 {0} 个方法。", patchedCount));
        }

        public override bool Unload()
        {
            harmony?.UnpatchSelf();
            Config.SettingChanged -= OnSettingChanged;
            if (lifecycleManager != null)
            {
                GameObject.Destroy(lifecycleManager.gameObject);
            }
            Logger.LogInfo("Demosaic 插件已卸载。");
            return base.Unload();
        }
    }

    #region Helper Components and Patches

    /// <summary>
    /// 负责检测一个渲染器是否是马赛克。
    /// </summary>
    public class MosaicDetector
    {
        private List<string> keywordList;
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private readonly ManualLogSource logger;

        public MosaicDetector(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public void UpdateKeywords(List<string> keywords, List<string> shaderKeywords, List<string> meshKeywords, List<string> exclusionKeywords)
        {
            keywordList = keywords;
            shaderKeywordList = shaderKeywords;
            meshKeywordList = meshKeywords;
            exclusionKeywordList = exclusionKeywords;
        }

        public bool IsMosaic(Renderer renderer)
        {
            // 1. 排除检测 (最高优先级)
            if (NameContainsKeyword(renderer.name, exclusionKeywordList))
            {
                return false;
            }

            // 2. 包含检测
            if (CheckAndLog(renderer.name, keywordList, "对象名检测")) return true;

            // 检查网格名
            Mesh mesh = (renderer is SkinnedMeshRenderer smr)
                ? smr.sharedMesh
                : (renderer.GetComponent<MeshFilter>() is { } meshFilter ? meshFilter.sharedMesh : null);
            if (mesh != null && CheckAndLog(mesh.name, meshKeywordList, "网格名检测")) return true;

            // 检查所有材质
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null) continue;
                if (CheckAndLog(mat.name, keywordList, "材质名检测")) return true;
                if (mat.shader != null && CheckAndLog(mat.shader.name, shaderKeywordList, "着色器检测")) return true;
                if (mat.mainTexture != null && CheckAndLog(mat.mainTexture.name, keywordList, "纹理名检测")) return true;
            }

            return false;
        }

        private bool NameContainsKeyword(string name, List<string> keywords)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return keywords.Any(keyword => name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0);
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

    /// <summary>
    /// 负责处理被检测到的马赛克渲染器。
    /// </summary>
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
            // 优先尝试配置的主模式
            if (removeMode == RemoveMode.Transparent && transparentMaterial != null)
            {
                var newMaterials = new Il2CppReferenceArray<Material>(renderer.sharedMaterials.Length);
                for (int i = 0; i < newMaterials.Length; i++)
                {
                    newMaterials[i] = transparentMaterial;
                }
                renderer.materials = newMaterials;
                logger.LogDebug("替换为透明材质: " + renderer.name);
                return; // 处理成功，退出
            }

            // 如果主模式是 Disable 或透明模式失败，则回退到禁用 GameObject
            renderer.gameObject.SetActive(false);
            if (removeMode == RemoveMode.Transparent)
            {
                logger.LogWarning("透明材质不可用，已使用备用方案禁用GameObject: " + renderer.name);
            }
            else
            {
                logger.LogDebug("禁用 GameObject: " + renderer.name);
            }
        }
    }

    /// <summary>
    /// 处理协程和Unity生命周期事件的专用组件
    /// </summary>
    public class PluginLifecycleManager : MonoBehaviour
    {
        // IL2CPP 必需的构造函数
        public PluginLifecycleManager(System.IntPtr ptr) : base(ptr) { }

        private System.Action<Scene, LoadSceneMode> _onSceneLoadedAction;

        void Awake()
        {
            _onSceneLoadedAction = new System.Action<Scene, LoadSceneMode>(OnSceneLoaded);
            SceneManager.sceneLoaded += _onSceneLoadedAction;
            StartCoroutine(nameof(PeriodicScanCoroutine));
        }

        void OnDestroy()
        {
            if (_onSceneLoadedAction != null)
            {
                SceneManager.sceneLoaded -= _onSceneLoadedAction;
            }
        }

        void Update()
        {
            if (DemosaicPlugin.Instance != null && Input.GetKeyDown(DemosaicPlugin.Instance.ForceScanHotkeyValue))
            {
                DemosaicPlugin.Logger.LogInfo(string.Format("快捷键 [{0}] 被按下，开始强制全场景异步扫描...", DemosaicPlugin.Instance.ForceScanHotkeyValue));
                StartScanCoroutine();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (DemosaicPlugin.Instance == null) return;
            DemosaicPlugin.Logger.LogInfo(string.Format("场景 '{0}' 加载完成，开始初次异步扫描...", scene.name));
            StartScanCoroutine();

            StartCoroutine(nameof(DelayedSecondScan), 2.0f);
        }

        public void StartScanCoroutine()
        {
            StopCoroutine(nameof(ScanAndRemoveCoroutine));
            StartCoroutine(nameof(ScanAndRemoveCoroutine));
        }

        public IEnumerator DelayedSecondScan(float delay)
        {
            yield return new WaitForSeconds(delay);
            DemosaicPlugin.Logger.LogInfo(string.Format("执行延迟二次扫描（延迟 {0} 秒），以捕获后加载的对象...", delay));
            StartScanCoroutine();
        }

        private IEnumerator PeriodicScanCoroutine()
        {
            while (true)
            {
                if (DemosaicPlugin.Instance == null)
                {
                    yield return new WaitForSeconds(5.0f);
                    continue;
                }

                float interval = DemosaicPlugin.Instance.PeriodicScanIntervalValue;
                if (interval > 0)
                {
                    yield return new WaitForSeconds(interval);

                    if (DemosaicPlugin.Instance.PeriodicScanIntervalValue > 0)
                    {
                        DemosaicPlugin.Logger.LogInfo(string.Format("执行周期性扫描（间隔 {0} 秒）...", interval));
                        StartScanCoroutine();
                    }
                }
                else
                {
                    yield return new WaitForSeconds(5.0f);
                }
            }
        }

        public IEnumerator ScanAndRemoveCoroutine()
        {
            if (DemosaicPlugin.Instance == null) yield break;

            DemosaicPlugin.Logger.LogDebug("正在清空已处理对象列表并开始异步扫描...");
            DemosaicPlugin.Instance.ClearProcessedRenderers();
            int foundCount = 0;
            int processedCount = 0;
            const int yieldInterval = 100;

            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            DemosaicPlugin.Logger.LogInfo(string.Format("开始扫描场景中的 {0} 个渲染器...", renderers.Length));

            foreach (var renderer in renderers)
            {
                processedCount++;
                if (DemosaicPlugin.Instance.ProcessRenderer(renderer))
                {
                    foundCount++;
                }

                if (processedCount % yieldInterval == 0)
                {
                    yield return null;
                }
            }

            if (foundCount > 0)
            {
                DemosaicPlugin.Logger.LogInfo(string.Format("异步扫描完成，共移除了 {0} 个马赛克对象。", foundCount));
            }
            else
            {
                DemosaicPlugin.Logger.LogInfo("异步扫描完成，未发现新的马赛克对象。");
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
            catch (System.Exception ex)
            {
                DemosaicPlugin.Logger.LogError(string.Format("处理新GameObject '{0}' 时发生错误: {1}", __instance.name, ex));
            }
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new System.Type[] { typeof(UnityEngine.Object) })]
    class Object_Instantiate_Patch
    {
        static void Postfix(UnityEngine.Object __result)
        {
            if (DemosaicPlugin.Instance == null) return;
            try
            {
                if (__result is GameObject go)
                {
                    DemosaicPlugin.Instance.ProcessNewGameObject(go);
                }
            }
            catch (System.Exception ex)
            {
                DemosaicPlugin.Logger.LogError(string.Format("处理实例化对象时发生错误: {0}", ex));
            }
        }
    }

    public static class GenericDisablePatch
    {
        public static bool Prefix()
        {
            return false;
        }
    }

    #endregion
}
