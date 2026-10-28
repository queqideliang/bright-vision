# Bright Vision 源码构建

Copyright © 2026 崛亮视觉 (Bright Vision)。自有公开源码采用 Apache-2.0；项目名称、公司名称与 BV 标识的商标使用权不由该许可证授予。保留 LICENSE、NOTICE 与第三方声明。

## Web 查看器

使用 Node.js 26 与 npm，在本目录执行：

```powershell
npm ci
npm test
npm run build
npm run serve:web
```

Web 构建不需要 Revit 或 Autodesk SDK。公开源代码中不附带 Revit 的 API DLL、客户模型、测试导出包或预编译插件。没有已审查插件模块的环境会明确 **SKIP** `prepare-revit` 的模块集成测试；其余测试正常执行。存在模块时这些集成测试必须完整执行，校验失败不能被当作跳过或通过。

## Windows 完整预览包

准备 Windows、本机 .NET Framework 编译器以及依法取得的目标 Revit API 引用；按 `../bright-revit-exporter/BUILDING.md` 和 `../bright-revit-installer/README.md` 从当前自有源码构建插件和安装模块。不要提交或分发 Autodesk SDK DLL。完整预览需要 2015–2026 的 14 个 year/runtime 目标；编译通过不表示已逐版本在 Revit 中验收。

`prepare-revit.mjs` 默认读取 `installer/revit-component-lock.json`，检查审查过的 1.7.0 模块全部 65 个文件：9 个根文件、14 个变体各 4 个文件（DLL、addin、LICENSE、NOTICE）。改变目录或下载一个同名包不会自动建立信任。

重新编译 C# 可能因时间戳或构建环境产生不同字节；这里不承诺逐字节可重现。开发者审核自己刚从当前源码构建的模块及导出器构建清单后，显式生成新的本机 lock：

```powershell
node scripts/review-revit-bundle.mjs `
  --bundle ABSOLUTE_LOCAL_MODULE_DIRECTORY `
  --exporter-build-manifest ABSOLUTE_LOCAL_EXPORTER_BUILD_MANIFEST `
  --acknowledge-reviewed-owned-source

$env:BRIGHT_REVIT_BUNDLE = 'ABSOLUTE_LOCAL_MODULE_DIRECTORY'
$env:BRIGHT_REVIT_TEST_BUNDLE = $env:BRIGHT_REVIT_BUNDLE
$env:BRIGHT_BUILD_PREVIEW_EXPORTER = '1'
npm test
npm run dist:win
```

审核命令不联网、不运行 DLL、不安装插件。它核对 24 个当前源码哈希、当前自有安装脚本、公司法律声明、14 个已编译目标、包中每个文件和清单的哈希，并拒绝额外文件及链接路径。构建清单是本地记录，不能证明任意第三方 DLL 真由那些源码编译；不要对未审查下载包执行该授权命令。lock 变更应进入代码审查；默认打包从不自动重写 lock。

预览构建编辑 EXE 的公司与产品资源，但不签名；未签名文件可能仍被安全软件提示。正式发行使用 `npm run dist:win:release`，要求有效签名与时间戳；不要关闭或绕过安全软件。构建助手若已丢失或哈希变化，构建会停止，不自动恢复被移除的程序。

## 官方更新配置

`electron/update-release.json` 是主进程的打包配置，目前固定为公司确认的 `queqideliang/bright-vision`、`enabled:true`、`channel:preview`；主进程只查询此官方仓库。衍生构建可设置 `enabled:false`、清空 owner/repository，此状态不联网。修改源配置必须重新打包才能改变已安装程序。界面不接受任意更新 URL、访问令牌、脚本或命令；仓库存在不表示已有可用 Release。

Releases 使用 `viewer-vX.Y.Z` 与 schema 2 清单；检查、下载、安装是分开的步骤。下载要通过版本、文件大小、SHA-256 与 GitHub 官方 HTTPS 地址边界校验；安装仍需用户确认。本地更新目录继续支持。准备发布见 `../bright-open-source/RELEASE_PLAN.md`。创建仓库本身不会给旧安装程序增加在线更新能力；用户需要先安装包含此客户端的新版本。

稳定订阅只接收正式发布；预览订阅可接收更高版本的预览或正式发布，方便预览用户升级到正式版。清单渠道必须与所选 Release 的真实 `prerelease` 标记一致；相同版本不重复安装。状态分别记录订阅渠道与候选发布渠道。
