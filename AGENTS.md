# Agent Instructions

## 项目结构

本项目包含两个独立的 BepInEx 6 插件变体，共享相同的功能逻辑：

| 变体 | 目录 | 目标运行时 |
|------|------|-----------|
| IL2CPP | `Demosaic-il2cpp/` | BepInEx 6 for Unity IL2CPP |
| Mono | `Demosaic-mono/` | BepInEx 6 for Unity Mono |

每个变体是独立的 .NET 项目，互不引用。

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

## 部署

编译产出的 DLL 放入目标游戏的 `BepInEx/plugins/` 目录即可加载。
