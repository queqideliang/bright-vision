# 崛亮视觉 · Bright Vision 的 GitHub 发布与更新

官方仓库已由用户确认为 [queqideliang/bright-vision](https://github.com/queqideliang/bright-vision)。客户端 `electron/update-release.json` 固定该仓库并启用 preview 订阅；本目录的准备脚本仅离线生成发行清单，不会自动上传或公开发布。项目采用标准开源，许可和署名另见 `legal/LICENSE` 与 `legal/NOTICE`；云端源代码先保持私有。

## 下载与更新的关系

GitHub 公共仓库放经审计的自有源代码。安装包放 Releases 附件；用户下载并安装含新更新能力的版本后，客户端才可检查 GitHub。旧的本地更新器不会因创建仓库就自动获得 HTTPS 更新能力。当前稳定 0.4 更新源、已冻结的 BV 1.6／查看器 0.5.1 产物不被本轮准备操作替换。

GitHub 每个 Release 文件必须小于 2 GiB，不能把大型安装包当作 Git 源文件提交；详见[官方 Releases 说明](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)。官方 API 的 `latest` 排除草稿和预发布；本产品独立区分 `viewer-vX.Y.Z` 与 `revit-vX.Y.Z`，客户端按固定仓库、标签前缀和预览／稳定标识筛选，见[官方 Releases API](https://docs.github.com/en/rest/releases/releases)。

客户端显式使用 `X-GitHub-Api-Version: 2026-03-10`，该版本在准备时为官方支持版本，见 [API 版本说明](https://docs.github.com/en/rest/about-the-rest-api/api-versions)。0.6.0 与 0.7.0 公开预览已分别完成生产更新模块的无认证检查、完整下载、独立 SHA-256 和 staging 核验；均未执行系统安装。后续每版附件仍需逐次验证，不能以 mock 或上一版结果代替。

当前公开预览是查看器 **0.7.0**、内嵌 Revit 插件 **1.7.0**。[viewer-v0.7.0](https://github.com/queqideliang/bright-vision/releases/tag/viewer-v0.7.0) 的源码提交为 `7f8001d40eaba14cfc2457ba893b36b168c55b82`，四个 Release 附件的长度与远程 SHA-256 已核对。生产更新线上测试通过 5 项检查、7 次无认证 HTTPS 请求，完整下载 122,995,529 字节并通过 `verifyStaged`；同版本不重复下载、stable 排除 preview。旧版本测试是 `currentVersion:0.6.0` 的协议输入模拟，未运行旧客户端或安装器。0.6.0 与独立 1.7.0 的原 Release／标签保留，两端版本不强行保持同一数字。

最新独立插件为 [BV 1.8.0](https://github.com/queqideliang/bright-vision/releases/tag/revit-v1.8.0)，对应源码 `997b1c2df62f67452b0165485c202d24f7284093`，已通过 [CI 40793987502](https://github.com/queqideliang/bright-vision/actions/runs/40793987502)。四个公开附件通过 9 次无认证 HTTPS 请求逐字节核对，安装包为 1,674,752 字节，下载缓存的 EXE 未执行。另已完成本机四年份 CLI 升级核验，但未启动 Revit。**这是独立附件发布／下载，不是插件自动更新 E2E**：目前没有插件自动更新器，查看器仍只订阅 `viewer-v*`，0.7 内置的 1.7 和原组件锁均未改变。

稳定订阅只接受非 `prerelease` 发布；预览订阅接受更高版本的预览或正式发布。schema 2 的 `channel` 对应实际 Release 标记，客户端分别暴露 `subscriptionChannel` 与 `releaseChannel`；同版本不重复安装。

## 生成可审查的发布目录

先取得最终安装包；如签名，先完成签名再计算散列，不在散列确认后改写 EXE。当前目标为已确认的官方公共仓库，输入文件与输出目录使用绝对路径：

```powershell
node tools/bright-open-source/prepare-github-release.mjs `
  --repository queqideliang/bright-vision --product viewer --channel preview --version 0.7.0 `
  --revit-version 1.7.0 --installer ABSOLUTE_INSTALLER_PATH --output NEW_ABSOLUTE_DIRECTORY
```

生成器离线复制最终安装包，写出 `update.json`、`SHA256SUMS.txt`、可编辑的 `release-notes.md` 与本地 `release-descriptor.json`。拒绝已有输出目录、其他产品文件名、重定向路径和变化中的输入；不运行 EXE、不读密钥、不联网、不写 `.git`。独立插件用 `--product revit --version 1.8.0`，安装包名为 `BV-1.8.0-Setup.exe`；该产品不是查看器的远程更新源。

```powershell
node tools/bright-open-source/prepare-github-release.mjs --verify PREPARED_DIRECTORY
```

schema 2 固定保留公司署名。以下以历史 0.6.0 展示字段，大小与散列是示意占位值，不是已发布附件的实际数据：

```json
{
  "schemaVersion": 2,
  "productId": "local.bim.modelviewer",
  "publisher": "崛亮视觉",
  "channel": "preview",
  "version": "0.6.0",
  "release": { "owner": "queqideliang", "repository": "bright-vision", "tag": "viewer-v0.6.0", "url": "https://github.com/queqideliang/bright-vision/releases/tag/viewer-v0.6.0" },
  "installer": { "fileName": "Bright.Vision-0.6.0-Setup.exe", "size": 12345, "sha256": "REPLACED_BY_THE_GENERATOR", "url": "https://github.com/queqideliang/bright-vision/releases/download/viewer-v0.6.0/Bright.Vision-0.6.0-Setup.exe" },
  "components": { "revitExporterVersion": "1.7.0" }
}
```

上例是格式示意，大小与散列不是实际产品值，不可直接作为更新清单。

## 发布步骤与 CI

源码构建与首次 Windows 完整包的本机审核步骤见 [查看器 BUILDING.md](../bim-viewer/BUILDING.md)。公开 CI 不包含 Autodesk DLL 或预编译模块，缺少模块时仅相关集成测试明确标为 SKIP。重新编译的 DLL 可能因构建环境而改变；维护者审核当前源码、安装助手和法律声明后，显式运行 `review-revit-bundle.mjs --acknowledge-reviewed-owned-source` 更新本机锁定文件，再执行完整模块测试。默认打包仍强制校验所有锁定字节，不信任任意下载模块，不宣称逐字节可重现。

`templates/source-ci.yml.example` 是可审查的源代码检查模板，复制到已经清理的公共仓库后才生效。它只在 GitHub 托管环境检查纯 JavaScript／TypeScript 与 Web 构建，不持有签名密钥、不创建 Releases，不将 Autodesk API DLL 分发到公共仓库。启用前将官方 action 标签固定到已审查的具体提交。完整 Revit 构建继续使用有合法本地 API 引用的构建环境。

Windows 合同测试 `tests/Test-PublishGitHubDraft.ps1` 将 GitHub 工具解析为自己的惰性记录脚本，验证参数传递、WhatIf、固定附件、仓库拒绝和稳定通道签名门槛；不调用真实 GitHub、不读取凭据、不运行安装程序。此测试与真实公司仓库发布验收分别记录。

确定清洁源码提交和远程标签后，可显式运行：

```powershell
tools/bright-open-source/Publish-GitHubDraft.ps1 -PreparedDirectory PREPARED_DIRECTORY -Repository queqideliang/bright-vision
```

该脚本重新校验最终字节，调用已登录的 GitHub CLI，只创建草稿并上传本次列出的附件；`--verify-tag` 要求远程标签已存在，不创建标签、不覆盖已有发布、不自动公开。草稿便于核对说明和所有文件后再发布；这是产品发布流程设计，不表示本次已经执行。参见[GitHub CLI 官方选项](https://cli.github.com/manual/gh_release_create)与[官方发布管理](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository)。稳定发布沿用项目的发布者签名政策，须有效 Authenticode、时间戳和明确的证书主体；未签名开发预览不能被描述为已解决杀毒误报或发布者信任问题。

## 客户端边界

- 主进程只读取包内固定仓库配置；渲染进程只能选择“本地目录”或“官方 GitHub”，不能提交任意 URL、账号、令牌或运行参数。
- 每次检查最多 3 页、每页 20 个 Release、每页最多 2 MiB；清单最多 64 KiB；安装包小于 2 GiB。超时与取消会终止下载。
- 仅使用 HTTPS，限制 API 仓库与发布路径；文件下载只允许 GitHub 官方发布存储重定向，最多 5 次。拒绝其他服务器、其他仓库、凭据 URL、端口和 HTTP 降级。
- 检查产品、发布者、版本、渠道、资源大小和 SHA-256。下载到独立随机缓存目录；不允许链接路径或把模型 ZIP 当更新程序。
- 下载模块不启动任何进程。安装前仍由用户确认，随后再次读取私有缓存校验散列，再以无 shell、无额外参数的方式启动该安装包。清单中的脚本／命令字段会被拒绝。
- 只请求公开版本与安装包；模型、工程设置、源文件路径不进入更新请求。SHA-256 验证完整性，不能单独证明签名发布者身份。

仓库身份与客户端配置现已确认；具体 Release 是否已上传、公开及可下载，须按当次实际验收记录核对。空仓库或只有源码时没有兼容的安装包更新。mock 测试通过不等于已公开安装包或已完成用户在线升级。
