using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
#if DEMOSAIC_IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
#endif

namespace DemosaicPlugin
{
    /// <summary>
    /// 插件核心逻辑。Mono 与 IL2CPP 变体共用，两个变体的入口类只负责
    /// 创建引擎、驱动 <see cref="Update"/>、安装 / 卸载 Harmony 补丁。
    /// </summary>
    internal sealed partial class DemosaicEngine
    {
        public static DemosaicEngine Instance { get; private set; }

        /// <summary>引擎使用的日志源，供补丁等静态上下文复用。</summary>
        public ManualLogSource Log => _log;

        /// <summary>材质 setter hook 递归保护：处理期间不再触发 hook。用计数支持嵌套。</summary>
        private static int _materialGuardDepth;

        public static bool IsProcessingMaterial => _materialGuardDepth > 0;

        /// <summary>进入材质处理区，须以 using 成对释放（可安全嵌套）。</summary>
        public static MaterialGuard EnterMaterialGuard()
        {
            _materialGuardDepth++;
            return default;
        }

        /// <summary>零大小 using 守卫，Dispose 时退出材质处理区。</summary>
        public readonly struct MaterialGuard : IDisposable
        {
            public void Dispose() => _materialGuardDepth--;
        }

        private const int ExportFlushThreshold = 100;

        private readonly ManualLogSource _log;
        private readonly ConfigFile _config;
        private readonly DemosaicSettings _settings;
        private readonly MosaicDetector _detector;
        private readonly MosaicProcessor _processor;
        private readonly TransparentMaterialProvider _materialProvider;

        private readonly HashSet<int> _processedRendererIds = new HashSet<int>();
        private readonly HashSet<int> _processedCameraEffectIds = new HashSet<int>();
        private readonly HashSet<int> _processedDecalIds = new HashSet<int>();
        private readonly List<Renderer> _scanTargets = new List<Renderer>();
        private readonly List<Renderer> _exportTargets = new List<Renderer>();
        private readonly StringBuilder _exportBuffer = new StringBuilder(4096);
        private readonly Stopwatch _budgetWatch = new Stopwatch();

        /// <summary>监视列表条目：Renderer 与其实例 ID（对象销毁后 ID 仍可用于清理）。</summary>
        private struct WatchEntry
        {
            public Renderer Renderer;
            public int RendererId;
        }

        // 两个列表交替使用，便于在时间预算耗尽时把未复核的尾部原样保留
        private List<WatchEntry> _watchList = new List<WatchEntry>();
        private List<WatchEntry> _watchScratch = new List<WatchEntry>();
        private readonly HashSet<int> _watchIds = new HashSet<int>();
        private float _watchTimer;

        /// <summary>同一根对象在此冷却期内重复激活时，跳过整棵子树的递归评估（对象池每帧 SetActive 的热路径）。</summary>
        private const float RootActivationRetrySeconds = 1f;
        private const int CleanRootCacheCap = 20000;
        private readonly Dictionary<int, float> _cleanRootRetryAt = new Dictionary<int, float>();

#if DEMOSAIC_IL2CPP
        // IL2CPP 侧 SceneManager 事件类型是 Il2CppInterop 生成的 UnityAction，
        // 它由 System.Action 隐式转换而来（UnityAction 本身的构造函数只接受 IntPtr）
        private Action<Scene, LoadSceneMode> _sceneLoadedAction;
        private Action<Scene> _sceneUnloadedAction;
#else
        // Mono 侧是 Unity 原生的 UnityEngine.Events.UnityAction
        private UnityEngine.Events.UnityAction<Scene, LoadSceneMode> _sceneLoadedAction;
        private UnityEngine.Events.UnityAction<Scene> _sceneUnloadedAction;
#endif

        private bool _activated;
        private bool _scanning;
        private int _scanIndex;
        private bool _exporting;
        private int _exportIndex;
        private float _delayTimer = -1f;
        private float _periodicTimer;

        // URP DecalProjector 的反射解析结果（类型名固定，整个会话只解析一次）
        private Type _decalProjectorType;
        private PropertyInfo _decalMaterialProperty;
        private bool _decalProjectorResolved;

        // 组件马赛克开关：目标类型解析结果 + 复查节流状态


        // 关键字猜测结果按属性名缓存：原实现每轮每属性都要 ToUpperInvariant + 拼 8 个字符串

        // 目标组件实例列表缓存：FindObjectsOfType 是全场景遍历，没必要每轮（默认 1 秒）都做一次


