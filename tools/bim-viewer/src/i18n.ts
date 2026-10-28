export type Language = 'zh' | 'en';
export const LANGUAGE_STORAGE_KEY = 'bright-vision.language';

export const englishMessages: Readonly<Record<string, string>> = {
  '模型助手': 'Model assistant', '漫游与飞行': 'Walk and fly',
  '人物漫游与飞行': 'Character walk and flight', '本地模型助手': 'Local model assistant',
  '打开“漫游与飞行”，核对模型长度单位。选择“人物漫游”后点击模型地面放置人物；WASD 行走，右键环看，Shift 加速。选择“自由飞行”后点击三维窗口启用，Q/E 升降；Esc 返回旋转查看。': 'Open Walk and fly and verify the model length unit. Choose Character walk, then click a model floor to place the character. Use WASD, right-drag to look and Shift for speed. Choose Free flight and click the 3D view to activate; Q/E moves down/up. Esc returns to orbit.',
  '打开“模型助手”，可查询模型概览、类别数量、楼层分布、所选属性、缺失项和原始量，也可输入“查找：关键词”或“缺少参数：参数名”。结果附构件定位依据；切换模型或查询范围后请重新查询。此版本使用本地有限规则查询，模型数据不上传。': 'Open Model assistant for a summary, category counts, levels, selected properties, missing data and raw quantities. You can also enter “find: keyword” or “missing property: name”. Results include component location evidence. Query again when the model or scope changes. This version uses limited local rule queries; model data is not uploaded.',
  '人物漫游采用第三人称跟随视角；飞行可自由查看模型。': 'Walk with a visible character and follow camera, or fly freely through the model.',
  '导航模式': 'Navigation mode', '旋转查看': 'Orbit', '人物漫游': 'Character walk', '自由飞行': 'Free flight',
  '未知，请先核对': 'Unknown — verify first', '移动速度（m/s）': 'Movement speed (m/s)', '人物眼高（m）': 'Character eye height (m)',
  '退出导航': 'Exit navigation', '重新放置人物': 'Place character again',
  '打开模型后可使用人物漫游和飞行。': 'Open a model to walk with a character or fly.',
  '请先核对并指定模型长度单位，再进入导航。': 'Verify and set the model length unit before entering navigation.',
  '旋转查看模式。可选择人物漫游或自由飞行。': 'Orbit mode. Choose character walk or free flight.',
  '请在三维窗口点击可见地面，放置人物起点。': 'Click a visible floor in the 3D view to place the character.',
  '前方表面或障碍未通过当前检测，本次移动已停止。': 'The next surface or obstacle did not pass the current checks. Movement stopped.',
  '点击三维窗口以启用移动；输入框和面板中不会行走。': 'Click the 3D view to activate movement. Typing in fields and panels does not move the character.',
  '人物漫游中 · WASD 移动 · 右键环看 · Esc 退出': 'Character walk · WASD to move · Right-drag to look · Esc to exit',
  '自由飞行中 · WASD 移动 · Q/E 升降 · Esc 退出': 'Free flight · WASD to move · Q/E up/down · Esc to exit',
  '人物漫游 · WASD 移动 · 右键环看 · Esc 退出': 'Character walk · WASD · Right-drag to look · Esc to exit',
  '自由飞行 · WASD / Q E · Esc 退出': 'Free flight · WASD / Q E · Esc to exit',
  'WASD 移动，右键拖动环看，Shift 加速；飞行时 Q/E 升降。点击三维窗口后启用键盘，Esc 返回旋转查看。': 'WASD to move, right-drag to look and Shift to speed up; Q/E moves down/up in flight. Click the 3D view to activate the keyboard. Esc returns to orbit.',
  '人物需放置在可确认的模型表面。当前为基础表面检测与射线阻挡，复杂楼梯、狭缝和完整身体碰撞尚待扩展。': 'Place the character on a verified model surface. Basic surface and ray checks are provided; complex stairs, narrow gaps and full body collision need further work.',
  '移动速度需在 0.1 至 10 m/s 之间。': 'Movement speed must be between 0.1 and 10 m/s.',
  '人物眼高需在 0.8 至 2.2 m 之间。': 'Character eye height must be between 0.8 and 2.2 m.',
  '构件量表': 'Quantity schedule',
  '量表范围': 'Schedule scope', '分组方式': 'Group by',
  '当前筛选': 'Current filter', '当前选择': 'Current selection',
  '按构件': 'Component', '按类别': 'Category', '按楼层': 'Level',
  '导出 CSV': 'Export CSV', '上一页': 'Previous', '下一页': 'Next',
  '长度': 'Length', '面积': 'Area', '体积': 'Volume',
  '构件 / 分组': 'Component / group', '计量对象': 'Quantity objects',
  '搜索：{0}': 'Search: {0}',
  '涉及来源的整体量，未按楼层拆分': 'Whole-source quantities, not split across levels',
  '正在更新筛选结果，请稍候…': 'Updating filter results. Please wait…',
  '范围：{0}。显示操作不裁剪计量几何；“当前选择”随选中对象变化。': 'Scope: {0}. Display operations do not clip quantity geometry; Current selection follows selected objects.',
  '来源：Revit 实例参数，采用其原始计量口径；本表不计算地区规则扣减。点击名称定位构件。': 'Source: Revit instance parameters with their original definitions. Regional measurement deductions are not calculated. Click a name to locate components.',
  '{0} 个计量对象 · {1} 条模型记录 · 合并 {2} 条重复来源记录': '{0} quantity objects · {1} model records · {2} duplicate source records merged',
  '有效 {0} / {1}': 'Available {0} / {1}',
  '此模型包没有计量快照，当前仅统计对象数。请用 BV 1.4.0 或更新插件重新导出；缺失量不会按零计算。': 'This package has no quantity snapshots. Only object counts are shown. Re-export with BV 1.4.0 or later; missing quantities are not treated as zero.',
  '部分指标缺失、无效或不可读取，合计只包含有效值。将鼠标停在数值上可查看覆盖情况。': 'Some metrics are missing, invalid or unreadable. Totals include available values only. Hover over a value to see its coverage.',
  '数值合计超出可表示范围，请缩小筛选范围。': 'The total exceeds the supported numeric range. Narrow the filter.',
  '有效 {0} · 缺失 {1} · 无效 {2} · 读取失败 {3}': 'Available {0} · Missing {1} · Invalid {2} · Read failed {3}',
  '定位 {0} 个计量对象': 'Locate {0} quantity objects',
  '已合并 {0} 条重复记录': '{0} duplicate records merged',
  '请先选择要统计的构件。': 'Select components to include in the schedule.',
  '当前筛选没有构件。': 'No components match the current filter.',
  '第 {0} / {1} 页 · 共 {2} 行': 'Page {0} / {1} · {2} rows',
  '已导出当前范围的量表 CSV。': 'The quantity schedule CSV was exported for the current scope.',
  '量表导出未完成，请缩小筛选范围后重试。': 'The schedule could not be exported. Narrow the filter and try again.',
  '文件在本机处理': 'Files stay on this device',
  'Revit插件': 'Revit plugin',
  'Revit导出插件': 'Revit exporter',
  '管理Revit插件': 'Manage Revit plugin',
  'Windows 桌面版包含 Revit 导出插件，可在这里查看安装状态并安装或修复。': 'The Windows desktop app includes the Revit exporter. Check its status and install or repair it here.',
  '没有 Revit 也能直接查看已导出的模型 ZIP。': 'You can open exported model ZIP files without Revit.',
  '关闭Revit插件管理': 'Close Revit plugin management',
  '随应用提供的导出插件：{0}': 'Included exporter: {0}',
  '未检测到 Revit。仍可直接打开导出的模型 ZIP，无需安装 Revit。': 'No Revit installation was detected. You can still open exported model ZIP files without installing Revit.',
  '已检测到 Revit，可安装或修复导出插件。': 'Revit was detected. You can install or repair the exporter.',
  '导出插件已注册。重启 Revit，在 BV → bright vision 面板打开“土建原始量工作台”或“本地导出模型 ZIP”。': 'The Revit plugin is registered. Restart Revit, then open the Civil raw quantity workbench or Export model ZIP from BV → bright vision.',
  'Revit 正在运行。请保存模型并关闭全部 Revit 窗口，再点“重新检测”。': 'Revit is running. Save your models and close all Revit windows, then click Refresh.',
  '插件操作未完成。请重新检测后重试，或查看详情。': 'The plugin operation could not be completed. Refresh and try again, or view the details.',
  '正在处理 Revit 插件，请稍候…': 'Processing the Revit plugin. Please wait…',
  '网页版不提供本地插件安装。请使用 Windows 桌面版管理 Revit 插件。': 'The web version cannot install local plugins. Use the Windows desktop app to manage the Revit plugin.',
  '正在检测 Revit 与插件注册状态…': 'Checking Revit installations and plugin registration…',
  '检测到的 Revit': 'Detected Revit', '插件注册': 'Plugin registration',
  '运行环境待确认': 'Runtime needs verification', '暂不支持此运行环境': 'Runtime not supported',
  '已注册': 'Registered', '未注册': 'Not registered', '未找到可管理的 Revit 版本。': 'No Revit versions are available to manage.',
  '导出插件已包含在本应用中，无需另行下载安装包。安装新的 Revit 后，也可在这里补注册。': 'The exporter is included with this app. No separate installer download is needed. Return here to register it after installing another Revit version.',
  '安装或修复前请保存模型并关闭全部 Revit 窗口。卸载插件会保留本工作台，并按安装记录恢复原有导出入口。': 'Save your models and close all Revit windows before installing or repairing. Uninstalling the plugin keeps this workspace and restores previous exporter registrations recorded during installation.',
  '查看详情（原始诊断）': 'View details (original diagnostic)', '重新检测': 'Refresh',
  '安装 / 修复插件': 'Install / repair plugin', '卸载插件': 'Uninstall plugin',
  '使用说明': 'Help', '更新': 'Update', '打开模型': 'Open model', '切换界面语言': 'Switch interface language',
  '模型构件': 'Model components', '模型结构': 'Model hierarchy',
  '搜索名称、ID 或族': 'Search name, ID, or family', '搜索构件': 'Search components',
  '楼层': 'Level', '类别': 'Category', '按楼层筛选': 'Filter by level', '按类别筛选': 'Filter by category',
  '全部楼层': 'All levels', '全部类别': 'All categories',
  '尚未打开模型': 'No model is open', '未打开模型': 'No model open',
  '隔离筛选结果': 'Isolate results', '打开模型后，在这里浏览构件和楼层。': 'Open a model to browse components and levels here.',
  '显示更多构件': 'Show more components', '三维模型': '3D model', '查看工具': 'Viewing tools',
  '显示整个模型（Home）': 'Fit the entire model (Home)', '全部居中': 'Fit all',
  '定位选中构件（F）': 'Focus selected components (F)', '定位': 'Focus',
  '隐藏选中构件（H）': 'Hide selected components (H)', '隐藏': 'Hide',
  '只显示选中构件（I）': 'Isolate selected components (I)', '隔离': 'Isolate',
  '恢复显示全部构件': 'Show every component', '显示全部': 'Show all', '显示': 'Show',
  '关闭模型': 'Close model', '模型分析工具': 'Model analysis tools', '未选择构件': 'No components selected',
  '隔离时保留透明背景': 'Ghost context on isolate', '剖切盒': 'Section box', '测量': 'Measure',
  '视点': 'Viewpoints', '统计与审查': 'Statistics & review', '模型工具': 'Model tools', '关闭模型工具': 'Close model tools',
  '用六个边界控制查看范围，或围绕选中构件剖切。': 'Set six boundaries to limit the view, or section around selected components.',
  '剖切所选': 'Section selection', '剖切全部': 'Section all', '关闭剖切': 'Clear section',
  '选中构件边距': 'Selection padding', '最小值': 'Minimum', '最大值': 'Maximum',
  'X 最小值': 'X minimum', 'X 最大值': 'X maximum', 'Y 最小值': 'Y minimum', 'Y 最大值': 'Y maximum', 'Z 最小值': 'Z minimum', 'Z 最大值': 'Z maximum',
  '应用剖切范围': 'Apply section bounds', '尚未启用剖切；坐标使用模型原始单位。': 'Section is off. Coordinates use the original model units.',
  '剖切已启用；盒外部分隐藏，坐标使用模型原始单位。': 'Section is on. Parts outside the box are hidden; coordinates use the original model units.',
  '开启后依次点击模型表面的两个点，计算直线距离。': 'Start measuring, then click two points on model surfaces to measure their straight-line distance.',
  '开始测量': 'Start measurement', '结束拾点': 'Stop picking', '清除测量': 'Clear measurement',
  '模型长度单位': 'Model length unit', '未知，保留模型单位': 'Unknown; keep model units',
  '米（m）': 'Meters (m)', '毫米（mm）': 'Millimeters (mm)', '英尺（ft）': 'Feet (ft)', '显示单位': 'Display unit', '模型单位': 'model units',
  '尚未测量': 'No measurement yet', '旧模型包未声明单位时，请核对源模型后指定。当前是表面两点直线距离，不含顶点或边吸附。': 'For packages without a declared unit, check the source model before choosing one. This measures between surface points without vertex or edge snapping.',
  '保存相机、选择、显示和剖切状态，视点保存在本机。': 'Save the camera, selection, visibility, and section state. Viewpoints stay on this device.',
  '视点名称': 'Viewpoint name', '例如：二层设备检查': 'For example: Level 2 equipment review', '保存当前视点': 'Save current viewpoint',
  '已保存视点': 'Saved viewpoints', '恢复视点': 'Restore viewpoint', '删除视点': 'Delete viewpoint',
  '同一模型重新打开后可恢复；关闭程序不会删除保存的视点。': 'Restore a viewpoint when reopening the same model. Saved viewpoints remain after closing the app.',
  '统计当前筛选结果。点击类别筛选，点击构件定位。': 'Statistics for the current results. Click a category to filter or a component to focus it.',
  '按类别统计': 'By category', '三角面最多的构件': 'Components with the most triangles',
  '面数按导出几何及实例累计，可辅助寻找高开销构件，不等同于工程量。碰撞检测、模型版本对比和参数审查在后续阶段交付。': 'Triangle counts include exported geometry and instances. They help identify costly components and are not quantities. Clash detection, version comparison, and parameter audits are planned for later stages.',
  '模型工作台': 'Model workspace', '把模型带到眼前': 'Bring your model into view', '直接打开模型 ZIP，': 'Open an exported model ZIP to',
  '查看几何、楼层与构件信息。': 'explore geometry, levels, and components.', '选择模型文件': 'Choose model file',
  '先体验演示模型': 'Try the demo model', '模型最大 2 GB · 本机处理 · 支持拖放文件': 'Models up to 2 GB · Local processing · Drag and drop files',
  '正在打开模型': 'Opening model', '读取文件': 'Reading file', '取消加载': 'Cancel loading', '松开以打开模型': 'Drop to open the model',
  '左键旋转 · 滚轮缩放 · 右键平移': 'Left drag: orbit · Wheel: zoom · Right drag: pan',
  '构件属性': 'Component properties', '构件信息': 'Component information', '取消选择': 'Clear selection',
  '查看构件信息': 'Inspect a component', '点击模型或左侧列表中的构件，': 'Click a component in the model or list', '查看它的名称、类别和参数。': 'to see its name, category, and parameters.',
  '准备就绪': 'Ready', '模型未能打开': 'Model could not be opened', '关闭提示': 'Dismiss',
  '选择包内模型': 'Choose a model in the package', '取消选择模型': 'Cancel model selection', '模型': 'Model',
  '请选择已导出的模型 ZIP。原始 RVT 暂不支持独立读取，不会启动外部建模软件。': 'Choose an exported model ZIP. Raw RVT files cannot be opened independently; no authoring application will be started.',
  '打开选中模型': 'Open selected model', '关闭使用说明': 'Close help',
  '选择已导出的模型 ZIP 或模型总包，也可直接拖进窗口。全部解析、显示和属性查询均在当前设备进行。': 'Choose an exported model ZIP or a package containing models, or drag it into the window. Parsing, rendering, and property queries run on this device.',
  '查看与选择': 'Navigation and selection',
  '左键拖动旋转，右键拖动平移，滚轮缩放。点击构件查看属性；使用“定位”“隐藏”“隔离”调整视图。“显示全部”恢复隐藏的构件。': 'Drag with the left button to orbit, drag with the right button to pan, and use the wheel to zoom. Click a component for properties. Focus, Hide, and Isolate adjust the view; Show all restores hidden components. Ctrl-click adds or removes components from the selection.',
  '支持范围': 'Supported files',
  '模型 ZIP 的文件上限为 2 GB（2,147,483,648 字节），在当前设备分段解析。总包可直接打开，再选择包内模型。查看器不会启动任何 Revit；原始 RVT 的独立读取尚未实现。': 'Model ZIP files up to 2 GB (2,147,483,648 bytes) are read in chunks on this device. Open a model package to choose a model inside it. This viewer does not start Revit and cannot independently read raw RVT files.',
  '数据核对': 'Checking the data',
  '属性按文件中的实例、类型、族信息原样展示。缺失或无效引用会在模型概览中提示；当前仍需对照原 RVT 验收坐标、单位和参数完整性。': 'Instance, type, and family properties are shown as supplied by the file. Missing or invalid references appear in the model overview. Verify coordinates, units, and parameter completeness against the original RVT.',
  '快捷键': 'Keyboard shortcuts', 'Home：全部居中　F：定位　H：隐藏　I：隔离　Esc：取消选择': 'Home: fit all · F: focus · H: hide · I: isolate · Esc: clear selection or stop picking',
  '预览版 {0}': 'Preview {0}', '包内找到 {0} 个模型，请选择要打开的一个。': 'Found {0} models in the package. Choose one to open.',
  ' · RVT 暂未支持直读': ' · Raw RVT not supported', '已选 {0} 个构件 · Ctrl 点击多选': '{0} components selected · Ctrl-click to multi-select',
  'Ctrl 点击可多选': 'Ctrl-click to multi-select', '已隐藏 {0} 个构件': '{0} components hidden', '{0} 个透明背景构件': '{0} ghosted context components',
  '模型已就绪': 'Model ready', '{0} 构件 · {1} 三角面': '{0} components · {1} triangles',
  '{0} 次绘制 · {1} 个空间批次 · {2} 个复用实例 · {3} 个有几何的显示构件。三角面为 GPU 提交数量，可能包含隐藏的面。渲染数组 {4} MiB；空间索引估计 {5} MiB；最近拾取 {6} 批 / {7} 面 / {8} ms。': '{0} draw calls · {1} spatial batches · {2} reused instances · {3} visible components with geometry. Triangle counts are GPU submissions and can include hidden faces. Render arrays: {4} MiB; estimated spatial index: {5} MiB; latest pick: {6} batches / {7} triangles / {8} ms.',
  '正在筛选…': 'Filtering…', '{0} 个构件': '{0} components', '隔离筛选得到的 {0} 个构件': 'Isolate the {0} filtered components',
  '没有符合条件的构件': 'No matching components', '展开': 'Expand', '折叠': 'Collapse', '选择本组 {0} 个构件': 'Select all {0} components in this group',
  '未分类': 'Uncategorized', '未提供族': 'Family not provided', '未提供类型': 'Type not provided', '{0}构件 {1}': '{0} component {1}',
  '模型概览': 'Model overview', '选择构件，查看文件中保留的参数。': 'Select a component to inspect the parameters supplied by the file.',
  '构件': 'Components', '几何记录': 'Geometry records', '数据提示 · {0}': 'Data notices · {0}',
  '当前模型未提供这类属性。': 'This model does not provide these properties.', '未提供': 'Not provided',
  '当前选择集合': 'Current selection', '已选 {0} 个构件': '{0} components selected', '{0} 个类别 · {1} 个三角面': '{0} categories · {1} triangles',
  '定位、隐藏、隔离和剖切均作用于当前选择集合。Ctrl 点击增减选择，单击一个构件查看具体属性。': 'Focus, Hide, Isolate, and Section apply to the current selection. Ctrl-click to add or remove components; click one component to inspect its properties.',
  '复制构件 ID': 'Copy component ID', '构件 ID 已复制': 'Component ID copied', '构件 ID：{0}': 'Component ID: {0}',
  '族': 'Family', '类型': 'Type', '属性来源': 'Property source', '实例': 'Instance', '、': ', ',
  '构件索引加载已取消。': 'Component indexing was canceled.', '构件索引进程中断。': 'The component index worker stopped.',
  '构件索引进程中断，请重新打开模型。': 'The component index worker stopped. Reopen the model.',
  '当前筛选：{0} 个构件，{1} 个三角面（含重复实例）。渲染数组 {2} MiB，复用实例 {3} 个。': 'Current results: {0} components and {1} triangles, including repeated instances. Render arrays: {2} MiB; reused instances: {3}.',
  '{0} 面': '{0} triangles', '已按类别筛选：{0}': 'Filtered by category: {0}', '{0} 面 · {1}': '{0} triangles · {1}',
  '当前筛选没有可统计的三角面。': 'No triangles to count in the current results.',
  '已选第一点，请选择第二点': 'First point selected. Pick the second point.', '请在模型表面选择第一点': 'Pick the first point on a model surface.',
  '{0} 个视点保存在本机。将自动恢复相机、选择、显示和剖切。': '{0} viewpoints saved locally. Restoring one applies its camera, selection, visibility, and section state.',
  '尚未保存视点。视点只保存在本机当前应用中。': 'No viewpoints saved yet. Viewpoints are stored locally in this app.',
  '视点未保存：本机存储空间不足或不可用。可减少视点数量后重试。': 'The viewpoint could not be saved because local storage is full or unavailable. Remove unused viewpoints and try again.',
  '两点测量': 'Two-point measurement', '命名视点': 'Named viewpoints', '准备三维场景': 'Preparing the 3D scene',
  '模型包未声明长度单位。请核对源模型后指定；当前是表面两点直线距离，不含顶点或边吸附。': 'The package does not declare a length unit. Check the source model before choosing one. This measures straight-line distance between surface points without vertex or edge snapping.',
  '模型包声明长度单位为 {0}。当前是表面两点直线距离，不含顶点或边吸附。': 'The package declares {0} as its length unit. This measures straight-line distance between surface points without vertex or edge snapping.',
  '模型已打开，有 {0} 项数据提示，可在右侧模型概览查看。': 'Model opened with {0} data notices. See the model overview on the right.',
  '三维场景创建失败': 'Could not create the 3D scene.', '已取消选择模型': 'Model selection canceled.',
  '原始 RVT 尚未支持独立读取，请选择已导出的模型 ZIP。': 'Raw RVT files cannot be read independently. Choose an exported model ZIP.',
  'ZIP 嵌套超过四层，请先解压外层。': 'The ZIP nesting exceeds four levels. Extract an outer archive first.',
  '读取包内模型': 'Reading the packaged model', '包内模型读取失败': 'Could not read the packaged model.',
  '模型处理进程中断。请检查设备可用内存，或关闭其他程序后重试。': 'The model worker stopped. Check available memory or close other programs and try again.',
  '当前设备无法初始化三维窗口，请启用硬件加速后重试。': 'The 3D view could not be initialized. Enable hardware acceleration and try again.',
  '原始 RVT 尚未支持独立读取。请选择已导出的模型 ZIP；查看器不会启动 Revit。': 'Raw RVT files cannot be read independently. Choose an exported model ZIP; this viewer does not start Revit.',
  '请选择已导出的模型 ZIP。': 'Choose an exported model ZIP.', '模型文件超过 2 GB（2,147,483,648 字节）。': 'The model exceeds 2 GB (2,147,483,648 bytes).',
  '读取模型目录': 'Reading the model directory', '文件读取失败': 'Could not read the file.', '无法创建三维窗口': '3D view unavailable',
  '当前设备无法初始化 WebGL：{0}': 'WebGL could not be initialized on this device: {0}', '请检查显卡或浏览器设置': 'Check your graphics driver or browser settings.',
  '加载已取消': 'Loading canceled.', '当前选择没有可显示几何，请选择三维构件后剖切。': 'The selection has no displayable geometry. Select a 3D component before sectioning.',
  '边距必须为非负有限数值。': 'Padding must be a finite, nonnegative number.', '请填写有效边界，各轴最小值不能大于最大值。': 'Enter valid bounds. The minimum cannot exceed the maximum on any axis.',
  '每个模型最多保存 20 个视点，请先删除不需要的视点。': 'A model can have up to 20 saved viewpoints. Delete an unused viewpoint first.',
  '视点 {0}': 'Viewpoint {0}', '未命名视点': 'Untitled viewpoint', '视点无法恢复。': 'Could not restore the viewpoint.', '已恢复视点：{0}': 'Viewpoint restored: {0}',
  '三维模型，拖动旋转，滚轮缩放，单击选择构件': '3D model: drag to orbit, use the wheel to zoom, and click to select a component',
  '检查模型包': 'Checking the model package', '分段读取几何和属性': 'Reading geometry and properties in chunks', '读取楼层和纹理': 'Reading levels and textures',
  '模型解析完成': 'Model parsing complete', '读取选中的模型': 'Reading the selected model', '准备模型场景': 'Preparing the model scene',
  '构建实例与空间索引': 'Building instances and spatial indexes', '构建三维视图': 'Building the 3D view',
  '显卡上下文已丢失，等待恢复；如画面未恢复请重新打开模型。': 'The graphics context was lost. Waiting for recovery; reopen the model if the view does not return.',
  '无法创建三维视图。请启用浏览器硬件加速，或更新显卡驱动后重试。': 'Could not create the 3D view. Enable browser hardware acceleration or update the graphics driver and try again.',
  '视点数据无效或版本不受支持。': 'The viewpoint data is invalid or uses an unsupported version.',
  '模型加载失败。原始诊断：': 'The model could not be loaded. Original diagnostic:',
  '操作未完成。原始诊断：': 'The action could not be completed. Original diagnostic:',
  '源诊断可能保留原文。': 'Source diagnostics may retain their original language.',
  '正在处理模型…': 'Processing model…',
};

