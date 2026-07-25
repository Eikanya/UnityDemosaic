# 通用去马赛克插件 (Demosaic Plugin)

一个面向 Unity 游戏的 BepInEx 6 插件，通过多维特征识别并移除马赛克/审查效果。支持 Mono 与 IL2CPP 双后端。

## 功能概览

### 检测能力

- **多维关键词匹配**：对象名、材质名、Shader 名、Shader 属性名、Mesh 名、组件名、纹理名（Mono）
- **Camera 后处理检测**：自动禁用 Camera 上名称匹配关键词的 MonoBehaviour 后处理组件
- **Decal/Projector 检测**：检测 Built-in RP 的 Projector 和 URP 的 DecalProjector 材质
- **Renderer 材质 setter Hook**：实时捕获游戏运行时动态赋值的马赛克材质（零延迟）
- **动态方法拦截**：按方法名关键词禁用游戏中施加马赛克的函数（高级功能）
- **白名单排除**：排除词机制防止误伤正常对象

### 处理模式

| 模式 | 说明 | 风险 |
|------|------|------|
| **Smart**（默认） | 仅替换 Renderer 中匹配马赛克关键词的材质槽为透明，保留同对象上的非马赛克材质 | 最低 |
| Transparent | 替换全部材质为透明 | 中 |
| Disable | 禁用整个 GameObject | 中 |
| Destroy（仅 Mono） | 物理销毁对象 | 高，可能导致 NullReference |

### 性能设计

- 分批扫描（可配置每帧处理数量）
- 材质/Shader/组件检测结果缓存
- 材质 setter Hook 帧内去重
- 透明材质 GC 防护（`HideAndDontSave`）
- 周期扫描 + 事件驱动（Instantiate/SetActive/材质赋值）

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

### 通用设置

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `EnablePlugin` / `Enable` | `true` | 是否启用插件 |
| `RemoveMode` / `Mode` | `Smart` | 处理模式 |
| `ManualScanKey` / `ForceScanHotkey` | `F10` | 手动全场景扫描热键 |
| `ExportSceneKey` | `F11` | 导出场景 Renderer 信息到日志 |
| `IncludeInactiveObjects` | `true` | 是否扫描未激活对象 |
| `DetectParentObjectNames` | `true` | 检测父节点名称（适合 MosaicRoot/Quad 层级） |
| `LogProcessedObjects` | `false` | 是否记录每个处理对象的日志 |

### 扫描设置

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `PeriodicScanInterval` | `10` | 周期扫描间隔（秒），`0` 禁用 |
| `SceneLoadScanDelay` | `1.5` | 场景加载后延迟扫描时间（秒） |
| `ScanBatchSize` | `500` | 每帧处理的对象数量 |

### 检测关键词

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `ObjectNameKeywords` | `mosaic,censored,pixelated,mozic,mazic,mozaic,moza` | 对象名关键词 |
| `MaterialNameKeywords` | `mosaic,censored,pixel,mozic,mazic,moza` | 材质名关键词 |
| `ShaderNameKeywords` | `mosaic,pixelate,censor,moza,mozic,mazic,mozaic` | Shader 名关键词 |
| `ShaderPropertyKeywords` | `_PixelSize,_BlockSize,_MosaicFactor` | Shader 属性名关键词 |
| `MeshNameKeywords` | `censor,mosaic,moza,mozic,mazic,mozaic` | 网格名关键词 |
| `TextureKeywords` | `mosaic` | 纹理名关键词（IL2CPP 版已禁用） |
| `ComponentNameKeywords` | *(空)* | 组件名关键词 |
| `ExclusionKeywords` | *(空)* | 白名单关键词，命中后不处理 |

### 高级功能

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `EnableCameraEffectDetection` | `true` | Camera 后处理组件检测 |
| `CameraEffectKeywords` | `mosaic,censor,pixelat,moza,mozic,mazic` | Camera 组件名关键词 |
| `EnableDecalDetection` | `true` | Projector/DecalProjector 检测 |
| `EnableMaterialSetterHook` | `true` | Renderer 材质 setter 实时 Hook |
| `DisableMethods` | `false` | 方法名拦截（谨慎使用） |
| `MethodDisableKeywords` | `censor,mosaic` | 方法拦截关键词 |
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

### 玩具/道具激活时马赛克有延迟

插件已 Hook：
- `GameObject.SetActive` → 激活时立即检测
- `Object.Instantiate` → 新生成对象立即检测
- `Renderer.material/sharedMaterials` setter → 动态材质赋值立即检测

如果仍有延迟，可能是游戏通过 Shader 参数（如 `_MosaicFactor`）激活马赛克。此时可缩短 `PeriodicScanInterval` 或使用 `DisableMethods` 拦截对应函数。

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

### IL2CPP 纹理检测被禁用

IL2CPP 环境下 `GetTexturePropertyNameIDs` 可能触发 `AccessViolationException`，因此纹理名检测已安全禁用。如需通过纹理识别马赛克，请使用材质名或 Shader 属性名替代。

## 版本历史

### v1.5.0

- **Smart 模式增强**：修复空父节点降级为 Disable 的问题，统一 IsMosaicMaterial 判定维度
- **新增 Camera 后处理检测**：自动禁用匹配关键词的后处理组件
- **新增 Decal/Projector 检测**：支持 Built-in Projector 和 URP DecalProjector
- **新增 Renderer 材质 setter Hook**：零延迟捕获动态材质赋值
- **动态方法拦截优化**：新增排除词防误杀、条件性 Prefix、详细日志
- **扩展透明材质 Shader 兼容性**：支持 Sprites/Default、Unlit 系列
- **Mono 版新增 F11 场景导出**
- **Mono 版透明材质 GC 防护**
- **两变体关键词配置统一**

### v1.4.x

- 初始发布，支持 Disable/Transparent/Smart 模式
- 多维关键词检测 + 分批扫描
- Harmony 方法拦截（高级功能）

## 许可

MIT