        // 材质马赛克属性归零：目标 Renderer 缓存 + Shader 属性表缓存（按 Shader 实例 ID）


        /// <summary>解析目标组件类型时不扫这些框架程序集，避免一次性反射上百个程序集造成卡顿。</summary>
        private static readonly string[] FrameworkAssemblyPrefixes =
        {
            "System", "Microsoft", "Il2Cpp", "BepInEx", "0Harmony", "Harmony", "Mono.",
            "mono", "netstandard", "mscorlib", "Demosaic", "UnityEngine", "Unity."
        };

        private static int _lastMaterialHookFrame = -1;
        private static readonly HashSet<int> MaterialHookFrameDedup = new HashSet<int>();

        private DemosaicEngine(ManualLogSource log, ConfigFile config, DemosaicSettings settings)
        {
            _log = log;
            _config = config;
            _settings = settings;
            _detector = new MosaicDetector(settings, log);
            _materialProvider = new TransparentMaterialProvider(log);
            _processor = new MosaicProcessor(settings, log, _detector, _materialProvider);
        }

        public static DemosaicEngine Create(ManualLogSource log, ConfigFile config)
        {
            SafeInput.Log = log;

            var settings = DemosaicSettings.Bind(config, log);
            var engine = new DemosaicEngine(log, config, settings);
            Instance = engine;
            engine.Initialize();
            return engine;
        }

        private void Initialize()
        {
            // 即使启动时被配置禁用也要先订阅 SettingChanged，
            // 否则运行时把 EnablePlugin 改回 true 会永远停在半初始化状态。
            _config.SettingChanged += OnSettingChanged;
            TryActivate();
        }

        /// <summary>幂等激活：准备透明材质、订阅场景事件并安排首次扫描。</summary>
        private void TryActivate()
        {
            if (_activated) return;

            if (!_settings.EnablePlugin.Value)
            {
                _log.LogInfo("插件已在配置中被禁用，等待配置开启。");
                return;
            }

            _activated = true;
            _materialProvider.TryCreate();

            // 组件类型解析要遍历程序集，放在插件加载期（此时没有帧压力），避免游戏中途卡顿
            if (_settings.EnableMosaicSwitchOff.Value)
                ResolveSwitchTypes();

#if DEMOSAIC_IL2CPP
            _sceneLoadedAction = new Action<Scene, LoadSceneMode>(OnSceneLoaded);
            _sceneUnloadedAction = new Action<Scene>(OnSceneUnloaded);
#else
            _sceneLoadedAction = new UnityEngine.Events.UnityAction<Scene, LoadSceneMode>(OnSceneLoaded);
            _sceneUnloadedAction = new UnityEngine.Events.UnityAction<Scene>(OnSceneUnloaded);
#endif
            SceneManager.sceneLoaded += _sceneLoadedAction;
            SceneManager.sceneUnloaded += _sceneUnloadedAction;

            if (SceneManager.GetActiveScene().isLoaded)
                _delayTimer = Mathf.Max(0f, _settings.SceneLoadScanDelay.Value);

            _log.LogInfo($"Demosaic {DemosaicVersion.Value} 加载成功，处理模式: {_settings.RemoveModeEntry.Value}");
        }

        public void Dispose()
        {
            _config.SettingChanged -= OnSettingChanged;

            if (_sceneLoadedAction != null)
            {
                SceneManager.sceneLoaded -= _sceneLoadedAction;
                _sceneLoadedAction = null;
            }
            if (_sceneUnloadedAction != null)
            {
                SceneManager.sceneUnloaded -= _sceneUnloadedAction;
                _sceneUnloadedAction = null;
            }

            CancelPendingWork();
            FlushExport();

            // 监视列表与去抖表持有场景对象引用，卸载时必须放开
            ResetProcessedSets();

            // 静态守卫跨插件重载存活，卸载时归零以免残留处理中状态
            _materialGuardDepth = 0;

            if (Instance == this) Instance = null;
        }

        /// <summary>丢弃进行中的扫描 / 导出，并取消待触发的延迟扫描。</summary>
        private void CancelPendingWork()
        {
            _scanning = false;
            _exporting = false;
            _delayTimer = -1f;
            _scanTargets.Clear();
            _exportTargets.Clear();
        }

