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
    /// DemosaicEngine 的开关组件归零部分（partial class，字段与主文件共享）。
    /// </summary>
    internal sealed partial class DemosaicEngine
    {
private bool ResolveSwitchTypes()
        {
            if (_switchTypesResolved)
                return _switchTypes != null && _switchTypes.Length > 0;
            if (Time.time < _switchResolveRetryAt)
                return false;

            var keywords = _settings.MosaicSwitchTypeKeywordArray;
            if (keywords == null || keywords.Length == 0)
            {
                _switchResolveRetryAt = Time.time + 5f;
                if (!_switchTypesWarned)
                {
                    _switchTypesWarned = true;
                    _log.LogDebug("未配置马赛克开关组件类型（MosaicSwitchComponentTypes），组件归零功能跳过。");
                }
                return false;
            }

            var found = new List<Type>(4);
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
                    if (!MosaicDetector.ContainsAny(type.Name, keywords) && !MosaicDetector.ContainsAny(type.FullName, keywords)) continue;
                    if (!found.Contains(type)) found.Add(type);
                }
            }

            if (found.Count == 0)
            {
                // 程序集可能还没加载完（插件刚启动时），过几秒再试，避免永久放弃
                _switchResolveRetryAt = Time.time + 5f;
                if (!_switchTypesWarned)
                {
                    _switchTypesWarned = true;
                    _log.LogDebug($"暂未找到马赛克开关组件类型 [{string.Join(",", keywords)}]，将持续重试。");
                }
                return false;
            }

            _switchTypes = found.ToArray();
            _switchTypesResolved = true;

            var names = new string[_switchTypes.Length];
            for (int i = 0; i < _switchTypes.Length; i++) names[i] = _switchTypes[i].FullName;
            _log.LogInfo($"马赛克开关组件已就绪: {string.Join(", ", names)}");
            return true;
        }

