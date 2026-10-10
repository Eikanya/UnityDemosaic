# Agent Instructions

## 项目结构

本项目包含两个独立的 BepInEx 6 插件变体，**共享同一份核心源码**：

| 变体 | 目录 | 目标运行时 |
|------|------|-----------|
| 共享核心 | `Demosaic.Core/` | 无（源码以 `Compile Include` 链接进两个变体） |
| IL2CPP | `Demosaic-il2cpp/` | BepInEx 6 for Unity IL2CPP |
| Mono | `Demosaic-mono/` | BepInEx 6 for Unity Mono |

每个变体是独立的 .NET 项目，互不引用；`Demosaic.Core/` **不是**独立项目，不要给它加 csproj。

修改业务逻辑时**只改 `Demosaic.Core/`**；只有以下差异才需要 `#if DEMOSAIC_IL2CPP`：

- IL2CPP 需要 `TryCast<T>()` 而不是 `as`（统一走 `RuntimeInterop.AsGameObject / AsComponent / AsMonoBehaviour / AsMaterial / AsSkinnedMeshRenderer`，不要在业务代码里写 `#if` + `as`）
- IL2CPP 的 `Renderer.sharedMaterials` 是 `Il2CppReferenceArray<Material>`（统一走 `RuntimeInterop`）
- IL2CPP 组件类型名必须用 `GetIl2CppType().Name`（统一走 `RuntimeInterop.GetComponentTypeName`）
- IL2CPP 场景事件委托由 `System.Action` 隐式转换，Mono 用 `UnityEngine.Events.UnityAction`
- IL2CPP **禁止** Hook `Renderer` 的任何材质 setter（`material` / `sharedMaterial` / `materials` / `sharedMaterials`）。
  Harmony 给 il2cpp 原生方法装 detour 后，DMD 包装器「回原方法」的路径会再次落进同一 detour ⇒ 无限递归 ⇒ 击穿线程栈保护页 ⇒
  进程级闪退（`Fatal error. AccessViolationException @ set_sharedMaterial`，catch 不住）。
  实时材质捕获只走 `SetActive` / `Instantiate` 事件 + 监视列表；`ApplyMaterialSetterPatches` 在 IL2CPP 下整体空实现。
- 马赛克开关可能**不在材质上，而在组件字段上**（典型：`PaconeSoft.LightID.LIDPenetrator` 的 `enableMosaic` / `mosaicRatio` /
  `mosaicColor`，由 `ApplyToMaterial()` 推给该 mesh 自己的 shader）。这类走 `EnableMosaicSwitchOff`：**只关字段，不碰材质槽**。
  ⚠ 不要让这类对象落进 `ShaderNameKeywords` / `ObjectNameKeywords` —— Smart 模式会替换材质槽，把本体一起变透明
  （症状：马赛克和器官一起消失）。排查用 F11（看 Shader 名，`LIDPatched` 即为该族）+ F9（看字段现值与目标材质）。
  另注：LID 代码在独立程序集 `LightIDSystem`，要用 `DisableMethods` 拦它必须把该程序集加进
  `MethodPatchTargetAssemblies`（默认只有 Assembly-CSharp，配了也拦不到）。
  📌 未解案例（NoviceSuccubusLillia 男码，2026-10）:字段 / 材质实例 / 材质资产 / 全局属性 / 关键字 /
  Material 写入拦截 / DrawMesh **全部排除后码仍在**（`_LID_Mosaic*` 读到 0、无关键字、无 MPB）；
  换掉 LID 补丁 shader 码会消失但器官也一起消失（可见性依赖该 shader 的形变）⇒ 像素化在
  `Hidden/LIDPatched/<hash>` 变体的渲染路径里。
  ⚠ 2026-10-10 实测补记：`MALE_penis_mosaic`（内置 Standard）**不是**那层码 ——
  把它整体透明化（运行时实测确认命中）后码仍在。运行时手段（字段/属性/资产/补丁/方法劫持/全局属性/
  关键字/pass/换 shader/**按材质名透明化**）到此全部穷尽，**唯一剩下的路是离线改包**
  （改 bundle 里那个 shader 资产）。不要再重复任何运行时方案。

## 构建

### 前置条件

- .NET SDK 6.0 或更高版本
- 各变体的 `libs/` 目录已填充（见下方依赖获取）

### 构建命令

```bash
# 构建 IL2CPP 变体（在项目子目录中执行）
dotnet build Demosaic-il2cpp/Demosaic-il2cppPlugin.csproj -c Release

# 构建 Mono 变体（在项目子目录中执行）
dotnet build Demosaic-mono/DemosaicPlugin.csproj -c Release
```

产出路径：`<变体>/bin/Release/netstandard2.1/`

## 依赖获取

`libs/` 目录被 `.gitignore` 排除。首次克隆后需手动填充。

### 来源一：BepInEx 6 框架

从 [BepInEx Releases](https://github.com/BepInEx/BepInEx/releases) 下载对应版本包：

- **IL2CPP 变体**：下载 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.***.zip`
  - 解压后从 `BepInEx/core/` 复制所需 DLL 至 `Demosaic-il2cpp/libs/`
  - 关键文件：`0Harmony.dll`, `BepInEx.Core.dll`, `BepInEx.Unity.Common.dll`, `BepInEx.Unity.IL2CPP.dll`, `Il2CppInterop.Runtime.dll`

- **Mono 变体**：下载 `BepInEx-Unity.Mono-win-x64-6.0.0-be.***.zip`
  - 解压后从 `BepInEx/core/` 复制所需 DLL 至 `Demosaic-mono/libs/`
  - 关键文件：`0Harmony.dll`, `BepInEx.Core.dll`, `BepInEx.Unity.Common.dll`, `BepInEx.Unity.Mono.dll`

### 来源二：Unity 游戏文件

UnityEngine DLL 来自目标游戏安装目录：

- **IL2CPP 游戏**：`<游戏目录>/BepInEx/interop/` (BepInEx 首次运行后生成)
  - 关键文件：`UnityEngine.CoreModule.dll`, `UnityEngine.InputLegacyModule.dll`, `Il2Cppmscorlib.dll`

- **Mono 游戏**：`<游戏目录>/<游戏名>_Data/Managed/`
  - 关键文件：`UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.InputLegacyModule.dll`

### 快速验证

填充 libs 后执行构建命令，无错误输出即表示依赖完整。

## 框架约束

- 目标框架：`netstandard2.1`
- BepInEx 版本：6.0.0-be（pre-release）
- C# 语言版本：10.0
- Nullable：禁用
- NoWarn：`MSB3277;CS0649;CS0169;CS8618`（设计性抑制，勿移除）
- 所有引用 DLL 的 `<Private>false</Private>` 表示不复制到输出目录（由 BepInEx 运行时提供）
- **版本号唯一来源是根目录 `Directory.Build.props` 的 `DemosaicVersion`**：改版本号只改这一处，
  `AssemblyVersion`/`FileVersion`/`[BepInPlugin]` 全部由它派生（构建时生成 `obj/.../DemosaicVersion.g.cs`）

## 部署

编译产出的 DLL 放入目标游戏的 `BepInEx/plugins/` 目录即可加载。
