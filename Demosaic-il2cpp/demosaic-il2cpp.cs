using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DemosaicPlugin
{
    /// <summary>
    /// IL2CPP 变体入口。全部业务逻辑位于 <c>Demosaic.Core</c>（共享源码），
    /// 本类只负责创建引擎、注入生命周期组件、安装/卸载补丁。
    /// </summary>
    [BepInPlugin("demosaic", "Demosaic", DemosaicVersion.Value)]
    public sealed class DemosaicIl2CppPlugin : BasePlugin
    {
        private DemosaicEngine _engine;
        private Harmony _harmony;
        private PluginLifecycleManager _lifecycle;

        public override void Load()
        {
            _engine = DemosaicEngine.Create(Log, Config);

            ClassInjector.RegisterTypeInIl2Cpp<PluginLifecycleManager>();
            _lifecycle = AddComponent<PluginLifecycleManager>();

            _harmony = new Harmony("demosaic.il2cpp");
            _engine.InstallPatches(_harmony);
        }

        public override bool Unload()
        {
            _harmony?.UnpatchSelf();

            if (_lifecycle != null)
            {
                Object.Destroy(_lifecycle.gameObject);
                _lifecycle = null;
            }

            _engine?.Dispose();
            return base.Unload();
        }
    }

    /// <summary>IL2CPP 没有 BaseUnityPlugin 的 Update，需要注入一个 MonoBehaviour 驱动引擎。</summary>
    public class PluginLifecycleManager : MonoBehaviour
    {
        public PluginLifecycleManager(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            DemosaicEngine.Instance?.Update(Time.deltaTime);
        }
    }
}
