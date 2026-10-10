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
            EnablePlugin = _config.Bind(SectionGeneral, "EnablePlugin", true, "启用或禁用整个插件。");
            RemoveModeEntry = _config.Bind(SectionGeneral, "RemoveMode", RemoveMode.Smart,
                "移除方式：Disable=禁用对象, Destroy=销毁对象, Transparent=全部材质替换为透明, Smart=仅替换马赛克材质槽（默认，误伤最小）。");
            ManualScanKey = _config.Bind(SectionGeneral, "ManualScanKey", KeyCode.F10, "按下此键强制重新扫描整个场景。");
            ExportSceneKey = _config.Bind(SectionGeneral, "ExportSceneKey", KeyCode.F11, "按下此键把场景 Renderer 信息导出到日志，用于分析关键词。");
            IncludeInactiveObjects = _config.Bind(SectionGeneral, "IncludeInactiveObjects", true,
                "扫描时是否包含未激活对象。Unity 2022+ 会优先使用更快的 FindObjectsByType。");
            DetectParentObjectNames = _config.Bind(SectionGeneral, "DetectParentObjectNames", true,
                "检测父节点名称，适合 MosaicRoot / Quad 这类按层级组织的马赛克。");
            LogProcessedObjects = _config.Bind(SectionGeneral, "LogProcessedObjects", false,
                "是否为每个被处理对象写 Info 日志。大量对象会明显影响性能。");

            PeriodicScanInterval = _config.Bind(SectionScan, "PeriodicScanInterval", 10f,
                "完整场景重扫间隔（秒），0 表示禁用。会重新发现并逐维度评估所有 Renderer，是开销较大的那条路径；日常的马赛克复发 / 改值激活由 WatchlistRecheckInterval 负责，不需要为了响应速度把它调很小。");
            WatchlistRecheckInterval = _config.Bind(SectionScan, "WatchlistRecheckInterval", 2f,
                "只复核监视列表（曾被判定为马赛克、或材质带处于关闭状态的马赛克属性的 Renderer）的间隔（秒）。不遍历场景、不重新发现对象，成本极低；0 表示禁用。");
            SceneLoadScanDelay = _config.Bind(SectionScan, "SceneLoadScanDelay", 1.5f, "场景加载后延迟扫描的时间（秒）。");
            ScanBatchSize = _config.Bind(SectionScan, "ScanBatchSize", 500, "每帧最多处理的对象数量。");
            ScanTimeBudgetMs = _config.Bind(SectionScan, "ScanTimeBudgetMs", 2f,
                "每帧扫描的时间预算（毫秒）。超过预算即使没到 ScanBatchSize 也会让出本帧，防止掉帧。");
            ReverifyProcessedRenderers = _config.Bind(SectionScan, "ReverifyProcessedRenderers", true,
                "周期扫描时复核已处理过的 Renderer。游戏若动态把材质改回马赛克，可保证再次被处理。");

            ObjectNameKeywords = _config.Bind(SectionDetection, "ObjectNameKeywords", "mosaic,censored,pixelated,mozic,mazic,mozaic,moza", "对象名关键词，逗号分隔。");
            MaterialNameKeywords = _config.Bind(SectionDetection, "MaterialNameKeywords", "mosaic,censored,pixel,mozic,mazic,moza", "材质名关键词，逗号分隔。");
            ShaderNameKeywords = _config.Bind(SectionDetection, "ShaderNameKeywords", "mosaic,pixelate,censor,moza,mozic,mazic,mozaic", "Shader 名关键词，逗号分隔。");
            MeshNameKeywords = _config.Bind(SectionDetection, "MeshNameKeywords", "censor,mosaic,moza,mozic,mazic,mozaic", "网格名关键词，逗号分隔。");
            TextureKeywords = _config.Bind(SectionDetection, "TextureKeywords", "mosaic", "纹理名关键词，逗号分隔。");
            ComponentNameKeywords = _config.Bind(SectionDetection, "ComponentNameKeywords", "", "组件名关键词，逗号分隔。");
            ShaderPropertyKeywords = _config.Bind(SectionDetection, "ShaderPropertyKeywords", "_PixelSize,_BlockSize,_MosaicFactor",
                "Shader 属性名关键词，逗号分隔。命中属性名后还要看材质上的当前值是否超过 ShaderPropertyValueThreshold。");
            ShaderPropertyValueThreshold = _config.Bind(SectionDetection, ShaderPropertyThresholdKey, 0f,
                "数值型 Shader 属性（Float/Range/Int）大于此阈值才判定马赛克生效，0 表示只要非 0 就算生效。" +
                "若某 Shader 的 _PixelSize / _BlockSize 默认为 1（无视觉效果）却被子节点名等维度连带误伤，可把本值调到 1。");
            ExclusionKeywords = _config.Bind(SectionDetection, "ExclusionKeywords", "", "白名单关键词（最高优先级），命中后一律不处理。");
