using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace DemosaicPlugin
{
    /// <summary>
    /// 配置定义（Mono / IL2CPP 共用同一套节名与默认值）。
    /// 旧版本的配置键会在首次加载时自动迁移，用户手改过的值不会丢。
    /// </summary>
    internal sealed class DemosaicSettings
    {
        private const string SectionGeneral = "General";
        private const string SectionScan = "Scan";
        private const string SectionDetection = "Detection";
        private const string SectionAdvanced = "Advanced";

        /// <summary>Shader 属性生效阈值的配置键，引擎在热重载时需要按名识别。</summary>
        public const string ShaderPropertyThresholdKey = "ShaderPropertyValueThreshold";

        private readonly ConfigFile _config;
        private readonly ManualLogSource _log;

        public ConfigEntry<bool> EnablePlugin;
        public ConfigEntry<RemoveMode> RemoveModeEntry;
        public ConfigEntry<KeyCode> ManualScanKey;
        public ConfigEntry<KeyCode> ExportSceneKey;
        public ConfigEntry<bool> IncludeInactiveObjects;
        public ConfigEntry<bool> DetectParentObjectNames;
        public ConfigEntry<bool> LogProcessedObjects;

        public ConfigEntry<float> PeriodicScanInterval;
        public ConfigEntry<float> WatchlistRecheckInterval;
        public ConfigEntry<float> SceneLoadScanDelay;
        public ConfigEntry<int> ScanBatchSize;
        public ConfigEntry<float> ScanTimeBudgetMs;
        public ConfigEntry<bool> ReverifyProcessedRenderers;

        public ConfigEntry<string> ObjectNameKeywords;
        public ConfigEntry<string> MaterialNameKeywords;
        public ConfigEntry<string> ShaderNameKeywords;
        public ConfigEntry<string> MeshNameKeywords;
        public ConfigEntry<string> TextureKeywords;
        public ConfigEntry<string> ComponentNameKeywords;
        public ConfigEntry<string> ShaderPropertyKeywords;
        public ConfigEntry<float> ShaderPropertyValueThreshold;
        public ConfigEntry<string> ExclusionKeywords;
        public ConfigEntry<bool> EnableTextureDetection;

        public ConfigEntry<bool> DisableMethods;
        public ConfigEntry<string> MethodDisableKeywords;
        public ConfigEntry<string> MethodExcludeKeywords;
        public ConfigEntry<string> MethodPatchTargetAssemblies;
        public ConfigEntry<bool> EnableCameraEffectDetection;
        public ConfigEntry<string> CameraEffectKeywords;
        public ConfigEntry<bool> EnableDecalDetection;
        public ConfigEntry<bool> EnableMaterialSetterHook;
        public ConfigEntry<bool> EnableMosaicSwitchOff;
        public ConfigEntry<string> MosaicSwitchComponentTypes;
        public ConfigEntry<string> MosaicSwitchFieldKeywords;
        public ConfigEntry<string> MosaicSwitchApplyMethods;
        public ConfigEntry<float> MosaicSwitchColorAlpha;
        public ConfigEntry<float> MosaicSwitchRecheckInterval;
        public ConfigEntry<KeyCode> ExportSwitchKey;
        public ConfigEntry<KeyCode> ScanCandidatesKey;
        public ConfigEntry<string> MosaicGateMemberKeywords;
        public ConfigEntry<bool> EnableMaterialMosaicOff;
        public ConfigEntry<string> MosaicMaterialPropertyKeywords;
        public ConfigEntry<string> MosaicMaterialShaderKeywords;
        public ConfigEntry<bool> MosaicForceRepatch;
        public ConfigEntry<bool> EnableMosaicKeywordOff;

        // 解析后的关键词缓存（避免每次判定都 Split）
        public string[] ObjectKeywordArray;
        public string[] MaterialKeywordArray;
        public string[] ShaderKeywordArray;
        public string[] MeshKeywordArray;
        public string[] TextureKeywordArray;
        public string[] ComponentKeywordArray;
        public string[] ShaderPropertyKeywordArray;
        public string[] ExclusionKeywordArray;
        public string[] MethodDisableKeywordArray;
        public string[] MethodExcludeKeywordArray;
        public string[] CameraEffectKeywordArray;
        public string[] MosaicSwitchTypeKeywordArray;
        public string[] MosaicSwitchFieldKeywordArray;
        public string[] MosaicSwitchMethodArray;
        public string[] MosaicGateMemberArray;
        public string[] MosaicMaterialPropertyArray;
        public string[] MosaicMaterialShaderArray;

        private DemosaicSettings(ConfigFile config, ManualLogSource log)
        {
            _config = config;
            _log = log;
        }

        public static DemosaicSettings Bind(ConfigFile config, ManualLogSource log)
        {
            var settings = new DemosaicSettings(config, log);
            settings.BindAll();
            settings.ImportLegacyValues();
            settings.RefreshKeywords();
            return settings;
        }

        private void BindAll()
        {
            EnablePlugin = _config.Bind(SectionGeneral, "EnablePlugin", true, "是否启用插件。");
            RemoveModeEntry = _config.Bind(SectionGeneral, "RemoveMode", RemoveMode.Smart,
                "去码模式：Smart=仅替换马赛克材质槽（默认），Transparent=全材质透明，Disable=禁用对象，Destroy=销毁对象。");
            ManualScanKey = _config.Bind(SectionGeneral, "ManualScanKey", KeyCode.F10, "手动重扫全场景热键。");
            ExportSceneKey = _config.Bind(SectionGeneral, "ExportSceneKey", KeyCode.F11, "导出场景 Renderer 列表到日志热键。");
            IncludeInactiveObjects = _config.Bind(SectionGeneral, "IncludeInactiveObjects", true, "扫描时包含未激活对象。");
            DetectParentObjectNames = _config.Bind(SectionGeneral, "DetectParentObjectNames", true, "检查父节点名称是否含马赛克关键词。");
            LogProcessedObjects = _config.Bind(SectionGeneral, "LogProcessedObjects", false, "逐条打印每个被处理对象的日志。");

            PeriodicScanInterval = _config.Bind(SectionScan, "PeriodicScanInterval", 10f, "全场景重扫间隔（秒，0=禁用）。");
            WatchlistRecheckInterval = _config.Bind(SectionScan, "WatchlistRecheckInterval", 2f, "监视列表复核间隔（秒，0=禁用）。");
            SceneLoadScanDelay = _config.Bind(SectionScan, "SceneLoadScanDelay", 1.5f, "场景加载后首次扫描延迟（秒）。");
            ScanBatchSize = _config.Bind(SectionScan, "ScanBatchSize", 500, "单帧最多扫描对象数。");
            ScanTimeBudgetMs = _config.Bind(SectionScan, "ScanTimeBudgetMs", 2f, "单帧扫描耗时上限（毫秒）。");
            ReverifyProcessedRenderers = _config.Bind(SectionScan, "ReverifyProcessedRenderers", true, "周期扫描时复核已处理过的 Renderer。");

            ObjectNameKeywords = _config.Bind(SectionDetection, "ObjectNameKeywords", "mosaic,censored,pixelated,mozic,mazic,mozaic,moza", "对象名关键词（逗号分隔）。");
            MaterialNameKeywords = _config.Bind(SectionDetection, "MaterialNameKeywords", "mosaic,censored,pixel,mozic,mazic,moza", "材质名关键词（逗号分隔）。");
            ShaderNameKeywords = _config.Bind(SectionDetection, "ShaderNameKeywords", "mosaic,pixelate,censor,moza,mozic,mazic,mozaic", "Shader 名关键词（逗号分隔）。");
            MeshNameKeywords = _config.Bind(SectionDetection, "MeshNameKeywords", "censor,mosaic,moza,mozic,mazic,mozaic", "网格名关键词（逗号分隔）。");
            TextureKeywords = _config.Bind(SectionDetection, "TextureKeywords", "mosaic", "纹理名关键词（逗号分隔）。");
            ComponentNameKeywords = _config.Bind(SectionDetection, "ComponentNameKeywords", "", "组件名关键词（逗号分隔）。");
            ShaderPropertyKeywords = _config.Bind(SectionDetection, "ShaderPropertyKeywords", "_PixelSize,_BlockSize,_MosaicFactor", "Shader 数值属性名关键词（逗号分隔）。");
            ShaderPropertyValueThreshold = _config.Bind(SectionDetection, ShaderPropertyThresholdKey, 0f, "Shader 数值属性判定生效的阈值（大于此值才判定为有码）。");
            ExclusionKeywords = _config.Bind(SectionDetection, "ExclusionKeywords", "", "白名单排除词（逗号分隔，命中则跳过）。");
#if DEMOSAIC_IL2CPP
            EnableTextureDetection = _config.Bind(SectionDetection, "EnableTextureDetection", false, "启用纹理名检测（IL2CPP 下默认关闭）。");
#else
            EnableTextureDetection = _config.Bind(SectionDetection, "EnableTextureDetection", true, "启用纹理名检测。");
#endif

            DisableMethods = _config.Bind(SectionAdvanced, "DisableMethods", false, "按方法名拦截马赛克相关函数。");
            MethodDisableKeywords = _config.Bind(SectionAdvanced, "MethodDisableKeywords", "mosaic,censor,moza", "要拦截的方法名关键词（逗号分隔）。");
            MethodExcludeKeywords = _config.Bind(SectionAdvanced, "MethodExcludeKeywords",
                "remove,destroy,clear,disable,hide,off,delete,undo,stop,cancel", "方法拦截排除词（逗号分隔）。");
            MethodPatchTargetAssemblies = _config.Bind(SectionAdvanced, "MethodPatchTargetAssemblies", "Assembly-CSharp", "方法拦截扫描的程序集名（逗号分隔）。");
            EnableCameraEffectDetection = _config.Bind(SectionAdvanced, "EnableCameraEffectDetection", true, "禁用相机上的马赛克后处理组件。");
            CameraEffectKeywords = _config.Bind(SectionAdvanced, "CameraEffectKeywords", "mosaic,censor,pixelat,moza,mozic,mazic", "相机后处理组件名关键词（逗号分隔）。");
            EnableDecalDetection = _config.Bind(SectionAdvanced, "EnableDecalDetection", true, "禁用命中马赛克材质的 Projector / DecalProjector。");
            EnableMaterialSetterHook = _config.Bind(SectionAdvanced, "EnableMaterialSetterHook", true, "监听 Renderer 材质赋值事件。");

            EnableMosaicSwitchOff = _config.Bind(SectionAdvanced, "EnableMosaicSwitchOff", true, "关闭挂载在组件字段上的马赛克开关（保留模型本身）。");
            MosaicSwitchComponentTypes = _config.Bind(SectionAdvanced, "MosaicSwitchComponentTypes", "LIDPenetrator", "马赛克开关组件类型名关键词（逗号分隔）。");
            MosaicSwitchFieldKeywords = _config.Bind(SectionAdvanced, "MosaicSwitchFieldKeywords",
                "enableMosaic,mosaicRatio,mosaicMinPx,mosaicColor", "组件上要归零的马赛克字段名关键词（逗号分隔）。");
            MosaicSwitchApplyMethods = _config.Bind(SectionAdvanced, "MosaicSwitchApplyMethods",
                "ApplyToMaterial,ApplyToMaterials", "修改组件字段后调用的无参同步方法名（逗号分隔）。");
            MosaicSwitchColorAlpha = _config.Bind(SectionAdvanced, "MosaicSwitchColorAlpha", 0f, "马赛克颜色属性的目标 Alpha 值。");
            MosaicSwitchRecheckInterval = _config.Bind(SectionAdvanced, "MosaicSwitchRecheckInterval", 1f, "组件与材质马赛克开关复查间隔（秒，0=禁用）。");
            ExportSwitchKey = _config.Bind(SectionAdvanced, "ExportSwitchKey", KeyCode.F9, "导出马赛克开关组件详情到日志热键。");
            ScanCandidatesKey = _config.Bind(SectionAdvanced, "ScanCandidatesKey", KeyCode.F8, "扫描并导出场景可疑马赛克物件到日志热键。");
            MosaicGateMemberKeywords = _config.Bind(SectionAdvanced, "MosaicGateMemberKeywords",
                "UpdateMosaicVisibility,mosaicRenderers,mosaicScanned,overrideShaderParameters", "F8 诊断扫描门控组件的成员名关键词（逗号分隔）。");
            EnableMaterialMosaicOff = _config.Bind(SectionAdvanced, "EnableMaterialMosaicOff", true, "归零材质上的马赛克属性并禁用马赛克 Shader Pass（如 LID_MOSAIC）。");
            MosaicMaterialPropertyKeywords = _config.Bind(SectionAdvanced, "MosaicMaterialPropertyKeywords", "mosaic", "材质上要归零的马赛克属性/Pass 名关键词（逗号分隔）。");
            MosaicMaterialShaderKeywords = _config.Bind(SectionAdvanced, "MosaicMaterialShaderKeywords", "", "限定仅处理 Shader 名含此关键词的材质（留空=不限制）。");
            MosaicForceRepatch = _config.Bind(SectionAdvanced, "MosaicForceRepatch", false, "修改组件开关后重启一次组件（实验项，默认关）。");
            EnableMosaicKeywordOff = _config.Bind(SectionAdvanced, "EnableMosaicKeywordOff", true, "同时关闭材质与全局的马赛克 Shader Keyword。");
        }

        public void RefreshKeywords()
        {
            ObjectKeywordArray = Parse(ObjectNameKeywords.Value);
            MaterialKeywordArray = Parse(MaterialNameKeywords.Value);
            ShaderKeywordArray = Parse(ShaderNameKeywords.Value);
            MeshKeywordArray = Parse(MeshNameKeywords.Value);
            TextureKeywordArray = Parse(TextureKeywords.Value);
            ComponentKeywordArray = Parse(ComponentNameKeywords.Value);
            ShaderPropertyKeywordArray = Parse(ShaderPropertyKeywords.Value);
            ExclusionKeywordArray = Parse(ExclusionKeywords.Value);
            MethodDisableKeywordArray = Parse(MethodDisableKeywords.Value);
            MethodExcludeKeywordArray = Parse(MethodExcludeKeywords.Value);
            CameraEffectKeywordArray = Parse(CameraEffectKeywords.Value);
            MosaicSwitchTypeKeywordArray = Parse(MosaicSwitchComponentTypes.Value);
            MosaicSwitchFieldKeywordArray = Parse(MosaicSwitchFieldKeywords.Value);
            MosaicSwitchMethodArray = Parse(MosaicSwitchApplyMethods.Value);
            MosaicGateMemberArray = Parse(MosaicGateMemberKeywords.Value);
            MosaicMaterialPropertyArray = Parse(MosaicMaterialPropertyKeywords.Value);
            MosaicMaterialShaderArray = Parse(MosaicMaterialShaderKeywords.Value);
        }

        public static string[] Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
            var parts = raw.Split(',');
            var result = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                var trimmed = parts[i].Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
            }
            return result.ToArray();
        }

        /// <summary>
        /// 迁移 1.5.0 及更早版本的配置键（Mono 变体用 "1. 通用" 等节名，IL2CPP 变体用 General/Enable 等键名）。
        /// 直接读取 cfg 文本文件，避免依赖 BepInEx 内部 API。
        /// 旧节会留在文件里（BepInEx 保存时不删未知小节），但**只在对应新键尚未写入文件时才迁移**，
        /// 因此不会覆盖用户对新键的后续修改；想彻底清理旧节需手工删除。
        /// </summary>
        private void ImportLegacyValues()
        {
            try
            {
                string path = _config.ConfigFilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                var legacy = ReadIni(path);
                if (legacy.Count == 0) return;

                int migrated = 0;
                migrated += Migrate(legacy, EnablePlugin, "1. 通用|EnablePlugin", "General|Enable");
                migrated += Migrate(legacy, RemoveModeEntry, "1. 通用|RemoveMode", "Remove|Mode");
                migrated += Migrate(legacy, ManualScanKey, "1. 通用|ManualScanKey", "General|ForceScanHotkey");
                migrated += Migrate(legacy, ExportSceneKey, "1. 通用|ExportSceneKey", "General|ExportSceneKey");
                migrated += Migrate(legacy, IncludeInactiveObjects, "1. 通用|IncludeInactiveObjects", "General|IncludeInactiveObjects");
                migrated += Migrate(legacy, DetectParentObjectNames, "1. 通用|DetectParentObjectNames", "General|DetectParentObjectNames");
                migrated += Migrate(legacy, LogProcessedObjects, "1. 通用|LogProcessedObjects", "General|LogProcessedObjects");

                migrated += Migrate(legacy, PeriodicScanInterval, "2. 扫描|PeriodicScanInterval", "Scan|PeriodicScanInterval");
                migrated += Migrate(legacy, SceneLoadScanDelay, "2. 扫描|SceneLoadScanDelay", "Scan|SceneLoadScanDelay");
                migrated += Migrate(legacy, ScanBatchSize, "2. 扫描|ScanBatchSize", "Scan|ScanBatchSize");

                migrated += Migrate(legacy, ObjectNameKeywords, "3. 关键词|ObjectNameKeywords", "Detection|ObjectNameKeywords");
                migrated += Migrate(legacy, MaterialNameKeywords, "3. 关键词|MaterialNameKeywords", "Detection|MaterialNameKeywords");
                migrated += Migrate(legacy, ShaderNameKeywords, "3. 关键词|ShaderNameKeywords", "Detection|ShaderNameKeywords");
                migrated += Migrate(legacy, MeshNameKeywords, "3. 关键词|MeshNameKeywords", "Detection|MeshNameKeywords");
                migrated += Migrate(legacy, TextureKeywords, "3. 关键词|TextureKeywords", "Detection|TextureKeywords");
                migrated += Migrate(legacy, ComponentNameKeywords, "3. 关键词|ComponentNameKeywords", "Detection|ComponentNameKeywords");
                migrated += Migrate(legacy, ShaderPropertyKeywords, "3. 关键词|ShaderPropertyKeywords", "Detection|ShaderPropertyKeywords");
                migrated += Migrate(legacy, ExclusionKeywords, "3. 关键词|ExclusionKeywords", "Detection|ExclusionKeywords");

                migrated += Migrate(legacy, DisableMethods, "4. 高级|DisableMethods", "Advanced|DisableMethods");
                migrated += Migrate(legacy, MethodDisableKeywords, "4. 高级|MethodDisableKeywords", "Advanced|MethodDisableKeywords");
                migrated += Migrate(legacy, MethodExcludeKeywords, "4. 高级|MethodExcludeKeywords", "Advanced|MethodExcludeKeywords");
                migrated += Migrate(legacy, MethodPatchTargetAssemblies, "4. 高级|AssemblyNamesToPatch", "Advanced|MethodPatchTargetAssemblies");
                migrated += Migrate(legacy, EnableCameraEffectDetection, "4. 高级|EnableCameraEffectDetection", "Advanced|EnableCameraEffectDetection");
                migrated += Migrate(legacy, CameraEffectKeywords, "4. 高级|CameraEffectKeywords", "Advanced|CameraEffectKeywords");
                migrated += Migrate(legacy, EnableDecalDetection, "4. 高级|EnableDecalDetection", "Advanced|EnableDecalDetection");
                migrated += Migrate(legacy, EnableMaterialSetterHook, "4. 高级|EnableMaterialSetterHook", "Advanced|EnableMaterialSetterHook");

                if (migrated > 0)
                {
                    _log?.LogInfo($"已从旧版配置迁移 {migrated} 个设置项（旧节保留在文件里但已不再生效，可手工删除）。");
                    _config.Save();
                }
            }
            catch (Exception ex)
            {
                _log?.LogDebug($"旧配置迁移跳过: {ex.Message}");
            }
        }

        private static int Migrate<T>(Dictionary<string, string> legacy, ConfigEntry<T> entry, params string[] legacyKeys)
        {
            if (entry == null) return 0;

            // 新键已存在于 cfg ⇒ 说明此前已迁移过（或用户自己改过），一律不再用旧值覆盖。
            // 旧实现缺这道保护，而 BepInEx 保存配置时不会删除未知小节 ⇒ 老节永远留在文件里，
            // 每次启动都把用户对新键的修改覆盖回去。
            var currentLookup = entry.Definition.Section + "||" + entry.Definition.Key;
            if (legacy.ContainsKey(currentLookup)) return 0;

            for (int i = 0; i < legacyKeys.Length; i++)
            {
                var key = legacyKeys[i];
                if (key == null) continue;
                var separator = key.IndexOf('|');
                if (separator <= 0) continue;

                var definition = new ConfigDefinition(key.Substring(0, separator), key.Substring(separator + 1));
                var lookup = definition.Section + "||" + definition.Key;
                if (!legacy.TryGetValue(lookup, out var rawValue)) continue;
                if (string.IsNullOrWhiteSpace(rawValue)) continue;

                try
                {
                    entry.SetSerializedValue(rawValue.Trim());
                    return 1;
                }
                catch (Exception)
                {
                    // 值无法解析时保留默认值
                }
            }
            return 0;
        }

        private static Dictionary<string, string> ReadIni(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var section = string.Empty;

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                if (line[0] == '[')
                {
                    var end = line.IndexOf(']');
                    section = end > 1 ? line.Substring(1, end - 1).Trim() : string.Empty;
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0) continue;

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                map[section + "||" + key] = value;
            }

            return map;
        }
    }
}
