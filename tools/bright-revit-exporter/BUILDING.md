# 从社区源码构建 Revit 插件

自有源码采用 Apache-2.0。编译引用需要开发者自行合法取得对应 Revit API 和 .NET reference assemblies；源码仓库不分发 Autodesk DLL、厂商安装程序或样例模型。

当前目标为 **BV 1.8.0 开发预览／第三期第二批**。新增面积与工程报表仍在开发和后台验证；不要将旧 1.7 安装包、Revit 2020 SDK 的 81 项断言或新版的编译成功，写成本版已安装、已发布或已实机验收。

在 Windows 上复制 `reference-paths.example.json` 为仓库外或被忽略的本机配置，填写实际目录。`apiDirectories` 可按年份或 `年份-runtime` 指定；完整版本需分别提供 2015—2026 API，2025／2026 有 net8、net10 两个目标。没有配置时，脚本只查 `ReferenceCache/refs/<年份-runtime>` 与 `ReferenceCache/refs/<年份>`，不会搜索磁盘或自动下载 API。

```powershell
# 先在已装有相应合法开发引用的机器编译一个目标。
.\Build-Exporter.ps1 -HostConfigPath C:\DevConfig\bright-revit.json `
  -TargetIds 2020-net45 -OutputPath artifacts\BV-1.8.0-local-2020 -SkipFixture

# 为完整安装器编译全部 14 个目标。
.\Build-Exporter.ps1 -HostConfigPath C:\DevConfig\bright-revit.json `
  -OutputPath artifacts\BV-1.8.0-local-all -SkipFixture
```

2015—2020 使用 net45 reference assemblies，2021—2024 使用 net48；现代目标需要 .NET SDK 和相应 .NET／Windows Desktop reference packs。net8/net10 优先使用显式配置或缓存，否则从 SDK 已安装的对应主版本选择最新引用包。脚本不再固定维护者个人磁盘路径或特定补丁版本。

输出清单保存源码、API 和载荷散列，编译结束再次检查源码没有变化。编译成功不代表在所有 Revit 年份完成实际加载。2025-net10 的具体宿主兼容性应按发行说明核对。

`-SkipFixture` 只控制构建脚本内的旧导出夹具；领域、读取适配、WPF 和实际宿主测试分别执行并记录。新的墙体单元测试是项目自有 DLL，不启动临时测试 EXE。运行真实宿主测试需要维护者明确准备专用样例副本、一次性注册和模型回滚，不能把用户正在编辑的工程当测试素材。

## 1.8 分层验证要求

- **纯领域 DLL：** 验证单侧轴向投影面积 `L × H − U`、带符号台账、旧体积规则，以及旧 CSV 的 25／11 列兼容性。独立矩形单元格基准验证并集，不加载 Revit 或桌面窗口。
- **报表 DLL：** 验证 `BV.Wall.Report/2` 的完整来源绑定、工程设置副本、计量层／施工段／原生楼层／类型分组、覆盖和缺值、筛选范围、单位、CSV 安全及关联来源；不能只比较最终合计。
- **读取与 API 编译：** 保留现有实体一致性和他墙正体积交集拒绝门槛，明确引用与输出 SHA。14 个目标全部编译成功也不等于 14 个目标都在真实宿主执行。
- **离屏 WPF 与安装包 CLI：** 验证界面数据与当前过滤／分组、失效状态、导出及受管理安装结构；不等同于真实 Revit 窗口点击或修改本机安装。
- **真实宿主：** 新版本需另外明确年份、build、专用算例及回滚证据。本轮只授权后台验证，不启动或控制 Revit，不复用 1.7 SDK 结果冒充 1.8 结果。

测试报告应包含版本、当前源码散列、执行范围与未执行项。开发中的新源码需等待冻结后再统一构建，不能把并发修改中的文件混入最终负载。已有冻结版本及其报告不覆盖、不重签或改写。

完整 Windows 产品还需先构建独立安装模块，再在查看器侧审核并准备该模块。C# 编译器和环境可能改变二进制散列，不能把“源码可重建”描述为已经实现逐字节相同的可复现构建；新的自建模块需要自己的审核和清单。

API 引用、本机配置、编译输出、宿主验收日志和真实模型不提交到公共源码仓库。安装程序作为独立 GitHub Release 资产分发，不放进 Git 历史。