        // =====================================================================
        // 每帧驱动
        // =====================================================================
        public void Update(float deltaTime)
        {
            if (!_settings.EnablePlugin.Value) return;

            if (_delayTimer >= 0f)
            {
                _delayTimer -= deltaTime;
                if (_delayTimer < 0f)
                    StartScan();
            }

            float interval = Mathf.Max(0f, _settings.PeriodicScanInterval.Value);
            if (interval > 0f)
            {
                _periodicTimer += deltaTime;
                if (_periodicTimer >= interval)
                {
                    _periodicTimer = 0f;
                    StartScan();
                }
            }

            float watchInterval = Mathf.Max(0f, _settings.WatchlistRecheckInterval.Value);
            if (watchInterval > 0f)
            {
                _watchTimer += deltaTime;
                if (_watchTimer >= watchInterval)
                {
                    _watchTimer = 0f;
                    ProcessWatchlistRecheck();
                }
            }

            // 组件马赛克开关 + 材质马赛克属性：定时复查 + 被激活/实例化事件唤醒（最快 0.25 秒响应一次）
            if (_settings.EnableMosaicSwitchOff.Value || _settings.EnableMaterialMosaicOff.Value)
            {
                float switchInterval = _settings.MosaicSwitchRecheckInterval.Value;
                if (switchInterval > 0f)
                {
                    float due = Mathf.Max(0.25f, switchInterval);
                    _switchTimer += deltaTime;
                    if (_switchTimer >= due || (_switchDirty && _switchTimer >= 0.25f))
                    {
                        _switchTimer = 0f;
                        ProcessMosaicSwitches();
                        _switchDirty = false;
                        ApplyMaterialMosaicFix();
                    }
                }
            }

            if (SafeInput.GetKeyDown(_settings.ManualScanKey.Value))
            {
                _log.LogInfo("手动扫描热键触发。");
                RequestFullScan();
            }

            if (SafeInput.GetKeyDown(_settings.ExportSwitchKey.Value))
            {
                _log.LogInfo("开始导出马赛克开关组件信息...");
                DumpMosaicSwitches();
            }

            if (SafeInput.GetKeyDown(_settings.ScanCandidatesKey.Value))
            {
                _log.LogInfo("开始扫描场景中的可疑马赛克物件...");
                ScanMosaicCandidates();
            }

            if (SafeInput.GetKeyDown(_settings.ExportSceneKey.Value))
            {
                _log.LogInfo("开始将场景渲染器信息导出到日志...");
                StartExport();
            }

            ProcessScanBatch();
            ProcessExportBatch();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _log.LogInfo($"场景已加载: {scene.name}，准备延迟扫描。");
            ResetProcessedSets();
            _delayTimer = Mathf.Max(0f, _settings.SceneLoadScanDelay.Value);
            _switchDirty = true;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            CancelPendingWork();
            ResetProcessedSets();
            _detector.ClearCache();
            _switchInstanceCache.Clear();
            _switchInstancesRefreshedAt = -1f;
        }

        private void ResetProcessedSets()
        {
            _processedRendererIds.Clear();
            _processedCameraEffectIds.Clear();
            _processedDecalIds.Clear();
            _watchList.Clear();
            _watchScratch.Clear();
            _watchIds.Clear();
            _watchTimer = 0f;
            _cleanRootRetryAt.Clear();
            _switchLoggedIds.Clear();
            _materialFixTargets.Clear();
            _materialFixIds.Clear();
            _switchDirty = true;
        }

