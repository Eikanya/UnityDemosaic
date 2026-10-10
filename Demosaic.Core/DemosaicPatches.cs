using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DemosaicPlugin
{
    /// <summary>
    /// Harmony 补丁入口。SetActive 用特性声明（由 PatchAll 统一应用），
    /// 其余需要遍历重载的方法在引擎里手动安装。
    /// </summary>
    internal static class DemosaicPatches
    {
        /// <summary>
        /// 马赛克属性"写入拦截"：Hook Material.SetFloat/SetInt/SetColor/SetVector，
        /// 凡是属性名命中关键词的写入**直接不执行**（返回 false 跳过原方法）。
        /// 用于游戏每帧把马赛克参数写回来的情况 —— 与其每秒抢一次，不如让它的写入落空。
        /// </summary>
 
        /// <summary>
        /// 绘制调用探针：Hook Graphics.DrawMesh*/RenderMesh* 与 Shader.SetGlobal*，
        /// 把「谁在画什么（mesh/material/shader）」与「谁设了什么全局值」打到日志（按组合去重、有上限）。
        /// 用于定位没有任何可识别名字的绘制者 / 全局参数的下发方。
        /// </summary>
 
        public static void InstantiatePostfix(Object __result)
        {
            var engine = DemosaicEngine.Instance;
            if (engine == null || __result == null) return;

            try
            {
                var go = RuntimeInterop.AsGameObject(__result);
                if (go == null)
                {
                    var component = RuntimeInterop.AsComponent(__result);
                    if (component != null) go = component.gameObject;
                }

                if (go != null) engine.ProcessGameObject(go);
            }
            catch (System.Exception ex)
            {
                DemosaicEngine.Instance?.Log?.LogError($"处理实例化对象错误: {ex}");
            }
        }

        public static void MaterialSetterPostfix(Renderer __instance)
        {
            DemosaicEngine.Instance?.OnMaterialAssigned(__instance);
        }

        /// <summary>条件性 Prefix：插件激活时返回 false 拦截原方法，插件缺席则放行。</summary>
        public static bool GenericDisablePrefix()
        {
            return DemosaicEngine.Instance == null;
        }
    }

    [HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive))]
    internal static class GameObjectSetActivePatch
    {
        public static void Postfix(GameObject __instance, bool value)
        {
            if (!value) return;
            var engine = DemosaicEngine.Instance;
            if (engine == null) return;

            try
            {
                engine.ProcessGameObject(__instance);
            }
            catch (System.Exception ex)
            {
                DemosaicEngine.Instance?.Log?.LogError($"处理 GameObject '{__instance.name}' 时出错: {ex}");
            }
        }
    }
}
