# 通用去马赛克插件 (Demosaic Plugin)

一个面向 Unity 游戏的 BepInEx 6 插件，通过多维特征识别并移除马赛克/审查效果。支持 Mono 与 IL2CPP 双后端。

## 功能概览

### 检测能力

- **多维关键词匹配**：对象名、材质名、Shader 名、Shader 属性名、Mesh 名、组件名、纹理名（Mono）
- **Shader 属性值感知**：属性名命中后还要读材质上的当前值，值为关闭（未超过 `ShaderPropertyValueThreshold`）不判为马赛克
- **Camera 后处理检测**：自动禁用 Camera 上名称匹配关键词的 MonoBehaviour 后处理组件
- **Decal/Projector 检测**：检测 Built-in RP 的 Projector 和 URP 的 DecalProjector 材质
- **Renderer 材质 setter Hook**：实时捕获游戏运行时动态赋值的马赛克材质（零延迟）
- **动态方法拦截**：按方法名关键词禁用游戏中施加马赛克的函数（高级功能）
- **组件开关归零**：关闭挂在组件字段上的马赛克开关（如 LightID 的 `LIDPenetrator.enableMosaic`），**不替换材质槽**，适合"马赛克与本体共用同一材质"的游戏
- **白名单排除**：排除词机制防止误伤正常对象

### 处理模式

| 模式 | 说明 | 风险 |
|------|------|------|
| **Smart**（默认） | 仅替换 Renderer 中匹配马赛克关键词的材质槽为透明，保留同对象上的非马赛克材质 | 最低 |
| Transparent | 替换全部材质为透明 | 中 |
| Disable | 禁用整个 GameObject | 中 |
| Destroy（仅 Mono） | 物理销毁对象 | 高，可能导致 NullReference |

### 第三条处理路径：组件开关归零（`EnableMosaicSwitchOff`）

有些游戏的马赛克既不是独立物件、也不在材质槽上，而是**挂在组件字段上的 shader 效果**：

```csharp
// 典型例子：PaconeSoft.LightID.LIDPenetrator
enableMosaic  = true;      // 主开关
mosaicMinPx   = 4f;        // 最小马赛克块（像素）
mosaicRatio   = 0.02f;     // 块大小 = 屏幕长边 × 该比例
mosaicColor.a = 1f;        // alpha 控制「原图 ↔ 马赛克」混合比例
ApplyToMaterial();         // 把上面这些推给该 mesh 自己的材质
```

这类马赛克的**材质名 / 对象名 / Shader 名里都没有关键词**，靠名字维度匹配不到；而一旦靠别的维度匹配上，
上面三种模式的动作（替换材质槽 / 禁用对象 / 销毁）会把**器官本体一起弄没**。

所以插件提供第三条路：**只关组件字段，不碰材质槽**。

- 按 `MosaicSwitchComponentTypes` 找到目标组件（默认 `LIDPenetrator`）；
- 把名称命中 `MosaicSwitchFieldKeywords` 的字段归零：bool → `false`，float/int → `0`，Color → alpha 设为 `MosaicSwitchColorAlpha`；
- 调用 `MosaicSwitchApplyMethods` 里列出的无参方法（默认 `ApplyToMaterial`）把新值同步给材质；
- 每 `MosaicSwitchRecheckInterval` 秒复查一次，游戏重新打开马赛克时会再关掉。

> 这套组件属于 [PaconeSoft LightID] 系统，同作者/同系作品共用，改一次即可覆盖。

### 性能设计

