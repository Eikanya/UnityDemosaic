namespace DemosaicPlugin
{
    /// <summary>
    /// 马赛克移除方式。两个变体（Mono / IL2CPP）共享同一套枚举。
    /// </summary>
    public enum RemoveMode
    {
        /// <summary>禁用对象：性能最好，不破坏游戏原本的依赖和逻辑。</summary>
        Disable,

        /// <summary>物理销毁对象：可能导致其他引用该对象的脚本 NullReference。</summary>
        Destroy,

        /// <summary>替换全部材质为透明：对象仍在渲染管线中，有微小开销。</summary>
        Transparent,

        /// <summary>仅替换命中马赛克关键词的材质槽为透明，保留同对象上的非马赛克材质。</summary>
        Smart
    }
}
