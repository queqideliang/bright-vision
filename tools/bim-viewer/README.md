# Bright Vision · 崛亮视觉 BIM 模型查看器

独立的 Windows / Web BIM 查看器，直接读取 Bright Vision 导出的模型 ZIP。接收者无需安装 Revit；导出 RVT 时才需要对应版本的 Revit 与自有插件。

当前源码为 **0.6.0 预览版**，统一桌面包目标内含 **BV 1.7.0** 导出插件。官方仓库已确认为 [queqideliang/bright-vision](https://github.com/queqideliang/bright-vision)，Windows 更新器固定订阅该仓库的 preview 通道。仓库和客户端配置完成不表示安装包已发布；实际下载、版本与支持范围以可见 Releases 和验收清单为准。

## 当前功能

- 中英文界面，可保留当前模型与查看状态切换语言；官方标题、帮助、页脚显示 **崛亮视觉 · Bright Vision**。
- 直接打开或拖入模型 ZIP，支持 ZIP64、中文文件名、多模型目录、ZIP 套 ZIP 总包。
- 构件树、楼层／类别筛选、搜索、属性、定位、多选、隐藏、隔离和恢复显示。
- 透明背景、六平面剖切盒、剖切所选、两点表面测量、单位换算、本地命名视点。
- 类别统计、高面数审查、虚拟构件树；几何实例复用、空间合批、分层拾取、Worker 中索引与筛选。
- 原始量表读取导出快照，支持构件／类别／楼层汇总、范围筛选、三维联动、单位和来源标记及 CSV 导出。
- Windows 统一包可检测本机 Revit 并注册当前用户的导出插件；没有 Revit 不影响查看 ZIP。Revit 正在运行时延后替换插件。
- Update / 更新支持可信本地目录与固定官方 GitHub 源；主进程验证来源、版本、大小和 SHA-256，安装前需要用户确认。

模型和属性在本机处理。查看页面不允许外部网络连接；配置了真实官方仓库的主进程更新器只请求公开版本和安装包，模型、工程设置及源文件路径不会进入更新请求。

## 支持边界

**尚不支持直接读取 RVT**，也不接入任意 FBX、3ds Max 或其他格式。文件选择与拖放明确拒绝 RVT，不自动启动 Revit 转换。模型 ZIP 应包含预期的构件、几何、材质和属性数据；任意压缩包不能当作模型使用。

原始量表采用 Revit 实例参数的原始计量口径，明确换算为 m／m²／m³，保留缺失、错误、覆盖数和冲突；不把显示网格当作法定计量依据，不把缺失数据计为零。不同筛选范围可能重复涉及同一来源，不应直接相加成净量。格式见 [QUANTITY_SNAPSHOT.md](../bright-revit-exporter/QUANTITY_SNAPSHOT.md)。Revit 内工程设置与墙体试算由独立插件提供，不能等同于完整地区清单／定额算量软件。

输入 ZIP 最大 2 GiB（2,147,483,648 字节），当前解压总量上限 32 GiB、单文件 16 GiB、单条 JSON 记录 64 MiB、单张纹理 256 MiB。文件大小不代表渲染内存需求，边界测试不等同于真实大型模型性能验收。完整 Hi-Z、显存流送、自动低精网格、精确碰撞与版本对比等后续范围见 [ROADMAP.md](ROADMAP.md)。

Revit 2015–2026 共 14 个运行时构建目标；编译支持与真实宿主验收分别记录。不能将某个版本的 SDK 测试说成全部 Revit 版本均已实际测试。

## 使用与构建

安装版打开 Bright Vision 后选择导出的 ZIP；免安装版须保留整个应用目录。模型中的名称和属性按源数据展示。总包选中的 ZIP 在应用数据目录写入数据缓存，解包助手随 Windows 包固定提供；运行时不编译程序或自动恢复被安全软件移除的文件。

源码 Web 构建不需要 Revit。Node.js 26 环境下：

```powershell
npm ci
npm test
npm run build
npm run serve:web
```

开发服务只监听 `127.0.0.1`，静态 `dist/` 支持子目录部署。Windows 完整预览、本地合法 API 引用、显式插件审核 lock、自动化验收与签名策略见 [BUILDING.md](BUILDING.md)。没有插件模块的公开 CI 会明确跳过相关集成检查，不能据此声称统一安装包已验收。

## 更新与发布

当前 `electron/update-release.json` 已固定 `queqideliang/bright-vision`、`enabled:true`、`channel:preview`，Windows 主进程可检查该仓库公开的查看器 Releases；仍可选择可信本地更新目录。没有兼容公开 Release 时会明确提示尚无可用发布，不把仓库存在当作有新版本。界面不能提供任意 URL、访问令牌、脚本或运行命令。若衍生构建禁用配置且不填写仓库，则不会发起官方更新请求。

本地更新保留 `update.json` 与安装包同目录的方式。创建 GitHub 仓库本身不会更新旧的已安装程序；旧版需要先安装包含新更新器的版本。预览与稳定通道分开，当前稳定 0.4 更新源和历史冻结预览包不由本轮源码准备自动替换。

发布准备、schema 2 与草稿上传工具见 [RELEASE_PLAN.md](../bright-open-source/RELEASE_PLAN.md)。SHA-256 只验证字节完整性；预览未签名，正式发行须按发布策略完成 Authenticode 与时间戳。这里不承诺消除安全软件误报，也不要求关闭安全软件。

## 开源与公司署名

官方社区版显示 **崛亮视觉 · Bright Vision**。自有公开代码使用 [Apache-2.0](LICENSE)，保留 [NOTICE](NOTICE) 和适用的版权。衍生项目可以修改界面品牌，不额外要求所有衍生界面永久显示官方名称；许可证不授予 BV、公司或产品名称的商标使用权。免费基础版与未来独立 Pro 模块／服务的边界见 [社区与服务说明](../bright-open-source/legal/COMMUNITY-AND-SERVICES.md)。云端、第三方程序、Autodesk API DLL、客户模型、私密配置与凭据不在本次开源范围。

Three.js、fflate、Electron 等保留各自许可证，见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与构建后的 `licenses/`。公开源码和安装包仅应包含经审查可再分发的文件。