function initialLanguage(): Language {
  try { return globalThis.localStorage?.getItem(LANGUAGE_STORAGE_KEY) === 'en' ? 'en' : 'zh'; }
  catch { return 'zh'; }
}
let language: Language = initialLanguage();
const renderedMessages = new Map<string, { source: string; parameters: Readonly<Record<string, string | number>> }>();

export function getLanguage(): Language { return language; }
export function locale(): 'zh-CN' | 'en-US' { return language === 'en' ? 'en-US' : 'zh-CN'; }
export function setLanguage(next: Language): void {
  language = next;
  try { globalThis.localStorage?.setItem(LANGUAGE_STORAGE_KEY, next); } catch { /* Language still works without storage. */ }
}

export function tr(source: string, parameters: Readonly<Record<string, string | number>> = {}): string {
  const pattern = language === 'en' ? englishMessages[source] ?? source : source;
  const text = pattern.replace(/\{([^{}]+)\}/g, (token, key: string) => parameters[key] === undefined ? token : String(parameters[key]));
  if (renderedMessages.size >= 512) renderedMessages.delete(renderedMessages.keys().next().value!);
  renderedMessages.set(text, { source, parameters });
  return text;
}

/** Translate app messages only; never use this on source model names or values. */
export function translatedMessage(message: string, failure = false): string {
  const previous = renderedMessages.get(message);
  if (previous) return tr(previous.source, previous.parameters);
  if (englishMessages[message]) return tr(message);
  if (language !== 'en' || !/[\u3400-\u9fff]/.test(message)) return message;
  return `${tr(failure ? '模型加载失败。原始诊断：' : '操作未完成。原始诊断：')}\n${message}`;
}