- 分批扫描（每帧处理数量 + **每帧时间预算**双约束）
- 材质/Shader/组件/**对象名/父链**检测结果全部缓存（父链为三态缓存，无命中也记录）
- **监视列表（watchlist）**：只收录「曾被判定为马赛克」与「材质带马赛克属性但当前值为关闭」的 Renderer，默认每 2 秒复核一次；不做场景对象发现，也不触碰无关 Renderer
- 材质 setter Hook 帧内去重
- 透明材质 GC 防护（`HideAndDontSave`）+ 被回收后自动重建
- 事件驱动（Instantiate / SetActive / 材质赋值）+ 低频完整场景重扫兜底
- 激活去抖：同一根对象在 1 秒内被反复 `SetActive(true)`（对象池常见）时，跳过整棵子树的递归评估
- 完整场景重扫会复核已处理对象（可关闭），防止游戏把材质改回马赛克后失效

## 安装

### Mono 游戏

1. 安装 [BepInEx 6.x for Mono](https://github.com/BepInEx/BepInEx/releases)
2. 编译 `Demosaic-mono` 项目，或使用预编译 DLL
3. 将 `demosaic-mono.dll` 放入 `BepInEx/plugins/`
4. 启动游戏，在 `BepInEx/config/demosaic.cfg` 中调整配置

### IL2CPP 游戏

1. 安装 [BepInEx 6.x for IL2CPP](https://github.com/BepInEx/BepInEx/releases)
2. 编译 `Demosaic-il2cpp` 项目，或使用预编译 DLL
3. 将 `Demosaic.IL2CPP.dll` 放入 `BepInEx/plugins/`
4. 启动游戏，在 `BepInEx/config/demosaic.cfg` 中调整配置

## 配置说明

> 自 v1.6.0 起两个变体共用同一套配置（`BepInEx/config/demosaic.cfg`，节名 `General / Scan / Detection / Advanced`）。
> 旧配置键（`1. 通用` 等中文节名、`General.Enable`、`Remove.Mode`）会在首次启动时自动迁移。

### 通用设置（`General`）

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `EnablePlugin` | `true` | 是否启用插件。可运行时切换：改回 `true` 会立即完成激活并全量扫描，改为 `false` 只停止扫描，已处理对象保持现状 |
| `RemoveMode` | `Smart` | 处理模式：Disable / Destroy / Transparent / Smart |
| `ManualScanKey` | `F10` | 手动全场景扫描热键 |
| `ExportSceneKey` | `F11` | 导出场景 Renderer 信息到日志 |
| `IncludeInactiveObjects` | `true` | 是否扫描未激活对象 |
| `DetectParentObjectNames` | `true` | 检测父节点名称（适合 MosaicRoot/Quad 层级） |
| `LogProcessedObjects` | `false` | 是否记录每个处理对象的日志 |

### 扫描设置（`Scan`）

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `PeriodicScanInterval` | `10` | **完整场景重扫**间隔（秒），`0` 禁用。重新发现并逐维度评估全部 Renderer，开销较大 |
| `WatchlistRecheckInterval` | `2` | 只复核监视列表（曾命中马赛克 + 材质属性处于关闭状态的 Renderer）的间隔（秒），`0` 禁用。马赛克复发与改值激活主要靠它 |
| `SceneLoadScanDelay` | `1.5` | 场景加载后延迟扫描时间（秒） |
| `ScanBatchSize` | `500` | 每帧最多处理的对象数量 |
| `ScanTimeBudgetMs` | `2.0` | **每帧扫描时间预算（毫秒）**，超过即让出本帧，防止掉帧 |
| `ReverifyProcessedRenderers` | `true` | 复核已处理对象（完整重扫与监视列表都走这条判定），游戏把材质改回马赛克时可再次处理 |

### 检测关键词（`Detection`）

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `ObjectNameKeywords` | `mosaic,censored,pixelated,mozic,mazic,mozaic,moza` | 对象名关键词 |
| `MaterialNameKeywords` | `mosaic,censored,pixel,mozic,mazic,moza` | 材质名关键词 |
| `ShaderNameKeywords` | `mosaic,pixelate,censor,moza,mozic,mazic,mozaic` | Shader 名关键词 |
| `ShaderPropertyKeywords` | `_PixelSize,_BlockSize,_MosaicFactor` | Shader 属性名关键词 |
| `ShaderPropertyValueThreshold` | `0` | 数值型属性（Float / Range / Int）**大于**此值才判定马赛克生效；`0` 表示只要非 0 就算生效。Color / Vector / Texture 类属性仍按名命中 |
| `MeshNameKeywords` | `censor,mosaic,moza,mozic,mazic,mozaic` | 网格名关键词 |
| `TextureKeywords` | `mosaic` | 纹理名关键词 |
| `EnableTextureDetection` | Mono `true` / IL2CPP `false` | 是否启用纹理名检测（IL2CPP 下有崩溃风险，默认关闭） |
| `ComponentNameKeywords` | *(空)* | 组件名关键词 |
| `ExclusionKeywords` | *(空)* | 白名单关键词，命中后不处理 |

### 高级功能（`Advanced`）

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `EnableCameraEffectDetection` | `true` | Camera 后处理组件检测 |
| `CameraEffectKeywords` | `mosaic,censor,pixelat,moza,mozic,mazic` | Camera 组件名关键词 |
| `EnableDecalDetection` | `true` | Projector/DecalProjector 检测 |
| `EnableMaterialSetterHook` | `true` | Renderer 材质 setter 实时 Hook。**IL2CPP 下会被自动跳过**（见下方"IL2CPP 不 Hook 材质 setter"） |
| `EnableMosaicSwitchOff` | `true` | 关闭「马赛克开关挂在组件字段上」的马赛克（详见上文"第三条处理路径"） |
| `MosaicSwitchComponentTypes` | `LIDPenetrator` | 目标组件类型名关键词（同时匹配类型短名与全名），留空则关闭本功能 |
| `MosaicSwitchFieldKeywords` | `enableMosaic,mosaicRatio,mosaicMinPx,mosaicColor` | 要归零的字段名关键词，逗号分隔 |
| `MosaicSwitchApplyMethods` | `ApplyToMaterial,ApplyToMaterials` | 字段改完后调用的无参同步方法名，留空表示不做额外同步 |
| `MosaicSwitchColorAlpha` | `0` | Color 类字段要设置的 alpha 值（本作 0 = 完全原图；语义相反的游戏改成 1） |
| `MosaicSwitchRecheckInterval` | `1` | 组件开关复查间隔（秒），`0` 禁用 |
| `ExportSwitchKey` | `F9` | 导出场景中的马赛克开关组件（字段现值 + 目标材质 shaderKeywords）到日志 |
| `DisableMethods` | `false` | 方法名拦截（谨慎使用） |
| `MethodDisableKeywords` | `mosaic,censor,moza` | 方法拦截关键词 |
| `MethodExcludeKeywords` | `remove,destroy,clear,disable,hide,off,delete,undo,stop,cancel` | 方法排除词（防止误杀去马赛克方法） |
| `MethodPatchTargetAssemblies` | `Assembly-CSharp` | 目标程序集 |

## 使用方法

### 基本使用

1. 安装后启动游戏，插件自动以 Smart 模式工作
2. 如果马赛克未被去除，按 `F11` 导出场景信息到 `BepInEx/LogOutput.log`
3. 在日志中找到马赛克对象的名称/材质/Shader，将关键词添加到对应配置
4. 按 `F10` 强制重新扫描

### 场景导出格式

```text
[Demosaic Export] GO: CensorPlane | Material: mosaic_mat | Shader: Unlit/Mosaic | Mesh: Plane
```

### Smart 模式原理

```
Renderer 材质槽:
  [0] body_skin     → 不含关键词 → 保留 ✓
  [1] mosaic_overlay → 含 "mosaic" → 替换为透明 ✓
  [2] hair_material  → 不含关键词 → 保留 ✓
```

仅替换命中马赛克关键词的材质槽，保留身体、头发等非马赛克材质。

## 编译

### 前置条件

- .NET SDK 6.0+
- 各变体的 `libs/` 目录已填充（见 [AGENTS.md](AGENTS.md) 获取依赖来源）

### 构建命令

```bash
# IL2CPP 变体
dotnet build Demosaic-il2cpp/Demosaic-il2cppPlugin.csproj -c Release

# Mono 变体
dotnet build Demosaic-mono/DemosaicPlugin.csproj -c Release
```

产出路径：
- IL2CPP: `Demosaic-il2cpp/bin/Release/netstandard2.1/Demosaic.IL2CPP.dll`
- Mono: `Demosaic-mono/bin/Release/netstandard2.1/demosaic-mono.dll`

## 常见问题

### 整个角色或 UI 消失

关键词过宽导致误判。解决方案：
1. 将角色关键对象名添加到 `ExclusionKeywords`
2. 收窄 `ComponentNameKeywords` 和 `ObjectNameKeywords`
3. 确认使用 Smart 模式（非 Disable/Destroy）
4. 若误伤来自 Shader 属性维度（Shader 声明了 `_PixelSize` 之类但效果并未开启），收窄 `ShaderPropertyKeywords`，或把 `ShaderPropertyValueThreshold` 调到 `1`

### 玩具/道具激活时马赛克有延迟

插件已 Hook：
- `GameObject.SetActive` → 激活时立即检测
- `Object.Instantiate` → 新生成对象立即检测
- `Renderer.material/sharedMaterials` setter → 动态材质赋值立即检测

如果仍有延迟，可能是游戏通过 Shader 参数（如 `_MosaicFactor`）激活马赛克。这类材质会被放进监视列表，默认在 `WatchlistRecheckInterval`（2 秒）内重新读取属性值并处理；把该值调小即可更快响应，要零延迟则用 `DisableMethods` 拦截对应函数。

### 透明模式不生效

插件按以下顺序查找可用 Shader 创建透明材质：
1. `Universal Render Pipeline/Lit`（URP）
2. `HDRP/Lit` / `HD Render Pipeline/Lit`
3. `Standard`
4. `Standard (Specular setup)`
5. `Unlit/Transparent`
6. `Unlit/Color`
7. `Sprites/Default`

如果全部不可用（极端裁剪），Smart/Transparent 模式将降级为 Disable。

### 某个部位（例如男性性器官）去不了码，或者反而整个消失了

这是**两种失败模式同时存在**的典型症状，根因通常不是关键词没调好，而是这个部位的马赛克**长在它自己的材质/组件上**：

| 症状 | 机制 |
|------|------|
| 去不了码 | 马赛克开关是组件字段（如 `LIDPenetrator.enableMosaic`），对象名 / 材质名 / Shader 名里**一个关键词都没有** ⇒ 所有名字维度全部落空 |
| 反而消失 | 一旦靠别的维度（或你手动加的关键词）匹配上，Smart 模式的动作是**替换材质槽为透明**，而马赛克与本体**共用同一个材质槽** ⇒ 本体一起透明 |

排查顺序：

1. 按 `F11` 导出场景，找到那个部位的 `GO / Material / Shader / Mesh`：
   ```
   [Demosaic Export] GO: MALE_penis | Material: Panis_Mat (Instance) | Shader: Hidden/LIDPatched/<hash>/Opaque | Mesh: MALE_penis
   ```
   Shader 名带 `LIDPatched` 就属于本文说的组件字段型马赛克。
2. 按 `F9` 导出开关组件，确认字段现值与目标材质：
   ```
   [Demosaic Switch] PaconeSoft.LightID.LIDPenetrator on 'MALE_penis' | enableMosaic=True | mosaicRatio=0.02 | ... | target='MALE_penis' | mat[0]=Panis_Mat shader=Hidden/LIDPatched/.../Opaque keywords=[]
   ```
3. 靠 `EnableMosaicSwitchOff`（默认已开）把它关掉即可 —— **不要**把 `LIDPatched` / 部位名加进 `ShaderNameKeywords` / `ObjectNameKeywords`，那会走替换材质槽的路径，把本体一起弄没。

> 判断"码是烤在贴图里、还是运行时效果"：看该 Shader/组件有没有开关与尺寸参数，以及 `mosaicColor` 这类混合色字段。
> 有开关（`enableMosaic` / `mosaicRatio` / `mosaicMinPx`）就说明**原图数据仍在**，只是被像素化效果盖住了，关掉即可出原图。


### IL2CPP 不 Hook 材质 setter

IL2CPP 下 `Renderer` 的四个材质 setter（`material` / `sharedMaterial` / `materials` / `sharedMaterials`）**一律不 Hook**：
Harmony 给 il2cpp 原生方法装 detour 后，DMD 包装器"回原方法"的路径会再次落进同一 detour，形成无限递归并击穿线程栈保护页，
表现为进程级闪退（`Fatal error. AccessViolationException @ set_sharedMaterial`，catch 不住）。
因此 `EnableMaterialSetterHook` 在 IL2CPP 下会被跳过并打一行警告，动态材质变更改由 `SetActive` / `Instantiate` 事件与监视列表兜底。

### 日志里看不到输出

- `BepInEx/config/BepInEx.cfg` 的 `[Logging.Disk] AppendLog = false` 会让**每次启动都清空重写** `LogOutput.log`，上次导出的内容重启即消失；要留存改成 `true`。
- `[Logging.Disk] LogLevels` 默认不含 `Debug`，插件的 `LogDebug` 全部被丢弃；排查时把 `Debug` 加上。
- 排障期间可把 `InstantFlushing` 改成 `true`，硬崩时尾部日志不丢（平时保持 `false`，有性能开销）。



### IL2CPP 纹理检测默认关闭

IL2CPP 环境下 `GetTexturePropertyNameIDs` 可能触发 `AccessViolationException`，因此纹理名检测默认关闭（Mono 默认开启）。
需要靠纹理识别时，把 `Detection.EnableTextureDetection` 设为 `true` 并自行承担风险；更稳妥的做法是用材质名或 Shader 属性名替代。

## 项目结构

业务逻辑统一放在 `Demosaic.Core/`，两个变体通过 `<Compile Include="..\Demosaic.Core\**\*.cs" />` 链接同一份源码，
只在 `DEMOSAIC_IL2CPP` 条件下走各自的运行时分支（`TryCast`、`Il2CppReferenceArray`、事件委托类型等）。

| 文件 | 职责 |
|------|------|
| `Demosaic.Core/DemosaicEngine.cs` | 引擎主文件：生命周期、场景扫描（时间预算分批）、监视列表、事件钩子、Camera/Decal 检测、导出、Harmony 安装 |
| `Demosaic.Core/DemosaicEngine.Switch.cs` | 引擎 partial：组件开关归零（类型解析、实例缓存、字段反射计划） |
| `Demosaic.Core/DemosaicEngine.MaterialFix.cs` | 引擎 partial：材质马赛克属性归零（Shader 属性探针、关键字处理） |
| `Demosaic.Core/DemosaicEngine.Diagnostics.cs` | 引擎 partial：F8 候选扫描 / F9 开关导出 / 资源与门控诊断 |
| `Demosaic.Core/DemosaicSettings.cs` | 配置定义、关键词解析、旧版配置迁移 |
| `Demosaic.Core/MosaicDetector.cs` | 多维判定 + 全维度缓存 |
| `Demosaic.Core/MosaicProcessor.cs` | 四种处理模式 |
| `Demosaic.Core/TransparentMaterial.cs` | 透明材质工厂（URP/Built-in 兼容，永不主动销毁） |
| `Demosaic.Core/RuntimeInterop.cs` | 屏蔽 Mono / IL2CPP 类型差异 |
| `Demosaic.Core/SceneObjectFinder.cs`、`SafeInput.cs`、`DemosaicPatches.cs` | 场景查找 / 输入兼容 / Harmony 补丁 |
| `Demosaic-mono/demosaic-mono.cs`、`Demosaic-il2cpp/demosaic-il2cpp.cs` | 变体入口（各约 40 行） |

版本号唯一来源：根目录 `Directory.Build.props` 的 `DemosaicVersion`，构建时生成 `DemosaicVersion.g.cs`，
`[BepInPlugin]` 与程序集版本都取自它。


## 许可

MIT
