using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace DemosaicPlugin
{
    /// <summary>
    /// 马赛克检测器。Mono / IL2CPP 共用同一套判定维度：
    /// 对象名 → 父链名 → 材质（名 / Shader名 / Shader属性值 / 纹理名）→ 网格名 → 组件名。
    /// 所有维度都带缓存；父链缓存为三态（命中 / 排除 / 无），避免每帧重复遍历整条父链。
    /// Shader 属性维度会读取材质当前值，材质结论因此为三态（命中 / 属性关闭 / 干净），
    /// "属性关闭" 的结论每隔数秒重探一次，以捕捉游戏改 _MosaicFactor 这类参数后才激活的马赛克。
    /// </summary>
    internal sealed class MosaicDetector
    {
        private enum Verdict : byte
        {
            None = 0,
            Hit = 1,
            Excluded = 2
        }

        /// <summary>
        /// 材质维度结论。Hit = 确认马赛克；Dormant = Shader 声明了马赛克属性但当前值为关闭，
        /// 结论会随游戏改值失效，需要留在监视列表里定期重探；Clean = 无相关信号（属性维度未参与判定）。
        /// </summary>
        private enum MaterialVerdict : byte
        {
            Clean = 0,
            Dormant = 1,
            Hit = 2
        }

        private struct ShaderPropertyProbe
        {
            public string Name;
            public ShaderPropertyType Type;
        }

        /// <summary>"属性存在但值为关闭" 的材质的重探间隔（秒），用于捕捉游戏运行时改值激活马赛克。</summary>
        private const float DormantReprobeSeconds = 2f;

        private readonly DemosaicSettings _settings;
        private readonly ManualLogSource _log;

        private readonly Dictionary<int, MaterialVerdict> _materialCache = new Dictionary<int, MaterialVerdict>();
        private readonly Dictionary<int, float> _dormantRetryAt = new Dictionary<int, float>();
        private readonly Dictionary<int, bool> _shaderNameCache = new Dictionary<int, bool>();
        private readonly Dictionary<int, ShaderPropertyProbe[]> _shaderProbeCache = new Dictionary<int, ShaderPropertyProbe[]>();
        private readonly Dictionary<string, bool> _componentTypeCache = new Dictionary<string, bool>();
        private readonly Dictionary<int, Verdict> _nameCache = new Dictionary<int, Verdict>();
        private readonly Dictionary<int, Verdict> _parentChainCache = new Dictionary<int, Verdict>();

        // 同一 Renderer 在一次判定里会被读多次材质数组，而 IL2CPP 每次读取都要做一次 interop 拷贝
        private int _materialsMemoId = -1;
        private Material[] _materialsMemo;

        public MosaicDetector(DemosaicSettings settings, ManualLogSource log)
        {
            _settings = settings;
            _log = log;
        }

        public void ClearCache()
        {
            _materialCache.Clear();
            _dormantRetryAt.Clear();
            _shaderNameCache.Clear();
            _shaderProbeCache.Clear();
            _componentTypeCache.Clear();
            _nameCache.Clear();
            _parentChainCache.Clear();
            _materialsMemo = null;
            _materialsMemoId = -1;
        }

        private const int MaterialCacheLimit = 8192;
        private const int ShaderCacheLimit = 1024;

        /// <summary>
        /// 缓存规模超限时整片清理。长时间不切场景时缓存会持续膨胀，而且同场景内卸载资源后
        /// 实例 ID 可能被复用，旧结论会张冠李戴；这里用"超过阈值才清"的折中，正常规模不会触发。
        /// 由引擎在每轮完整扫描结束后调用（判断成本只有两次 Count）。
        /// </summary>
        public void TrimCachesIfOversized()
        {
            if (_materialCache.Count > MaterialCacheLimit)
            {
                _materialCache.Clear();
                _dormantRetryAt.Clear();
                _materialsMemo = null;
                _materialsMemoId = -1;
            }

            if (_shaderNameCache.Count > ShaderCacheLimit)
                _shaderNameCache.Clear();

            if (_shaderProbeCache.Count > ShaderCacheLimit)
                _shaderProbeCache.Clear();
        }

        /// <summary>完整材质检测（材质名 + Shader名 + Shader属性值 + 纹理名），结论缓存。</summary>
        public bool CheckMaterialFull(Material mat)
        {
            return GetMaterialVerdict(mat) == MaterialVerdict.Hit;
        }

        /// <summary>材质带马赛克命名的数值属性、但当前值为关闭：结论会随游戏改值失效。</summary>
        public bool IsMaterialDormant(Material mat)
        {
            return GetMaterialVerdict(mat) == MaterialVerdict.Dormant;
        }

        private MaterialVerdict GetMaterialVerdict(Material mat)
        {
            if (mat == null) return MaterialVerdict.Clean;

            int matId = mat.GetInstanceID();
            if (_materialCache.TryGetValue(matId, out var cached))
            {
                // 属性值可能被游戏随时改动，"存在但关闭" 的结论到期后需要重探
                if (!_dormantRetryAt.TryGetValue(matId, out var retryAt) || Time.time < retryAt)
                    return cached;
            }

            MaterialVerdict verdict = EvaluateMaterial(mat);
            _materialCache[matId] = verdict;

            if (verdict == MaterialVerdict.Dormant)
                _dormantRetryAt[matId] = Time.time + DormantReprobeSeconds;
            else
                _dormantRetryAt.Remove(matId);

            return verdict;
        }

        /// <summary>只判断 GameObject 名 / 父链名，供 Smart 模式的"全透明回退"使用。</summary>
        public bool IsObjectNameOrParentMosaic(GameObject go)
        {
            if (go == null) return false;

            if (GetNameVerdict(go) == Verdict.Hit) return true;
            if (_settings.DetectParentObjectNames.Value && GetParentVerdict(go.transform?.parent) == Verdict.Hit)
                return true;

            return false;
        }

        public bool IsMosaic(GameObject go)
        {
            return IsMosaicCore(go, null);
        }

        public bool IsMosaic(Renderer renderer)
        {
            if (renderer == null) return false;
            var go = renderer.gameObject;
            if (go == null) return false;
            return IsMosaicCore(go, renderer);
        }

        /// <summary>廉价复核：材质缓存命中后基本只剩字典查找，用于周期扫描时复查已处理对象。</summary>
        public bool HasMosaicMaterial(Renderer renderer)
        {
            var materials = GetMaterials(renderer);
            if (materials == null) return false;

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && CheckMaterialFull(materials[i]))
                    return true;
            }
            return false;
        }

        /// <summary>是否有材质的马赛克属性处于关闭状态——这类 Renderer 需要留在监视列表里。</summary>
        public bool HasDormantMaterial(Renderer renderer)
        {
            var materials = GetMaterials(renderer);
            if (materials == null) return false;

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && IsMaterialDormant(materials[i]))
                    return true;
            }
            return false;
        }

        /// <summary>取 Renderer 的材质数组，同一 Renderer 连续判定只读一次（IL2CPP 每次读取都要 interop 拷贝）。</summary>
        private Material[] GetMaterials(Renderer renderer)
        {
            if (renderer == null) return null;

            int id = renderer.GetInstanceID();
            if (id != _materialsMemoId || _materialsMemo == null)
            {
                _materialsMemo = RuntimeInterop.GetSharedMaterials(renderer);
                _materialsMemoId = id;
            }
            return _materialsMemo;
        }

        private bool IsMosaicCore(GameObject go, Renderer knownRenderer)
        {
            if (go == null) return false;

            var nameVerdict = GetNameVerdict(go);
            if (nameVerdict == Verdict.Hit) return true;
            if (nameVerdict == Verdict.Excluded) return false;

            if (_settings.DetectParentObjectNames.Value)
            {
                var parentVerdict = GetParentVerdict(go.transform != null ? go.transform.parent : null);
                if (parentVerdict == Verdict.Hit) return true;
                if (parentVerdict == Verdict.Excluded) return false;
            }

            var renderer = knownRenderer ?? go.GetComponent<Renderer>();
            if (renderer != null && HasMosaicMaterial(renderer))
                return true;

            var meshName = knownRenderer != null
                ? RuntimeInterop.GetMeshName(knownRenderer)
                : RuntimeInterop.GetMeshName(go);
            if (!string.IsNullOrEmpty(meshName) && ContainsAny(meshName, _settings.MeshKeywordArray))
                return true;

            return CheckComponents(go);
        }

        private Verdict GetNameVerdict(GameObject go)
        {
            int id = go.GetInstanceID();
            if (_nameCache.TryGetValue(id, out var cached))
                return cached;

            string name = go.name;
            Verdict verdict = Verdict.None;
            if (ContainsAny(name, _settings.ExclusionKeywordArray))
                verdict = Verdict.Excluded;
            else if (ContainsAny(name, _settings.ObjectKeywordArray))
                verdict = Verdict.Hit;

            _nameCache[id] = verdict;
            return verdict;
        }

        private Verdict GetParentVerdict(Transform parent)
        {
            if (parent == null) return Verdict.None;

            int id = parent.GetInstanceID();
            if (_parentChainCache.TryGetValue(id, out var cached))
                return cached;

            Verdict verdict = Verdict.None;
            var current = parent;
            while (current != null)
            {
                string name = current.name;
                if (ContainsAny(name, _settings.ExclusionKeywordArray))
                {
                    verdict = Verdict.Excluded;
                    break;
                }
                if (ContainsAny(name, _settings.ObjectKeywordArray))
                {
                    verdict = Verdict.Hit;
                    break;
                }
                current = current.parent;
            }

            // 三态全部写入缓存：即便"无命中"也避免下一帧重复遍历整条父链
            _parentChainCache[id] = verdict;
            return verdict;
        }

        private bool CheckComponents(GameObject go)
        {
            var keywords = _settings.ComponentKeywordArray;
            if (keywords == null || keywords.Length == 0) return false;

            // 注意：这里刻意用数组重载而非 List 重载 —— IL2CPP 的 interop 不保证生成 GetComponents(List) 版本
            var components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) continue;

                string typeName = RuntimeInterop.GetComponentTypeName(component);
                if (string.IsNullOrEmpty(typeName)) continue;

                if (_componentTypeCache.TryGetValue(typeName, out var cached))
                {
                    if (cached) return true;
                    continue;
                }

                bool match = ContainsAny(typeName, keywords);
                _componentTypeCache[typeName] = match;
                if (match) return true;
            }

            return false;
        }

        private MaterialVerdict EvaluateMaterial(Material mat)
        {
            if (ContainsAny(mat.name, _settings.MaterialKeywordArray)) return MaterialVerdict.Hit;

            var shader = mat.shader;
            if (shader != null)
            {
                int shaderId = shader.GetInstanceID();
                if (!_shaderNameCache.TryGetValue(shaderId, out var nameHit))
                {
                    nameHit = ContainsAny(shader.name, _settings.ShaderKeywordArray);
                    _shaderNameCache[shaderId] = nameHit;
                }
                if (nameHit) return MaterialVerdict.Hit;

                var propVerdict = CheckShaderProperties(mat, shader);
                if (propVerdict == MaterialVerdict.Hit) return MaterialVerdict.Hit;
                if (propVerdict == MaterialVerdict.Dormant)
                {
                    // 属性维度说"有马赛克开关但当前关着"：后续维度若命中则以命中为准
                    return CombineWithTextureProbe(mat, MaterialVerdict.Dormant);
                }
            }

            return CombineWithTextureProbe(mat, MaterialVerdict.Clean);
        }

        private MaterialVerdict CombineWithTextureProbe(Material mat, MaterialVerdict fallback)
        {
            if (_settings.EnableTextureDetection.Value && _settings.TextureKeywordArray.Length > 0)
            {
                try
                {
                    var propertyIds = mat.GetTexturePropertyNameIDs();
                    for (int i = 0; i < propertyIds.Length; i++)
                    {
                        var texture = mat.GetTexture(propertyIds[i]);
                        if (texture != null && ContainsAny(texture.name, _settings.TextureKeywordArray))
                            return MaterialVerdict.Hit;
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogDebug($"纹理检测不可用，已跳过: {ex.Message}");
                }
            }

            return fallback;
        }

        /// <summary>
        /// Shader 属性维度：属性名命中还要看材质上的当前值，值为关闭时不再判定为马赛克。
        /// 这样 "Shader 声明了 _PixelSize 但效果未开启" 的正常材质不会被误伤。
        /// </summary>
        private MaterialVerdict CheckShaderProperties(Material mat, Shader shader)
        {
            var probes = GetShaderProbes(shader);
            if (probes == null || probes.Length == 0) return MaterialVerdict.Clean;

            float threshold = _settings.ShaderPropertyValueThreshold.Value;
            var verdict = MaterialVerdict.Clean;

            for (int i = 0; i < probes.Length; i++)
            {
                var probe = probes[i];

                // 非数值属性没有可读的"强弱"状态，沿用按名命中
                bool numeric = probe.Type == ShaderPropertyType.Float ||
                               probe.Type == ShaderPropertyType.Range ||
                               probe.Type == ShaderPropertyType.Int;
                if (!numeric) return MaterialVerdict.Hit;

                try
                {
                    float value = probe.Type == ShaderPropertyType.Int
                        ? mat.GetInt(probe.Name)
                        : mat.GetFloat(probe.Name);

                    if (value > threshold) return MaterialVerdict.Hit;
                    verdict = MaterialVerdict.Dormant;
                }
                catch (Exception ex)
                {
                    _log?.LogDebug($"Shader 属性值读取失败，退回按名命中: {probe.Name} - {ex.Message}");
                    return MaterialVerdict.Hit;
                }
            }

            return verdict;
        }

        /// <summary>枚举 Shader 上名称命中关键词的属性，结果按 Shader 缓存（无命中时缓存 null）。</summary>
        private ShaderPropertyProbe[] GetShaderProbes(Shader shader)
        {
            int shaderId = shader.GetInstanceID();
            if (_shaderProbeCache.TryGetValue(shaderId, out var cached)) return cached;

            ShaderPropertyProbe[] probes = null;
            var keywords = _settings.ShaderPropertyKeywordArray;

            if (keywords != null && keywords.Length > 0)
            {
                try
                {
                    int count = shader.GetPropertyCount();
                    List<ShaderPropertyProbe> found = null;
                    for (int i = 0; i < count; i++)
                    {
                        string name = shader.GetPropertyName(i);
                        if (!ContainsAny(name, keywords)) continue;

                        found = found ?? new List<ShaderPropertyProbe>(4);
                        found.Add(new ShaderPropertyProbe { Name = name, Type = shader.GetPropertyType(i) });
                    }
                    if (found != null) probes = found.ToArray();
                }
                catch (Exception ex)
                {
                    _log?.LogDebug($"Shader 属性枚举不可用，已跳过: {ex.Message}");
                }
            }

            _shaderProbeCache[shaderId] = probes;
            return probes;
        }

        /// <summary>关键词匹配（大小写不敏感的子串判断）。全插件唯一实现，供引擎 / 处理器共用。</summary>
        internal static bool ContainsAny(string target, string[] keywords)
        {
            if (string.IsNullOrEmpty(target) || keywords == null || keywords.Length == 0) return false;
            for (int i = 0; i < keywords.Length; i++)
            {
                if (target.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
