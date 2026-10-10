using System;
using System.Collections.Generic;
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
#endif

namespace DemosaicPlugin
{
    /// <summary>
    /// DemosaicEngine 的材质修复部分（partial class，字段与主文件共享）。
    /// </summary>
    internal sealed partial class DemosaicEngine
    {
private struct ShaderPropEntry
        {
            public string Name;
            public UnityEngine.Rendering.ShaderPropertyType Type;
            /// <summary><c>[Toggle(XXX)]</c> 里的关键字名；空表示不是 Toggle 属性。</summary>
            public string ToggleKeyword;
        }

private ShaderPropEntry[] GetShaderProps(Shader shader)
        {
            int id = shader.GetInstanceID();
            if (_shaderPropCache.TryGetValue(id, out var cached)) return cached;

            ShaderPropEntry[] entries;
            try
            {
                int count = shader.GetPropertyCount();
                entries = new ShaderPropEntry[count];
                for (int i = 0; i < count; i++)
                {
                    string propertyName = shader.GetPropertyName(i);
                    entries[i] = new ShaderPropEntry
                    {
                        Name = propertyName,
                        Type = shader.GetPropertyType(i),
                        ToggleKeyword = GetToggleKeyword(shader, i)
                    };
                }
            }
            catch (Exception)
            {
                entries = Array.Empty<ShaderPropEntry>();
            }

            _shaderPropCache[id] = entries;
            return entries;
        }

private void TrackMaterialFixTarget(Renderer renderer)
        {
            if (renderer == null) return;

            int id = renderer.GetInstanceID();
            if (_materialFixIds.Contains(id)) return;

            var materials = RuntimeInterop.GetSharedMaterials(renderer);
            if (materials == null) return;

            for (int i = 0; i < materials.Length && i < 4; i++)
            {
                var material = materials[i];
                if (material == null) continue;

                var shader = material.shader;
                if (shader == null || !ShaderInScope(shader)) continue;
                if (!HasMosaicProperty(shader)) continue;

                if (_materialFixIds.Add(id)) _materialFixTargets.Add(renderer);
                return;
            }
        }

private bool ShaderInScope(Shader shader)
        {
            var keywords = _settings.MosaicMaterialShaderArray;
            if (keywords == null || keywords.Length == 0) return true;   // 留空表示不限定
            return MosaicDetector.ContainsAny(shader.name, keywords);
        }

private bool HasMosaicProperty(Shader shader)
        {
            var keywords = _settings.MosaicMaterialPropertyArray;
            if (keywords == null || keywords.Length == 0) return false;

            var props = GetShaderProps(shader);
            for (int i = 0; i < props.Length; i++)
            {
                if (MosaicDetector.ContainsAny(props[i].Name, keywords)) return true;
            }
            return false;
        }

private static string GetToggleKeyword(Shader shader, int index)
        {
            try
            {
                var attributes = shader.GetPropertyAttributes(index);
                if (attributes == null) return null;

                for (int i = 0; i < attributes.Length; i++)
                {
                    string text = attributes[i];
                    if (string.IsNullOrEmpty(text)) continue;
                    if (text.IndexOf("Toggle", StringComparison.OrdinalIgnoreCase) != 0) continue;

                    int open = text.IndexOf('(');
                    int close = text.IndexOf(')', open + 1);
                    if (open < 0 || close <= open + 1) continue;
                    return text.Substring(open + 1, close - open - 1).Trim();
                }
            }
            catch (Exception)
            {
                // 老版本 / 被裁剪的 shader 可能读不到属性
            }
            return null;
        }

private bool TryDisableKeyword(Material material, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return false;

            bool disabled = false;

            try
            {
                if (material.IsKeywordEnabled(keyword))
                {
                    material.DisableKeyword(keyword);
                    disabled = true;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"关闭材质关键字 {keyword} 失败: {ex.Message}");
            }

            try
            {
                // 全局关键字（Shader.EnableKeyword）不会出现在 material.shaderKeywords 里 ——
                // 这正好解释了「F8 看到 kw=[] 却仍然有码」。
                if (Shader.IsKeywordEnabled(keyword))
                {
                    Shader.DisableKeyword(keyword);
                    disabled = true;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"关闭全局关键字 {keyword} 失败: {ex.Message}");
            }

            return disabled;
        }

private static void AppendMaterialChange(ref StringBuilder changed, string name, string before, string after)
        {
            changed = changed ?? new StringBuilder(64);
            changed.Append(name).Append(' ').Append(before).Append('→').Append(after).Append(' ');
        }


private bool ZeroMaterialMosaicProperties(Material material, out string detail)
        {
            detail = null;

            var shader = material.shader;
            if (shader == null || !ShaderInScope(shader)) return false;

            var keywords = _settings.MosaicMaterialPropertyArray;
            if (keywords == null || keywords.Length == 0) return false;

            float targetAlpha = _settings.MosaicSwitchColorAlpha.Value;
            var props = GetShaderProps(shader);
            StringBuilder changed = null;

            for (int i = 0; i < props.Length; i++)
            {
                var entry = props[i];
                if (!MosaicDetector.ContainsAny(entry.Name, keywords)) continue;

                try
                {
                    // 值归零
                    if (entry.Type == UnityEngine.Rendering.ShaderPropertyType.Float ||
                        entry.Type == UnityEngine.Rendering.ShaderPropertyType.Range)
                    {
                        float current = material.GetFloat(entry.Name);
                        if (current != 0f)
                        {
                            material.SetFloat(entry.Name, 0f);
                            AppendMaterialChange(ref changed, entry.Name, current.ToString(), "0");
                        }
                    }
                    else if (entry.Type == UnityEngine.Rendering.ShaderPropertyType.Int)
                    {
                        int current = material.GetInt(entry.Name);
                        if (current != 0)
                        {
                            material.SetInt(entry.Name, 0);
                            AppendMaterialChange(ref changed, entry.Name, current.ToString(), "0");
                        }
                    }
                    else if (entry.Type == UnityEngine.Rendering.ShaderPropertyType.Color)
                    {
                        var color = material.GetColor(entry.Name);
                        if (!Mathf.Approximately(color.a, targetAlpha))
                        {
                            float before = color.a;
                            color.a = targetAlpha;
                            material.SetColor(entry.Name, color);
                            AppendMaterialChange(ref changed, entry.Name, string.Concat("a=", before), string.Concat("a=", targetAlpha));
                        }
                    }

                    // [Toggle] 属性：真正决定分支的是关键字，必须一起关掉，光改 float 没用。
                    // 注意这一步**不能**被"值已经是 0 就跳过"带过 —— 恰恰是值已归零但关键字还开着的情况最需要它。
                    if (_settings.EnableMosaicKeywordOff.Value)
                    {
                        if (entry.ToggleKeyword != null)
                        {
                            if (TryDisableKeyword(material, entry.ToggleKeyword))
                                AppendMaterialChange(ref changed, entry.ToggleKeyword, "关键字=开", "关");
                        }
                        else
                        {
                            var guessed = GuessKeywords(entry.Name);
                            for (int g = 0; g < guessed.Length; g++)
                            {
                                if (!TryDisableKeyword(material, guessed[g])) continue;
                                AppendMaterialChange(ref changed, guessed[g], "关键字=开", "关");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"材质属性归零失败 {material.name}.{entry.Name}: {ex.Message}");
                }
            }

            if (changed == null) return false;
            detail = changed.ToString().TrimEnd();
            return true;
        }
        /// <summary>按命名约定猜可能的关键字名；结果按属性名缓存，避免每轮复查重复拼接（属性名集合很稳定）。</summary>
        private string[] GuessKeywords(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return Array.Empty<string>();
            if (_keywordGuessCache.TryGetValue(propertyName, out var cached)) return cached;

            string trimmed = propertyName.StartsWith("_", StringComparison.Ordinal)
                ? propertyName.Substring(1)
                : propertyName;
            string upper = trimmed.ToUpperInvariant();

            var result = new[]
            {
                "_" + upper + "_ON",
                upper + "_ON",
                "_LID_MOSAIC_ON",
                "LID_MOSAIC_ON",
                "_LID_MOSAICENABLED_ON",
                "LID_MOSAICENABLED_ON",
                "_MOSAIC_ON",
                "MOSAIC_ON"
            };

            _keywordGuessCache[propertyName] = result;
            return result;
        }

        /// <summary>把名单上所有材质里的马赛克属性归零。</summary>
        internal void ApplyMaterialMosaicFix()
        {
            if (!_settings.EnableMaterialMosaicOff.Value) return;
            if (_materialFixTargets.Count == 0) return;

            int fixedCount = 0;
            string sample = null;

            for (int i = _materialFixTargets.Count - 1; i >= 0; i--)
            {
                var renderer = _materialFixTargets[i];
                if (renderer == null)
                {
                    _materialFixTargets.RemoveAt(i);
                    continue;
                }

                var materials = RuntimeInterop.GetSharedMaterials(renderer);
                if (materials == null) continue;

                for (int m = 0; m < materials.Length && m < 4; m++)
                {
                    var material = materials[m];
                    if (material == null) continue;
                    if (!ZeroMaterialMosaicProperties(material, out var detail)) continue;

                    fixedCount++;
                    if (sample == null)
                    {
                        var go = renderer.gameObject;
                        sample = string.Concat(go != null ? go.name : "?", "/", material.name, " [", detail, "]");
                    }
                }
            }

            if (fixedCount > 0)
                _log.LogInfo($"已归零材质马赛克属性: {fixedCount} 个材质 | 例: {sample}");
        }



        private readonly List<Renderer> _materialFixTargets = new List<Renderer>();
        private readonly HashSet<int> _materialFixIds = new HashSet<int>();
        private readonly Dictionary<int, ShaderPropEntry[]> _shaderPropCache = new Dictionary<int, ShaderPropEntry[]>();
        // 关键字猜测结果按属性名缓存：原实现每轮每属性都要 ToUpperInvariant + 拼 8 个字符串
        private readonly Dictionary<string, string[]> _keywordGuessCache = new Dictionary<string, string[]>();

    }
}
