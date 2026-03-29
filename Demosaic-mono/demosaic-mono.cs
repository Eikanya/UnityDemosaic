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
        Transparent  // 替换为透明材质 (安全，但对象仍在渲染管线中，有微小的性能开销)
    }

    // =================================================================================
    // 主插件类
    // =================================================================================
    [BepInPlugin("demosaic", "Demosaic", "1.3.0")] 
    public class DemosaicPlugin : BaseUnityPlugin
    {
        public static DemosaicPlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;
        private MosaicDetector _detector;
        private MosaicProcessor _processor;

        // --- 配置文件变量声明 ---
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

        // --- GC (垃圾回收) 优化：复用变量 ---
        // 避免在 Update 或 协程中频繁 new 对象，防止游戏出现周期性卡顿
        private WaitForSeconds _periodicWait;
        private WaitForSeconds _delayWait;
        private List<Renderer> _rendererBuffer = new List<Renderer>(); // 用于无 GC 的获取组件

        private void Awake()
        {
            // 解决 Windows 控制台输出中文时可能出现的乱码问题
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch (Exception ex) { Logger.LogWarning(string.Format("无法设置控制台输出编码为UTF-8: {0}", ex.Message)); }

            Instance = this;
            Log = Logger;
            Log.LogInfo("demosaic正在加载...");

            LoadConfig();
            
            // 尝试禁用 BepInEx 的 Config.SaveOnConfigChanged 功能。
            // 目的：防止玩家在游戏运行时手动修改了 cfg 文本文件，却被内存中的旧配置强行覆盖还原。
            try
            {
                var configFileType = typeof(ConfigEntryBase).Assembly.GetType("BepInEx.Configuration.ConfigFile");
                var saveOnConfigChangedProperty = configFileType?.GetProperty("SaveOnConfigChanged", BindingFlags.Public | BindingFlags.Instance);
                if (saveOnConfigChangedProperty != null && saveOnConfigChangedProperty.CanWrite)
                {
                    saveOnConfigChangedProperty.SetValue(Config, false);
                    Log.LogInfo("已禁用 BepInEx 的 Config.SaveOnConfigChanged。");
                }
            }
            catch (Exception ex) { Log.LogError(string.Format("尝试禁用 Config.SaveOnConfigChanged 时发生错误: {0}", ex.Message)); }

            if (!_enablePlugin.Value)
            {
                Log.LogInfo("插件已在配置文件中被禁用，正在停止初始化。");
                return;
            }

            // 初始化等待对象 (GC 优化)
            _periodicWait = new WaitForSeconds(_periodicScanInterval.Value);
            _delayWait = new WaitForSeconds(_sceneLoadScanDelay.Value);

            // 初始化检测器：传入所有配置的逗号分隔关键词
            _detector = new MosaicDetector(
                _objectNameKeywords.Value.Split(new char[] { ',' }),
                _materialNameKeywords.Value.Split(new char[] { ',' }),
                _shaderNameKeywords.Value.Split(new char[] { ',' }),
                _meshNameKeywords.Value.Split(new char[] { ',' }),
                _textureKeywords.Value.Split(new char[] { ',' }),
                _componentNameKeywords.Value.Split(new char[] { ',' }), 
                _shaderPropertyKeywords.Value.Split(new char[] { ',' })  
            );
            
            // 初始化处理器：负责执行销毁/隐藏操作
            _processor = new MosaicProcessor(_removeMode.Value);

            // 注册 Harmony，拦截 Unity 底层生成 (Instantiate) 和 激活 (SetActive) 事件
            _harmony = new Harmony("com.yourname.demosaic.harmony");
            ApplyHarmonyPatches();

            // 高级功能：通过反射根据名字拦截并禁用特定的防作弊或马赛克代码执行
            if (_disableMethods.Value) PatchMethodsByName();

            // 绑定场景生命周期事件
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded; 

            // 启动定时后台扫描机制
            if (_periodicScanInterval.Value > 0) StartCoroutine(PeriodicScan());
            
            // 首次加载场景时启动一次延迟扫描
            StartCoroutine(DelayedScan());

            Log.LogInfo("去马赛克插件加载成功！");
        }

        private void OnDestroy()
        {
            // 插件卸载或游戏退出时的清理工作，防止内存泄漏或 Hook 残留
            _harmony?.UnpatchSelf();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            StopAllCoroutines();
            Log.LogInfo("去马赛克插件已卸载。");
        }

        private void Update()
        {
            // 监听玩家是否按下了手动扫描的热键 (默认 F10)
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

        private void OnSceneUnloaded(Scene scene)
        {
            // 【极其重要】场景切换时清理核心的材质/组件缓存
            // 防止旧场景的缓存撑爆内存，也防止新场景的正常物体 ID 与旧马赛克碰撞导致误伤
            _detector.ClearCache();
        }

        private void LoadConfig()
        {
            // ... [配置绑定逻辑，与之前一致] ...
            _enablePlugin = Config.Bind("1. 通用", "EnablePlugin", true, "启用或禁用整个插件。");
            _removeMode = Config.Bind("1. 通用", "RemoveMode", RemoveMode.Disable, "移除马赛克的方式：Disable (禁用), Destroy (销毁), 或 Transparent (透明)。");
            _manualScanKey = Config.Bind("1. 通用", "ManualScanKey", KeyCode.F10, "按下此键可手动扫描整个场景中的马赛克。");
            
            _periodicScanInterval = Config.Bind("2. 扫描", "PeriodicScanInterval", 10f, "定期场景扫描的间隔（秒）。设置为0可禁用。");
            _sceneLoadScanDelay = Config.Bind("2. 扫描", "SceneLoadScanDelay", 1.5f, "新场景加载后延迟扫描的时间（秒）。");
            _scanBatchSize = Config.Bind("2. 扫描", "ScanBatchSize", 500, "全场景扫描时每帧处理的对象数量，以防止卡顿。");
            
            _objectNameKeywords = Config.Bind("3. 关键词", "ObjectNameKeywords", "mosaic,censored,pixelated", "用于通过名称识别马赛克游戏对象的关键词。");
            _materialNameKeywords = Config.Bind("3. 关键词", "MaterialNameKeywords", "mosaic,censored,pixel", "用于通过名称识别马赛克材质的关键词。");
            _shaderNameKeywords = Config.Bind("3. 关键词", "ShaderNameKeywords", "mosaic,pixelate,censor", "用于通过名称识别马赛克着色器的关键词。");
            _meshNameKeywords = Config.Bind("3. 关键词", "MeshNameKeywords", "censor,mosaic", "用于通过名称识别马赛克网格的关键词。");
            _textureKeywords = Config.Bind("3. 关键词", "TextureKeywords", "mosaic", "用于在纹理名称中检查的关键词。");
            _componentNameKeywords = Config.Bind("3. 关键词", "ComponentNameKeywords", "Mosaic,CensorEffect", "用于通过附加的脚本组件名称识别的关键词。");
            _shaderPropertyKeywords = Config.Bind("3. 关键词", "ShaderPropertyKeywords", "_PixelSize,_BlockSize,_MosaicFactor", "用于通过着色器属性名称识别的关键词。");
            
            _disableMethods = Config.Bind("4. 高级", "DisableMethods", false, "启用或禁用按名称修补方法的功能。");
            _methodDisableKeywords = Config.Bind("4. 高级", "MethodDisableKeywords", "censor,mosaic", "要禁用的方法名称的关键词。");
            _assemblyNamesToPatch = Config.Bind("4. 高级", "AssemblyNamesToPatch", "Assembly-CSharp", "要在其中搜索并修补方法的程序集名称列表。");
        }

        private void ApplyHarmonyPatches()
        {
            try
            {
                // Hook: 拦截 GameObject 的 SetActive(true) 事件
                var setActiveOriginal = AccessTools.Method(typeof(GameObject), "SetActive", new[] { typeof(bool) });
                if (setActiveOriginal != null)
                {
                    var setActivePostfix = new HarmonyMethod(typeof(DemosaicPlugin), nameof(SetActivePatch));
                    _harmony.Patch(setActiveOriginal, postfix: setActivePostfix);
                }

                // Hook: 拦截 UnityEngine.Object.Instantiate (生成物体) 事件
                var instantiateOriginal = AccessTools.Method(typeof(UnityEngine.Object), "Instantiate", new Type[] { typeof(UnityEngine.Object) });
                if (instantiateOriginal != null)
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

        // SetActive 的后缀补丁：当物体被激活时，检查它是否是马赛克
        private static void SetActivePatch(GameObject __instance, bool value)
        {
            // 只有由隐藏变为显示 (value == true) 时才检测，节省性能
            if (value && Instance != null) Instance.ProcessObject(__instance); 
        }

        // Instantiate 的后缀补丁：当游戏生成了新物体时，检查它是否是马赛克
        private static void InstantiatePatch(UnityEngine.Object __result)
        {
            // 确认生成的对象是游戏物体
            if (__result is GameObject go && Instance != null) Instance.ProcessObject(go); 
        }

        /// <summary>
        /// 对象处理入口
        /// 注解：此处故意移除了对 GameObject 自身的 InstanceID 缓存拦截。
        /// 因为游戏对象可能在实例化之后，过几帧才被挂上马赛克材质或脚本。
        /// 如果过早把它加入“不是马赛克”的白名单，就会导致漏检。
        /// 性能由下层的 Material/Shader/Type 缓存保障。
        /// </summary>
        public void ProcessObject(GameObject go)
        {
            if (go == null) return;

            // 检查父物体
            if (_detector.IsMosaic(go))
            {
                _processor.Process(go);
            }

            // 【GC 优化】使用 List<Renderer> 接收组件，避免每次调用产生 Renderer[] 数组导致的内存分配
            _rendererBuffer.Clear();
            go.GetComponentsInChildren<Renderer>(true, _rendererBuffer);
            
            // 遍历所有子物体的渲染器
            for(int i = 0; i < _rendererBuffer.Count; i++)
            {
                var renderer = _rendererBuffer[i];
                if (renderer != null && renderer.gameObject != go)
                {
                    if (_detector.IsMosaic(renderer.gameObject))
                    {
                        _processor.Process(renderer.gameObject);
                    }
                }
            }
            _rendererBuffer.Clear(); // 用完清空，留给下次使用
        }

        /// <summary>
        /// 高级功能：通过反射暴力搜索程序集中名为 Censor / Mosaic 的方法，并强制它们返回。
        /// 从而物理瘫痪游戏自带的马赛克生成代码。
        /// </summary>
        private void PatchMethodsByName()
        {
            var keywords = _methodDisableKeywords.Value.Split(new char[] { ',' }).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            if (keywords.Length == 0) return;

            var assemblyNames = _assemblyNamesToPatch.Value.Split(new char[] { ',' }).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
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
                            if (keywords.Any(keyword => method.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                try
                                {
                                    // 给检测到的方法注入一个 EmptyPatch (直接返回 false)，使其永远无法执行
                                    _harmony.Patch(method, prefix: emptyPrefix);
                                    patchedCount++;
                                }
                                catch (Exception) { /* 忽略泛型方法或不支持 Hook 的方法引发的异常 */ }
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }
            Log.LogInfo(string.Format("方法修补完成。总共修补了 {0} 个方法。", patchedCount));
        }

        // Harmony 拦截器的 Prefix，返回 false 表示拦截原本的方法执行
        private static bool EmptyPatch() => false;

        private IEnumerator DelayedScan()
        {
            yield return _delayWait; // 复用缓存的 WaitForSeconds
            yield return StartCoroutine(ScanSceneCoro());
        }

        private IEnumerator PeriodicScan()
        {
            while (true)
            {
                yield return _periodicWait; // 复用缓存的 WaitForSeconds
                yield return StartCoroutine(ScanSceneCoro());
            }
        }

        /// <summary>
        /// 全局扫描协程：分批处理防止游戏掉帧
        /// </summary>
        private IEnumerator ScanSceneCoro()
        {
            var allObjects = FindObjectsOfType<GameObject>();
            int processedCount = 0;

            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject go = allObjects[i];
                if (go != null && go.activeInHierarchy)
                {
                    if (_detector.IsMosaic(go))
                    {
                        _processor.Process(go);
                    }
                }

                processedCount++;
                // 每处理一定数量的对象，就暂停到下一帧再继续，防止瞬间卡死主线程
                if (processedCount % _scanBatchSize.Value == 0)
                {
                    yield return null; 
                }
            }
        }
    }

    // =================================================================================
    // 特定方法禁用补丁模板 (供高级用户手动配置使用)
    // =================================================================================
    [HarmonyPatch]
    public static class SpecificMethodPatchTemplate
    {
        static MethodBase TargetMethod()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (assembly == null) return null;

            var targetType = assembly.GetType("MosaicController");
            if (targetType == null) return null;

            var targetMethod = targetType.GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            return targetMethod;
        }

        static bool Prefix(MethodBase __originalMethod)
        {
            DemosaicPlugin.Log.LogWarning(string.Format("!!! Demosaic 插件已成功拦截并禁用方法: {0} !!!", __originalMethod.Name));
            return false;
        }
    }

    // =================================================================================
    // 马赛克检测器 (核心鉴别逻辑，拥有极高效率的资源三级缓存)
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

        // --- 核心三级缓存字典 (100% 安全) ---
        // 游戏引擎中，Material (材质)、Shader (着色器)、Component Type (组件类) 的定义是不会在运行时突变的。
        // 所以我们只要用它们的 ID 记住了它们是否是马赛克，终生都不用再进行复杂的字符串判定。
        private Dictionary<int, bool> _materialCache = new Dictionary<int, bool>();
        private Dictionary<int, bool> _shaderCache = new Dictionary<int, bool>();
        private Dictionary<Type, bool> _componentTypeCache = new Dictionary<Type, bool>();

        // 【GC 优化】复用的组件列表
        private List<Component> _componentBuffer = new List<Component>();

        public MosaicDetector(string[] obj, string[] mat, string[] shader, string[] mesh, string[] tex, string[] comp, string[] prop)
        {
            // 初始化时剔除掉空的关键词，防止由于配置错误引发的大规模误杀
            _objectNameKeywords = obj.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _materialNameKeywords = mat.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _shaderNameKeywords = shader.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _meshNameKeywords = mesh.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _textureKeywords = tex.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _componentNameKeywords = comp.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
            _shaderPropertyKeywords = prop.Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
        }

        public void ClearCache()
        {
            // 必须在切换场景时被调用
            _materialCache.Clear();
            _shaderCache.Clear();
            _componentTypeCache.Clear(); 
        }

        public bool IsMosaic(GameObject go)
        {
            if (go == null) return false;

            // 1. [第一级鉴别] 检查 GameObject 自身的名字
            if (ContainsAny(go.name, _objectNameKeywords)) return true;

            // 2. [第二级鉴别] 检查渲染器及其材质
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                // 【致命性能保护】绝对不能使用 renderer.materials，会隐式克隆出新材质导致内存泄漏
                var sharedMats = renderer.sharedMaterials;
                for(int i = 0; i < sharedMats.Length; i++)
                {
                    var mat = sharedMats[i];
                    if (mat == null) continue;
                    
                    int matId = mat.GetInstanceID();
                    
                    // 缓存拦截：如果这个材质已经判定过，直接极速返回结果 (O(1) 耗时)
                    if (_materialCache.TryGetValue(matId, out bool isMatMosaic))
                    {
                        if (isMatMosaic) return true;
                        continue; 
                    }

                    // 第一次遇到该材质，进入深层鉴定
                    bool isCurrentMatMosaic = CheckMaterialIsMosaic(mat);
                    _materialCache[matId] = isCurrentMatMosaic; // 将结果录入缓存
                    
                    if (isCurrentMatMosaic) return true;
                }
            }

            // 3. [第三级鉴别] 检查 Mesh 网格的名字
            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                if (ContainsAny(meshFilter.sharedMesh.name, _meshNameKeywords)) return true;
            }

            // 4. [第四级鉴别] 检查物体上挂载的所有脚本 (如 MaleSonsMosaicActivation 等)
            if (_componentNameKeywords.Length > 0)
            {
                _componentBuffer.Clear();
                go.GetComponents<Component>(_componentBuffer); // 【GC优化】使用复用列表

                for (int i = 0; i < _componentBuffer.Count; i++)
                {
                    var component = _componentBuffer[i];
                    if (component == null) continue;
                    
                    Type compType = component.GetType();
                    
                    // 缓存拦截：无论脚本名字多长，第二次遇到该类型的脚本直接判定
                    if (_componentTypeCache.TryGetValue(compType, out bool isMosaicComp))
                    {
                        if (isMosaicComp) return true;
                        continue;
                    }

                    // 第一次遇到，执行字符串搜索
                    bool match = ContainsAny(compType.Name, _componentNameKeywords);
                    _componentTypeCache[compType] = match; // 写入缓存
                    
                    if (match) return true;
                }
                _componentBuffer.Clear();
            }

            return false; // 通过了所有检测，不是马赛克
        }

        /// <summary>
        /// 深入判定一个材质是否为马赛克
        /// </summary>
        private bool CheckMaterialIsMosaic(Material mat)
        {
            // 检测材质球的名称
            if (ContainsAny(mat.name, _materialNameKeywords)) return true;

            // 检测该材质使用的着色器 (Shader)
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

            // 深度检测：遍历该材质上绑定的所有贴图 (纹理) 的名称
            if (_textureKeywords.Length > 0)
            {
                int[] texturePropertyIDs = mat.GetTexturePropertyNameIDs();
                for (int i = 0; i < texturePropertyIDs.Length; i++)
                {
                    var texture = mat.GetTexture(texturePropertyIDs[i]);
                    if (texture != null && ContainsAny(texture.name, _textureKeywords)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 深入判定一个 Shader 是否具有马赛克的特征
        /// </summary>
        private bool CheckShaderIsMosaic(Shader shader)
        {
            if (ContainsAny(shader.name, _shaderNameKeywords)) return true;

            // 遍历着色器暴露出来的所有变量名 (如 "_PixelSize")。
            // 注意：这非常耗时！但由于外层加入了 _shaderCache，每个着色器这辈子只会被遍历一次。
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

        /// <summary>
        /// 辅助方法：判断目标字符串是否包含数组中的任何一个关键字 (忽略大小写)
        /// </summary>
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
    // 马赛克处理器 (用于执行物理销毁、隐身等终结操作)
    // =================================================================================
    public class MosaicProcessor
    {
        private readonly RemoveMode _removeMode;
        private Material _transparentMaterial;

        public MosaicProcessor(RemoveMode removeMode)
        {
            _removeMode = removeMode;
            // 如果玩家选择 Transparent (透明模式)，则在初始化时先准备好一个完全隐形的公共材质
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
                    _transparentMaterial.color = new Color(0, 0, 0, 0); // 颜色 Alpha = 0，达到光学隐形
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

            DemosaicPlugin.Log.LogInfo(string.Format("已去除马赛克 ({0}模式): {1}", _removeMode, go.name));

            switch (_removeMode)
            {
                case RemoveMode.Disable:
                    go.SetActive(false); // 禁用物体。最推荐的方法。
                    break;
                case RemoveMode.Destroy:
                    UnityEngine.Object.Destroy(go); // 从内存抹除。有小概率导致游戏报错。
                    break;
                case RemoveMode.Transparent:
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null && _transparentMaterial != null)
                    {
                        // 【内存安全】使用 sharedMaterials 而不是 materials，避免在隐形时意外创建新材质
                        int matCount = renderer.sharedMaterials.Length;
                        
                        var materials = new Material[matCount];
                        for (int i = 0; i < matCount; i++)
                        {
                            materials[i] = _transparentMaterial; // 全局替换为同一个隐形材质
                        }
                        
                        renderer.sharedMaterials = materials;
                    }
                    else
                    {
                        // 降级处理：有些马赛克不是通过 Renderer 画出来的 (可能是UI等)，那就干脆禁用它
                        DemosaicPlugin.Log.LogWarning(string.Format("对 '{0}' 应用透明模式失败 (缺少渲染器或材质)。正在降级为禁用该对象。", go.name));
                        go.SetActive(false); 
                    }
                    break;
            }
        }
    }
}