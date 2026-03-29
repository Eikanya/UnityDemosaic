通用去马赛克插件 (Demosaic Plugin)

Unity 游戏中的马赛克查找，可以通过 AssetStudio 或 AssetRipper 解包查看游戏是否为动态马赛克，主要看 Texture 图片有没马赛克，如果没有基本可以去除。一般通过 shared、material、mesh 和 dll 函数加载马赛克。简单的可以搜索关键词，然后使用 UABE 去修改数值或者删除 mesh，但是这样效率不高，又不一定能准确去除。dll 函数可以使用 dnSpy 去修改。而对于复杂的动态生成或 IL2CPP 平台，则需要使用 BepInEx/Harmony 插件进行拦截。

这是一个为 Unity 游戏设计的 BepInEx 插件，旨在通过多种灵活的检测方式，自动且极速地移除游戏中的马赛克或审查效果。它具有高度的可配置性，并为 Mono 和 IL2CPP 两个脚本后端分别提供了极致优化的版本。

✨ 核心功能与终极性能优化

双平台极致适配: 提供独立的 Demosaic-Mono.dll 和 Demosaic-IL2CPP.dll，完美适配不同类型的 Unity 游戏。

🚀 三级缓存架构:

引入了基于 InstanceID 和 Type 的 Material、Shader、Component 三级字典缓存。

解决痛点：彻底解决了 IL2CPP 跨语言封送（C++ 到 C#）读取字符串时造成的性能灾难。复杂对象的检测时间从毫秒级降至 O(1) 极速跳过，即使同屏瞬间生成上千发子弹或粒子，游戏也丝般顺滑。

🛡️ 零内存泄漏与 GC 优化:

彻底修复了 Unity 引擎中调用 .materials 会隐式无限克隆材质导致内存爆炸的致命问题，全面安全替换为 .sharedMaterials。

(Mono版) 全面复用 List<T> 和 WaitForSeconds 缓存，消除扫描期间的 GC (垃圾回收) 分配，告别周期性微卡顿。

高效的事件驱动扫描: 通过实时挂钩（Hook） GameObject.SetActive 和 UnityEngine.Object.Instantiate，无论是场景预设还是动态生成的对象，都能被立即拦截处理。

无痛分批扫描机制:

Mono版：通过后台协程分多帧执行全场景扫描。

IL2CPP版：重构了专用的状态机（State Machine）分批扫描器，完美替代 IL2CPP 下容易报错的协程，彻底杜绝 F10 扫描或加载场景时的主线程卡死。

多维度、深层次的检测系统:

可通过游戏对象、材质、着色器、网格和纹理的名称来识别马赛克。

组件名称检测: 支持模糊匹配超长混淆脚本名（如 MaleSonsMosaicActivation...）。

着色器属性分析: 深入分析材质所使用的着色器内部属性（如 _PixelSize, _BlockSize）。

高级代码拦截 (反作弊/反审查): 支持动态反射扫描目标程序集，直接瘫痪特定方法（如 ApplyCensorEffect）的执行。提供 [HarmonyPatch] 模板供开发者精准爆破。

🛠️ 原理与方法 (How it Works)

本插件通过一套多层级、事件驱动的策略来确保高效且全面地移除马赛克，同时将性能影响降至最低。

1. 检测原理：基于关键词与缓存的识别

插件的核心是关键词匹配，但辅以极速的缓存拦截。它会检查以下资源的名称：

游戏对象 (GameObject)

材质 (Material) & 纹理 (Texture)

着色器 (Shader) 及其内部属性

网格 (Mesh)

组件/脚本类名 (Component Type)

2. 移除方法：多种处理方式

一旦检测到目标，插件会根据您的配置执行以下操作之一：

Disable (禁用 - 🌟强烈推荐): 直接禁用包含马赛克渲染器的游戏对象。性能最好，最安全，不会打断可能关联的游戏逻辑，也不会污染全局资产。

Transparent (透明): 将对象的材质替换为一个共享的、完全透明的隐形材质。适用于想保留脚本运行但隐藏模型外观的特殊情况。(注：已优化为不污染全局资产的安全替换模式)。

Destroy (销毁 - 不推荐): 从内存中彻底删除对象。有概率导致引用该对象的其他游戏代码报错 NullReferenceException。

