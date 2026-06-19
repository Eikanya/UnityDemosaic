# 通用去马赛克插件 (Demosaic Plugin)

这是一个面向 Unity 游戏的 BepInEx 插件，用于按名称、材质、Shader、网格和组件特征识别并隐藏马赛克或审查效果。项目分别提供 Mono 与 IL2CPP 两个版本。

## 功能概览

- 支持 Mono 与 IL2CPP 后端。
- 支持对象名、材质名、Shader 名、Shader 属性名、Mesh 名、组件名等多维度检测。
- 使用缓存减少重复检测开销。
- 使用分批扫描降低单帧卡顿。
- 支持场景加载扫描、周期扫描、手动热键扫描。
- IL2CPP 版本支持通过热键导出当前场景 Renderer 信息，方便定位关键词。
- 支持白名单关键词，避免误伤关键对象。

## 安装

### Mono 游戏

1. 安装 BepInEx 6.x for Mono。
2. 编译 `Demosaic-mono` 项目。
3. 将生成的 `demosaic-mono.dll` 放入 `BepInEx/plugins/`。
4. 启动游戏一次后，在 `BepInEx/config/` 中编辑生成的配置文件。

### IL2CPP 游戏

1. 安装 BepInEx 6.x for IL2CPP。
2. 编译 `Demosaic-il2cpp` 项目。
3. 将生成的 `Demosaic.IL2CPP.dll` 放入 `BepInEx/plugins/`。
4. 启动游戏一次后，在 `BepInEx/config/` 中编辑生成的配置文件。

## 主要配置

### 通用

Mono:

- `EnablePlugin`: 是否启用插件。
- `RemoveMode`: `Disable`、`Destroy`、`Transparent`。
- `ManualScanKey`: 手动扫描热键，默认 `F10`。
- `IncludeInactiveObjects`: 扫描未激活对象。Unity 2022+ 会优先使用 `FindObjectsByType(..., None)`，减少排序开销。
- `DetectParentObjectNames`: 检测 Renderer 父节点名称，适合 `MosaicRoot/Quad` 这类层级。
- `LogProcessedObjects`: 是否为每个处理对象写 Info 日志。大量命中时建议保持 `false`。

IL2CPP:

- `Enable`: 是否启用插件。
- `Mode`: `Disable` 或 `Transparent`。
- `ForceScanHotkey`: 手动扫描热键，默认 `F10`。
- `ExportSceneKey`: 导出场景 Renderer 信息热键，默认 `F11`。
- `IncludeInactiveObjects`: 扫描未激活对象。Unity 2022+ 会优先使用 `FindObjectsByType(..., None)`，减少排序开销。
- `DetectParentObjectNames`: 检测 Renderer 父节点名称，适合 `MosaicRoot/Quad` 这类层级。
- `LogProcessedObjects`: 是否为每个处理对象写 Info 日志。大量命中时建议保持 `false`。

### 扫描

- `PeriodicScanInterval`: 周期扫描间隔，设为 `0` 可禁用。
- `SceneLoadScanDelay`: 场景加载后的延迟扫描时间。
- `ScanBatchSize`: 每帧处理数量。插件会强制使用最小值 `1`，避免配置为 `0` 后扫描卡死或报错。

性能建议：

- Unity 2022/2023/6 游戏中，保持 `IncludeInactiveObjects = true` 可以让插件走新版无排序查找路径，同时提升对预加载对象的命中率。
- 如果场景很大或 IL2CPP 游戏明显卡顿，将 `ScanBatchSize` 降到 `100-200`，并适当增大 `PeriodicScanInterval`。
- 保持 `LogProcessedObjects = false`。BepInEx 写日志是同步成本，大量马赛克粒子或小块命中时会放大卡顿。

### 检测关键词

- `ObjectNameKeywords`: 对象名关键词。
- `MaterialNameKeywords`: 材质名关键词。
- `ShaderNameKeywords`: Shader 名关键词。
- `MeshNameKeywords`: Mesh 名关键词。
- `TextureKeywords`: 纹理名关键词。IL2CPP 版本保留配置，但实际禁用纹理检测以避免底层访问异常。
- `ComponentNameKeywords`: 组件名关键词。建议谨慎填写，过宽会误伤角色或 UI。
- `ShaderPropertyKeywords`: Shader 属性名关键词。
- `ExclusionKeywords`: 白名单关键词。对象名命中后不会被处理。

### 高级方法拦截

- `DisableMethods`: 是否启用按方法名拦截。
- `MethodDisableKeywords`: 方法名关键词。
- `AssemblyNamesToPatch` 或 `MethodPatchTargetAssemblies`: 目标程序集名。

方法拦截功能风险较高。当前实现只自动拦截非泛型、非特殊名、非抽象、返回值为 `void` 的方法，仍建议优先使用更精确的资源关键词定位。

## IL2CPP 场景导出

按下 `ExportSceneKey` 后，插件会把当前激活 Renderer 的对象名、材质名、Shader 名和 Mesh 名输出到 `BepInEx/LogOutput.log`，格式类似：

```text
[Demosaic Export] GO: CensorPlane | Material: mosaic_mat | Shader: Unlit/Mosaic | Mesh: Plane
```

将可疑名称加入对应关键词后，按 `F10` 重新扫描即可。

## 编译

每个项目目录下需要准备 `libs` 文件夹，放入目标游戏和 BepInEx 对应版本的依赖 DLL。

```bash
dotnet build -c Release
```

输出位置：

- Mono: `Demosaic-mono/bin/Release/netstandard2.1/demosaic-mono.dll`
- IL2CPP: `Demosaic-il2cpp/bin/Release/netstandard2.1/Demosaic.IL2CPP.dll`

## 常见问题

### 整个角色或 UI 消失

通常是关键词过宽。优先检查 `ComponentNameKeywords`、`MaterialNameKeywords` 和 `ObjectNameKeywords`，并用 `ExclusionKeywords` 白名单保护不应处理的对象。

### 动态生成的对象没有立即处理

插件会拦截常见 `SetActive` 和 `Instantiate` 重载，但不同 Unity 版本或游戏封装方式可能仍有遗漏。周期扫描和手动扫描会作为兜底。

如果对象层级是父节点名包含 `mosaic/censor`，而实际 Renderer 在子节点 `Quad/Plane` 上，请开启 `DetectParentObjectNames`。

### 透明模式不生效

透明模式依赖可用的 `Standard` Shader。URP/HDRP 或深度写入特殊的 Shader 可能需要针对游戏单独适配。

## 许可

MIT
