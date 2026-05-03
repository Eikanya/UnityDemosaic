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

        // GC 优化复用
        private WaitForSeconds _periodicWait;
        private WaitForSeconds _delayWait;
        private List<Renderer> _rendererBuffer = new List<Renderer>();
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
            _periodicWait = new WaitForSeconds(_periodicScanInterval.Value);
            _delayWait = new WaitForSeconds(_sceneLoadScanDelay.Value);

            // 初始化检测器与处理器
            ReloadDetector();
            _processor = new MosaicProcessor(_removeMode.Value);

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
            Log.LogInfo("Demosaic 已卸载。");
        }

        private void Update()
        {
            if (Input.GetKeyDown(_manualScanKey.Value))
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
        }

        private void LoadConfig()
        {
            _enablePlugin = Config.Bind("1. 通用", "EnablePlugin", true, "启用或禁用整个插件。");
            _removeMode = Config.Bind("1. 通用", "RemoveMode", RemoveMode.Disable, "移除方式：Disable (禁用), Destroy (销毁), 或 Transparent (透明)。");
            _manualScanKey = Config.Bind("1. 通用", "ManualScanKey", KeyCode.F10, "按下此键可手动扫描场景。");

            _periodicScanInterval = Config.Bind("2. 扫描", "PeriodicScanInterval", 10f, "周期扫描间隔（秒），0 则禁用。");
            _sceneLoadScanDelay = Config.Bind("2. 扫描", "SceneLoadScanDelay", 1.5f, "场景加载后延迟扫描的时间（秒）。");
            _scanBatchSize = Config.Bind("2. 扫描", "ScanBatchSize", 500, "每帧最多处理的对象数量，防止卡顿。");

            _objectNameKeywords = Config.Bind("3. 关键词", "ObjectNameKeywords", "moza,mosaic", "对象名关键词。");
            _materialNameKeywords = Config.Bind("3. 关键词", "MaterialNameKeywords", "moza,mosaic", "材质名关键词。");
            _shaderNameKeywords = Config.Bind("3. 关键词", "ShaderNameKeywords", "moza,mosaic,censorb", "着色器名关键词。");
            _meshNameKeywords = Config.Bind("3. 关键词", "MeshNameKeywords", "moza,mosaic,censorb", "网格名关键词。");
            _textureKeywords = Config.Bind("3. 关键词", "TextureKeywords", "mosaic", "纹理名关键词。");
            _componentNameKeywords = Config.Bind("3. 关键词", "ComponentNameKeywords", "", "组件名关键词。");
            _shaderPropertyKeywords = Config.Bind("3. 关键词", "ShaderPropertyKeywords", "moza,mosaic", "着色器属性名关键词。");

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
                _shaderPropertyKeywords.Value.Split(new[] { ',' })
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
                Log.LogInfo("关键词配置已更新，下次扫描将生效。");
            }

            // 扫描间隔变更时，更新等待对象
            if (e.ChangedSetting.Definition.Key == "PeriodicScanInterval")
            {
                _periodicWait = new WaitForSeconds(_periodicScanInterval.Value);
                // 重启周期协程
                if (_periodicScanCoroutine != null) StopCoroutine(_periodicScanCoroutine);
                if (_periodicScanInterval.Value > 0)
                    _periodicScanCoroutine = StartCoroutine(PeriodicScan());
            }
            if (e.ChangedSetting.Definition.Key == "SceneLoadScanDelay")
            {
                _delayWait = new WaitForSeconds(_sceneLoadScanDelay.Value);
            }
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

        private static void SetActivePatch(GameObject __instance, bool value)
        {
            if (value && Instance != null) Instance.ProcessObject(__instance);
        }

        private static void InstantiatePatch(UnityEngine.Object __result)
        {
            if (__result is GameObject go && Instance != null) Instance.ProcessObject(go);
        }

        public void ProcessObject(GameObject go)
        {
            if (go == null) return;

            if (_detector.IsMosaic(go))
            {
                _processor.Process(go);
            }

            _rendererBuffer.Clear();
            go.GetComponentsInChildren<Renderer>(true, _rendererBuffer);
            for (int i = 0; i < _rendererBuffer.Count; i++)
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
            _rendererBuffer.Clear();
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
            int batchSize = _scanBatchSize.Value;
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

        private Dictionary<int, bool> _materialCache = new Dictionary<int, bool>();
        private Dictionary<int, bool> _shaderCache = new Dictionary<int, bool>();
        private Dictionary<Type, bool> _componentTypeCache = new Dictionary<Type, bool>();
        private List<Component> _componentBuffer = new List<Component>();

        public MosaicDetector(string[] obj, string[] mat, string[] shader, string[] mesh, string[] tex, string[] comp, string[] prop)
        {
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
            _materialCache.Clear();
            _shaderCache.Clear();
            _componentTypeCache.Clear();
        }

        public bool IsMosaic(GameObject go)
        {
            if (go == null) return false;

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
                    DemosaicPlugin.Log.LogError("找不到 Standard 着色器，透明模式将不可用。");
                }
            }
        }

        public void Process(GameObject go)
        {
            if (go == null) return;

            DemosaicPlugin.Log.LogDebug($"去除马赛克 ({_removeMode})：{go.name}");

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
                        int matCount = renderer.sharedMaterials.Length;
                        var materials = new Material[matCount];
                        for (int i = 0; i < matCount; i++) materials[i] = _transparentMaterial;
                        renderer.sharedMaterials = materials;
                    }
                    else
                    {
                        DemosaicPlugin.Log.LogWarning($"无法对 {go.name} 应用透明模式，降级为禁用对象。");
                        go.SetActive(false);
                    }
                    break;
            }
        }

        public void Dispose()
        {
            if (_transparentMaterial != null)
                UnityEngine.Object.Destroy(_transparentMaterial);
        }
    }
}