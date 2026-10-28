# 崛亮视觉 · Bright Vision

Bright Vision 是本地 BIM 模型查看器和独立 Revit 导出／计量插件。官方产品由 **崛亮视觉** 维护，自有社区源码采用 **Apache-2.0**，保留适用的公司版权、NOTICE 与第三方许可。衍生项目可以修改品牌。

当前源码目标为查看器 **0.6.0**、插件／安装器 **1.7.0 开发预览**。实际下载、签名状态、已测 Revit build、已知限制和业务验收以本仓库每次 Release 说明为准；源码存在不代表所有功能或年份已实测。

用户已确认官方源码仓库为 [queqideliang/bright-vision](https://github.com/queqideliang/bright-vision)。源码与安装包正在准备公开；仓库推送、Release 附件上传及在线更新实测尚未由这份源码说明确认完成。

## 可以做什么

- **接收和查看模型：** 从 ZIP 查看几何、构件属性、模型结构和原始量，筛选、隔离、剖切、测量与保存视点。接收者无需安装 Revit；不能直接打开 RVT。
- **从 Revit 导出：** 选择模型名、三维视图、输出目录及内容，生成模型 ZIP，显示耗时和源文件／输出包大小。ZIP 是显示与交换包，不是完整 RVT 的无损压缩备份。
- **在 Revit 核查原始量：** 墙梁柱板量表、阶段／设计选项、问题状态、定位和 CSV；计量层映射、单一施工段归属及本地工程设置。
- **首批墙体试算：** 明确范围内的普通直墙与矩形 Opening 并集扣量、实际实体一致性验证和台账。同阶段主模型墙体的正体积交集会被拒绝，未 join 的重复墙也不能独立相加。

完整土建规则算量、地区清单／定额、模板量、任意门窗切洞、链接与梁柱板交接扣减、云端重算和完整计算书仍属后续范围。参见 [功能分期](tools/bright-revit-exporter/CIVIL_QUANTITY_ROADMAP.md)。

## 使用与构建

安装包发布后，用户可在本仓库 Releases 下载并核对对应限制。当前官方更新配置采用上述固定仓库的预览频道，线上检查与下载仍待发布后的实际验收。创建仓库不会给旧安装版本增加联网更新能力；需要先安装包含该配置的新客户端。

开发者先从 Web 查看器开始，不需要 Revit 或 Autodesk SDK：

```sh
cd tools/bim-viewer
npm ci
npm test
npm run build
npm run serve:web
```

使用 Node.js 26 和 npm。完整 Windows 包还需本机 .NET 编译工具、合法取得的 Revit API 引用、自己构建并显式审阅的插件模块。公共源码不附带 API DLL、预编译插件或模型；缺少模块时，仅相应集成测试明确 SKIP，不能据此宣称打包通过。

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
