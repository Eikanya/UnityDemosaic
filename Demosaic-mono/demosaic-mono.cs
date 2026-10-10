using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using UnityEngine;

namespace DemosaicPlugin
{
    /// <summary>
    /// Mono 变体入口。全部业务逻辑位于 <c>Demosaic.Core</c>（共享源码），
    /// 本类只负责创建引擎、每帧驱动、安装/卸载补丁。
    /// </summary>
    [BepInPlugin("demosaic", "Demosaic", DemosaicVersion.Value)]
    public sealed class DemosaicMonoPlugin : BaseUnityPlugin
    {
        private DemosaicEngine _engine;
        private Harmony _harmony;

        private void Awake()
        {
            _engine = DemosaicEngine.Create(Logger, Config);
            _harmony = new Harmony("demosaic.mono");
            _engine.InstallPatches(_harmony);
        }

        private void Update()
        {
            _engine?.Update(Time.deltaTime);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _engine?.Dispose();
        }
    }
}
