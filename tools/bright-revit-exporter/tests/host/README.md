# BV 1.7 墙体试算：Revit 2020 开发验收

本目录只提供当前墙体试算的开发用单次 SDK 驱动，不是产品功能入口。源码公开不附带 Autodesk API DLL、Revit 安装程序、官方样例、用户 RVT 或历史实机日志；这些工具也不会进入产品 DLL 和安装器。

Revit 2020 指定 build 的受限墙体 SDK／独立数据核验已通过，具体算例、证据门槛和未覆盖项见 [验证范围](REVIT2020_VALIDATION.md)。

## 前提与范围

需要合法安装的 Revit 2020、对应 API 和 .NET Framework 参考程序集。默认安装路径使用系统 Program Files 下的 Autodesk/Revit 2020；其他位置可显式传 `-RevitPath`。构建使用编译引用，不运行厂商 API 程序集。

先构建完整 BV 1.7.0 的 24 份生产源码／14 变体，并通过安装器安装当前用户的 `2020-net45`。本次生产 build-manifest、安装 ownership state 和 DLL 的 SHA 必须一致。14 目标编译不表示 14 目标均已完成真实宿主验收。

驱动只在配置与随机 runId 匹配时启用，普通 Revit 启动时不自动测试。Launch 拒绝已有 Revit 进程。它复制本机安装目录中的 `Samples/rst_basic_sample_project.rvt` 到专用开发目录，通过官方模型命令行启动可见 Revit，并在有效 SDK 事件上下文验证独立墙体与显式开洞夹具。临时 TransactionGroup 必须回滚，恢复选择、核对文档状态；不保存样例，不切换 ActiveView，不关闭用户工程或 Revit。

地区清单／定额、模板量、全类型墙扣减、其他 Revit 年份、Ribbon 点击、原生工作台窗口和导出 ZIP 的 UI 流程不在这个驱动的验收范围。失败或超时不等于已通过。

## 构建与启动

从干净源码仓库根目录构建驱动，默认输出在本目录的 `artifacts/civil-wall-2020`：

```powershell
& tools/bright-revit-exporter/tests/host/Build-CivilWallHostAcceptance.ps1
```

可显式指定 `-RevitPath`、`-FrameworkDirectory` 和工作区内的 `-OutputPath`。提供者须自行合法取得引用，不能将本机 DLL 或生成目录提交仓库。

保存工程并退出全部 Revit 后，在已授权的本机开发验收中启动：

```powershell
& tools/bright-revit-exporter/tests/host/Invoke-CivilWallHostAcceptance.ps1 `
  -Action Launch `
  -ProductionBuildManifestPath tools/bright-revit-exporter/artifacts/BV-1.7.0/build-manifest.json
```

如果驱动或 Revit 使用其他位置，同时传入 `-DriverPath`、`-RevitPath`。Launch 核对完整目标矩阵、24 份源码及每个负载的 SHA、已安装 DLL 与 ownership state、驱动及其编译 API 的 SHA。不会跳过发布者验证、安全、许可或登录提示，也不会终止已有 Revit 会话。

报告和样例副本仅写仓库下的 `test/revit-civil-acceptance/phase3-20261028` 专用生成目录。Launch 返回本次随机 run 目录，其中 `output/civil-wall-host-status.json` 是阶段状态，`output/civil-wall-host-report.json` 是最终 PASS／FAIL 与宿主 build、夹具、回滚及原始量记录。不要把此生成目录或模型复制到公共仓库。

取得最终报告后，从仓库根目录独立核对来源、输入／输出散列、矩形并集、台账和 CSV：

```powershell
node tools/bright-revit-exporter/tests/verify-civil-wall-host.mjs RETURNED_ABSOLUTE_RUN_DIRECTORY
```

该校验器核对 SDK 捕获的实体证据，不是第二个原生几何会话，也不验证 Ribbon 或原生窗口。验证 PASS 与 SDK 运行 PASS 应分别记录。

## 清理与证据边界

测试结束后，传入 Launch 返回的精确 run 目录：

```powershell
& tools/bright-revit-exporter/tests/host/Invoke-CivilWallHostAcceptance.ps1 `
  -Action Cleanup -RunDirectory RETURNED_ABSOLUTE_RUN_DIRECTORY
```

Cleanup 只移除 `%APPDATA%/Autodesk/Revit/Addins/2020/BV.CivilWallAcceptance.<runId>.addin` 中路径、nonce、名称和 SHA 均匹配的自有临时清单。被改动的文件会保留并报错；不会关闭 Revit，也不删除生产插件、其他插件或样例。夹具回滚由驱动负责，不要把移除注册误认为取消正在执行的 SDK 测试。

正式结论应引用本次宿主 build、最终报告、所用源码／负载 SHA 和具体断言。源码或独立领域测试通过不代替真实 SDK 执行；真实 SDK 报告也不代替原生窗口的交互验收。历史不同版本的驱动和私有实机证据不随当前社区源码分发。
