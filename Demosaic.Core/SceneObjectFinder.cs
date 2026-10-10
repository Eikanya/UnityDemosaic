using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;
#if DEMOSAIC_IL2CPP
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace DemosaicPlugin
{
    /// <summary>
    /// 场景对象查找。优先使用 Unity 2022+ 的 FindObjectsByType（支持未激活对象），
    /// 旧版本自动回退到 FindObjectsOfType。返回值统一为托管 List，屏蔽 Mono/IL2CPP 的数组差异。
    /// </summary>
    internal static class SceneObjectFinder
    {
        private static readonly Dictionary<Type, MethodInfo> GenericFindCache = new Dictionary<Type, MethodInfo>();
        private static MethodInfo _genericFindDefinition;
        private static object _includeInactive;
        private static object _excludeInactive;
        private static object _sortNone;
        private static bool _initialized;

        public static List<T> FindObjects<T>(bool includeInactive) where T : Object
        {
            var list = new List<T>();
            FillObjects(list, includeInactive);
            return list;
        }

        public static int FillRenderers(List<Renderer> into, bool includeInactive)
        {
            return FillObjects(into, includeInactive);
        }

        /// <summary>把结果直接追加进调用方复用的列表，避免每轮扫描都新建一个中间集合。</summary>
        public static int FillObjects<T>(List<T> into, bool includeInactive) where T : Object
        {
            Initialize();

            object raw = null;
            bool usedFastPath = false;

            if (_genericFindDefinition != null)
            {
                MethodInfo closed = null;
                lock (GenericFindCache)
                {
                    if (!GenericFindCache.TryGetValue(typeof(T), out closed))
                    {
                        try
                        {
                            closed = _genericFindDefinition.MakeGenericMethod(typeof(T));
                        }
                        catch
                        {
                            closed = null;
                        }
                        GenericFindCache[typeof(T)] = closed;
                    }
                }

                if (closed != null)
                {
                    try
                    {
                        raw = closed.Invoke(null, new[] { includeInactive ? _includeInactive : _excludeInactive, _sortNone });
                        usedFastPath = true;
                    }
                    catch
                    {
                        _genericFindDefinition = null;
                    }
                }
            }

            if (!usedFastPath)
                raw = Object.FindObjectsOfType<T>();

            int before = into.Count;
            if (FillFrom(into, raw) < 0)
                FillFrom(into, Object.FindObjectsOfType<T>());

            return into.Count - before;
        }

        /// <summary>追加结果，返回追加数量；类型无法解释时返回 -1，由调用方回退到旧接口。</summary>
        private static int FillFrom<T>(List<T> into, object result) where T : Object
        {
            if (result == null) return -1;
            int before = into.Count;

#if DEMOSAIC_IL2CPP
            var il2cppArray = result as Il2CppArrayBase<T>;
            if (il2cppArray != null)
            {
                for (int i = 0; i < il2cppArray.Length; i++)
                    into.Add(il2cppArray[i]);
                return into.Count - before;
            }
#endif
            var array = result as T[];
            if (array != null)
            {
                for (int i = 0; i < array.Length; i++)
                    into.Add(array[i]);
                return into.Count - before;
            }

            return -1;
        }

        private static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            var objectType = typeof(Object);
            var inactiveType = objectType.Assembly.GetType("UnityEngine.FindObjectsInactive");
            var sortModeType = objectType.Assembly.GetType("UnityEngine.FindObjectsSortMode");
            if (inactiveType == null || sortModeType == null) return;

            try
            {
                _includeInactive = Enum.Parse(inactiveType, "Include");
                _excludeInactive = Enum.Parse(inactiveType, "Exclude");
                _sortNone = Enum.Parse(sortModeType, "None");
            }
            catch
            {
                return;
            }

            _genericFindDefinition = objectType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "FindObjectsByType" &&
                                 method.IsGenericMethodDefinition &&
                                 method.GetParameters().Length == 2)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters[0].ParameterType == inactiveType &&
                           parameters[1].ParameterType == sortModeType;
                });
        }
    }
}
