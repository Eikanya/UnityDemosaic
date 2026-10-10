using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace DemosaicPlugin
{
    /// <summary>
    /// 安全输入：优先旧版 Input，异常时降级到新版 Input System（反射）。
    /// 反射结果全部缓存，热路径上不再重复 GetValue/Invoke。
    /// </summary>
    internal static class SafeInput
    {
        internal static ManualLogSource Log;

        private static bool _legacyInputAvailable = true;
        private static bool _warned;

        private static bool _newInputInitialized;
        private static PropertyInfo _keyboardCurrentProp;
        private static MethodInfo _keyboardIndexer;
        private static PropertyInfo _keyWasPressedProp;
        private static PropertyInfo _keyIsPressedProp;
        private static Type _keyEnumType;

        private static readonly Dictionary<KeyCode, object> KeyEnumCache = new Dictionary<KeyCode, object>();
        // 只用于 isPressed 回退路径的边沿检测（记录"已经按住"的键）
        private static readonly HashSet<KeyCode> HeldKeys = new HashSet<KeyCode>();
        private static readonly Dictionary<KeyCode, object> KeyControlCache = new Dictionary<KeyCode, object>();

        public static bool GetKeyDown(KeyCode key)
        {
            if (_legacyInputAvailable)
            {
                try
                {
                    return Input.GetKeyDown(key);
                }
                catch (Exception ex)
                {
                    // 任何异常都不再向外抛（旧实现会对"非 Input System"的异常 rethrow，
                    // 那会让宿主每帧吃一个异常）；一律降级到反射路径，并只警告一次。
                    bool looksLikeNewInputSystem =
                        ex is InvalidOperationException ||
                        (ex.Message?.Contains("Input System") ?? false) ||
                        (ex.InnerException?.Message?.Contains("Input System") ?? false);

                    _legacyInputAvailable = false;
                    if (!_warned)
                    {
                        _warned = true;
                        Log?.LogWarning(looksLikeNewInputSystem
                            ? "检测到游戏使用新版 Input System，旧版 Input 已禁用，改用反射读取新输入系统。"
                            : $"旧版 Input 读取失败（{ex.GetType().Name}），已改走反射路径。");
                    }
                }
            }

            return TryNewInputSystemKeyDown(key);
        }

        private static bool TryNewInputSystemKeyDown(KeyCode key)
        {
            try
            {
                if (!_newInputInitialized)
                {
                    InitializeNewInputSystem();
                    _newInputInitialized = true;
                }

                if (_keyboardCurrentProp == null || _keyboardIndexer == null)
                    return false;
                if (_keyWasPressedProp == null && _keyIsPressedProp == null)
                    return false;

                var keyControl = GetKeyControl(key);
                if (keyControl == null) return false;

                if (_keyWasPressedProp != null)
                {
                    if (_keyWasPressedProp.GetValue(keyControl) is bool pressed) return pressed;
                }
                else if (_keyIsPressedProp != null)
                {
                    // isPressed 是电平信号：按住会一直是 true。这里自己补边沿检测，
                    // 否则按住 F10/F11 会每帧触发全量重扫 / 导出。
                    if (_keyIsPressedProp.GetValue(keyControl) is bool isPressed)
                    {
                        if (!isPressed)
                        {
                            HeldKeys.Remove(key);
                            return false;
                        }

                        return HeldKeys.Add(key);   // 首次按下返回 true，持续按住返回 false
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>缓存 KeyControl 实例，避免每帧反射 Invoke。</summary>
        private static object GetKeyControl(KeyCode key)
        {
            if (KeyControlCache.TryGetValue(key, out var cached))
                return cached;

            object result = null;
            var keyboard = _keyboardCurrentProp?.GetValue(null);
            if (keyboard != null)
            {
                var keyEnum = GetKeyEnumValue(key);
                if (keyEnum != null)
                    result = _keyboardIndexer.Invoke(keyboard, new[] { keyEnum });
            }

            KeyControlCache[key] = result;
            return result;
        }

        private static object GetKeyEnumValue(KeyCode key)
        {
            if (_keyEnumType == null) return null;
            if (!KeyEnumCache.TryGetValue(key, out var keyObj))
            {
                try
                {
                    keyObj = Enum.Parse(_keyEnumType, key.ToString(), true);
                }
                catch
                {
                    keyObj = null;
                }
                KeyEnumCache[key] = keyObj;
            }
            return keyObj;
        }

        private static void InitializeNewInputSystem()
        {
            try
            {
                Type keyboardType = null;
                _keyEnumType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name;
                    if (asmName == null) continue;
                    if (!asmName.Contains("InputSystem")) continue;

                    keyboardType = keyboardType ?? asm.GetType("UnityEngine.InputSystem.Keyboard");
                    _keyEnumType = _keyEnumType ?? asm.GetType("UnityEngine.InputSystem.Key");
                }

                if (keyboardType == null)
                    keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (_keyEnumType == null)
                    _keyEnumType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");

                if (keyboardType == null || _keyEnumType == null)
                {
                    Log?.LogWarning("未能找到 InputSystem.Keyboard / Key 类型，快捷键功能不可用。");
                    return;
                }

                _keyboardCurrentProp = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _keyboardIndexer = keyboardType.GetProperty("Item", new[] { _keyEnumType })?.GetGetMethod()
                                   ?? keyboardType.GetMethod("get_Item", new[] { _keyEnumType });

                var buttonControlType = FindButtonControlType();
                if (buttonControlType != null)
                {
                    _keyWasPressedProp = buttonControlType.GetProperty("wasPressedThisFrame");
                    _keyIsPressedProp = buttonControlType.GetProperty("isPressed");
                }

                if (_keyboardCurrentProp != null && _keyboardIndexer != null && (_keyWasPressedProp != null || _keyIsPressedProp != null))
                    Log?.LogInfo("新版 Input System 键盘支持初始化成功。");
                else
                    Log?.LogWarning("Input System Keyboard 初始化不完整，快捷键可能不可用。");
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"初始化新版 Input System 失败: {ex.Message}");
            }
        }

        private static Type FindButtonControlType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (asmName == null || !asmName.Contains("InputSystem")) continue;

                var type = asm.GetType("UnityEngine.InputSystem.Controls.ButtonControl")
                           ?? asm.GetType("UnityEngine.InputSystem.Controls.KeyControl");
                if (type != null) return type;
            }

            return Type.GetType("UnityEngine.InputSystem.Controls.ButtonControl, Unity.InputSystem")
                   ?? Type.GetType("UnityEngine.InputSystem.Controls.KeyControl, Unity.InputSystem");
        }
    }
}
