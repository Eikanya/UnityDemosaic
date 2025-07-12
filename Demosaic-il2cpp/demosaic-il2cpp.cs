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

    // =================================================================================
    // 主插件类
    // =================================================================================
    [BepInPlugin("demosaic", "Demosaic", "1.2.0")] 
    public class DemosaicPlugin : BasePlugin
    {
        // 配置项
        private ConfigEntry<bool> enablePlugin;
        private ConfigEntry<string> keywords;
        private ConfigEntry<RemoveMode> removeMode;
        private ConfigEntry<string> shaderKeywords;
        private ConfigEntry<string> meshKeywords;
        private ConfigEntry<string> exclusionKeywords;
        private ConfigEntry<string> componentNameKeywords;
        private ConfigEntry<string> shaderPropertyKeywords;
        private ConfigEntry<string> methodDisableKeywords;
        private ConfigEntry<string> methodPatchTargetAssemblies;
        private ConfigEntry<KeyCode> forceScanHotkey;
        internal KeyCode ForceScanHotkeyValue => forceScanHotkey.Value;

        // 优化字段
        private List<string> keywordList;
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private List<string> componentNameKeywordList;
        private List<string> shaderPropertyKeywordList;
        private List<string> methodDisableKeywordList;
        private readonly HashSet<Renderer> processedRenderers = new HashSet<Renderer>();
        private Material transparentMaterial;
        private Harmony harmony;
        private MosaicDetector mosaicDetector;
        private MosaicProcessor mosaicProcessor;
        private PluginLifecycleManager lifecycleManager;

        // 单例，方便补丁代码访问
        public static DemosaicPlugin Instance { get; private set; }

        // 静态日志实例
        internal static ManualLogSource Logger { get; private set; }

        // 关键词常量
        private const string DefaultKeywords = "mosaic,censor";
        private const string DefaultShaderKeywords = "mosaic,censor";
        private const string DefaultMeshKeywords = "mosaic,censor";
        private const string DefaultExclusionKeywords = "";
        private const string DefaultComponentNameKeywords = "MosaicEffect,CensorEffect";
        private const string DefaultShaderPropertyKeywords = "_PixelSize,_BlockSize,_MosaicFactor";

        public override void Load()
        {
            // 尝试设置控制台输出编码为UTF-8，解决中文乱码问题
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch (Exception ex)
            {
                // 如果无法设置编码（例如在某些受限环境中），记录警告但不阻止插件加载
                Logger.LogWarning(string.Format("无法设置控制台输出编码为UTF-8: {0}", ex.Message));
            }

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
            Logger.LogInfo(string.Format("Demosaic 插件加载成功！去除方式: {0}, 强制刷新快捷键: {1}", removeMode.Value, forceScanHotkey.Value));

            try
            {
                harmony = new Harmony("demosaic");
                harmony.PatchAll(typeof(DemosaicPlugin).Assembly);
                Logger.LogInfo("Harmony 补丁应用成功，已启用实时对象处理。");

                PatchMethodsByName(harmony);
            }
            catch (System.Exception e)
            {
                Logger.LogError("Harmony 补丁应用失败: " + e);
            }
        }

        private void SetupConfiguration()
        {
            enablePlugin = Config.Bind("General", "Enable", true, "是否启用去马赛克插件");
            forceScanHotkey = Config.Bind("General", "ForceScanHotkey", KeyCode.F10, "按下此快捷键可强制重新扫描并移除所有马赛克");
            
            keywords = Config.Bind("Detection", "Keywords", DefaultKeywords, "检测对象/材质/纹理名称的关键词，逗号分隔");
            shaderKeywords = Config.Bind("Detection", "ShaderKeywords", DefaultShaderKeywords, "检测马赛克着色器的关键词，逗号分隔");
            meshKeywords = Config.Bind("Detection", "MeshKeywords", DefaultMeshKeywords, "检测马赛克网格的关键词，逗号分隔");
            componentNameKeywords = Config.Bind("Detection", "ComponentNameKeywords", DefaultComponentNameKeywords, "检测组件名称的关键词，逗号分隔");
            shaderPropertyKeywords = Config.Bind("Detection", "ShaderPropertyKeywords", DefaultShaderPropertyKeywords, "检测着色器属性的关键词，逗号分隔");
            exclusionKeywords = Config.Bind("Detection", "ExclusionKeywords", DefaultExclusionKeywords, "如果对象名称包含这些关键词，则会跳过检测（最高优先级），逗号分隔");

            methodDisableKeywords = Config.Bind("Advanced", "MethodDisableKeywords", "censor,mosaic", "如果方法名包含这些关键词，则会尝试禁用该方法（高级功能，需要重启游戏生效），逗号分隔");
            methodPatchTargetAssemblies = Config.Bind("Advanced", "MethodPatchTargetAssemblies", "Assembly-CSharp", "需要进行方法扫描的目标程序集名称，逗号分隔。留空则扫描所有程序集（可能很慢）。");

            removeMode = Config.Bind("Remove", "Mode", RemoveMode.Disable, "去除方式：Disable=禁用GameObject，Transparent=替换为透明材质");

            Config.SettingChanged += OnSettingChanged;
        }

        private void ReloadAllKeywords()
        {
            Logger.LogInfo("正在加载/重载所有关键词列表...");
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
            var shader = Shader.Find("Unlit/Transparent");
            if (shader == null)
            {
                Logger.LogWarning("未找到 Unlit/Transparent 着色器，尝试使用 Standard 着色器。");
                shader = Shader.Find("Standard");
            }

            if (shader != null)
            {
                transparentMaterial = new Material(shader);
                if (shader.name == "Standard")
                {
                    transparentMaterial.SetFloat("_Mode", 3);
                    transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    transparentMaterial.SetInt("_ZWrite", 0);
                    transparentMaterial.DisableKeyword("_ALPHATEST_ON");
                    transparentMaterial.EnableKeyword("_ALPHABLEND_ON");
                    transparentMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    transparentMaterial.renderQueue = 3000;
                }
                transparentMaterial.color = Color.clear;
            }
            else
            {
                Logger.LogError("未能找到 Unlit/Transparent 或 Standard 着色器。透明模式将无法正常工作。");
            }
        }

        private List<string> ParseKeywordString(string keywordString)
        {
            return keywordString.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        internal void StartScan()
        {
            lifecycleManager?.ScanAllRenderers();
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

        internal void ClearProcessedRenderers()
        {
            processedRenderers.Clear();
        }

        public void ProcessNewGameObject(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
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
                methodPatchTargetAssemblies.Value.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)
                                                  .Select(s => s.Trim()),
                System.StringComparer.OrdinalIgnoreCase);

            var allAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            var assembliesToScan = allAssemblies
                .Where(asm =>
                {
                    if (asm.IsDynamic) return false;
                    if (targetAssemblyNames.Count == 0)
                    {
                        try
                        {
                            return !string.IsNullOrEmpty(asm.Location) && !asm.Location.Contains("System") && !asm.Location.Contains("mscorlib");
                        }
                        catch { return false; }
                    }
                    return targetAssemblyNames.Contains(asm.GetName().Name);
                })
                .ToList();

            Logger.LogInfo(string.Format("将要扫描 {0} 个程序集进行方法注入。", assembliesToScan.Count));

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
                catch (ReflectionTypeLoadException ex)
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

    public class MosaicDetector
    {
        private List<string> keywordList;
        private List<string> shaderKeywordList;
        private List<string> meshKeywordList;
        private List<string> exclusionKeywordList;
        private List<string> componentNameKeywordList;
        private List<string> shaderPropertyKeywordList;
        private readonly ManualLogSource logger;

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

        public bool IsMosaic(Renderer renderer)
        {
            var go = renderer.gameObject;

            if (NameContainsKeyword(go.name, exclusionKeywordList)) return false;
            if (CheckAndLog(go.name, keywordList, "对象名检测")) return true;

            Mesh mesh = (renderer is SkinnedMeshRenderer smr)
                ? smr.sharedMesh
                : (renderer.GetComponent<MeshFilter>() is { } meshFilter ? meshFilter.sharedMesh : null);
            if (mesh != null && CheckAndLog(mesh.name, meshKeywordList, "网格名检测")) return true;

            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null) continue;
                if (CheckAndLog(mat.name, keywordList, "材质名检测")) return true;
                if (mat.shader != null)
                {
                    if (CheckAndLog(mat.shader.name, shaderKeywordList, "着色器检测")) return true;
                    if (CheckShaderProperties(mat)) return CheckAndLog(mat.name, new List<string> { "着色器属性检测" }, "着色器属性检测");
                }
                if (CheckTextures(mat)) return CheckAndLog(mat.name, new List<string> { "纹理名检测" }, "纹理名检测");
            }

            if (CheckComponents(go)) return CheckAndLog(go.name, new List<string> { "组件名检测" }, "组件名检测");

            return false;
        }

        private bool CheckShaderProperties(Material mat)
        {
            if (shaderPropertyKeywordList == null || shaderPropertyKeywordList.Count == 0) return false;
            for (int i = 0; i < mat.shader.GetPropertyCount(); i++)
            {
                if (NameContainsKeyword(mat.shader.GetPropertyName(i), shaderPropertyKeywordList)) return true;
            }
            return false;
        }

        private bool CheckTextures(Material mat)
        {
            if (keywordList == null || keywordList.Count == 0) return false;
            var texturePropertyIDs = mat.GetTexturePropertyNameIDs();
            foreach (var propID in texturePropertyIDs)
            {
                var texture = mat.GetTexture(propID);
                if (texture != null && NameContainsKeyword(texture.name, keywordList)) return true;
            }
            return false;
        }

        private bool CheckComponents(GameObject go)
        {
            if (componentNameKeywordList == null || componentNameKeywordList.Count == 0) return false;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component != null && NameContainsKeyword(component.GetIl2CppType().Name, componentNameKeywordList)) return true;
            }
            return false;
        }

        private bool NameContainsKeyword(string name, List<string> keywords)
        {
            if (string.IsNullOrEmpty(name) || keywords == null) return false;
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
                    var newMaterials = new Il2CppReferenceArray<Material>(renderer.sharedMaterials.Length);
                    for (int i = 0; i < newMaterials.Length; i++)
                    {
                        newMaterials[i] = transparentMaterial;
                    }
                    renderer.materials = newMaterials;
                    logger.LogInfo("已去除马赛克 (透明模式 - 新材质): " + renderer.name);
                    return;
                }
                else
                {
                    // Fallback to modifying existing materials if transparentMaterial is not available
                    foreach (var mat in renderer.sharedMaterials)
                    {
                        if (mat != null)
                        {
                            // Set render mode to Fade
                            mat.SetOverrideTag("RenderType", "Transparent");
                            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                            mat.SetInt("_ZWrite", 0);
                            mat.DisableKeyword("_ALPHATEST_ON");
                            mat.EnableKeyword("_ALPHABLEND_ON");
                            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                            mat.renderQueue = 3000;

                            // Try to set _Mode property if it exists (common for Standard shader)
                            if (mat.HasProperty("_Mode"))
                            {
                                mat.SetFloat("_Mode", 3); // 3 for Fade
                            }
                            mat.color = Color.clear; // Set to fully transparent
                        }
                    }
                    logger.LogInfo("已去除马赛克 (透明模式 - 修改现有材质): " + renderer.name);
                    return;
                }
            }

            // 如果不是透明模式，或者透明模式的两种尝试都失败，则回退到禁用GameObject和销毁Mesh组件
            // 尝试销毁MeshFilter或SkinnedMeshRenderer组件
            if (renderer is MeshRenderer meshRenderer)
            {
                var meshFilter = meshRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null)
                {
                    UnityEngine.Object.Destroy(meshFilter);
                    logger.LogInfo("已销毁 MeshFilter 组件: " + renderer.name);
                }
            }
            else if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                UnityEngine.Object.Destroy(skinnedMeshRenderer);
                logger.LogInfo("已销毁 SkinnedMeshRenderer 组件: " + renderer.name);
            }

            // 备用方案：禁用GameObject
            renderer.gameObject.SetActive(false);
            logger.LogInfo("已去除马赛克 (禁用GameObject模式，并尝试销毁Mesh组件): " + renderer.name);
        }
    }

    public class PluginLifecycleManager : MonoBehaviour
    {
        public PluginLifecycleManager(System.IntPtr ptr) : base(ptr) { }

        private System.Action<Scene, LoadSceneMode> _onSceneLoadedAction;
        private bool _scanQueued = false;
        private float _scanTimer = 0f;
        private const float ScanDelay = 2.0f;

        void Awake()
        {
            _onSceneLoadedAction = new System.Action<Scene, LoadSceneMode>(OnSceneLoaded);
            SceneManager.sceneLoaded += _onSceneLoadedAction;
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
            // 处理延迟扫描
            if (_scanQueued)
            {
                _scanTimer += Time.deltaTime;
                if (_scanTimer >= ScanDelay)
                {
                    _scanQueued = false;
                    DemosaicPlugin.Logger.LogInfo(string.Format("执行延迟同步扫描（延迟 {0} 秒）...", ScanDelay));
                    ScanAllRenderers();
                }
            }

            // 处理热键
            if (DemosaicPlugin.Instance != null && Input.GetKeyDown(DemosaicPlugin.Instance.ForceScanHotkeyValue))
            {
                DemosaicPlugin.Logger.LogInfo(string.Format("快捷键 [{0}] 被按下，开始强制全场景同步扫描...", DemosaicPlugin.Instance.ForceScanHotkeyValue));
                ScanAllRenderers();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (DemosaicPlugin.Instance == null) return;
            DemosaicPlugin.Logger.LogInfo(string.Format("场景 '{0}' 加载完成，已计划延迟扫描...", scene.name));
            _scanQueued = true;
            _scanTimer = 0f;
        }

        public void ScanAllRenderers()
        {
            if (DemosaicPlugin.Instance == null) return;

            DemosaicPlugin.Logger.LogDebug("正在清空已处理对象列表并开始同步扫描...");
            DemosaicPlugin.Instance.ClearProcessedRenderers();
            int foundCount = 0;

            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            DemosaicPlugin.Logger.LogInfo(string.Format("开始扫描场景中的 {0} 个渲染器...", renderers.Length));

            foreach (var renderer in renderers)
            {
                if (DemosaicPlugin.Instance.ProcessRenderer(renderer))
                {
                    foundCount++;
                }
            }

            if (foundCount > 0)
                DemosaicPlugin.Logger.LogInfo(string.Format("同步扫描完成，共移除了 {0} 个马赛克对象。", foundCount));
            else
                DemosaicPlugin.Logger.LogInfo("同步扫描完成，未发现新的马赛克对象。");
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

    [HarmonyPatch]
    public static class MosaicControllerUpdatePatch
    {
        // 目标方法：MosaicController.Update
        static MethodBase TargetMethod()
        {    
            // 此方法针对HypnoApp，RJ308908
            // 目标方法：MosaicController.Update
            // 使用反射获取 MosaicController 类型和 Update 方法
            // 假设 MosaicController 在 Assembly-CSharp.dll 中
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (assembly == null)
            {
                DemosaicPlugin.Logger.LogError("MosaicControllerUpdatePatch: 未找到 Assembly-CSharp 程序集，无法修补 MosaicController.Update。");
                return null;
            }

            var mosaicControllerType = assembly.GetType("MosaicController");//马赛克类型：MosaicController
            if (mosaicControllerType == null)
            {
                DemosaicPlugin.Logger.LogError("MosaicControllerUpdatePatch: 未找到 MosaicController 类型，无法修补 MosaicController.Update。");
                return null;
            }

            var updateMethod = mosaicControllerType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance); //马赛克方法：Update 
            if (updateMethod == null)
            {
                DemosaicPlugin.Logger.LogError("MosaicControllerUpdatePatch: 未找到 MosaicController.Update 方法，无法修补。");
            }
            else
            {
                DemosaicPlugin.Logger.LogInfo(string.Format("MosaicControllerUpdatePatch: 已成功找到并准备修补方法: {0}.{1}", mosaicControllerType.Name, updateMethod.Name));
            }
            return updateMethod;
        }

        // Prefix 方法，在原始方法执行前运行，返回 false 阻止原始方法执行
        static bool Prefix(System.Reflection.MethodBase __originalMethod)
        {
            return false; // 阻止原始 Update 方法执行
        }
    }

    #endregion
}
