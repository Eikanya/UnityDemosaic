# 通用去马赛克插件 (Demosaic Plugin)

unity3D游戏中的马赛克查找，可以通过`AssetStudio`或`AssetRippe`解包查看游戏是否为动态马赛克，主要看texture图片有没马赛克，如果没有基本可以去除。一般通过shared、material、mesh和dll函数加载马赛克。简单的可以搜索关键词，然后使用UABE去修改数值或者删除mesh，但是这样效率不高，又不一定能准确去除。dll函数使用dnSpy去修改。复杂的il2cpp平台需要使用 BepInEx/Harmony 插件禁用方法。


这是一个为 Unity 游戏设计的 BepInEx 插件，旨在通过多种灵活的检测方式，自动移除游戏中的马赛克或审查效果。它具有高度的可配置性，并为 Mono 和 IL2CPP 两个脚本后端分别提供了优化版本。

✨ 核心功能
* 双平台支持: 提供独立的 Demosaic-Mono.dll 和 Demosaic-IL2CPP.dll，完美适配不同类型的游戏。

* 高效的事件驱动扫描: 通过监听 场景加载完成 事件和实时挂钩对象生命周期来触发扫描，性能更高，响应更及时。

* 全面的实时处理: 利用 Harmony 实时挂钩 `GameObject.SetActive`。无论是场景中预设的对象被激活，还是在游戏过程中动态创建的新对象，都能被立即检测和处理。

* 流畅的异步扫描: 当进行全场景扫描时（如加载新场景或按快捷键），扫描任务会在后台通过协程分多帧执行，有效避免了在复杂场景中因对象过多而导致的瞬间卡顿或掉帧。

* 多维度、深层次的检测系统:

* 可通过游戏对象、材质、着色器、网格和纹理的名称来识别马赛克。

    组件名称检测: 可识别挂载在对象上的特定脚本组件（例如 MosaicEffect.cs）。

    着色器属性分析: 能够深入分析材质所使用的着色器，通过其内部属性（如 `_PixelSize`, `_BlockSize`）来精准定位马赛克效果。

    灵活的移除模式: 可选择直接禁用马赛克对象、销毁它，或将其材质替换为透明。

* 高度可配置: 所有功能均可通过配置文件进行详细设置，大部分设置无需重启游戏即可生效。

* 高级代码禁用: 可根据关键词动态禁用游戏中的特定方法（例如，禁用名为 ApplyCensorEffect 的方法），从根源上阻止审查效果。

* 排除列表与热键: 支持配置排除关键词以防误杀，并可设置一个快捷键来手动触发全场景扫描。

🛠️ 原理与方法 (How it Works)
本插件通过一套多层级、事件驱动的策略来确保高效且全面地移除马赛克，同时将性能影响降至最低。

1. 检测原理：基于关键词的识别
插件的核心是关键词匹配。它会检查场景中渲染器（Renderer）关联的各种资源的名称是否包含您在配置文件中设定的关键词（如 mosaic, censor 等）。检测范围包括：

* 游戏对象 (GameObject) 的名称

* 材质 (Material) 的名称

* 着色器 (Shader) 的名称

* 着色器属性 (Shader Property) 的名称

* 网格 (Mesh) 的名称

* 纹理 (Texture) 的名称

* 组件 (Component) 的名称

2. 移除方法：多种处理方式
一旦检测到目标，插件会根据您的配置执行以下操作之一：

Disable (禁用): 直接禁用包含马赛克渲染器的整个游戏对象。这是最推荐的方式，因为它通常最彻底、最安全，能一并停止关联的脚本。

Destroy (销毁): 从场景中彻底删除马赛克对象。

Transparent (透明): 将对象的材质替换为一个共享的、完全透明的材质。适用于只想隐藏模型但不想影响其脚本逻辑的罕见情况。

3. 核心扫描策略：多层保障体系
为了应对各种复杂的游戏加载机制，插件构建了一个多层递进的扫描和处理体系：

第一层：实时补丁 (最高优先级)

技术: 使用 Harmony 库实时挂钩（Hook）了 Unity 引擎核心的 `GameObject.SetActive` 方法。

目的: 当任何游戏对象被激活的瞬间，插件会立即对其进行检查。这是最高效、最即时的防线，能处理绝大多数情况。

第二层：场景加载扫描

技术: 监听 `SceneManager.sceneLoaded` 事件。

目的: 当一个新场景加载完成后，立即触发一次全场景的异步扫描，确保场景中所有预设的对象都被检查一遍。

第三层：延迟二次扫描

技术: 在场景加载完成一定时间后（可配置），自动执行一次额外的全场景异步扫描。

目的: 这是一个非常重要的保险措施。很多游戏会在场景加载后，才通过异步或其它复杂逻辑来加载角色、特效等资源。这个延迟扫描就是为了捕获这些“迟到”的对象。

第四层：可配置的周期性扫描 

技术: 一个可由用户配置时间间隔的、持续运行的异步扫描协程。

目的: 这是为解决最棘手的“静默加载”场景（如某些检视模式）而设计的最终手段。此功能默认禁用 (PeriodicScanInterval = 0)，仅在需要时由用户手动开启。

1. 高级功能：从根源禁用方法
对于某些通过代码逻辑实现的审查效果，插件提供了一种更强大的移除方式。它会扫描指定的程序集，查找名称包含关键词的方法，并使用 Harmony 直接禁用它们，使其永远无法执行。

🚀 安装指南
请根据你的游戏类型选择对应的插件版本。

