using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace DemosaicPlugin
{
    /// <summary>
    /// 马赛克处理器。
    /// 设计要点：
    /// 1. 处理模式每次从配置实时读取，配置热改无需重建处理器（旧版重建会连带销毁已生效的透明材质）；
    /// 2. 透明材质由 <see cref="TransparentMaterialProvider"/> 统一提供且永不主动销毁。
    /// </summary>
    internal sealed class MosaicProcessor
    {
        private readonly DemosaicSettings _settings;
        private readonly ManualLogSource _log;
        private readonly MosaicDetector _detector;
        private readonly TransparentMaterialProvider _materialProvider;

        private readonly Dictionary<int, Material[]> _transparentArrays = new Dictionary<int, Material[]>();
        private Material _arraySourceMaterial;

        public MosaicProcessor(DemosaicSettings settings, ManualLogSource log, MosaicDetector detector,
            TransparentMaterialProvider materialProvider)
        {
            _settings = settings;
            _log = log;
            _detector = detector;
            _materialProvider = materialProvider;
        }

        public void Process(GameObject go)
        {
            if (go == null) return;

            using (DemosaicEngine.EnterMaterialGuard())
            {
                var mode = _settings.RemoveModeEntry.Value;
                bool verbose = _settings.LogProcessedObjects.Value;
                if (verbose) _log?.LogInfo($"去除马赛克 ({mode}): {go.name}");

                var material = mode == RemoveMode.Transparent || mode == RemoveMode.Smart
                    ? _materialProvider.Get()
                    : null;

                // 透明化时关闭阴影，避免透明面片继续投射出黑色阴影块
                if (mode == RemoveMode.Transparent || mode == RemoveMode.Smart)
                    DisableShadows(go.GetComponentsInChildren<Renderer>(true));

                switch (mode)
                {
                    case RemoveMode.Disable:
                        go.SetActive(false);
                        break;

                    case RemoveMode.Destroy:
                        Object.Destroy(go);
                        break;

                    case RemoveMode.Transparent:
                    {
                        var renderer = go.GetComponent<Renderer>();
                        var materials = renderer != null ? RuntimeInterop.GetSharedMaterials(renderer) : null;
                        if (material != null && materials != null && materials.Length > 0)
                        {
                            RuntimeInterop.SetSharedMaterials(renderer, GetTransparentMaterials(materials.Length, material));
                        }
                        else
                        {
                            _log?.LogWarning($"无法对 {go.name} 应用透明模式，降级为禁用对象。");
                            go.SetActive(false);
                        }
                        break;
                    }

                    case RemoveMode.Smart:
                    {
                        if (material == null)
                        {
                            _log?.LogWarning($"无法对 {go.name} 应用智能模式，降级为禁用对象。");
                            go.SetActive(false);
                            break;
                        }

                        var renderer = go.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            ProcessSmart(renderer, material);
                        }
                        else
                        {
                            // 空父节点被名称命中：逐个替换子 Renderer 的马赛克材质槽，而不是整棵树禁用
                            var children = go.GetComponentsInChildren<Renderer>(true);
                            for (int i = 0; i < children.Length; i++)
                                ProcessSmart(children[i], material);
                        }
                        break;
                    }
                }
            }
        }

        public void Process(Renderer renderer)
        {
            if (renderer == null) return;

            using (DemosaicEngine.EnterMaterialGuard())
            {
                var mode = _settings.RemoveModeEntry.Value;
                var material = mode == RemoveMode.Transparent || mode == RemoveMode.Smart
                    ? _materialProvider.Get()
                    : null;

                if (mode == RemoveMode.Transparent || mode == RemoveMode.Smart)
                    DisableShadows(new[] { renderer });

                switch (mode)
                {
                    case RemoveMode.Smart when material != null:
                        ProcessSmart(renderer, material);
                        return;

                    case RemoveMode.Transparent when material != null:
                    {
                        var materials = RuntimeInterop.GetSharedMaterials(renderer);
                        if (materials != null && materials.Length > 0)
                        {
                            RuntimeInterop.SetSharedMaterials(renderer, GetTransparentMaterials(materials.Length, material));
                            if (_settings.LogProcessedObjects.Value)
                                _log?.LogInfo($"已去除马赛克 (透明模式): {renderer.name}");
                            return;
                        }
                        break;
                    }
                }

                if (_settings.LogProcessedObjects.Value)
                    _log?.LogInfo($"已去除马赛克 ({_settings.RemoveModeEntry.Value}): {renderer.name}");

                if (_settings.RemoveModeEntry.Value == RemoveMode.Destroy)
                    Object.Destroy(renderer.gameObject);
                else
                    renderer.gameObject.SetActive(false);
            }
        }

        /// <summary>仅替换命中马赛克关键词的材质槽，保留同对象上的非马赛克材质。</summary>
        private void ProcessSmart(Renderer renderer, Material transparentMaterial)
        {
            var materials = RuntimeInterop.GetSharedMaterials(renderer);
            if (materials == null || materials.Length == 0)
            {
                if (_detector != null && _detector.IsObjectNameOrParentMosaic(renderer.gameObject))
                    renderer.gameObject.SetActive(false);
                return;
            }

            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                if (IsMosaicMaterial(materials[i]))
                {
                    materials[i] = transparentMaterial;
                    changed = true;
                }
            }

            if (changed)
            {
                RuntimeInterop.SetSharedMaterials(renderer, materials);
                if (_settings.LogProcessedObjects.Value)
                    _log?.LogInfo($"已去除马赛克 (智能模式 - 仅替换马赛克材质槽): {renderer.name}");
                return;
            }

            // 没有材质命中时，只有在对象名/父节点名命中的情况下才整体透明，
            // 避免"网格名/组件名命中"把整块皮肤误伤掉
            if (_detector != null && _detector.IsObjectNameOrParentMosaic(renderer.gameObject))
            {
                RuntimeInterop.SetSharedMaterials(renderer, GetTransparentMaterials(materials.Length, transparentMaterial));
                if (_settings.LogProcessedObjects.Value)
                    _log?.LogInfo($"已去除马赛克 (智能模式 - 全透明回退): {renderer.name}");
            }
            else if (_settings.LogProcessedObjects.Value)
            {
                _log?.LogWarning($"跳过智能模式全透明回退以避免假阳性: {renderer.name} (仅网格名或组件名命中)");
            }
        }

        private bool IsMosaicMaterial(Material mat)
        {
            if (mat == null) return false;

            // 与检测器共用完整判定维度（材质名 + Shader名 + Shader属性 + 纹理名），并自动缓存
            return _detector.CheckMaterialFull(mat);
        }

        private Material[] GetTransparentMaterials(int count, Material transparentMaterial)
        {
            // 材质被重建后缓存必须失效，否则会继续指向已销毁的材质
            if (_arraySourceMaterial != transparentMaterial)
            {
                _transparentArrays.Clear();
                _arraySourceMaterial = transparentMaterial;
            }

            if (!_transparentArrays.TryGetValue(count, out var materials))
            {
                materials = new Material[count];
                for (int i = 0; i < count; i++)
                    materials[i] = transparentMaterial;
                _transparentArrays[count] = materials;
            }
            return materials;
        }

        private static void DisableShadows(Renderer[] renderers)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                try
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
                catch
                {
                    // 部分 Renderer 类型不支持阴影设置
                }
            }
        }
    }
}
