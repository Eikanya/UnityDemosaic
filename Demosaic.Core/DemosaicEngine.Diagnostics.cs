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
    /// DemosaicEngine 的F8/F9 诊断输出部分（partial class，字段与主文件共享）。
    /// </summary>
    internal sealed partial class DemosaicEngine
    {
internal void DumpMosaicSwitches()
        {
            if (!ResolveSwitchTypes())
            {
                _log.LogInfo("[Demosaic Switch] 未找到马赛克开关组件类型，无内容可导出。");
                return;
            }

            var buffer = new StringBuilder(2048);
            var keywords = _settings.MosaicSwitchFieldKeywordArray;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            int total = 0;
            int converted = 0;
            int matched = 0;

            for (int t = 0; t < _switchTypes.Length; t++)
            {
                var type = _switchTypes[t];

                MethodInfo[] methods = null;
                PropertyInfo[] properties = null;
                FieldInfo[] fields = null;
                try { methods = type.GetMethods(flags); } catch { }
                try { properties = type.GetProperties(flags); } catch { }
                try { fields = type.GetFields(flags); } catch { }

                try
                {
#if DEMOSAIC_IL2CPP
                    var instances = Object.FindObjectsOfType(Il2CppType.From(type));
#else
                    var instances = Object.FindObjectsOfType(type, true);
#endif
                    if (instances == null) continue;

                    for (int i = 0; i < instances.Length; i++)
                    {
                        total++;
                        var raw = instances[i];
                        var target = AsConcreteType(raw, type);

                        buffer.Append("[Demosaic Switch] ").Append(type.FullName);
                        buffer.Append(" | rawType=").Append(raw?.GetType().Name ?? "null");
                        buffer.Append(" | converted=").Append(target != null);
                        if (target == null)
                        {
                            buffer.Append("  <<< 类型还原失败，该实例处理不了\n");
                            continue;
                        }
                        converted++;

                        var behaviour = RuntimeInterop.AsMonoBehaviour(target);
                        buffer.Append(" on '").Append(behaviour != null && behaviour.gameObject != null ? behaviour.gameObject.name : "?").Append('\'');

                        // 访问器方法对（首选路径）
                        if (methods != null)
                        {
                            for (int m = 0; m < methods.Length; m++)
                            {
                                var getter = methods[m];
                                if (!getter.Name.StartsWith("get_", StringComparison.Ordinal)) continue;
                                if (getter.GetParameters().Length != 0 || getter.ReturnType == typeof(void)) continue;

                                string member = getter.Name.Substring(4);
                                MethodInfo setter;
                                try { setter = type.GetMethod("set_" + member, flags, null, new[] { getter.ReturnType }, null); }
                                catch (Exception) { continue; }

                                try { AppendMember(buffer, member + "(prop)", getter.Invoke(target, null), keywords); }
                                catch (Exception ex) { buffer.Append(" | ").Append(member).Append("=读取失败(").Append(ex.GetType().Name).Append(')'); }

                                if (setter != null && keywords != null && MosaicDetector.ContainsAny(member, keywords)) matched++;
                            }
                        }

                        // 属性 + 字段（兜底路径）
                        if (properties != null)
                        {
                            for (int p = 0; p < properties.Length; p++)
                            {
                                var property = properties[p];
                                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                                try { AppendMember(buffer, property.Name + "(prop)", property.GetValue(target), keywords); }
                                catch { }
                            }
                        }

                        if (fields != null)
                        {
                            for (int f = 0; f < fields.Length; f++)
                            {
                                var field = fields[f];
                                if (field.IsLiteral || field.IsStatic) continue;
                                try { AppendMember(buffer, field.Name + "(field)", field.GetValue(target), keywords); }
                                catch { }
                            }
                        }

                        AppendSwitchTarget(buffer, target, type);
                        buffer.Append('\n');
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"导出开关组件失败 ({type.Name}): {ex.Message}");
                }
            }

            _log.LogInfo(buffer.ToString());
            _log.LogInfo($"[Demosaic Switch] 实例 {total} 个 / 成功还原类型 {converted} 个 / 命中关键词的可写成员 {matched} 个（带 * 即命中）。");
        }

private static void AppendMember(StringBuilder buffer, string name, object value, string[] keywords)
        {
            buffer.Append(" | ").Append(name).Append('=').Append(value);
            if (keywords != null && MosaicDetector.ContainsAny(name, keywords)) buffer.Append('*');
        }

private void AppendSwitchTarget(StringBuilder buffer, object target, Type type)
        {
            try
            {
                var renderer = TryGetTargetRenderer(target, type);
                if (renderer == null)
                {
                    buffer.Append(" | target=N/A");
                    return;
                }

                buffer.Append(" | target='").Append(renderer.name).Append('\'');
                if (HasPropertyBlock(renderer)) buffer.Append(" | 有 MaterialPropertyBlock");
                var materials = RuntimeInterop.GetSharedMaterials(renderer);
                if (materials == null) return;

                for (int i = 0; i < materials.Length && i < 4; i++)
                {
                    var material = materials[i];
                    if (material == null) continue;
                    buffer.Append(" | mat[").Append(i).Append("]=").Append(material.name)
                          .Append(" shader=").Append(material.shader != null ? material.shader.name : "N/A")
                          .Append(" keywords=[").Append(JoinShaderKeywords(material)).Append(']');
                    AppendMaterialPropertyProbes(buffer, material, _settings.MosaicSwitchFieldKeywordArray);
                }
            }
            catch (Exception ex)
            {
                buffer.Append(" | target 读取失败: ").Append(ex.Message);
            }
        }

private static void AppendMaterialPropertyProbes(StringBuilder buffer, Material material, string[] keywords)
        {
            var shader = material.shader;
            if (shader == null || keywords == null || keywords.Length == 0) return;

            try
            {
                int count = shader.GetPropertyCount();
                int printedTextures = 0;
                for (int i = 0; i < count; i++)
                {
                    string name = shader.GetPropertyName(i);
                    bool matched = MosaicDetector.ContainsAny(name, keywords);

                    // 名称没命中但**是贴图**的也打出来（上限 12 个）——
                    // 用于判断"码是不是烘焙在贴图里"，只按名字筛会一个贴图都看不到。
                    if (!matched)
                    {
                        if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                        if (printedTextures++ >= 12) continue;
                    }

                    buffer.Append(" | ").Append(name).Append('=');
                    switch (shader.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            buffer.Append(material.GetFloat(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Int:
                            buffer.Append(material.GetInt(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            buffer.Append(material.GetColor(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        {
                            var texture = material.GetTexture(name);
                            buffer.Append(texture != null ? "tex:" + texture.name : "tex:null");
                            break;
                        }
                        default:
                            buffer.Append("(非数值类型)");
                            break;
                    }
                    if (matched) buffer.Append('*');
                }
            }
            catch (Exception ex)
            {
                buffer.Append(" | shader 属性读取失败: ").Append(ex.Message);
            }
        }

internal static string JoinShaderKeywords(Material material)        {
            try
            {
                var keywords = material.shaderKeywords;
                if (keywords == null) return string.Empty;

#if DEMOSAIC_IL2CPP
                var parts = new string[keywords.Length];
                for (int i = 0; i < keywords.Length; i++) parts[i] = keywords[i];
                return string.Join(" ", parts);
#else
                return string.Join(" ", keywords);
#endif
            }
            catch (Exception)
            {
                return "N/A";
            }
        }

internal void ScanMosaicCandidates()
        {
            var renderers = new List<Renderer>();
            SceneObjectFinder.FillRenderers(renderers, true);

            if (renderers.Count == 0)
            {
                _log.LogInfo("[Demosaic Scan] 场景里没有 Renderer。");
                return;
            }

            var buffer = new StringBuilder(4096);
            int candidates = 0;

            for (int i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                var materials = RuntimeInterop.GetSharedMaterials(renderer);
                if (materials == null) continue;

                var go = renderer.gameObject;
                string goName = go != null ? go.name : "?";
                string reason = null;
                var details = new StringBuilder(160);

                int printedMaterials = 0;
                for (int m = 0; m < materials.Length; m++)
                {
                    var material = materials[m];
                    if (material == null) continue;

                    var shader = material.shader;
                    string shaderName = shader != null ? shader.name : "N/A";

                    if (reason == null && MosaicDetector.ContainsAny(goName, CandidateNameKeywords)) reason = "对象名命中";
                    if (reason == null && MosaicDetector.ContainsAny(material.name, CandidateNameKeywords)) reason = "材质名命中";
                    if (reason == null && MosaicDetector.ContainsAny(shaderName, CandidateNameKeywords)) reason = "Shader名命中";

                    // 判定覆盖**所有**材质槽（合并网格可能有几十个），只是日志最多打 4 个
                    if (printedMaterials++ >= 4) continue;

                    details.Append(" | mat[").Append(m).Append("]=").Append(material.name)
                           .Append(" shader=").Append(shaderName)
                           .Append(" kw=[").Append(JoinShaderKeywords(material)).Append(']');

                    if (shader == null) continue;

                    var props = GetShaderProps(shader);
                    int printedTextures = 0;
                    for (int p = 0; p < props.Length; p++)
                    {
                        var entry = props[p];
                        bool nameMatched = MosaicDetector.ContainsAny(entry.Name, CandidatePropertyKeywords);

                        // 名字没命中的**贴图**也打出来（上限 12 个），否则一个贴图都看不到，没法判断码是不是烘焙在贴图里
                        if (!nameMatched)
                        {
                            if (entry.Type != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                            if (printedTextures++ >= 12) continue;
                        }

                        bool on;
                        string valueText;
                        try
                        {
                            if (entry.Type == UnityEngine.Rendering.ShaderPropertyType.Float ||
                                entry.Type == UnityEngine.Rendering.ShaderPropertyType.Range)
                            {
                                float number = material.GetFloat(entry.Name);
                                on = number != 0f;
                                valueText = number.ToString();
                            }
                            else if (entry.Type == UnityEngine.Rendering.ShaderPropertyType.Int)
                            {
                                int number = material.GetInt(entry.Name);
                                on = number != 0;
                                valueText = number.ToString();
                            }
                    else if (entry.Type == UnityEngine.Rendering.ShaderPropertyType.Color)
                    {
                        var color = material.GetColor(entry.Name);
                        on = color.a != 0f;
                        valueText = color.ToString();
                    }
                    else if (entry.Type == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    {
                        var texture = material.GetTexture(entry.Name);
                        valueText = texture != null ? string.Concat("tex:", texture.name) : "tex:null";
                        on = texture != null && MosaicDetector.ContainsAny(texture.name, CandidatePropertyKeywords);
                    }
                    else
                    {
                        on = true;
                        valueText = "(非数值类型)";
                    }
                        }
                        catch (Exception ex)
                        {
                            details.Append(" | ").Append(entry.Name).Append("=读取失败(").Append(ex.GetType().Name).Append(')');
                            continue;
                        }

                        details.Append(" | ").Append(entry.Name).Append('=').Append(valueText);
                        if (on)
                        {
                            details.Append("<ON>");
                            if (reason == null) reason = "属性值开启";
                        }
                    }
                }

                if (reason == null) continue;

                candidates++;
                buffer.Append("[Demosaic Scan] ").Append(reason).Append(" | GO='").Append(goName).Append('\'');
                if (HasPropertyBlock(renderer)) buffer.Append(" | 有 MaterialPropertyBlock");
                buffer.Append(details).Append('\n');
            }

            int gates = 0;
            AppendMosaicGates(buffer, ref gates);
            AppendLoadedShaderVariants(buffer);
            AppendAssetScan(buffer);

            _log.LogInfo(buffer.ToString());
            _log.LogInfo($"[Demosaic Scan] 共扫描 {renderers.Count} 个 Renderer，可疑 {candidates} 个；" +
                         $"马赛克门控组件 {gates} 个（<ON> 表示该属性当前是开着的）。");
        }

private static bool HasPropertyBlock(Renderer renderer)
        {
            try
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                return !block.isEmpty;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly string[] CandidateNameKeywords =
        {
            "mosaic", "moza", "mozic", "mazic", "mozaic", "censor", "pixel", "penis", "chinpo",
            "penetrat", "phallus", "genital", "lidpatched", "_lid_", "pacone"
        };

        private static readonly string[] MosaicAssetKeywords =
        {
            "mosaic", "moza", "mozic", "mazic", "censor", "pixel", "strictmosaic"
        };

private void AppendAssetScan(StringBuilder buffer)
        {
            // ① 材质
            try
            {
#if DEMOSAIC_IL2CPP
                var materials = Resources.FindObjectsOfTypeAll(Il2CppType.From(typeof(Material)));
#else
                var materials = Resources.FindObjectsOfTypeAll(typeof(Material));
#endif
                if (materials != null)
                {
                    buffer.Append($"\n[Demosaic Asset] 已加载 Material {materials.Length} 个，其中像马赛克的：\n");
                    int found = 0;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var material = AsConcreteType(materials[i], typeof(Material)) as Material;
                        if (material == null) continue;

                        var shader = material.shader;
                        string shaderName = shader != null ? shader.name : "N/A";
                        if (!MosaicDetector.ContainsAny(material.name, MosaicAssetKeywords) &&
                            !MosaicDetector.ContainsAny(shaderName, MosaicAssetKeywords)) continue;

                        found++;
                        buffer.Append("   - mat='").Append(material.name).Append("' shader=").Append(shaderName);
                        AppendMaterialPropertyProbes(buffer, material, MosaicAssetKeywords);
                        buffer.Append('\n');
                    }
                    if (found == 0) buffer.Append("   (无)\n");
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"材质扫描失败: {ex.Message}");
            }

            // ② 贴图名（直接验证「码是不是烘焙在贴图里」）
            try
            {
#if DEMOSAIC_IL2CPP
                var textures = Resources.FindObjectsOfTypeAll(Il2CppType.From(typeof(Texture2D)));
#else
                var textures = Resources.FindObjectsOfTypeAll(typeof(Texture2D));
#endif
                if (textures != null)
                {
                    buffer.Append($"[Demosaic Asset] 已加载 Texture2D {textures.Length} 个，名字像马赛克的：\n");
                    int found = 0;
                    for (int i = 0; i < textures.Length; i++)
                    {
                        var texture = AsConcreteType(textures[i], typeof(Texture2D)) as Texture2D;
                        if (texture == null) continue;
                        if (!MosaicDetector.ContainsAny(texture.name, MosaicAssetKeywords)) continue;

                        found++;
                        buffer.Append("   - tex='").Append(texture.name).Append("' ")
                              .Append(texture.width).Append('x').Append(texture.height)
                              .Append(" format=").Append(texture.format).Append('\n');
                    }
                    if (found == 0) buffer.Append("   (无)\n");
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"贴图扫描失败: {ex.Message}");
            }

            // ③ UI 图形（马赛克若是 Canvas 覆盖层，Renderer 扫描根本看不到）
            try
            {
                var graphicType = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
                if (graphicType == null)
                {
                    buffer.Append("[Demosaic Asset] 未加载 UnityEngine.UI，跳过 UI 扫描。\n");
                    return;
                }

#if DEMOSAIC_IL2CPP
                var graphics = Resources.FindObjectsOfTypeAll(Il2CppType.From(graphicType));
#else
                var graphics = Resources.FindObjectsOfTypeAll(graphicType);
#endif
                if (graphics == null) return;

                var materialProperty = graphicType.GetProperty("material");
                buffer.Append($"[Demosaic Asset] 已加载 UI Graphic {graphics.Length} 个，材质像马赛克的：\n");

                int found = 0;
                for (int i = 0; i < graphics.Length; i++)
                {
                    var graphic = AsConcreteType(graphics[i], graphicType);
                    if (graphic == null) continue;

                    Material material = null;
                    try
                    {
                        if (materialProperty != null) material = materialProperty.GetValue(graphic) as Material;
                    }
                    catch (Exception) { }

                    var shader = material != null ? material.shader : null;
                    string shaderName = shader != null ? shader.name : null;
                    if (!MosaicDetector.ContainsAny(shaderName, MosaicAssetKeywords) &&
                        !(material != null && MosaicDetector.ContainsAny(material.name, MosaicAssetKeywords))) continue;

                    found++;
                    string goName = "?";
                    var behaviour = RuntimeInterop.AsMonoBehaviour(graphic);
                    if (behaviour != null && behaviour.gameObject != null) goName = behaviour.gameObject.name;

                    buffer.Append("   - UI='").Append(goName)
                          .Append("' mat='").Append(material != null ? material.name : "null")
                          .Append("' shader=").Append(shaderName ?? "N/A").Append('\n');
                }
                if (found == 0) buffer.Append("   (无)\n");
            }
            catch (Exception ex)
            {
                _log.LogDebug($"UI 扫描失败: {ex.Message}");
            }

            // ④ Renderer（用 FindObjectsOfTypeAll）：能拿到 FindObjectsByType 看不见的隐藏对象 / 预制体里的对象
            try
            {
#if DEMOSAIC_IL2CPP
                var allRenderers = Resources.FindObjectsOfTypeAll(Il2CppType.From(typeof(Renderer)));
#else
                var allRenderers = Resources.FindObjectsOfTypeAll(typeof(Renderer));
#endif
                if (allRenderers != null)
                {
                    buffer.Append($"[Demosaic Asset] 全部 Renderer（含隐藏/未实例化）{allRenderers.Length} 个，材质像马赛克的：\n");
                    int found = 0;
                    for (int i = 0; i < allRenderers.Length; i++)
                    {
                        var renderer = AsConcreteType(allRenderers[i], typeof(Renderer)) as Renderer;
                        if (renderer == null) continue;

                        var materials = RuntimeInterop.GetSharedMaterials(renderer);
                        if (materials == null) continue;

                        string hit = null;
                        for (int m = 0; m < materials.Length; m++)
                        {
                            var material = materials[m];
                            if (material == null) continue;
                            string shaderName = material.shader != null ? material.shader.name : "N/A";
                            if (!MosaicDetector.ContainsAny(material.name, MosaicAssetKeywords) && !MosaicDetector.ContainsAny(shaderName, MosaicAssetKeywords)) continue;
                            hit = string.Concat("mat[", m, "]=", material.name, " shader=", shaderName);
                            break;
                        }
                        if (hit == null) continue;

                        found++;
                        // ⚠ Renderer 继承自 Component 而非 MonoBehaviour，用 AsMonoBehaviour 转会得到 null（GO 会变成 '?'）
                        var go = renderer.gameObject;
                        buffer.Append("   - GO='").Append(go != null ? go.name : "?")
                              .Append("' renderer='").Append(renderer.name).Append('\'')
                              .Append(" active=").Append(go != null && go.activeInHierarchy)
                              .Append(" scene=").Append(GetSceneName(go))
                              .Append(" hideFlags=").Append(renderer.hideFlags)
                              .Append(' ').Append(hit).Append('\n');
                    }
                    if (found == 0) buffer.Append("   (无)\n");
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"全量 Renderer 扫描失败: {ex.Message}");
            }

            // ⑤ CanvasRenderer 的材质：UI 覆盖层可能不通过 Graphic.material，而是直接设在 CanvasRenderer 上
            //    （CanvasRenderer 在 UnityEngine.UIModule，本插件没有引用该程序集，故运行时反射解析）
            try
            {
                var canvasRendererType = Type.GetType("UnityEngine.CanvasRenderer, UnityEngine.UIModule");
                if (canvasRendererType == null)
                {
                    buffer.Append("[Demosaic Asset] 未加载 UnityEngine.UIModule，跳过 CanvasRenderer 扫描。\n");
                }
                else
                {
#if DEMOSAIC_IL2CPP
                    var canvasRenderers = Resources.FindObjectsOfTypeAll(Il2CppType.From(canvasRendererType));
#else
                    var canvasRenderers = Resources.FindObjectsOfTypeAll(canvasRendererType);
#endif
                    var countProperty = canvasRendererType.GetProperty("materialCount");
                    var getMaterialMethod = canvasRendererType.GetMethod("GetMaterial", new[] { typeof(int) });

                    if (canvasRenderers != null && countProperty != null && getMaterialMethod != null)
                    {
                        buffer.Append($"[Demosaic Asset] 已加载 CanvasRenderer {canvasRenderers.Length} 个，材质像马赛克的：\n");
                        int found = 0;
                        for (int i = 0; i < canvasRenderers.Length; i++)
                        {
                            var canvasRenderer = AsConcreteType(canvasRenderers[i], canvasRendererType);
                            if (canvasRenderer == null) continue;

                            int materialCount = 0;
                            try { materialCount = (int)countProperty.GetValue(canvasRenderer); } catch (Exception) { continue; }

                            for (int m = 0; m < materialCount && m < 4; m++)
                            {
                                Material material = null;
                                try { material = getMaterialMethod.Invoke(canvasRenderer, new object[] { m }) as Material; }
                                catch (Exception) { continue; }
                                if (material == null) continue;

                                string shaderName = material.shader != null ? material.shader.name : "N/A";
                                if (!MosaicDetector.ContainsAny(material.name, MosaicAssetKeywords) && !MosaicDetector.ContainsAny(shaderName, MosaicAssetKeywords)) continue;

                                found++;
                                // 同理：CanvasRenderer 也是 Component 而非 MonoBehaviour
                                var component = canvasRenderer as Component;
                                var go = component != null ? component.gameObject : null;
                                buffer.Append("   - GO='").Append(go != null ? go.name : "?")
                                      .Append("' active=").Append(go != null && go.activeInHierarchy)
                                      .Append(" mat='").Append(material.name).Append("' shader=").Append(shaderName).Append('\n');
                                break;
                            }
                        }
                        if (found == 0) buffer.Append("   (无)\n");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"CanvasRenderer 扫描失败: {ex.Message}");
            }

            // ⑥ 相机的 CommandBuffer：后处理式马赛克不走 Renderer，只挂在这里
            try
            {
                var cameraEventType = Type.GetType("UnityEngine.Rendering.CameraEvent, UnityEngine.CoreModule");
                var getCommandBuffers = cameraEventType != null
                    ? typeof(Camera).GetMethod("GetCommandBuffers", new[] { cameraEventType })
                    : null;

                if (getCommandBuffers == null)
                {
                    buffer.Append("[Demosaic Asset] 取不到 Camera.GetCommandBuffers，跳过相机后处理扫描。\n");
                }
                else
                {
                    var cameras = Camera.allCameras;
                    buffer.Append($"[Demosaic Asset] 相机 {cameras.Length} 个的 CommandBuffer：\n");
                    int found = 0;
                    for (int i = 0; i < cameras.Length; i++)
                    {
                        var camera = cameras[i];
                        if (camera == null) continue;

                        foreach (var cameraEvent in Enum.GetValues(cameraEventType))
                        {
                            Array buffers;
                            try { buffers = getCommandBuffers.Invoke(camera, new[] { cameraEvent }) as Array; }
                            catch (Exception) { continue; }
                            if (buffers == null) continue;

                            for (int b = 0; b < buffers.Length; b++)
                            {
                                var commandBuffer = buffers.GetValue(b);
                                if (commandBuffer == null) continue;
                                found++;

                                string bufferName = null;
                                try
                                {
                                    var nameProperty = commandBuffer.GetType().GetProperty("name");
                                    if (nameProperty != null) bufferName = nameProperty.GetValue(commandBuffer) as string;
                                }
                                catch (Exception) { }

                                buffer.Append("   - cam='").Append(camera.name).Append("' event=").Append(cameraEvent)
                                      .Append(" buffer='").Append(bufferName ?? "?").Append("'\n");
                            }
                        }
                    }
                    if (found == 0) buffer.Append("   (无)\n");
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"相机 CommandBuffer 扫描失败: {ex.Message}");
            }
        }

private static string GetSceneName(GameObject go)
        {
            try
            {
                if (go == null) return "?";
                var scene = go.scene;
                return string.IsNullOrEmpty(scene.name) ? "<无场景>" : scene.name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

private bool ResolveGateTypes()
        {
            if (_gateTypesResolved) return _gateTypes != null && _gateTypes.Length > 0;
            if (Time.time < _gateResolveRetryAt) return false;

            var keywords = _settings.MosaicGateMemberArray;
            if (keywords == null || keywords.Length == 0)
            {
                _gateResolveRetryAt = Time.time + 5f;
                return false;
            }

            var found = new List<Type>(2);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                var assembly = assemblies[a];
                if (assembly.IsDynamic) continue;

                string assemblyName;
                try { assemblyName = assembly.GetName().Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(assemblyName) || IsFrameworkAssembly(assemblyName)) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                for (int i = 0; i < types.Length; i++)
                {
                    var type = types[i];
                    if (type == null || type.IsAbstract || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                    if (!HasGateMember(type, keywords)) continue;
                    if (!found.Contains(type)) found.Add(type);
                }
            }

            if (found.Count == 0)
            {
                _gateResolveRetryAt = Time.time + 5f;
                if (!_gateTypesWarned)
                {
                    _gateTypesWarned = true;
                    _log.LogDebug($"暂未找到马赛克门控组件（成员签名 [{string.Join(",", keywords)}]），将持续重试。");
                }
                return false;
            }

            _gateTypes = found.ToArray();
            _gateTypesResolved = true;
            var names = new string[_gateTypes.Length];
            for (int i = 0; i < _gateTypes.Length; i++) names[i] = _gateTypes[i].FullName;
            _log.LogInfo($"马赛克门控组件已就绪: {string.Join(", ", names)}");
            return true;
        }

private static bool HasGateMember(Type type, string[] keywords)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            MethodInfo[] methods = null;
            try { methods = type.GetMethods(flags); } catch (Exception) { }
            if (methods != null)
            {
                for (int i = 0; i < methods.Length; i++)
                {
                    string name = methods[i].Name;
                    if (name.StartsWith("get_", StringComparison.Ordinal)) name = name.Substring(4);
                    else if (name.StartsWith("set_", StringComparison.Ordinal)) name = name.Substring(4);
                    if (MosaicDetector.ContainsAny(name, keywords)) return true;
                }
            }

            FieldInfo[] fields = null;
            try { fields = type.GetFields(flags); } catch (Exception) { }
            if (fields != null)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].Name.StartsWith("Native", StringComparison.Ordinal)) continue;
                    if (MosaicDetector.ContainsAny(fields[i].Name, keywords)) return true;
                }
            }
            return false;
        }

private void AppendMosaicGates(StringBuilder buffer, ref int reported)
        {
            if (!ResolveGateTypes()) return;

            var keywords = _settings.MosaicGateMemberArray;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            for (int t = 0; t < _gateTypes.Length; t++)
            {
                var type = _gateTypes[t];
                try
                {
#if DEMOSAIC_IL2CPP
                    var instances = Object.FindObjectsOfType(Il2CppType.From(type));
#else
                    var instances = Object.FindObjectsOfType(type, true);
#endif
                    if (instances == null) continue;

                    MethodInfo[] methods = null;
                    FieldInfo[] fields = null;
                    try { methods = type.GetMethods(flags); } catch (Exception) { }
                    try { fields = type.GetFields(flags); } catch (Exception) { }

                    for (int i = 0; i < instances.Length; i++)
                    {
                        var target = AsConcreteType(instances[i], type);
                        if (target == null) continue;

                        reported++;
                        var behaviour = RuntimeInterop.AsMonoBehaviour(target);
                        string goName = behaviour != null && behaviour.gameObject != null ? behaviour.gameObject.name : "?";
                        buffer.Append("[Demosaic Gate] ").Append(type.FullName).Append(" on '").Append(goName).Append('\'');

                        // ① 门控组件自己的开关/参数
                        if (methods != null)
                        {
                            for (int m = 0; m < methods.Length; m++)
                            {
                                var getter = methods[m];
                                if (!getter.Name.StartsWith("get_", StringComparison.Ordinal)) continue;
                                if (getter.GetParameters().Length != 0 || getter.ReturnType == typeof(void)) continue;

                                string member = getter.Name.Substring(4);
                                if (!MosaicDetector.ContainsAny(member, keywords) && !MosaicDetector.ContainsAny(member, CandidatePropertyKeywords)) continue;

                                object value = null;
                                try { value = getter.Invoke(target, null); } catch (Exception) { continue; }
                                AppendMemberOrList(buffer, member, value, GetRendererList(methods, member));
                            }
                        }

                        // ② 顺带把该组件身上所有 MaterialPropertyBlock 式的参数对象也打出来
                        if (fields != null)
                        {
                            for (int f = 0; f < fields.Length; f++)
                            {
                                var field = fields[f];
                                if (field.IsStatic || field.IsLiteral) continue;
                                if (field.Name.StartsWith("Native", StringComparison.Ordinal)) continue;
                                if (!MosaicDetector.ContainsAny(field.Name, keywords)) continue;
                                try { AppendMemberOrList(buffer, field.Name + "(field)", field.GetValue(target), null); }
                                catch (Exception) { }
                            }
                        }

                        buffer.Append('\n');
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"门控组件导出失败 ({type.Name}): {ex.Message}");
                }
            }
        }

private static Type GetRendererList(MethodInfo[] methods, string member)
        {
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "get_" + member) continue;
                var t = methods[i].ReturnType;
                if (t.IsGenericType && t.GetGenericArguments().Length == 1)
                    return t.GetGenericArguments()[0];
                return null;
            }
            return null;
        }

private static void AppendMemberOrList(StringBuilder buffer, string name, object value, Type elementType)
        {
            if (value == null)
            {
                buffer.Append(" | ").Append(name).Append("=null");
                return;
            }

            var valueType = value.GetType();
            PropertyInfo countProperty = null;
            PropertyInfo itemProperty = null;
            try
            {
                countProperty = valueType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                itemProperty = valueType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance,
                    null, null, new[] { typeof(int) }, null);
            }
            catch (Exception) { }

            if (countProperty == null || itemProperty == null || countProperty.PropertyType != typeof(int))
            {
                // 嵌套对象（如 DressController.shaderParameters）：展开一层，看它到底带了什么参数
                var valueType2 = value.GetType();
                if (!(value is string) && !valueType2.IsPrimitive && !valueType2.IsEnum && !(value is UnityEngine.Object))
                {
                    PropertyInfo[] subProperties = null;
                    try { subProperties = valueType2.GetProperties(BindingFlags.Public | BindingFlags.Instance); }
                    catch (Exception) { }
                    if (subProperties != null && subProperties.Length > 0)
                    {
                        buffer.Append(" | ").Append(name).Append("={");
                        int printed = 0;
                        for (int i = 0; i < subProperties.Length && printed < 12; i++)
                        {
                            var sub = subProperties[i];
                            if (!sub.CanRead || sub.GetIndexParameters().Length != 0) continue;
                            object subValue;
                            try { subValue = sub.GetValue(value); }
                            catch (Exception) { continue; }
                            if (printed > 0) buffer.Append(", ");
                            buffer.Append(sub.Name).Append('=').Append(subValue);
                            printed++;
                        }
                        buffer.Append('}');
                        return;
                    }
                }

                buffer.Append(" | ").Append(name).Append('=').Append(value);
                return;
            }

            int count;
            try { count = (int)countProperty.GetValue(value); }
            catch (Exception) { count = -1; }

            buffer.Append(" | ").Append(name).Append("(列表 ").Append(count).Append(" 项)=");
            if (count <= 0) return;

            for (int i = 0; i < count && i < 16; i++)
            {
                object element;
                try { element = itemProperty.GetValue(value, new object[] { i }); }
                catch (Exception ex) { buffer.Append(" <第").Append(i).Append("项读取失败:").Append(ex.GetType().Name).Append('>'); break; }

                buffer.Append("\n      [").Append(i).Append("] ");
                var renderer = element as Renderer;
                if (renderer != null)
                {
                    buffer.Append("Renderer '").Append(renderer.name).Append('\'');
                    AppendRendererState(buffer, renderer, CandidatePropertyKeywords);
                    buffer.Append('\n');
                    continue;
                }

                if (element is UnityEngine.Object unityObject) buffer.Append(unityObject.name);
                else buffer.Append(element);
                buffer.Append('\n');
            }
        }

private static void AppendRendererState(StringBuilder buffer, Renderer renderer, string[] propertyKeywords)
        {
            if (HasPropertyBlock(renderer)) buffer.Append(" 有MPB");
            var materials = RuntimeInterop.GetSharedMaterials(renderer);
            if (materials == null) return;

            for (int i = 0; i < materials.Length && i < 4; i++)
            {
                var material = materials[i];
                if (material == null) continue;
                buffer.Append(" | mat[").Append(i).Append("]=").Append(material.name)
                      .Append(" shader=").Append(material.shader != null ? material.shader.name : "N/A")
                      .Append(" kw=[").Append(JoinShaderKeywords(material)).Append(']');
                AppendMaterialPropertyProbes(buffer, material, propertyKeywords);
            }
        }

private void AppendLoadedShaderVariants(StringBuilder buffer)
        {
            var keywords = _settings.MosaicMaterialShaderArray;
            if (keywords == null || keywords.Length == 0) return;

            try
            {
#if DEMOSAIC_IL2CPP
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.From(typeof(Shader)));
#else
                var all = Resources.FindObjectsOfTypeAll(typeof(Shader));
#endif
                if (all == null) return;

                buffer.Append($"\n[Demosaic Shader] 内存中已加载 Shader {all.Length} 个，名称含 [lidpatched/pacone/mosaic] 的变体：\n");
                int found = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    // ⚠ IL2CPP 下 interop 数组元素是按声明类型 UnityObject 构造的，直接 as Shader 会全是 null
                    var shader = AsConcreteType(all[i], typeof(Shader)) as Shader;
                    if (shader == null) continue;

                    string shaderName = shader.name;
                    if (!MosaicDetector.ContainsAny(shaderName, keywords) &&
                        !MosaicDetector.ContainsAny(shaderName, ExtraShaderNameKeywords)) continue;

                    found++;
                    int propCount = -1;
                    try { propCount = shader.GetPropertyCount(); } catch (Exception) { }
                    buffer.Append("   - ").Append(shaderName)
                          .Append("  (Shader 属性 ").Append(propCount)
                          .Append(" 个, InstanceID ").Append(shader.GetInstanceID()).Append(")\n");
                }
                if (found == 0) buffer.Append("   (无)\n");
            }
            catch (Exception ex)
            {
                _log.LogDebug($"枚举 Shader 变体失败: {ex.Message}");
            }
        }


        private static readonly string[] CandidatePropertyKeywords = { "mosaic", "pixel", "censor", "moza" };
        private static readonly string[] ExtraShaderNameKeywords = { "pacone", "mosaic" };
        private float _gateResolveRetryAt;
        private Type[] _gateTypes;
        private bool _gateTypesResolved;
        private bool _gateTypesWarned;
    }
}
