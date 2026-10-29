# BV / 崛亮视觉 · Bright Vision Revit 插件安装器 1.8.0

本工具为当前 Windows 用户安装 Bright Vision 社区导出与土建工作台插件。官方署名为 **崛亮视觉 · Bright Vision**，自有社区源码采用 **Apache-2.0**；第三方组件保留各自许可。标准源码仓库根目录和安装模块包含 LICENSE／NOTICE，安装器不会将 Autodesk 软件、API DLL、旧第三方导出引擎或用户模型重新许可或附带分发。

本目录面向已独立发布的 **BV 1.8.0 开发预览／第三期第二批**。[下载安装包](https://github.com/queqideliang/bright-vision/releases/download/revit-v1.8.0/BV-1.8.0-Setup.exe)，[发行说明与 SHA-256](https://github.com/queqideliang/bright-vision/releases/tag/revit-v1.8.0)。源码 CI、分层后台和四个公开附件下载已核对，本机 2017／2018／2020／2026 已用 CLI 升级并核对；没有新的 Revit 宿主／窗口验收。1.7 安装包和 SDK 历史证据保留，不代替 1.8 新功能验收。

已发布安装包中的 README 是打包时快照，当前发行状态以 Release 为准；发布后的 main 文档可能与包内文本不同，不改写已发布 EXE、模块或标签。查看器 0.7 仍内置 1.7；独立插件 1.8 按单独安装包／受保护 CLI 升级，目前没有插件自动更新器。

## 使用与功能范围

1. 保存工程并退出全部 Revit。安装器要求关闭 Revit，不会替用户结束进程。
2. 运行 `Bright.Vision-Setup.exe`，核对自动检测且有匹配负载的年份，点击“安装插件”。
3. 重启 Revit，进入 **BV → bright vision**。保留“本地导出模型 ZIP”“土建原始量工作台”和“墙体试算与台账”。下述流程用于具备相应版本的安装包，不是本轮已执行安装的记录。
4. 导出时选择模型名、三维视图、目录和内容。接收 ZIP 的独立查看器无需安装 Revit；插件本身必须在 Revit 内使用，不能独立读取 RVT。
5. 卸载使用本安装器的“卸载插件”，或下述管理器的 Uninstall 动作。不要通过删除整个 Revit 插件目录卸载。

1.6 的工程设置继续提供计量层映射、单一施工段归属、本地工程 JSON 存取、筛选汇总与 CSV；只改变报表归属，不写回 RVT 或切分几何。

1.7 首批墙体试算采用项目约定 `BV.Wall.RectSolid/1`：对部分普通直墙构造参数矩形基准，扣除明确矩形 Opening 的并集，再与实际墙实体作双向差集校验；校验通过才生成基准、扣减、结果和逐条台账。Revit 原生量单列，不把原生净量当毛量重复扣减。门窗族的名义宽高不代替实际切洞；弧墙、附着／顶部约束、显式交接、未确认切洞及不一致几何会显示未支持或失败。它不是完整地区清单／定额规则、模板量或全类型墙算量。详细数据合同与范围见相邻的 `bright-revit-exporter` 源码说明。

1.8 的增量范围是同一几何门槛之上的 **单侧轴向投影面积与工程归属报表**：面积采用 `BV.Wall.AxisProjection/1`，以长×高减矩形洞口并集，台账保留带符号面积变化；不是抹灰、模板、外露表面、双面墙或建筑面积。新 `BV.Wall.Report/2` 面向完整来源绑定、计量层／施工段／原生楼层／类型分组、覆盖及失败状态、面积与体积／原生量对照和 CSV。工程设置继续只改变归属，不切分几何、不参与重新扣洞。旧体积规则和旧版 CSV 合同保留；领域、报表、离屏界面、API 编译及实机应分层验收，不能用安装器测试替代。完整类型映射、几何分段、真实族切洞、交接增扣及地区规则仍未完成。

## 年份与运行时

发行构建目标是 Revit **2015—2026**，完整包包含 14 个年份／运行时变体：2015—2020 为 net45，2021—2024 为 net48，2025／2026 分别有 net8 与 net10。现代宿主按实际配置选择运行时，无法确认时停止，不能只按年份猜测。

“能编译”与“在该 Revit 内验收”分别记录。完整包不等于 12 个年份、所有更新版本和全部功能均已实测；2025-net10 基于对应 net8 API 契约，具体宿主仍须核对。正式支持范围以每次发行说明中的年份、build、模型及结果为准。

## 许可文件与安装所有权

每个变体包括以下四个受管理文件，安装到 `%APPDATA%\Autodesk\Revit\Addins\<年份>`：

- `Bright.Vision.Revit.dll`：自有插件。
- `Bright.Vision.addin`：自有注册；名称 BV、VendorId BRVI，禁止热加载。
- `Bright.Vision.LICENSE`：完整 Apache-2.0 许可原文。
- `Bright.Vision.NOTICE`：崛亮视觉版权及适用通知。

许可文件使用产品前缀，避免覆盖其他插件的通用 LICENSE。模块根目录另有标准 LICENSE／NOTICE。公司署名及版权与第三方许可分别保留；衍生项目可改产品品牌，按许可证保留适用署名，不额外要求其界面永久显示官方名称。

安装状态记录每个自有文件的散列与恢复所有权。完整且未修改的旧两文件安装（如 1.6）可升级到四文件安装；新许可路径已有未管理文件时停止，不会擅自接管。所有选中年份先统一预检，中途失败会移除本次新增文件并恢复旧字节和状态。修改过、缺失或运行时不一致的旧安装不会被静默覆盖。

卸载只删除未改动的自有文件。用户修改过的 DLL 或许可文件及其所有权记录会保留，其他插件文件不会删除；恢复原字节后可重新完成卸载。不要把整个 Addins 年份目录当作本产品独占目录。

## 旧版本迁移

安装器保留自有旧桥接和已知旧入口的精确身份迁移机制。旧桥接会先验证文件和状态，再整体备份到 Revit 扫描目录之外；不需要或加载原引擎。当前用户旧清单可验证后备份，机器级注册只读检查，通过当前用户的自有覆盖注册处理，不改机器级原件。

安装失败回滚；卸载会尝试恢复已记录且未被修改的旧入口／桥接，因此卸载后重新出现旧按钮可能是恢复行为。未知注册、备份变化、文件锁或目标冲突会停止相关操作并保留材料。历史手工恢复脚本和过去冻结产物不是社区版正常安装入口，不随正常运行要求分发。

## 从合法本地引用构建

需要 Windows PowerShell、对应 .NET Framework reference assemblies／WindowsDesktop SDK，以及开发者自行合法取得的 Revit API 引用。仓库不附带 Autodesk API DLL 或官方／客户模型。安装器本身不需要读取用户模型或启动 Revit。

先在相邻导出器目录按 `reference-paths.example.json` 准备本地配置，再构建负载。配置写实际 API 和 framework 目录，使用者自己的本地配置不应提交源码仓库：

```powershell
# 在 tools/bright-revit-exporter 中运行；local-references.json 是自行填写的本地配置
.\Build-Exporter.ps1 -HostConfigPath .\local-references.json `
  -OutputPath .\artifacts\BV-1.8.0 -SkipFixture

# 在 tools/bright-revit-installer 中运行；仅构建，不安装
.\Build-BrightVisionInstaller.ps1 `
  -BuildManifestPath '..\bright-revit-exporter\artifacts\BV-1.8.0\build-manifest.json' `
  -OutputPath '.\artifacts\BV-1.8.0'
```

构建器读取已审阅的 LICENSE／NOTICE（社区仓库根目录或显式 `-LegalDirectory`），将其纳入负载和散列；不会把编译引用拷入分发包。输出目录必须不存在并与导出负载分离。若只构建本机一个目标，可在导出器指定 `-TargetIds`，再用安装器 `-AllowPartial` 显式构建局部包；局部包不能宣传为完整年份支持。

## 底层接口与验证

```powershell
.\Manage-BrightVisionPlugin.ps1 -Action Validate -BundlePath <module>
.\Manage-BrightVisionPlugin.ps1 -Action Detect -BundlePath <module>
.\Manage-BrightVisionPlugin.ps1 -Action Plan -BundlePath <module>
.\Manage-BrightVisionPlugin.ps1 -Action Install -BundlePath <module>
.\Manage-BrightVisionPlugin.ps1 -Action Uninstall -BundlePath <module>
```

Validate／Detect／Plan 不安装插件。Install／Uninstall 仍要求退出全部 Revit；即使使用隔离目标目录，也不关闭或绕过运行中保护。`-ReplaceLegacy:$false` 可保留旧入口；EXE 对应 `--replace-legacy false`。运行时覆盖只允许显式自定义目标目录，不用于默认真实安装。

`tests/Test-BrightVisionInstaller.ps1` 使用工作区惰性夹具，覆盖预检、版本升级、运行时、哈希、法律文件、部分写入回滚、修改文件保留和旧安装恢复，不运行真实插件。`tests/Test-PackagedInstaller.ps1` 另核实际 EXE 的版本／公司、内嵌资源、受管理安装和卸载，以及 Revit 官方清单解析；需提供合法本地 `RevitAddInUtility.dll` 路径，并只写隔离夹具。测试通过不代替在各宿主中的导出、试算或窗口验收。

当前生成物是否已签名、可公开下载，以及完整业务验收结果，都应在发行说明单独列明；源码许可证和文件 SHA 不是发布者签名。
