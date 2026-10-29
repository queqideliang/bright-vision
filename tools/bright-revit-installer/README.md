# BV / 崛亮视觉 · Bright Vision Revit 插件安装器

**版本范围说明：** 此公开源码包含 BV 1.11 的检查更新与功能介绍／使用指南入口；安装和功能入门见 [插件使用说明](../bright-revit-exporter/USER_GUIDE.md)。下文 1.10、1.9、1.8 的本机部署、发布状态及实机证据保留其历史语境，不作为 1.11 当前线上状态或本版实机通过结论。本次发布情况以 [1.11 发行页](https://github.com/queqideliang/bright-vision/releases/tag/revit-v1.11.0) 为准。

当前安装器源码面向 **BV 1.11.0 开发预览**：在 BV → bright vision 面板增加“检查更新”和“功能介绍／使用指南”。功能介绍内置离线正文；检查更新只访问官方 GitHub，不读取或上传模型。先检查版本、下载并校验安装包，再由使用者点击打开安装器；必须保存工程并退出全部 Revit，完成安装后重开生效。

首次获得这两个入口需手工安装 1.11 一次；旧 1.10／1.8 不会自动长出入口。下载与发布状态以 [官方 Release](https://github.com/queqideliang/bright-vision/releases) 为准，功能教程见插件内帮助及 [源码使用指南](https://github.com/queqideliang/bright-vision/blob/revit-v1.11.0/tools/bright-revit-exporter/USER_GUIDE.md)。当前属于开发预览，默认可检查预览版本，取消勾选后只检查稳定版；尚无稳定版时会明确说明。

本版完成 42 份生产源码／14 编译目标，更新协议、假 HTTP／缓存和离屏界面分别验证；真实 .NET 网络实现已完成匿名官方包下载和校验。以上不等于已在所有 Revit 宿主验收，也不继承 1.10 的 340 项 SDK 断言作为本版实机通过。安装包及 DLL 当前未签名，发布者确认由使用者核对。

## 1.10 历史记录

上一批安装器面向 **BV 1.10.0 交接核算开发版**：新增独立的墙—墙交接体积入口、完整邻墙组件、明确确认的优先顺序、逐条残量扣减、方案保存与几何绑定。它不推导交接后面积，也不包含墙梁柱板完整增扣、跨层切量或地区规则。本机 Revit 2017／2018／2020／2026 已通过 manager CLI 升级至 1.10；指定 Revit 2020 SDK 的 340 项断言／10 场景及独立数据复核通过，四年份部署不等于四年份功能实测，真实 Ribbon／窗口另验。1.10 尚未公开发布，公开独立插件仍为 1.8.0。安装见 下载与安装简明指南（本机验证资料，不随源码公开），分层证据见 1.10 验证索引（本机验证资料，不随源码公开）。原 1.9／1.8 附件和证据保持原范围，下文为历史状态。

上一批 **BV 1.9.0 开发版**增加默认自有项目几何模板、规则复制／导入导出、工程规则版本及批次绑定、可取消读取、有限真实矩形门窗切洞。1.9 曾部署本机四年份，现已升级至 1.10；1.9 尚未公开发布，其构建和 SDK 证据单独保留，不能用旧安装包实现新功能。第三期仍在开发，墙梁柱板交接、跨层切量及地区清单／定额尚未完成。下文 1.8 下载与验收是历史发行记录，不改写其冻结附件。

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
