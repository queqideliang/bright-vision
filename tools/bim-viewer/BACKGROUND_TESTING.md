# Bright Vision 后台测试

大多数逻辑、网页交互和打包前检查可以通过命令行完成。默认测试不操作系统鼠标、不抢占已经打开的浏览器，也不启动 Revit。**后台仍会使用这台电脑的 CPU、内存、磁盘和部分图形资源**；要让本机不承担运行负荷，可把适合的检查放到 GitHub Actions。

## 日常命令

在 `tools/bim-viewer` 目录执行，先安装项目要求的 Node.js 和依赖；已有依赖时无需反复 `npm ci`。

```powershell
npm run typecheck
npm test
npm run build
node scripts/background-smoke.mjs
```

也可用 `npm run test:background` 调用同一个后台浏览器入口。

`build` 已包含类型检查。上面单列 `typecheck` 是方便开发时快速检查，可按需要省去重复步骤。命令失败时先修复该步，不要把旧版 `dist` 的成功结果视为新源码通过。

新的 `background-smoke.mjs` 只测试已经构建的 `dist`。它会启动一个**独立的无界面 Chromium 进程**，创建临时浏览器配置和独立 context，仅在 `127.0.0.1` 的随机空闲端口服务本次构建。页面点击和键盘事件发生在这个无界面页面内，不发送到 Windows 的当前窗口。不会连接用户已开的 Chrome、读取个人浏览器配置、启动 Electron 或 Revit。

测试默认使用 Playwright 安装的 Chromium；Windows 上也可使用已安装的 Chrome 或 Edge 可执行文件，但仍由 Playwright 创建独立临时配置，不使用个人资料。可显式指定路径：

```powershell
$env:BIM_TEST_BROWSER = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
node scripts/background-smoke.mjs
```

脚本不会自动下载浏览器。CI 首次准备浏览器可在依赖安装后执行 `npx playwright install --with-deps chromium`；本机已有可用 Chrome 时无需下载。若进程启动被运行环境拒绝，报告实际错误，并使用平台允许的进程授权流程；不要关闭安全软件或添加排除项来让测试通过。

## 检查范围与证据

| 检查 | 运行位置 | 能证明什么 |
| --- | --- | --- |
| `npm test`、`npm run typecheck` | 本地 CLI / GitHub Actions | 已有解析、数据、单位、查询和控制逻辑的回归；类型约束 |
| `npm run build` | 本地 CLI / GitHub Actions | 当前源码可以构建为查看器网页资源 |
| `node scripts/background-smoke.mjs` | 独立无界面浏览器 | 合成演示模型实际绘制、选择与隔离、漫游/飞行交互、输入框保护、本地模型助手和关闭重开；拦截并检查外部请求 |
| 真正的 Revit SDK 验收 | 有授权的 Revit Windows 宿主 | Revit API 几何、事务回滚、导出读取、插件装载等特定场景 |
| 原生桌面/安装器验收 | 专门安排的 Windows 交互会话 | Ribbon、原生对话框、安装和升级等宿主行为 |

浏览器测试只用内置四构件演示模型和脚本生成的一构件安全测试 ZIP，不读取客户 RVT/ZIP。后者验证模型名称、参数中的 HTML 片段只显示为文字。日志、截图和 `report.json` 写入 `test-results/background-时间-进程号/`，每轮使用新目录。报告包含实际通过的检查、构建首页哈希和没有测试的层级。失败返回非零退出码；不会把失败、取消或没有执行的场景写成通过。

```powershell
# 只检查基础浏览器功能；不覆盖漫游、飞行或助手。
node scripts/background-smoke.mjs --baseline-only
```

正式验收新功能时不要加 `--baseline-only`。本地模型助手当前是明确标注来源的有限规则查询；此测试不证明外部大模型质量，也不测试真实 AI 服务的授权、网络或费用。场景中的人物可见和相机移动同时保留截图证据；实际工程的人体碰撞、台阶通行和复杂地面适配需另行覆盖。

新的后台入口不会读取 `BIM_TEST_ELECTRON`、`BIM_TEST_PACKAGED`、`BIM_TEST_USER_DATA` 来启动桌面测试。现有 `npm run test:browser` 默认也是无界面 Chromium，但开启这类旧开关会执行额外 Electron 分支；需要“只做后台网页检查”时使用新的入口，不复用个人桌面配置。

在测试终端按 `Ctrl+C` 可以终止本次命令；脚本清理它自己启动的浏览器和本地 HTTP 服务。它不会关闭用户的程序。退出无界面测试与停止真正的 Revit 导出是两件事，不能互相替代。

## 放到 GitHub，不占本机运行资源

公开 CI 模板固定使用 GitHub 托管的 `ubuntu-24.04`，按 `npm ci`、`npm test`、`npm run build` 的顺序验证源码，再以 `BIM_TEST_BROWSER=/usr/bin/google-chrome` 执行 `npm run test:background`。脚本通过 Playwright 启动该系统浏览器的独立临时配置，不复用用户浏览器。runner 已有 Chrome 时不必额外下载 Playwright Chromium；其他环境可在依赖安装后显式执行 `npx playwright install --with-deps chromium` 准备浏览器。

安装依赖或额外准备浏览器需要访问软件分发源；模型测试页面只访问本轮的本地 HTTP 服务，任何外部页面请求都被拦截并导致验收失败。无需 xvfb、Electron、API Key 或自托管 runner。脚本保留 Chromium 沙箱，不修改 AppArmor、用户命名空间或其他系统安全策略。首次 Linux runner 兼容性须由实际 CI 确认，不把 Windows 上的 headless 结果写成 Linux 已通过。

截图／报告可作为受审查的 workflow artifact 保存；默认输出不随源码发布。不要上传客户模型、用户配置、私钥或本机 Revit SDK 文件。

GitHub 托管 runner 适合纯源码和浏览器测试，不能直接替代已授权的 Revit 安装。真实 Revit 验收需要另行准备有对应安装与许可的 Windows 宿主；普通 Node 命令没有 Revit API 内核，不能据此宣称所有 Revit 年份和所有几何都已通过。现阶段实际宿主范围以验收报告为准，编译通过不等于宿主运行通过。

Revit 启动时的发布者确认、Windows 安全提示等仍由用户在真实环境核对处理。本流程不绕过提示、不改 360 或 Windows 安全设置，也不自动启动真实 Revit。