        public void RequestFullScan()
        {
            CancelPendingWork();
            ResetProcessedSets();
            _detector.ClearCache();
            StartScan();
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            var key = e.ChangedSetting.Definition.Key;
            if (key == null) return;

            if (key == "EnablePlugin")
            {
                if (_settings.EnablePlugin.Value)
                {
                    TryActivate();
                    RequestFullScan();
                    _log.LogInfo("插件已启用，开始扫描。");
                }
                else
                {
                    CancelPendingWork();
                    _log.LogInfo("插件已禁用，停止扫描；已处理过的对象保持现状。");
                }
                return;
            }

            if (key.StartsWith("MosaicSwitch", StringComparison.OrdinalIgnoreCase))
            {
                _settings.RefreshKeywords();
                ResetSwitchTypeCache();
                _switchDirty = true;
                _log.LogInfo($"配置 {key} 已更新，已重新解析马赛克开关组件并安排复查。");
                return;
            }

            if (key.IndexOf("Keywords", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key == "EnableTextureDetection" || key == "DetectParentObjectNames" ||
                key == DemosaicSettings.ShaderPropertyThresholdKey)
            {
                _settings.RefreshKeywords();
                _detector.ClearCache();
                ResetProcessedSets();
                StartScan();
                _log.LogInfo("关键词配置已更新，已重新扫描。");
                return;
            }

            if (key == "RemoveMode" || key == "LogProcessedObjects" || key == "ReverifyProcessedRenderers")
            {
                ResetProcessedSets();
                StartScan();
                _log.LogInfo($"配置 {key} 已更新，已重新扫描。");
            }
        }

        // =====================================================================
        // 扫描
        // =====================================================================
        private void StartScan()
        {
            _scanTargets.Clear();
            if (SceneObjectFinder.FillRenderers(_scanTargets, _settings.IncludeInactiveObjects.Value) == 0)
            {
                _scanning = false;
                ProcessCameraEffects();
                ProcessDecalsAndProjectors();
                ProcessMosaicSwitches();
                ApplyMaterialMosaicFix();
                return;
            }

            _scanIndex = 0;
            _scanning = true;
        }

        private void ProcessScanBatch()
        {
            if (!_scanning) return;

            int batchSize = Mathf.Max(1, _settings.ScanBatchSize.Value);
            double budgetMs = Mathf.Max(0.5f, _settings.ScanTimeBudgetMs.Value);

            _budgetWatch.Restart();
            int processed = 0;
            bool trackMaterialFix = _settings.EnableMaterialMosaicOff.Value;

            while (_scanIndex < _scanTargets.Count && processed < batchSize)
            {
                var renderer = _scanTargets[_scanIndex++];
                processed++;
                if (renderer != null)
                {
                    ProcessRenderer(renderer);
                    if (trackMaterialFix) TrackMaterialFixTarget(renderer);
                }

                // 每 32 个检查一次时间预算，避免低端机上单帧超时导致掉帧
                if ((processed & 31) == 0 && _budgetWatch.Elapsed.TotalMilliseconds >= budgetMs)
                    break;
            }

            _budgetWatch.Stop();

            if (_scanIndex >= _scanTargets.Count)
            {
                _scanning = false;
                _scanTargets.Clear();
                ProcessCameraEffects();
                ProcessDecalsAndProjectors();
                ProcessMosaicSwitches();
                ApplyMaterialMosaicFix();
                _detector.TrimCachesIfOversized();
            }
        }

        public bool ProcessRenderer(Renderer renderer)
        {
            if (renderer == null || !_settings.EnablePlugin.Value) return false;

            int rendererId = renderer.GetInstanceID();

            if (_processedRendererIds.Contains(rendererId))
            {
                // 已处理过：仅在开启复核且材质又变回马赛克时重新处理，
                // 否则直接跳过（缓存命中，成本极低）
                if (!_settings.ReverifyProcessedRenderers.Value) return false;
                if (!_detector.HasMosaicMaterial(renderer)) return false;
            }

            if (!_detector.IsMosaic(renderer))
            {
                // 材质带马赛克属性但当前关着：进监视列表，游戏改值后几秒内就能处理
                if (_detector.HasDormantMaterial(renderer))
                    AddWatch(renderer, rendererId);
                return false;
            }

            _processor.Process(renderer);
            _processedRendererIds.Add(rendererId);
            AddWatch(renderer, rendererId);
            return true;
        }

        /// <summary>
        /// 激活 / 实例化热路径。SetActive 在每个游戏里都被高频调用（对象池尤其明显），
        /// 所以同一根对象在短冷却期内重复激活时直接跳过整棵子树的递归评估。
        /// </summary>
        public void ProcessGameObject(GameObject go)
        {
            if (go == null || !_settings.EnablePlugin.Value) return;

            int rootId = go.GetInstanceID();
            if (_cleanRootRetryAt.TryGetValue(rootId, out var retryAt) && Time.time < retryAt)
                return;

            if (_detector.IsMosaic(go))
            {
                _processor.Process(go);
                return;
            }

            // 对象本身没命中时，逐个检查子 Renderer（含自身，
            // 旧实现跳过自身 Renderer，导致"父节点命中但自身材质命中"的对象被漏掉）
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            bool anyProcessed = false;
            bool hasMaterialFix = false;
            bool trackMaterialFix = _settings.EnableMaterialMosaicOff.Value;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (ProcessRenderer(r))
                    anyProcessed = true;
                if (trackMaterialFix)
                {
                    TrackMaterialFixTarget(r);
                    if (_materialFixIds.Contains(r.GetInstanceID()))
                        hasMaterialFix = true;
                }
            }

            if (hasMaterialFix)
            {
                anyProcessed = true;
                _switchDirty = true;
                ProcessMosaicSwitches();
                _switchDirty = false;
                ApplyMaterialMosaicFix();
            }

            // 子树里出现过马赛克就不记干净，下次激活仍需复核
            if (!anyProcessed)
            {
                if (_cleanRootRetryAt.Count >= CleanRootCacheCap)
                    _cleanRootRetryAt.Clear();
                _cleanRootRetryAt[rootId] = Time.time + RootActivationRetrySeconds;
            }
        }