3. 多层保障扫描体系

第一层：底层实时 Hook (最高优先级)：拦截引擎底层生成与激活事件，极速判定。

第二层：场景加载扫描：新场景加载时自动排队。

第三层：延迟二次扫描：捕获异步加载的“迟到”资源。

第四层：后台周期性扫描：对付最棘手的“静默加载”（如无声无息替换材质的 UI），分批执行，完全不掉帧。

🚀 安装指南

请根据你的游戏引擎类型选择对应的插件版本。

针对 Mono 游戏

安装 BepInEx: 为你的游戏安装 BepInEx 6.x for Mono。

下载插件: 将编译好的 demosaic.dll (Mono版) 放入游戏根目录下的 BepInEx/plugins/ 文件夹中。

运行游戏: 启动一次游戏。插件会自动在 BepInEx/config/ 目录下生成 demosaic.cfg 配置文件。

针对 IL2CPP 游戏

安装 BepInEx: 为你的游戏安装 BepInEx 6.x for IL2CPP。

下载插件: 将编译好的 demosaic.dll (IL2CPP版) 放入游戏根目录下的 BepInEx/plugins/ 文件夹中。

运行游戏: 启动一次游戏。插件会自动在 BepInEx/config/ 目录下生成 demosaic.cfg 配置文件。

⚙️ 配置说明

使用文本编辑器打开 BepInEx/config/demosaic.cfg 文件进行自定义。(注：修改配置后一般无需重启，切换场景或按 F10 即可重新加载检测。)

[1. 通用]

EnablePlugin: true 或 false。插件总开关。

RemoveMode: Disable, Destroy, Transparent。强烈推荐使用 Disable。

ManualScanKey: F10。手动触发全图分批扫描的热键。

[2. 扫描]

PeriodicScanInterval: 10。后台周期性扫描间隔（秒）。不需要可设为 0。

SceneLoadScanDelay: 1.5。新场景加载后，等待多久开始扫描（秒）。

ScanBatchSize: 500。每帧最多处理几个对象。数值越低越防卡顿，但扫描总耗时越长。

[3. 关键词]

ObjectNameKeywords: mosaic,censored,pixelated。对象名。

MaterialNameKeywords: mosaic,censored,pixel。材质名。

ShaderNameKeywords: mosaic,pixelate,censor。着色器名。

MeshNameKeywords: censor,mosaic。网格名。

TextureKeywords: mosaic。纹理名。

ComponentNameKeywords: Mosaic,CensorEffect。脚本/组件名（配置得短一点可以模糊匹配超长脚本名）。

ShaderPropertyKeywords: _PixelSize,_BlockSize,_MosaicFactor。着色器内部属性名。

[4. 高级]

DisableMethods: false。是否启用“反射方法爆破”功能。

MethodDisableKeywords: censor,mosaic。如果目标程序集中的方法名包含这些词，将被强制拦截并返回。

MethodPatchTargetAssemblies: Assembly-CSharp。目标程序集名称。留空会扫描全游戏（极慢，不推荐）。

👨‍💻 为开发者：从源码编译

环境要求

.NET SDK (推荐 .NET 6.0 或更高版本)

编译步骤

克隆本仓库。

在对应的项目目录 (Demosaic-mono 或 Demosaic-il2cpp) 下创建一个 Libs 文件夹。

从目标游戏的目录中提取底层依赖放入 Libs 中：

Demosaic-mono 所需依赖

0Harmony.dll (来自 BepInEx/core)

BepInEx.Core.dll (来自 BepInEx/core)

BepInEx.Unity.Mono.dll (来自 BepInEx/core)

UnityEngine.dll, UnityEngine.CoreModule.dll, UnityEngine.InputLegacyModule.dll (来自 游戏目录_Data/Managed)

Demosaic-il2cpp 所需依赖

0Harmony.dll, BepInEx.Core.dll, BepInEx.Unity.IL2CPP.dll, Il2CppInterop.Runtime.dll (来自 BepInEx/core)

UnityEngine.dll, UnityEngine.CoreModule.dll 等 (来自 BepInEx/interop)

Il2CppInterop.Runtime.InteropTypes.Arrays 相关核心库。

编译命令

在对应的项目根目录打开终端并执行：

dotnet build -c Release


📜 许可证

本项目采用 MIT 许可证。