#if DEMOSAIC_IL2CPP
            // IL2CPP 下 GetTexturePropertyNameIDs 可能触发 AccessViolationException，默认关闭
            EnableTextureDetection = _config.Bind(SectionDetection, "EnableTextureDetection", false, "是否启用纹理名检测（IL2CPP 下可能崩溃，默认关闭）。");
#else
            EnableTextureDetection = _config.Bind(SectionDetection, "EnableTextureDetection", true, "是否启用纹理名检测。");
#endif

            DisableMethods = _config.Bind(SectionAdvanced, "DisableMethods", false, "启用按名称拦截方法（反射扫描整个程序集，谨慎使用）。");
            MethodDisableKeywords = _config.Bind(SectionAdvanced, "MethodDisableKeywords", "mosaic,censor,moza", "要拦截的方法名关键词。");
            MethodExcludeKeywords = _config.Bind(SectionAdvanced, "MethodExcludeKeywords",
                "remove,destroy,clear,disable,hide,off,delete,undo,stop,cancel", "方法名排除词，同时命中排除词的方法不会被拦截（防止误杀去马赛克方法）。");
            MethodPatchTargetAssemblies = _config.Bind(SectionAdvanced, "MethodPatchTargetAssemblies", "Assembly-CSharp", "方法拦截要扫描的程序集名称，逗号分隔。");
            EnableCameraEffectDetection = _config.Bind(SectionAdvanced, "EnableCameraEffectDetection", true, "是否禁用 Camera 上名称匹配关键词的后处理组件。");
            CameraEffectKeywords = _config.Bind(SectionAdvanced, "CameraEffectKeywords", "mosaic,censor,pixelat,moza,mozic,mazic", "Camera 后处理组件名关键词。");
            EnableDecalDetection = _config.Bind(SectionAdvanced, "EnableDecalDetection", true, "是否禁用材质匹配马赛克关键词的 Projector / DecalProjector。");
            EnableMaterialSetterHook = _config.Bind(SectionAdvanced, "EnableMaterialSetterHook", true,
                "是否 Hook Renderer 材质 setter，实时捕获游戏动态赋值的马赛克材质。");

            EnableMosaicSwitchOff = _config.Bind(SectionAdvanced, "EnableMosaicSwitchOff", true,
                "是否关闭「马赛克开关挂在组件字段上」的马赛克（如 PaconeSoft.LightID.LIDPenetrator.enableMosaic）。" +
                "动作是把命中的字段归零/关闭后同步给材质，**不替换材质槽**，所以不会像 Smart 模式那样把物体一起变透明。");
            MosaicSwitchComponentTypes = _config.Bind(SectionAdvanced, "MosaicSwitchComponentTypes", "LIDPenetrator",
                "要处理的组件类型名关键词（同时匹配类型短名与全名），逗号分隔。留空则关闭本功能。" +
                "例：LIDPenetrator，或写全名 PaconeSoft.LightID.LIDPenetrator 更精确。");
            MosaicSwitchFieldKeywords = _config.Bind(SectionAdvanced, "MosaicSwitchFieldKeywords",
                "enableMosaic,mosaicRatio,mosaicMinPx,mosaicColor",
                "组件上要归零的字段名关键词，逗号分隔。bool 置 false、float/int 置 0、Color 把 alpha 设为 MosaicSwitchColorAlpha。");
            MosaicSwitchApplyMethods = _config.Bind(SectionAdvanced, "MosaicSwitchApplyMethods",
                "ApplyToMaterial,ApplyToMaterials",
                "字段改完后要调用的无参同步方法名，逗号分隔（组件把字段推给材质用的）。留空表示字段只会被改，不做额外同步。");
            MosaicSwitchColorAlpha = _config.Bind(SectionAdvanced, "MosaicSwitchColorAlpha", 0f,
                "Color 类字段要设置的 alpha 值。本作 mosaicColor 的 alpha 是「原图 ↔ 马赛克」混合比例，0 = 完全原图；" +
                "若某游戏语义相反（1 = 原图），把它改成 1。");
            MosaicSwitchRecheckInterval = _config.Bind(SectionAdvanced, "MosaicSwitchRecheckInterval", 1f,
                "组件马赛克开关的复查间隔（秒）。0 表示禁用。只在存在目标组件的场景里才有开销（每轮一次按类型查找）。");
            ExportSwitchKey = _config.Bind(SectionAdvanced, "ExportSwitchKey", KeyCode.F9,
                "按下此键把场景中的马赛克开关组件（字段名与当前值、目标 Renderer 的材质与 shaderKeywords）导出到日志，用于排查为什么没关上。");
            ScanCandidatesKey = _config.Bind(SectionAdvanced, "ScanCandidatesKey", KeyCode.F8,
                "按下此键扫描整个场景，列出所有可疑马赛克物件：名字命中关键词的 Renderer、材质上马赛克类属性处于开启状态的 Renderer、" +
                "以及带马赛克 shader 关键字或材质属性块的 Renderer。用于定位「关掉的不是那块码」的情况。");
            MosaicGateMemberKeywords = _config.Bind(SectionAdvanced, "MosaicGateMemberKeywords",
                "UpdateMosaicVisibility,mosaicRenderers,mosaicScanned,overrideShaderParameters",
                "马赛克「门控组件」的成员签名关键词（逗号分隔）。游戏侧类名常被混淆成 <数字>，但成员名可读；" +
                "F8 会按这些成员名找到控制马赛克的组件，并把它的开关字段与它管辖的 Renderer 列表一起打印出来。");
            EnableMaterialMosaicOff = _config.Bind(SectionAdvanced, "EnableMaterialMosaicOff", true,
                "是否直接把**材质上**的马赛克属性归零。有些游戏不读组件字段，而是把 _LID_MosaicEnabled / _LID_MosaicRatio 这类属性" +
                "直接写在材质实例上（组件字段改成 0 也不影响渲染），这时只能改材质。");
            MosaicMaterialPropertyKeywords = _config.Bind(SectionAdvanced, "MosaicMaterialPropertyKeywords", "mosaic",
                "材质上要归零的 Shader 属性名关键词，逗号分隔。默认 mosaic 可命中 _LID_MosaicEnabled / _LID_MosaicMinPx / " +
                "_LID_MosaicRatio / _LID_MosaicColor 一族。");
            MosaicMaterialShaderKeywords = _config.Bind(SectionAdvanced, "MosaicMaterialShaderKeywords", "lidpatched",
                "只对 Shader 名命中此关键词的材质做归零（限定作用范围，避免误伤其它材质的同名属性）。留空表示不限定。");
            MosaicForceRepatch = _config.Bind(SectionAdvanced, "MosaicForceRepatch", false,
                "实验开关（默认关）：关掉开关字段后，把组件 enabled 关一次再开一次，逼它按新设置重新生成 shader 补丁 / 重新推值。" +
                "有些系统的马赛克是被编译进运行时 patch 出来的 shader 变体的，这时只改字段和材质都没用，需要它重新补丁。" +
                "若开启后画面出现异常（形变丢失等），关掉即可。");
                   EnableMosaicKeywordOff = _config.Bind(SectionAdvanced, "EnableMosaicKeywordOff", true,
                "是否连同关掉马赛克属性的 **shader 关键字**。Unity 里 [Toggle(_X_ON)] 那种属性，" +
                "运行时真正决定分支的是关键字，只改 float 值毫无视觉效果（这正是「改了但画面没反应」的常见原因）。" +
                "本项会先按 Shader 属性上的 [Toggle(...)] 取真实关键字名，取不到时再按命名约定猜，材质局部与全局两处都关。");
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