        // =====================================================================
        // 监视列表：只盯"曾命中马赛克"与"属性处于关闭状态"的 Renderer
        // =====================================================================
        private void AddWatch(Renderer renderer, int rendererId)
        {
            if (_watchIds.Add(rendererId))
                _watchList.Add(new WatchEntry { Renderer = renderer, RendererId = rendererId });
        }

        /// <summary>
        /// 复核监视列表：不做场景对象发现，也不评估与马赛克无关的 Renderer，因此可以跑得比全量扫描勤得多。
        /// 马赛克复发与游戏改 Shader 参数激活，都靠这条路径兜住。
        /// </summary>
        private void ProcessWatchlistRecheck()
        {
            if (_watchList.Count == 0) return;

            double budgetMs = Mathf.Max(0.5f, _settings.ScanTimeBudgetMs.Value);
            _budgetWatch.Restart();
            _watchScratch.Clear();

            int processed = 0;
            bool budgetOut = false;

            for (int i = 0; i < _watchList.Count; i++)
            {
                var entry = _watchList[i];

                if (budgetOut)
                {
                    _watchScratch.Add(entry);
                    continue;
                }

                if (entry.Renderer == null)
                {
                    _watchIds.Remove(entry.RendererId);
                    continue;
                }

                ProcessRenderer(entry.Renderer);

                bool keep = _processedRendererIds.Contains(entry.RendererId) ||
                            _detector.HasDormantMaterial(entry.Renderer);
                if (keep)
                    _watchScratch.Add(entry);
                else
                    _watchIds.Remove(entry.RendererId);

                processed++;
                if ((processed & 31) == 0 && _budgetWatch.Elapsed.TotalMilliseconds >= budgetMs)
                    budgetOut = true;
            }

            var swap = _watchList;
            _watchList = _watchScratch;
            _watchScratch = swap;
        }

        // =====================================================================
        // 材质 setter hook 回调
        // =====================================================================
        internal bool MaterialSetterHookEnabled => _settings.EnableMaterialSetterHook.Value;

