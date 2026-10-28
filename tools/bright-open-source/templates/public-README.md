# 崛亮视觉 · Bright Vision

Bright Vision 是本地 BIM 模型查看器和独立 Revit 导出／计量插件。官方产品由 **崛亮视觉** 维护，自有社区源码采用 **Apache-2.0**，保留适用的公司版权、NOTICE 与第三方许可。衍生项目可以修改品牌。

当前源码目标为查看器 **0.7.0**、插件／安装器 **1.7.0 开发预览**。0.7.0 新增可见人物的第三人称漫游、自由飞行与确定性本地模型助手。实际下载、签名状态、已测 Revit build、已知限制和业务验收以本仓库每次 Release 说明为准；源码或本地包完成不代表 0.7.0 已上传。

官方源码已发布至 [queqideliang/bright-vision](https://github.com/queqideliang/bright-vision)。以下是已经公开的预览版本；0.7.0 的可用附件请查看 [Releases](https://github.com/queqideliang/bright-vision/releases)：

**[下载 Windows 完整安装包 · 0.6.0](https://github.com/queqideliang/bright-vision/releases/download/viewer-v0.6.0/Bright.Vision-0.6.0-Setup.exe)** — 包含查看器与 Revit 插件 1.7.0。[发行说明](https://github.com/queqideliang/bright-vision/releases/tag/viewer-v0.6.0)

**[仅下载 Revit 插件 · 1.7.0](https://github.com/queqideliang/bright-vision/releases/download/revit-v1.7.0/BV-1.7.0-Setup.exe)** — 适用于已有查看器、只需安装导出／计量插件的电脑。[发行说明](https://github.com/queqideliang/bright-vision/releases/tag/revit-v1.7.0)

## 可以做什么

- **接收和查看模型：** 从 ZIP 查看几何、构件属性、模型结构和原始量，筛选、隔离、剖切、测量与保存视点。接收者无需安装 Revit；不能直接打开 RVT。
- **0.7 人物漫游与飞行：** 可见人形、第三人称跟随、手动地面起点、基础地面采样与射线阻挡；自由飞行和移动速度／眼高。需核对长度单位；不是完整人体碰撞，没有自动寻路或疏散计算。
- **0.7 本地模型助手：** 查询模型概览、类别、楼层、所选属性、缺失项与原始量，附构件定位依据。使用明确语法，例如 `查找：墙`、`缺少参数：防火等级`，不理解任意自然语言。它是本地规则程序，不是大语言模型，不调用云端 AI 或上传模型。
- **从 Revit 导出：** 选择模型名、三维视图、输出目录及内容，生成模型 ZIP，显示耗时和源文件／输出包大小。ZIP 是显示与交换包，不是完整 RVT 的无损压缩备份。
- **在 Revit 核查原始量：** 墙梁柱板量表、阶段／设计选项、问题状态、定位和 CSV；计量层映射、单一施工段归属及本地工程设置。
- **首批墙体试算：** 明确范围内的普通直墙与矩形 Opening 并集扣量、实际实体一致性验证和台账。同阶段主模型墙体的正体积交集会被拒绝，未 join 的重复墙也不能独立相加。

完整土建规则算量、地区清单／定额、模板量、任意门窗切洞、链接与梁柱板交接扣减、云端重算和完整计算书仍属后续范围。参见 [功能分期](tools/bright-revit-exporter/CIVIL_QUANTITY_ROADMAP.md)。

**Revit 2020 指定 build 的受限墙体 SDK／独立数据验收已通过**，算例与未覆盖项见 [验证范围](tools/bright-revit-exporter/tests/host/REVIT2020_VALIDATION.md)。真实窗口及其他年份／build 仍需另验。

## 使用与构建

上方链接对应已公开的开发预览附件，安装前请阅读发行说明中的签名状态及已知限制。官方更新配置采用上述固定仓库的预览频道；生产更新模块的公网检查、完整下载及 SHA-256 核验已通过实际验证，系统安装验收另行记录。旧安装版本需要先安装包含该配置的新客户端。

开发者先从 Web 查看器开始，不需要 Revit 或 Autodesk SDK：

```sh
cd tools/bim-viewer
npm ci
npm test
npm run build
npm run serve:web
```

使用 Node.js 26 和 npm。完整 Windows 包还需本机 .NET 编译工具、合法取得的 Revit API 引用、自己构建并显式审阅的插件模块。公共源码不附带 API DLL、预编译插件或模型；缺少模块时，仅相应集成测试明确 SKIP，不能据此宣称打包通过。

0.7.0 本轮检查仅使用 CLI 与独立的无界面 Chromium，不操作原生桌面、不启动 Electron 窗口或真实 Revit，不读取客户模型。CI 在 Web 构建后使用 `ubuntu-24.04` 的系统 Chrome 运行 `npm run test:background`，保留浏览器沙箱，校验合成模型中的导航、助手与零外部页面请求；实际 CI 结果另行确认。运行方式与覆盖边界见 [后台测试](tools/bim-viewer/BACKGROUND_TESTING.md)。

| 模块 | 说明 |
|---|---|
| [本地查看器](tools/bim-viewer/README.md) | [Web／Windows 构建、模块审核与更新配置](tools/bim-viewer/BUILDING.md) |
| [Revit 插件](tools/bright-revit-exporter/README.md) | [合法 API 引用构建](tools/bright-revit-exporter/BUILDING.md)、[原始量格式合同](tools/bright-revit-exporter/QUANTITY_SNAPSHOT.md) |
| [独立安装器](tools/bright-revit-installer/README.md) | 检测 Revit、版本升级、受管理文件、回滚及卸载 |
| [发行工具](tools/bright-open-source/RELEASE_PLAN.md) | 离线发行清单、草稿发布与更新配置，发布操作需维护者明确执行 |

Revit 编译目标为 2015—2026 共 14 个年份／运行时变体，逐宿主兼容性另验。构建产物可能因环境改变，不承诺逐字节可重现。文件 SHA 用于完整性检查，不能替代发布者签名。

## 开源与后续服务

本仓库只包含上述社区模块。云端平台、客户数据、厂商软件、私有配置和本机验证材料不随源码公开。基础社区源码可按 Apache-2.0 使用、修改、商业使用和分发；后续独立托管、支持或专业组件的范围与收费另行说明，不收回已有社区许可。

参见 [LICENSE](LICENSE)、[NOTICE](NOTICE)、[第三方通知](THIRD-PARTY-NOTICES.md)、[品牌说明](BRANDING.md)与[社区／服务边界](COMMUNITY-AND-SERVICES.md)。参与开发见 [CONTRIBUTING](CONTRIBUTING.md)，安全问题见 [SECURITY](SECURITY.md)。