export function translatedProgress(message: string): string {
  if (renderedMessages.has(message) || englishMessages[message]) return translatedMessage(message);
  return language === 'en' && /[\u3400-\u9fff]/.test(message) ? tr('正在处理模型…') : message;
}

/** Bind the initial UI markup once. Imported model content is never scanned. */
export function bindStaticTranslations(root: HTMLElement): () => void {
  const texts: { node: Text; source: string; leading: string; trailing: string }[] = [];
  const attributes: { element: Element; name: string; source: string }[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const raw = node.nodeValue ?? '';
    const source = raw.trim();
    if (englishMessages[source]) texts.push({ node: node as Text, source, leading: raw.match(/^\s*/)?.[0] ?? '', trailing: raw.match(/\s*$/)?.[0] ?? '' });
  }
  for (const element of [root, ...root.querySelectorAll('*')]) {
    for (const name of ['title', 'aria-label', 'placeholder']) {
      const source = element.getAttribute(name);
      if (source && englishMessages[source]) attributes.push({ element, name, source });
    }
  }
  return () => {
    for (const binding of texts) if (binding.node.isConnected) binding.node.nodeValue = binding.leading + tr(binding.source) + binding.trailing;
    for (const binding of attributes) if (binding.element.isConnected) binding.element.setAttribute(binding.name, tr(binding.source));
  };
}