        internal void OnMaterialAssigned(Renderer renderer)
        {
            if (renderer == null || IsProcessingMaterial) return;
            if (!_settings.EnableMaterialSetterHook.Value) return;

            try
            {
                int rendererId = renderer.GetInstanceID();

                // 帧内去重：同一帧同一 Renderer 只处理一次
                int currentFrame = Time.frameCount;
                if (currentFrame != _lastMaterialHookFrame)
                {
                    _lastMaterialHookFrame = currentFrame;
                    MaterialHookFrameDedup.Clear();
                }
                if (!MaterialHookFrameDedup.Add(rendererId)) return;

                if (_detector.IsMosaic(renderer))
                {
                    using (EnterMaterialGuard())
                    {
                        _processor.Process(renderer);
                        _processedRendererIds.Add(rendererId);
                        AddWatch(renderer, rendererId);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"材质 setter hook 处理异常: {ex.Message}");
            }
        }

        // =====================================================================
        // Camera 后处理 / Decal / Projector
        // =====================================================================
        internal void ProcessCameraEffects()
        {
            if (!_settings.EnableCameraEffectDetection.Value) return;

            var keywords = _settings.CameraEffectKeywordArray;
            if (keywords == null || keywords.Length == 0) return;

            var cameras = SceneObjectFinder.FindObjects<Camera>(_settings.IncludeInactiveObjects.Value);
            for (int i = 0; i < cameras.Count; i++)
            {
                var cam = cameras[i];
                if (cam == null) continue;

                var components = cam.gameObject.GetComponents<MonoBehaviour>();
                for (int j = 0; j < components.Length; j++)
                {
                    var comp = components[j];
                    if (comp == null) continue;

                    int compId = comp.GetInstanceID();
                    if (_processedCameraEffectIds.Contains(compId)) continue;

                    string typeName = RuntimeInterop.GetComponentTypeName(comp);
                    if (string.IsNullOrEmpty(typeName)) continue;

                    if (!MosaicDetector.ContainsAny(typeName, keywords)) continue;

                    comp.enabled = false;
                    _processedCameraEffectIds.Add(compId);
                    _log.LogInfo($"已禁用 Camera 后处理组件: {typeName} (on {cam.gameObject.name})");
                }
            }
        }

        internal void ProcessDecalsAndProjectors()
        {
            if (!_settings.EnableDecalDetection.Value) return;

            // 1. Built-in RP 的 Projector
            try
            {
                var projectors = SceneObjectFinder.FindObjects<Projector>(_settings.IncludeInactiveObjects.Value);
                for (int i = 0; i < projectors.Count; i++)
                {
                    var proj = projectors[i];
                    if (proj == null) continue;

                    int projId = proj.GetInstanceID();
                    if (_processedDecalIds.Contains(projId)) continue;

                    var mat = proj.material;
                    if (mat != null && _detector.CheckMaterialFull(mat))
                    {
                        proj.enabled = false;
                        _processedDecalIds.Add(projId);
                        _log.LogInfo($"已禁用 Projector: {proj.gameObject.name} (材质: {mat.name})");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"Projector 检测跳过: {ex.Message}");
            }

            // 2. URP 的 DecalProjector（无编译期引用，走反射）
            ProcessDecalProjectors();
        }

        /// <summary>解析 URP DecalProjector 的类型与材质属性；结论（含"不可用"）只算一次，避免每轮扫描重复反射。</summary>
        private bool TryResolveDecalProjector()
        {
            if (_decalProjectorResolved)
                return _decalProjectorType != null && _decalMaterialProperty != null;

            _decalProjectorResolved = true;
            try
            {
                _decalProjectorType =
                    Type.GetType("UnityEngine.Rendering.Universal.DecalProjector, Unity.RenderPipelines.Universal.Runtime")
                    ?? Type.GetType("UnityEngine.Rendering.Universal.DecalProjector, Unity.RenderPipelines.Universal");

                if (_decalProjectorType != null)
                    _decalMaterialProperty = _decalProjectorType.GetProperty("material")
                                             ?? _decalProjectorType.GetProperty("m_Material");
            }
            catch (Exception ex)
            {
                _log.LogDebug($"DecalProjector 类型解析失败: {ex.Message}");
            }

            if (_decalProjectorType == null)
                _log.LogDebug("未找到 DecalProjector 类型（游戏未使用 URP Decal），跳过 Decal 检测。");
            else if (_decalMaterialProperty == null)
                _log.LogDebug("DecalProjector 的 material 属性不可用，跳过 Decal 检测。");

            return _decalProjectorType != null && _decalMaterialProperty != null;
        }

        private void ProcessDecalProjectors()
        {
            if (!TryResolveDecalProjector()) return;

            try
            {
#if DEMOSAIC_IL2CPP
                var decals = Object.FindObjectsOfType(Il2CppType.From(_decalProjectorType));
#else
                var decals = Object.FindObjectsOfType(_decalProjectorType, true);
#endif
                if (decals == null) return;

                for (int i = 0; i < decals.Length; i++)
                {
                    var raw = decals[i];
                    if (raw == null) continue;

                    int decalId = raw.GetInstanceID();
                    if (_processedDecalIds.Contains(decalId)) continue;

                    var behaviour = RuntimeInterop.AsMonoBehaviour(raw);
                    if (behaviour == null) continue;

                    var mat = RuntimeInterop.AsMaterial(_decalMaterialProperty.GetValue(behaviour));
                    if (mat != null && _detector.CheckMaterialFull(mat))
                    {
                        behaviour.enabled = false;
                        _processedDecalIds.Add(decalId);
                        _log.LogInfo($"已禁用 DecalProjector: {behaviour.gameObject.name} (材质: {mat.name})");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"DecalProjector 检测跳过: {ex.Message}");
            }
        }


        /// <summary>
        /// 把 <c>FindObjectsOfType</c> 返回的对象还原成具体代理类型。
        ///
        /// IL2CPP 下 interop 数组的元素是按「声明类型」构造的（这里是 <c>UnityEngine.Object</c>），
        /// 再用 <c>TryCast&lt;MonoBehaviour&gt;</c> 拿到的对象托管类型就是 MonoBehaviour —— 
        /// 于是 <c>GetType()</c> 只能看到基类成员、拿具体类型的方法去 Invoke 会抛
        /// "Object does not match target type"。这里按同一指针重建正确类型的包装。
        /// </summary>
        private static object AsConcreteType(object raw, Type concreteType)
        {
            if (raw == null || concreteType == null) return null;
            if (concreteType.IsInstanceOfType(raw)) return raw;

#if DEMOSAIC_IL2CPP
            if (raw is Il2CppObjectBase baseObj)
            {
                // 1) Il2CppInterop 提供的类型无关转换（新版本有 TryCast(Type) 重载）
                try
                {
                    var tryCast = typeof(Il2CppObjectBase).GetMethod("TryCast", new[] { typeof(Type) });
                    if (tryCast != null)
                    {
                        var casted = tryCast.Invoke(baseObj, new object[] { concreteType });
                        if (casted != null) return casted;
                    }
                }
                catch (Exception)
                {
                    // 老版本没有该重载，走下面的构造函数
                }

                // 2) 用同一指针构造具体代理类型（Il2CppInterop 生成的类都有 (IntPtr) 构造）
                try
                {
                    var ctor = concreteType.GetConstructor(new[] { typeof(IntPtr) });
                    if (ctor != null)
                    {
                        var created = ctor.Invoke(new object[] { baseObj.Pointer });
                        if (created != null) return created;
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
#endif
            return null;
        }


        // =====================================================================
        // 场景导出
        // =====================================================================
        private void StartExport()
        {
            _exportTargets.Clear();
            _exportBuffer.Clear();

            if (SceneObjectFinder.FillRenderers(_exportTargets, false) == 0)
            {
                _log.LogInfo("场景中未找到任何渲染器。");
                return;
            }

            _exportIndex = 0;
            _exporting = true;
            _log.LogInfo($"准备导出 {_exportTargets.Count} 个渲染器信息...");
        }

        private void ProcessExportBatch()
        {
            if (!_exporting) return;

            int batchSize = Mathf.Max(1, _settings.ScanBatchSize.Value);
            _budgetWatch.Restart();
            int processed = 0;

            while (_exportIndex < _exportTargets.Count && processed < batchSize)
            {
                var renderer = _exportTargets[_exportIndex++];
                processed++;

                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                    AppendExportLine(renderer);

                if ((processed & 31) == 0 && _budgetWatch.Elapsed.TotalMilliseconds >= 2.0)
                    break;
            }

            _budgetWatch.Stop();

            if (_exportIndex >= _exportTargets.Count)
            {
                _exporting = false;
                _exportTargets.Clear();
                FlushExport();
                _log.LogInfo("场景渲染器信息导出完成！");
            }
            else if (_exportBuffer.Length >= ExportFlushThreshold * 64)
            {
                // 边扫边刷，避免结束时一次性写几万行把主线程卡住
                FlushExport();
            }
        }

        private void AppendExportLine(Renderer renderer)
        {
            var go = renderer.gameObject;
            string meshName = RuntimeInterop.GetMeshName(renderer) ?? "N/A";
            string goName = go.name;

            var materials = RuntimeInterop.GetSharedMaterials(renderer);
            if (materials == null) return;

            for (int i = 0; i < materials.Length; i++)
            {
                var mat = materials[i];
                if (mat == null) continue;

                string shaderName = mat.shader != null ? mat.shader.name : "N/A";
                _exportBuffer.Append("[Demosaic Export] GO: ").Append(goName)
                    .Append(" | Material: ").Append(mat.name)
                    .Append(" | Shader: ").Append(shaderName)
                    .Append(" | Mesh: ").Append(meshName)
                    .Append('\n');
            }
        }

        private void FlushExport()
        {
            if (_exportBuffer.Length == 0) return;
            _log.LogInfo(_exportBuffer.ToString());
            _exportBuffer.Clear();
        }

        // =====================================================================
        // Harmony
        // =====================================================================
        public void InstallPatches(Harmony harmony)
        {
            try
            {
                harmony.PatchAll(typeof(DemosaicEngine).Assembly);
                ApplyInstantiatePatches(harmony);

                if (_settings.EnableMaterialSetterHook.Value)
                    ApplyMaterialSetterPatches(harmony);

                if (_settings.DisableMethods.Value)
                    PatchMethodsByName(harmony);

                _log.LogInfo("Harmony 补丁应用成功，已启用实时对象拦截。");
            }
            catch (Exception e)
            {
                _log.LogError("Harmony 补丁应用失败: " + e);
            }
        }

        /// <summary>诊断：Hook 绘制调用（DrawMesh*/RenderMesh*）与全局属性写入（SetGlobal*），定位无名绘制者。</summary>
 
 
        private void ApplyInstantiatePatches(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(DemosaicPatches), nameof(DemosaicPatches.InstantiatePostfix));

            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Object)))
            {
                if (method.Name != nameof(Object.Instantiate)) continue;
                if (method.IsGenericMethodDefinition || method.ContainsGenericParameters) continue;

                try
                {
                    harmony.Patch(method, postfix: postfix);
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"跳过 Instantiate 重载 {method}: {ex.Message}");
                }
            }
        }

        private void ApplyMaterialSetterPatches(Harmony harmony)
        {
#if DEMOSAIC_IL2CPP
            // IL2CPP 下 4 个 Renderer 材质 setter（material / sharedMaterial / materials / sharedMaterials）
            // 一个都不能 Hook：Harmony 给 il2cpp 原生方法装 detour 后，DMD 包装器「回原方法」的路径
            // 会再次落进同一 detour，形成无限递归并击穿线程栈保护页。
            // 症状是进程级闪退（Fatal error. AccessViolationException @ set_sharedMaterial，
            // 调用栈里 il2cpp_runtime_invoke / DMD / (il2cpp -> managed) 三段重复 N 次），
            // 无法被 try/catch 拦截。原实现只排除了数组 setter，单值 setter 是同一机制，一并排除。
            // 实时捕获改由 SetActive / Instantiate Hook + 监视列表（WatchlistRecheckInterval）承担。
            _log.LogWarning("IL2CPP 运行时不支持 Renderer 材质 setter Hook（会递归崩溃），已跳过；" +
                            "动态材质变更改由 SetActive / Instantiate 事件与监视列表兜底。");
#else
            var postfix = new HarmonyMethod(typeof(DemosaicPatches), nameof(DemosaicPatches.MaterialSetterPostfix));
            int patched = 0;

            patched += TryPatch(harmony, AccessTools.PropertySetter(typeof(Renderer), "material"), postfix);
            patched += TryPatch(harmony, AccessTools.PropertySetter(typeof(Renderer), "sharedMaterial"), postfix);
            patched += TryPatch(harmony, AccessTools.PropertySetter(typeof(Renderer), "materials"), postfix);
            patched += TryPatch(harmony, AccessTools.PropertySetter(typeof(Renderer), "sharedMaterials"), postfix);

            if (patched > 0)
                _log.LogInfo($"已 Hook {patched} 个 Renderer 材质 setter，可实时捕获动态材质变更。");
#endif
        }

        private int TryPatch(Harmony harmony, MethodInfo method, HarmonyMethod postfix)
        {
            if (method == null) return 0;
            try
            {
                harmony.Patch(method, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                _log.LogDebug($"材质 setter patch 失败: {ex.Message}");
                return 0;
            }
        }

        private void PatchMethodsByName(Harmony harmony)
        {
            var keywords = _settings.MethodDisableKeywordArray;
            if (keywords == null || keywords.Length == 0) return;

            var excludeKeywords = _settings.MethodExcludeKeywordArray;
            var assemblyNames = DemosaicSettings.Parse(_settings.MethodPatchTargetAssemblies.Value);
            if (assemblyNames.Length == 0) return;

            var prefix = new HarmonyMethod(typeof(DemosaicPatches), nameof(DemosaicPatches.GenericDisablePrefix));
            int patchedCount = 0;
            int skipped = 0;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var name = assembly.GetName().Name;
                if (string.IsNullOrEmpty(name)) continue;

                bool targeted = false;
                for (int i = 0; i < assemblyNames.Length; i++)
                {
                    if (string.Equals(assemblyNames[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        targeted = true;
                        break;
                    }
                }
                if (!targeted) continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                               BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (method.IsSpecialName || method.IsGenericMethod || method.ContainsGenericParameters ||
                                method.IsAbstract || method.ReturnType != typeof(void))
                                continue;

                            string methodName = method.Name;
                            if (!MosaicDetector.ContainsAny(methodName, keywords)) continue;
                            if (MosaicDetector.ContainsAny(methodName, excludeKeywords))
                            {
                                skipped++;
                                _log.LogDebug($"排除跳过: {type.FullName}.{methodName}");
                                continue;
                            }

                            try
                            {
                                harmony.Patch(method, prefix: prefix);
                                patchedCount++;
                                _log.LogDebug($"已拦截: {type.FullName}.{methodName}");
                            }
                            catch (Exception ex)
                            {
                                _log.LogDebug($"拦截失败: {type.FullName}.{methodName} - {ex.Message}");
                            }
                        }
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    // 部分程序集无法完整加载，跳过
                }
            }

            _log.LogInfo($"方法拦截完成，共拦截 {patchedCount} 个方法，排除跳过 {skipped} 个。");
        }

    }
}
