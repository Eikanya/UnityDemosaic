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


namespace DemosaicPlugin
{
    public enum RemoveMode
    {
        Disable,
        Destroy,
        Transparent
    }

    // =================================================================================
    // 主插件类
    // =================================================================================
    [BepInPlugin("demosaic", "Demosaic", "1.0.0")] 
    public class DemosaicPlugin : BaseUnityPlugin
    {
        public static DemosaicPlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;
        private MosaicDetector _detector;
        private MosaicProcessor _processor;

        // --- 配置文件 ---
        private ConfigEntry<bool> _enablePlugin;
        private ConfigEntry<RemoveMode> _removeMode;
        private ConfigEntry<KeyCode> _manualScanKey;
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
        private ConfigEntry<bool> _disableMethods;
        private ConfigEntry<string> _methodDisableKeywords;
        private ConfigEntry<string> _assemblyNamesToPatch;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Log.LogInfo("去马赛克插件正在加载...");

            // 1. 读取配置
            LoadConfig();

            if (!_enablePlugin.Value)
            {
                Log.LogInfo("插件已在配置文件中被禁用，正在停止初始化。");
                return;
            }

            // 2. 初始化检测器和处理器
            _detector = new MosaicDetector(
                _objectNameKeywords.Value.Split(','),
                _materialNameKeywords.Value.Split(','),
                _shaderNameKeywords.Value.Split(','),
                _meshNameKeywords.Value.Split(','),
                _textureKeywords.Value.Split(','),
                _componentNameKeywords.Value.Split(','), 
                _shaderPropertyKeywords.Value.Split(',')  
            );
            _processor = new MosaicProcessor(_removeMode.Value);

            // 3. 应用 Harmony 补丁
            _harmony = new Harmony("com.yourname.demosaic.harmony");
            ApplyHarmonyPatches();

            // 4. 禁用指定方法 (如果启用)
            if (_disableMethods.Value)
            {
                PatchMethodsByName();
            }

            // 5. 注册Unity事件并启动协程
            SceneManager.sceneLoaded += OnSceneLoaded;

            if (_periodicScanInterval.Value > 0)
            {
                StartCoroutine(PeriodicScan());
            }
            // 首次加载时进行一次扫描
            StartCoroutine(DelayedScan());

            Log.LogInfo("去马赛克插件加载成功！");
        }