private static bool IsFrameworkAssembly(string assemblyName)
        {
            for (int i = 0; i < FrameworkAssemblyPrefixes.Length; i++)
            {
                if (assemblyName.StartsWith(FrameworkAssemblyPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

private void ResetSwitchTypeCache()
        {
            _switchTypes = null;
            _switchTypesResolved = false;
            _switchTypesWarned = false;
            _switchResolveRetryAt = 0f;
            _switchLoggedIds.Clear();
            _switchInstanceCache.Clear();
            _switchInstancesRefreshedAt = -1f;
            _switchFieldPlanCache.Clear();
        }

internal void ProcessMosaicSwitches()
        {
            if (!_settings.EnableMosaicSwitchOff.Value) return;
            if (!ResolveSwitchTypes()) return;

            for (int t = 0; t < _switchTypes.Length; t++)
            {
                var type = _switchTypes[t];
                try
                {
                    var instances = GetSwitchInstances(type);
                    if (instances == null) continue;

                    for (int i = 0; i < instances.Length; i++)
                    {
                        var target = AsConcreteType(instances[i], type);
                        if (target == null)
                        {
                            _log.LogDebug($"{type.Name} 实例无法还原成具体代理类型（raw: {instances[i]?.GetType().Name}），跳过。");
                            continue;
                        }

                        // 组件的目标 Renderer 上可能直接写着马赛克属性，改完组件立刻把材质也压一遍
                        var targetRenderer = TryGetTargetRenderer(target, type);
                        if (targetRenderer != null) TrackMaterialFixTarget(targetRenderer);

                        DisableMosaicSwitchOn(target, type);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"马赛克开关组件扫描跳过 ({type.Name}): {ex.Message}");
                }
            }
        }

private static void AddChange(ref List<string> list, string name, object before, object after)
        {
            list = list ?? new List<string>(4);
            list.Add(string.Concat(name, " ", FormatSwitchValue(before), "→", FormatSwitchValue(after)));
        }

private static string FormatSwitchValue(object value)
        {
            if (value == null) return "null";
            if (value is Color color) return string.Concat("a=", color.a);
            return value.ToString();
        }

private static bool TryComputeSwitchValue(Type valueType, object current, float targetAlpha, out object replacement)
        {
            replacement = null;

            if (valueType == typeof(bool))
            {
                if (!(current is bool on) || !on) return false;
                replacement = false;
                return true;
            }

            if (valueType == typeof(float))
            {
                if (!(current is float number) || number == 0f) return false;
                replacement = 0f;
                return true;
            }

            if (valueType == typeof(int))
            {
                if (!(current is int number) || number == 0) return false;
                replacement = 0;
                return true;
            }

            if (valueType == typeof(Color))
            {
                if (!(current is Color color) || Mathf.Approximately(color.a, targetAlpha)) return false;
                color.a = targetAlpha;
                replacement = color;
                return true;
            }

            return false;
        }

private string InvokeSwitchApplyMethods(object target, Type type)
        {
            var names = _settings.MosaicSwitchMethodArray;
            if (names == null || names.Length == 0) return "未配置同步方法";

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var result = new StringBuilder(96);

            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    var method = type.GetMethod(names[i], flags, null, Type.EmptyTypes, null);
                    if (method == null)
                    {
                        result.Append(names[i]).Append("=无此方法 ");
                        continue;
                    }

                    var returned = method.Invoke(target, null);
                    result.Append(names[i]).Append("()=OK");
                    if (returned != null) result.Append('(').Append(returned).Append(')');
                    result.Append(' ');
                }
                catch (Exception ex)
                {
                    result.Append(names[i]).Append("()=失败:").Append(ex.InnerException != null ? ex.InnerException.Message : ex.Message).Append(' ');
                }
            }

            return result.ToString().TrimEnd();
        }

private static Renderer TryGetTargetRenderer(object target, Type type)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var getter = type.GetMethod("GetTargetRenderer", flags, null, Type.EmptyTypes, null);
                var renderer = getter != null ? getter.Invoke(target, null) as Renderer : null;
                if (renderer != null) return renderer;

                var behaviour = RuntimeInterop.AsMonoBehaviour(target);
                return behaviour != null ? behaviour.GetComponent<Renderer>() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }


        private Type[] _switchTypes;
        private bool _switchTypesResolved;
        private bool _switchTypesWarned;
        private float _switchResolveRetryAt;
        private float _switchTimer;
        private bool _switchDirty;
        private readonly HashSet<int> _switchLoggedIds = new HashSet<int>();
        // 目标组件实例列表缓存：FindObjectsOfType 是全场景遍历，没必要每轮（默认 1 秒）都做一次
        private readonly Dictionary<Type, object[]> _switchInstanceCache = new Dictionary<Type, object[]>();
        private float _switchInstancesRefreshedAt = -1f;
        private const float SwitchInstanceRefreshSeconds = 2f;


        /// <summary>
        /// 取目标组件的场景实例（带缓存，默认 2 秒刷新一次）。
        /// <c>FindObjectsOfType</c> 是全场景遍历，而开关复查默认每 1 秒跑一轮 —— 没必要每轮都扫。
        /// 新生成的组件最多晚一个刷新窗口被处理，可接受。
        /// </summary>
        private object[] GetSwitchInstances(Type type)
        {
            bool refresh = _switchDirty ||
                           _switchInstancesRefreshedAt < 0f ||
                           Time.time - _switchInstancesRefreshedAt >= SwitchInstanceRefreshSeconds;

            if (!refresh && _switchInstanceCache.TryGetValue(type, out var cached))
                return cached;

            if (refresh)
            {
                _switchInstanceCache.Clear();
                _switchInstancesRefreshedAt = Time.time;
            }

            object[] result = null;
            try
            {
#if DEMOSAIC_IL2CPP
                var instances = Object.FindObjectsOfType(Il2CppType.From(type), true);
#else
                var instances = Object.FindObjectsOfType(type, true);
#endif
                if (instances != null)
                {
                    result = new object[instances.Length];
                    for (int i = 0; i < instances.Length; i++)
                        result[i] = instances[i];
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug($"枚举 {type.Name} 实例失败: {ex.Message}");
            }

            _switchInstanceCache[type] = result;
            return result;
        }


        /// <summary>开关组件的一次性反射计划：命中关键词的可写成员。按类型缓存，避免每轮重做 GetMethods/GetProperties/GetFields。</summary>
        private sealed class SwitchFieldPlan
        {
            public MethodInfo[] GetterAccessors;
            public MethodInfo[] SetterAccessors;
            public PropertyInfo[] Properties;
            public FieldInfo[] Fields;
        }

        private readonly Dictionary<Type, SwitchFieldPlan> _switchFieldPlanCache = new Dictionary<Type, SwitchFieldPlan>();

        /// <summary>构建（并缓存）某个开关类型的成员计划；关键词变化时由 ResetSwitchTypeCache 统一失效。</summary>
        private SwitchFieldPlan GetSwitchFieldPlan(Type type)
        {
            if (_switchFieldPlanCache.TryGetValue(type, out var cached)) return cached;

            var keywords = _settings.MosaicSwitchFieldKeywordArray;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // ① 访问器方法（首选）：Il2CppInterop 对 il2cpp 字段/属性都会生成 get_/set_ 这一对，
            //    方法名始终可靠；而 Property 表里的名字在某些程序集会被写成 <数字> 占位名，按属性名匹配会漏。
            var getters = new List<MethodInfo>();
            var setters = new List<MethodInfo>();
            MethodInfo[] methods = null;
            try { methods = type.GetMethods(flags); }
            catch (Exception ex) { _log.LogDebug($"读取 {type.Name} 方法失败: {ex.Message}"); }

            if (methods != null)
            {
                for (int i = 0; i < methods.Length; i++)
                {
                    var getter = methods[i];
                    if (!getter.Name.StartsWith("get_", StringComparison.Ordinal)) continue;
                    if (getter.GetParameters().Length != 0) continue;
                    if (getter.ReturnType == typeof(void)) continue;

                    string member = getter.Name.Substring(4);
                    if (!MosaicDetector.ContainsAny(member, keywords)) continue;

                    MethodInfo setter = null;
                    try { setter = type.GetMethod("set_" + member, flags, null, new[] { getter.ReturnType }, null); }
                    catch (Exception) { }
                    if (setter == null) continue;

                    getters.Add(getter);
                    setters.Add(setter);
                }
            }

            // ② 属性与字段（兜底）：覆盖不走访问器、而是直接暴露成属性/字段的 interop 版本。
            var properties = new List<PropertyInfo>();
            PropertyInfo[] rawProperties = null;
            try { rawProperties = type.GetProperties(flags); }
            catch (Exception ex) { _log.LogDebug($"读取 {type.Name} 属性失败: {ex.Message}"); }

            if (rawProperties != null)
            {
                for (int i = 0; i < rawProperties.Length; i++)
                {
                    var property = rawProperties[i];
                    if (!property.CanRead || !property.CanWrite) continue;
                    if (property.GetIndexParameters().Length != 0) continue;
                    if (!MosaicDetector.ContainsAny(property.Name, keywords)) continue;
                    properties.Add(property);
                }
            }

            var fields = new List<FieldInfo>();
            FieldInfo[] rawFields = null;
            try { rawFields = type.GetFields(flags); }
            catch (Exception ex) { _log.LogDebug($"读取 {type.Name} 字段失败: {ex.Message}"); }

            if (rawFields != null)
            {
                for (int i = 0; i < rawFields.Length; i++)
                {
                    var field = rawFields[i];
                    if (field.IsInitOnly || field.IsLiteral || field.IsStatic) continue;
                    if (!MosaicDetector.ContainsAny(field.Name, keywords)) continue;
                    fields.Add(field);
                }
            }

            var plan = new SwitchFieldPlan
            {
                GetterAccessors = getters.ToArray(),
                SetterAccessors = setters.ToArray(),
                Properties = properties.ToArray(),
                Fields = fields.ToArray()
            };
            _switchFieldPlanCache[type] = plan;
            return plan;
        }

        /// <summary>把单个组件上命中关键词的开关字段归零，并同步给材质。返回是否真的改了东西。</summary>
        private bool DisableMosaicSwitchOn(object target, Type type)
        {
            var keywords = _settings.MosaicSwitchFieldKeywordArray;
            if (keywords == null || keywords.Length == 0) return false;

            var plan = GetSwitchFieldPlan(type);
            float targetAlpha = _settings.MosaicSwitchColorAlpha.Value;
            bool changed = false;
            List<string> changedFields = null;

            for (int i = 0; i < plan.GetterAccessors.Length; i++)
            {
                var getter = plan.GetterAccessors[i];
                var setter = plan.SetterAccessors[i];
                string member = getter.Name.Substring(4);

                try
                {
                    object current = getter.Invoke(target, null);
                    if (!TryComputeSwitchValue(getter.ReturnType, current, targetAlpha, out var replacement))
                        continue;

                    setter.Invoke(target, new[] { replacement });
                    changed = true;
                    AddChange(ref changedFields, member, current, replacement);
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"马赛克开关写入失败 {type.Name}.{member}: {ex.Message}");
                }
            }

            for (int i = 0; i < plan.Properties.Length; i++)
            {
                var property = plan.Properties[i];
                try
                {
                    object current = property.GetValue(target);
                    if (!TryComputeSwitchValue(property.PropertyType, current, targetAlpha, out var replacement))
                        continue;

                    property.SetValue(target, replacement);
                    changed = true;
                    AddChange(ref changedFields, property.Name, current, replacement);
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"马赛克开关写入失败 {type.Name}.{property.Name}: {ex.Message}");
                }
            }

            for (int i = 0; i < plan.Fields.Length; i++)
            {
                var field = plan.Fields[i];
                try
                {
                    object current = field.GetValue(target);
                    if (!TryComputeSwitchValue(field.FieldType, current, targetAlpha, out var replacement))
                        continue;

                    field.SetValue(target, replacement);
                    changed = true;
                    AddChange(ref changedFields, field.Name, current, replacement);
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"马赛克开关写入失败 {type.Name}.{field.Name}: {ex.Message}");
                }
            }
            if (!changed) return false;

            string applyResult = InvokeSwitchApplyMethods(target, type);
            var behaviour = RuntimeInterop.AsMonoBehaviour(target);

            // 实验开关：有些系统把马赛克编译进运行时 patch 出来的 shader 变体，只改字段/材质没用。
            // 关一次再开一次组件，逼它按新设置重新 patch / 重新推值。
            if (_settings.MosaicForceRepatch.Value && behaviour != null)
            {
                try
                {
                    behaviour.enabled = false;
                    behaviour.enabled = true;
                    applyResult += " | 已强制重 patch";
                }
                catch (Exception ex)
                {
                    applyResult += $" | 强制重 patch 失败: {ex.Message}";
                }
            }

            int id = behaviour != null ? behaviour.GetInstanceID() : 0;
            if (!_switchLoggedIds.Contains(id))
            {
                _switchLoggedIds.Add(id);
                string goName = behaviour != null && behaviour.gameObject != null ? behaviour.gameObject.name : "?";
                _log.LogInfo($"已关闭组件马赛克开关: {type.Name} on '{goName}' [{string.Join(", ", changedFields)}]" +
                             $" | 同步: {applyResult}");
            }
            return true;
        }
    }
}