针对 Mono 游戏
1. 安装 BepInEx: 为你的游戏安装 BepInEx 6.x for Mono。

2. 下载插件: 从本项目的 "Releases" 页面下载最新的 Demosaic-Mono.dll。

3. 放置插件: 将 Demosaic-Mono.dll 放入游戏根目录下的 BepInEx/plugins/ 文件夹中。

4. 运行游戏: 启动一次游戏。插件会自动在 BepInEx/config/ 目录下生成一个名为 demosaic.cfg 的配置文件。

针对 IL2CPP 游戏
1. 安装 BepInEx: 为你的游戏安装 BepInEx 6.x for IL2CPP。

2. 下载插件: 从本项目的 "Releases" 页面下载最新的 Demosaic-IL2CPP.dll。

3. 放置插件: 将 Demosaic-IL2CPP.dll 放入游戏根目录下的 BepInEx/plugins/ 文件夹中。

4. 运行游戏: 启动一次游戏。插件会自动在 BepInEx/config/ 目录下生成一个名为 demosaic.cfg 的配置文件。

⚙️ 配置说明
使用文本编辑器打开 BepInEx/config/demosaic.cfg 文件进行自定义。

[1. 通用]
* EnablePlugin: true 或 false。插件的总开关。

* RemoveMode: Disable, Destroy, Transparent。移除模式。强烈推荐 Disable，因为 Transparent 依赖游戏内的特定着色器，可能无效。

* ManualScanKey: F10。手动触发全场景异步扫描的快捷键。

[2. 扫描]
* PeriodicScanInterval: 10。周期性扫描的间隔时间（秒）。设为 0 可禁用。

* SceneLoadScanDelay: 1.5。场景加载后延迟扫描的时间（秒）。

* ScanBatchSize: 500。异步扫描时每帧处理的对象数量，用于防止卡顿。

[3. 关键词]
* ObjectNameKeywords: mosaic,censored,pixelated,h-mosaic。用于检测对象名称的关键词。

* MaterialNameKeywords: mosaic,censored,pixel,h-mosaic。用于检测材质名称的关键词。

* ShaderNameKeywords: mosaic,pixelate,censor。用于检测着色器名称的关键词。

* MeshNameKeywords: censor,mosaic。用于检测网格名称的关键词。

* TextureKeywords: mosaic。用于检测纹理名称的关键词。

* ComponentNameKeywords: MosaicEffect,CensorEffect。【新增】 用于检测组件名称的关键词。

* ShaderPropertyKeywords: _PixelSize,_BlockSize,_MosaicFactor。【新增】 用于检测着色器属性的关键词。

[4. 高级]
* DisableMethods: false。是否启用“方法禁用”功能。

* MethodDisableKeywords: Mosa,Mosaic,Censor。方法禁用关键词。如果游戏代码中某个方法的名称包含此列表中的词语，插件会尝试禁用该方法。修改后需要重启游戏才能生效。

* MethodPatchTargetAssemblies: Assembly-CSharp。方法扫描目标程序集。指定需要进行方法扫描的程序集名称，多个用逗号分隔。修改后需重启。

❓ 已知问题与解决方案
1. BepInEx 控制台中文显示乱码
这是 Windows 系统的环境问题，与插件本身无关。

原因: 插件使用 UTF-8 编码输出日志，但 Windows 控制台默认使用本地代码页（如 GBK）解码，导致编码不匹配。

解决方案: 修改 BepInEx 配置文件 BepInEx/config/BepInEx.cfg，在 [Logging.Console] 段下，设置 ConsoleOutEncoding = UTF-8。

2. IL2CPP 版本出现 Il2CppInterop 警告
在 IL2CPP 版本的日志中，你可能会看到关于 Unsupported parameter 或 Unsupported return type 的警告。

原因: 这是 Il2CppInterop 工具的局限性，它在分析 .NET 代码和原生 C++ 代码之间的交互时，对某些复杂类型支持不完善。

影响: 这些是分析阶段的警告，通常不会影响插件的正常运行，可以安全地忽略。

👨‍💻 为开发者：从源码编译
环境要求
.NET SDK (推荐 .NET 6 或更高版本)

编译步骤
* 1.克隆本仓库。

* 2.根据你要编译的版本，在对应的项目目录 (Demosaic-mono 或 Demosaic-il2cpp) 下创建一个 Libs 文件夹。

* 3.从目标游戏的目录中复制所需的 DLL 文件到 Libs 文件夹。

Demosaic-mono 所需依赖
1. 从 (游戏根目录)/BepInEx/core/ 复制:

`0Harmony.dll`

`BepInEx.Core.dll`

`BepInEx.Unity.Mono.dll`

2. 从 (游戏根目录)/(游戏名)_Data/Managed/ 复制:

`UnityEngine.dll`

`UnityEngine.CoreModule.dll`

`UnityEngine.InputLegacyModule.dll`

Demosaic-il2cpp 所需依赖
1. 从 (游戏根目录)/BepInEx/core/ 复制:

`0Harmony.dll`

`BepInEx.Core.dll`

`BepInEx.Unity.IL2CPP.dll`

`Il2CppInterop.Runtime.dll`

2. 从 (游戏根目录)/BepInEx/interop/ (或 Managed 目录) 复制:

`UnityEngine.dll`

`UnityEngine.CoreModule.dll`

`UnityEngine.InputLegacyModule.dll`

编译命令
在对应的项目根目录打开终端并执行以下命令。

```
dotnet build -c Release

```

📜 许可证
本项目采用 MIT 许可证。