        // --- Unity 生命周期方法 ---
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            StopAllCoroutines();
            Log.LogInfo("去马赛克插件已卸载。");
        }

        private void Update()
        {
            if (Input.GetKeyDown(_manualScanKey.Value))
            {
                Log.LogInfo("手动扫描已通过热键触发。");
                StartCoroutine(ScanSceneCoro());
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.LogInfo(string.Format("新场景已加载: {0}。正在开始延迟扫描。", scene.name));
            StartCoroutine(DelayedScan());
        }

        private void LoadConfig()
        {
            // --- 通用设置 ---
            _enablePlugin = Config.Bind("1. 通用", "EnablePlugin", true, "启用或禁用整个插件。");
            _removeMode = Config.Bind("1. 通用", "RemoveMode", RemoveMode.Disable, "移除马赛克的方式：Disable (禁用), Destroy (销毁), 或 Transparent (透明)。注意：如果日志提示找不到Standard着色器，请使用Disable或Destroy。");
            _manualScanKey = Config.Bind("1. 通用", "ManualScanKey", KeyCode.F10, "按下此键可手动扫描整个场景中的马赛克。");

            // --- 扫描设置 ---
            _periodicScanInterval = Config.Bind("2. 扫描", "PeriodicScanInterval", 10f, "定期场景扫描的间隔（秒）。设置为0可禁用。");
            _sceneLoadScanDelay = Config.Bind("2. 扫描", "SceneLoadScanDelay", 1.5f, "新场景加载后延迟扫描的时间（秒）。有助于捕捉延迟加载的对象。");
            _scanBatchSize = Config.Bind("2. 扫描", "ScanBatchSize", 500, "全场景扫描时每帧处理的对象数量，以防止卡顿。");

            // --- 关键词 ---
            _objectNameKeywords = Config.Bind("3. 关键词", "ObjectNameKeywords", "mosaic,censored,pixelated,h-mosaic", "用于通过名称识别马赛克游戏对象的关键词（逗号分隔）。");
            _materialNameKeywords = Config.Bind("3. 关键词", "MaterialNameKeywords", "mosaic,censored,pixel,h-mosaic", "用于通过名称识别马赛克材质的关键词（逗号分隔）。");
            _shaderNameKeywords = Config.Bind("3. 关键词", "ShaderNameKeywords", "mosaic,pixelate,censor", "用于通过名称识别马赛克着色器的关键词（逗号分隔）。");
            _meshNameKeywords = Config.Bind("3. 关键词", "MeshNameKeywords", "censor,mosaic", "用于通过名称识别马赛克网格的关键词（逗号分隔）。");
            _textureKeywords = Config.Bind("3. 关键词", "TextureKeywords", "mosaic", "用于在纹理名称中检查的关键词（逗号分隔）。");
            _componentNameKeywords = Config.Bind("3. 关键词", "ComponentNameKeywords", "MosaicEffect,CensorEffect", "【新增】用于通过附加的脚本组件名称识别的关键词（逗号分隔）。");
            _shaderPropertyKeywords = Config.Bind("3. 关键词", "ShaderPropertyKeywords", "_PixelSize,_BlockSize,_MosaicFactor", "【新增】用于通过着色器属性名称识别的关键词（逗号分隔）。");

            // --- 高级设置 ---
            _disableMethods = Config.Bind("4. 高级", "DisableMethods", false, "启用或禁用按名称修补方法的功能。这是一个高级功能。");
            _methodDisableKeywords = Config.Bind("4. 高级", "MethodDisableKeywords", "Mosa,Mosaic,Censor", "要禁用的方法名称的关键词（逗号分隔）。");
            _assemblyNamesToPatch = Config.Bind("4. 高级", "AssemblyNamesToPatch", "Assembly-CSharp", "要在其中搜索并修补方法的程序集名称列表（逗号分隔）。");
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
                    Log.LogInfo("成功修补 GameObject.SetActive。");
                }
                else
                {
                    Log.LogError("未能找到需要修补的 GameObject.SetActive。");
                }
            }
            catch (Exception e)
            {
                Log.LogError("应用 Harmony 补丁时出错: " + e);
            }
        }

        private static void SetActivePatch(GameObject __instance, bool value)
        {
            if (value && Instance != null)
            {
                Instance.ProcessObject(__instance);
            }
        }

        public void ProcessObject(GameObject go)
        {
            if (go == null) return;

            if (_detector.IsMosaic(go))
            {
                _processor.Process(go);
            }

            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null && _detector.IsMosaic(renderer.gameObject))
                {
                    _processor.Process(renderer.gameObject);
                }
            }
        }

        private void PatchMethodsByName()
        {
            var keywords = _methodDisableKeywords.Value.Split(',').Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            if (keywords.Length == 0)
            {
                Log.LogWarning("方法禁用功能已启用，但未提供任何关键词。");
                return;
            }

            var assemblyNames = _assemblyNamesToPatch.Value.Split(',').Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => assemblyNames.Contains(asm.GetName().Name))
                .ToList();

            if (!assemblies.Any())
            {
                Log.LogError("未能找到任何指定的待修补程序集: " + _assemblyNamesToPatch.Value);
                return;
            }

            Log.LogInfo("正在程序集中搜索待修补的方法: " + string.Join(", ", assemblies.Select(a => a.GetName().Name)));

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
                            if (keywords.Any(keyword => method.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                try
                                {
                                    _harmony.Patch(method, prefix: emptyPrefix);
                                    Log.LogDebug(string.Format("已修补方法: {0}.{1}", type.FullName, method.Name));
                                    patchedCount++;
                                }
                                catch (Exception e)
                                {
                                    Log.LogError(string.Format("修补方法 {0}.{1} 失败: {2}", type.FullName, method.Name, e.Message));
                                }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Log.LogError("无法从程序集 " + assembly.GetName().Name + " 加载类型: " + ex);
                    foreach (var loaderException in ex.LoaderExceptions)
                    {
                        Log.LogError(" - " + loaderException.Message);
                    }
                }
            }
            Log.LogInfo(string.Format("方法修补完成。总共修补了 {0} 个方法。", patchedCount));
        }

        private static bool EmptyPatch() => false;

        // --- 扫描协程 ---
        private IEnumerator DelayedScan()
        {
            yield return new WaitForSeconds(_sceneLoadScanDelay.Value);
            Log.LogInfo("正在执行延迟扫描...");
            yield return StartCoroutine(ScanSceneCoro());
        }

        private IEnumerator PeriodicScan()
        {
            while (true)
            {
                yield return new WaitForSeconds(_periodicScanInterval.Value);
                Log.LogInfo("正在执行周期性扫描...");
                yield return StartCoroutine(ScanSceneCoro());
            }
        }

        private IEnumerator ScanSceneCoro()
        {
            Log.LogInfo("开始全场景扫描...");
            var allObjects = FindObjectsOfType<GameObject>();
            int processedCount = 0;
            int removedCount = 0;

            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject go = allObjects[i];
                if (go != null && go.activeInHierarchy)
                {
                    if (_detector.IsMosaic(go))
                    {
                        _processor.Process(go);
                        removedCount++;
                    }
                }

                processedCount++;
                if (processedCount % _scanBatchSize.Value == 0)
                {
                    yield return null;
                }
            }
            Log.LogInfo(string.Format("全场景扫描完成。共处理 {0} 个对象，移除了 {1} 个马赛克。", processedCount, removedCount));
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

        public MosaicDetector(string[] objectNameKeywords, string[] materialNameKeywords, string[] shaderNameKeywords, string[] meshNameKeywords, string[] textureKeywords, string[] componentNameKeywords, string[] shaderPropertyKeywords)
        {
            _objectNameKeywords = objectNameKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _materialNameKeywords = materialNameKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _shaderNameKeywords = shaderNameKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _meshNameKeywords = meshNameKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _textureKeywords = textureKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _componentNameKeywords = componentNameKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _shaderPropertyKeywords = shaderPropertyKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        }

        public bool IsMosaic(GameObject go)
        {
            if (go == null) return false;

            // 1. 按对象名称检测
            if (_objectNameKeywords.Any(keyword => go.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                DemosaicPlugin.Log.LogInfo(string.Format("通过对象名称 '{0}' 检测到马赛克。", go.name));
                return true;
            }

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                foreach (var mat in renderer.materials)
                {
                    if (mat == null) continue;
                    // 2. 按材质名称检测
                    if (_materialNameKeywords.Any(keyword => mat.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        DemosaicPlugin.Log.LogInfo(string.Format("在对象 '{0}' 上通过材质名称 '{1}' 检测到马赛克。", go.name, mat.name));
                        return true;
                    }
                    if (mat.shader != null)
                    {
                        // 3. 按着色器名称检测
                        if (_shaderNameKeywords.Any(keyword => mat.shader.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            DemosaicPlugin.Log.LogInfo(string.Format("在对象 '{0}' 上通过着色器名称 '{1}' 检测到马赛克。", go.name, mat.shader.name));
                            return true;
                        }

                        // 4. 【新增】按着色器属性检测
                        if (_shaderPropertyKeywords.Length > 0)
                        {
                            for (int i = 0; i < mat.shader.GetPropertyCount(); i++)
                            {
                                var propName = mat.shader.GetPropertyName(i);
                                if (_shaderPropertyKeywords.Any(keyword => propName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    DemosaicPlugin.Log.LogInfo(string.Format("在对象 '{0}' 的材质 '{1}' 中通过着色器属性 '{2}' 检测到马赛克。", go.name, mat.name, propName));
                                    return true;
                                }
                            }
                        }
                    }
                    // 5. 按纹理名称检测
                    if (_textureKeywords.Length > 0)
                    {
                        int[] texturePropertyIDs = mat.GetTexturePropertyNameIDs();
                        foreach (var propID in texturePropertyIDs)
                        {
                            var texture = mat.GetTexture(propID);
                            if (texture != null && _textureKeywords.Any(keyword => texture.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                DemosaicPlugin.Log.LogInfo(string.Format("在对象 '{0}' 的材质 '{1}' 中通过纹理名称 '{2}' 检测到马赛克。", go.name, mat.name, texture.name));
                                return true;
                            }
                        }
                    }
                }
            }

            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                // 6. 按网格名称检测
                if (_meshNameKeywords.Any(keyword => meshFilter.sharedMesh.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    DemosaicPlugin.Log.LogInfo(string.Format("在对象 '{0}' 上通过网格名称 '{1}' 检测到马赛克。", go.name, meshFilter.sharedMesh.name));
                    return true;
                }
            }

            // 7. 【新增】按组件名称检测
            if (_componentNameKeywords.Length > 0)
            {
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null) continue;
                    var componentTypeName = component.GetType().Name;
                    if (_componentNameKeywords.Any(keyword => componentTypeName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        DemosaicPlugin.Log.LogInfo(string.Format("在对象 '{0}' 上通过组件名称 '{1}' 检测到马赛克。", go.name, componentTypeName));
                        return true;
                    }
                }
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
        private Material _transparentMaterial;

        public MosaicProcessor(RemoveMode removeMode)
        {
            _removeMode = removeMode;
            if (_removeMode == RemoveMode.Transparent)
            {
                var shader = Shader.Find("Standard");
                if (shader != null)
                {
                    _transparentMaterial = new Material(shader);
                    _transparentMaterial.SetFloat("_Mode", 3);
                    _transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _transparentMaterial.SetInt("_ZWrite", 0);
                    _transparentMaterial.DisableKeyword("_ALPHATEST_ON");
                    _transparentMaterial.EnableKeyword("_ALPHABLEND_ON");
                    _transparentMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    _transparentMaterial.renderQueue = 3000;
                    _transparentMaterial.color = new Color(0, 0, 0, 0);
                }
                else
                {
                    DemosaicPlugin.Log.LogError("未能找到 'Standard' 着色器以创建透明材质。透明模式可能无法正常工作。");
                }
            }
        }

        public void Process(GameObject go)
        {
            if (go == null) return;

            DemosaicPlugin.Log.LogInfo(string.Format("正在以模式: {0} 处理 '{1}'。", _removeMode, go.name));

            switch (_removeMode)
            {
                case RemoveMode.Disable:
                    go.SetActive(false);
                    break;
                case RemoveMode.Destroy:
                    UnityEngine.Object.Destroy(go);
                    break;
                case RemoveMode.Transparent:
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null && _transparentMaterial != null)
                    {
                        var materials = new Material[renderer.materials.Length];
                        for (int i = 0; i < materials.Length; i++)
                        {
                            materials[i] = _transparentMaterial;
                        }
                        renderer.materials = materials;
                    }
                    else
                    {
                        DemosaicPlugin.Log.LogWarning(string.Format("对 '{0}' 应用透明模式失败 (缺少渲染器或材质)。正在降级为禁用该对象。", go.name));
                        go.SetActive(false);
                    }
                    break;
            }
        }
    }
}
