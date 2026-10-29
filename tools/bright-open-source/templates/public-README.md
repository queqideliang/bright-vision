# 崛亮视觉 · Bright Vision

BV 是面向 Revit 的社区插件，提供模型 ZIP 导出、土建原始量核对、工程归属设置及部分普通墙的面积／体积试算。独立查看器可在没有 Revit 的电脑上打开导出的 ZIP；查看器不能直接读取 RVT。

1.11 在 **BV → bright vision** 增加 **检查更新** 和 **功能介绍／使用指南** 两个入口。使用指南包含从安装、导出到计量工作台的入门说明；检查更新访问固定的官方 GitHub 仓库，不上传模型或工程资料。

## 下载与入门

- [独立 Revit 插件 1.11.0 安装包](https://github.com/queqideliang/bright-vision/releases/download/revit-v1.11.0/BV-1.11.0-Setup.exe) · [1.11 发行说明、发布状态与 SHA-256](https://github.com/queqideliang/bright-vision/releases/tag/revit-v1.11.0)
- [Windows 查看器 0.7.0 安装包](https://github.com/queqideliang/bright-vision/releases/download/viewer-v0.7.0/Bright.Vision-0.7.0-Setup.exe) · [查看器发行说明](https://github.com/queqideliang/bright-vision/releases/tag/viewer-v0.7.0)

本次发布状态、附件是否可下载、签名状态和已知问题，以对应发行页为准。查看器 0.7.0 内置旧插件 1.7.0；需要两者时，先安装查看器，再安装独立插件 1.11，二者版本号分别维护。

1. 首次获得新入口，需要手动下载并安装一次独立插件 1.11。保存模型并退出全部 Revit，在安装器中选择检测到的年份并安装；重新启动 Revit 后生效。旧版本中没有此更新入口。
2. 在 **BV → bright vision → 功能介绍／使用指南** 阅读对应操作；导出模型时选择非模板三维视图和输出位置，生成 ZIP 后用查看器打开。原始量和墙体工作台直接在 Revit 内使用。
3. 后续进入 **检查更新**。默认包含开发预览版本，可取消勾选以只检查稳定版；尚无稳定版或网络失败会分别提示，本机比线上版本新时不提供降级。

详细步骤见 [插件使用说明](tools/bright-revit-exporter/USER_GUIDE.md)。

## 检查、下载与安装

在更新窗口核对版本与渠道，并打开官方发行页阅读更新说明。发现新版后可下载，文件大小与 SHA-256 校验通过后才可打开安装器。**下载并校验完成、安装器已打开，均不表示插件已经更新。** 请保存并退出全部 Revit，再在安装器中升级，重新打开 Revit 后使用新版本；运行中的插件 DLL 不作热替换。

开发预览可能有待完成的能力或验证范围。SHA-256 用于完整性检查，不能代替发布者签名；请核对发行说明。

## 功能范围与源码

计量功能仍有明确边界：只处理已实现并通过几何证明的普通直墙及限定交接体积，未支持项和失败项单列；交接体积不能除以厚度冒充新的面积。完整地区清单／定额、模板量、任意族、链接及墙梁柱板完整扣减仍属后续范围。参见 [分期路线](tools/bright-revit-exporter/CIVIL_QUANTITY_ROADMAP.md)。

1.11 的生产清单为 42 份 C# 源码、2015—2026 共 14 个年份／运行时编译目标。编译、纯协议测试与离屏 WPF 不等于每个 Revit 宿主的实机验收；1.10 及更早版本的实机证据不能继承为 1.11 结论。具体版本的验证范围以发行说明为准。

开发者见 [插件构建](tools/bright-revit-exporter/BUILDING.md)、[查看器构建](tools/bim-viewer/BUILDING.md)和[安装器](tools/bright-revit-installer/README.md)。构建需自行合法取得 Revit API 与相应 .NET 引用；本仓库不附带 Autodesk DLL、厂商程序、客户模型、SDK 运行报告或本机配置。查看器 0.7 的 105 份运行文件和内嵌插件组件锁保持其版本范围。

## 开源与署名

Copyright 2026 崛亮视觉（Bright Vision）。自有社区源码采用 [Apache License 2.0](LICENSE)，使用、修改与分发请保留适用的版权及 [NOTICE](NOTICE)；第三方组件保留各自许可，见 [第三方通知](THIRD-PARTY-NOTICES.md)。另见 [品牌说明](BRANDING.md)、[社区与服务边界](COMMUNITY-AND-SERVICES.md)、[参与开发](CONTRIBUTING.md)及[安全说明](SECURITY.md)。
