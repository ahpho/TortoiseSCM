# GUI 与右键工作流验收（2026-09-29）

本轮按用户要求验证软件，不扩展产品功能。结论是：当前源码的可执行 GUI 回归通过，但不能宣称全部桌面 GUI 已验收。发现安装版本落后、重命名说明裁切和签出/撤销入口体验差异；真实 Explorer 点击、状态覆盖图标及 Beyond Compare 人工三方编辑仍缺证据。

## 版本、方法与结果

- 产品源码：`92d000736cf20c8ce8882c0a33b127ac754e14f0`，`src/TortoiseSCM.sln`，Release x64；本轮只修改测试和文档。
- Windows：`10.0.26200.0`。Explorer 注册目录与实际加载 DLL 一致，但均为 `0.1.0-dev-shell-dispatch-fix-20260928`，安装清单提交 `317a334286b7c196c0659a993aae3d5b1f377343`。
- 当前用户没有注册 `*TortoiseSCM*` Appx 包。现代菜单接口通过不等于用户桌面已显示现代菜单。
- computer-use 的 native pipe 在首次调用、重试、重置后均返回 `os error 2 / 系统找不到指定的文件`。因此没有实际操作 Explorer 菜单、重启桌面、切换系统主题或编辑真实 BC 窗口。
- `build-tortoisescm.ps1 -Test` 完整通过，包含 Core、2,297 项主 CLI、Shell、现代菜单及 WinForms 检查。
- 当前生产 DLL 的独立复验为 **537 条 PASS**，覆盖现代接口与经典派发；菜单作用域包括文件、目录、背景、工作区外目录与盘符、多选及跨工作区拒绝。测试在隔离目录加载 DLL，以参数记录程序替代目标 GUI，不是在 Explorer 中点击。
- **523 项真实服务器 GUI 流程断言通过**：基础操作 256、历史 64、更新 60、首次拉取 22、补充文件操作 110、Blame 11。重复执行的 110 项只统计一次。
- 完整 UI 套件还在隔离真实工作区运行成功，包含真实状态、历史、分支浏览、仓库浏览和布局；其中不少写操作使用模拟回调，不能算成真实服务器写入。另行通过真实关系图加载、提交详情及固定快照入口检查。
- 最新 Gluon 脏目录更新 **38 项真实后端检查通过**；GUI 预检及冲突按钮由完整 UI 套件验证。未将独立结果文件注入当成人工 BC 编辑。

对照采用本仓库保留的 `src/Resources/TortoiseProcENG.rc`、`src/TortoiseProc` 和上游菜单源码，并核对官方文档：[TortoiseSVN 提交](https://tortoisesvn.net/docs/release/TortoiseSVN_en/tsvn-dug-commit.html)、[变更分组](https://tortoisesvn.net/docs/release/TortoiseSVN_en/tsvn-dug-changelists.html)、[差异查看](https://tortoisesvn.net/docs/release/TortoiseSVN_en/tsvn-dug-diff.html)、[TortoiseGit 日志及文件右键操作](https://tortoisegit.org/docs/tortoisegit/tgit-dug-showlog.html)。主要核对选择范围、取消行为、日志三窗格、版本比较和恢复动作，而不是把 Git index、push、rebase 等语义移植为 Plastic 命令。

## 全部 27 个 Shell 命令的验收矩阵

“真实”指进程内真实 WinForms 和真实 Plastic；“模拟”指控件/事件执行真实，但服务端结果由测试提供。所有行的 **Explorer 实际点击均未验收**。菜单存在、参数正确、GUI 可用是三个不同层次。

| Shell 命令 | 对照的 Tortoise 操作 | 本轮证据与结论 | 尚未验证或限制 |
| --- | --- | --- | --- |
| `status` | 检查修改 | 真实：目录/文件范围、列表、版本控制/私有筛选、勾选、高亮行、空选择 | 大仓库长期刷新与真实图标显示 |
| `checkin` | 提交 | 真实 Standard/Gluon：空说明拒绝、预览取消、单文件/递归目录提交、排除兄弟文件、独立消费者验证、成功清空 | 网络故障后服务端结果核对；预检取消、changelist 仍为 ROADMAP 待办 |
| `update` | 更新 | 真实：打开不写入、取消、Gluon 所选目录增删改、保留范围外版本、Standard 整体更新、重复点击保护、冲突失败 | 桌面入口；大下载进度和超时体验 |
| `add` | 添加 | 真实：取消、单文件、递归目录、私有兄弟保留、随后提交；DLL 派发复验通过 | Explorer 点击 |
| `checkout` | 获取可编辑版本/锁工作流 | 真实：Shell 路由先打开待定窗口；执行 GUI handler 时要求精确路径确认，并出现原生 CO 状态 | 新测试直接调用 handler，未通过“操作”菜单鼠标选择；CO 不等于获得服务器独占锁 |
| `undo` | 撤销本地修改 | 真实：行右键取消保留内容，确认后恢复基线；高亮文件与另一个勾选私有文件不同，后者保留 | Shell 路由不会直接执行撤销，需要继续选择菜单/文件 |
| `diff` | 比较工作副本 | 当前构建 Core/CLI 和 GUI 工具路由检查通过；历史比较的真实文件内容核对通过 | 本轮未打开真实 BC 工作副本比较；不能把工具替身当 BC 验收 |
| `history` | 显示日志 | 真实：直接范围日志、完整加载、目录/文件范围、F5、筛选、取消、版本标记、任意版本比较输入、旧版本恢复为待提交 | 跨重命名身份追踪仍未完成；目录历史仍扫描全仓提交 |
| `gluon` | 打开官方客户端 | 命令/路由和后端验证；本轮没有实际启动官方 Gluon | 官方客户端启动、登录及窗口交接未验收 |
| `settings` | 设置外部工具 | 模拟 GUI：BC 发现/无效路径提示、差异/合并共用路径、正常/最小窗口 | 未修改用户设置，真实 BC Pro 可用性未验收 |
| `move` | 重命名/移动 | 真实 Standard/Gluon：Shell 路由打开路径窗口、取消保留原文件、确认后内容不变且产生 MV | 已发现较长源路径裁切操作说明；跨目录/拖放未新增实测 |
| `remove` | 删除受控项 | 真实：单文件/目录确认与取消、原生 DE、删除后提交、消费者核对 | 批量删除仍未实现 |
| `ignore` | 加入忽略列表 | 真实：取消不改规则，确认写精确路径，保留原字节和受控重命名项 | 本轮未覆盖通配符、批量忽略或全局忽略设置 |
| `locks` | 锁管理 | 真实锁查询；模拟本人/他人锁行的按钮权限与最小布局 | 未制造真实服务器独占锁竞争 |
| `unlock` | 释放锁 | 与锁管理共享入口，模拟他人锁禁用及本人锁启用；Core 检查通过 | 本轮未实际释放服务器锁 |
| `merge` | 合并/解决冲突 | 完整 UI：预览、目录/内容选择、独立结果应用边界、身份变化拒绝；真实 Gluon 脏更新后端补充 | 本轮未完成真实 Standard 跨分支 GUI 合并及 BC 人工三方保存/放弃 |
| `export` | 保存历史版本 | 历史文件/仓库浏览真实版本内容读取；GUI 导出启用条件、固定端点和 Core/CLI 导出检查 | 本轮未在原生“另存为”窗口完整保存文件；递归目录导出未实现 |
| `rollback` | 回滚到版本 | 共享历史入口；真实历史恢复工作流及模拟回滚保护、窗口导航通过 | 不将单文件恢复通过等同于本轮完成整仓回滚/快照切换 |
| `recover` | 恢复历史文件 | 真实历史恢复：确认/取消、生成待定修改、Standard/Gluon、范围排除 | 入口共享日志，需再次选版本；完整重命名身份追踪未完成 |
| `branches` | 分支/切换 | 真实列表、头详情、分支历史、范围；模拟创建、重命名、删除、Partial 预览/取消/身份变化和禁用规则 | 本轮未从真实 GUI 完成分支写入；当前/非叶子重命名及 Partial 跨分支合并未开放 |
| `shelves` | 类似暂存工作 | 模拟保存说明、列表、文件明细、比较/导出、应用/删除确认及失败状态 | 本轮未实际 GUI 保存/应用/删除服务器 shelveset；不等同 Git stash/pop |
| `blame` | 逐行追溯 | 新增真实 GUI：加载/刷新、作者/版本/原文、行菜单、普通/最小尺寸、工作文件及 selector 不变 | 菜单存在已验证；未实际执行剪贴板复制和由 Blame 二次打开日志 |
| `repository-browser` | 仓库浏览器 | 真实固定提交树、文件/目录、文本预览、固定快照导航及最小窗口 | UTF-8/大小限制保留；递归导出与 Xlink 不在已通过范围 |
| `labels` | 标签 | 模拟列表筛选、目标提交、创建/删除确认、身份复核、失败刷新和固定快照；Core/CLI 通过 | 本轮无真实 GUI 标签写入；移动/重命名未实现 |
| `revision-graph` | 版本关系图 | 模拟分页/取消/父边与合并边显示；真实服务器节点、选中提交文件明细和固定快照入口通过 | 分支过滤、图像导出、大图布局仍未完成 |
| `version` | 关于/版本信息 | 模拟 GUI 版本信息 51 项；真实安装 manifest、注册路径、Explorer 模块核对；DLL 派发 | 实际菜单打开版本窗口未验收 |
| `create-workspace` | 检出已有仓库 | 真实查询/创建 Gluon、空分支、Standard 后端兼容、下载字节、后续 GUI 提交；窗口模拟重名/失败/取消 | 当前安装版工作区外入口缺失；首次稀疏范围、分支列表、下载失败恢复向导未实现 |

补充窗口操作：提交说明历史/模板 128 项 GUI 断言通过；Partial 脏更新 UI 39 项；Partial 切换 UI 63 项及结构预览 UI 130 项通过。模拟用例覆盖拒绝确认、异步忙碌期间禁用、迟到结果、失败后刷新等，不借用通过数量扩大实际桌面覆盖范围。

## 发现的问题

### GUI-AUDIT-01：当前安装版缺少工作区外首次拉取入口（P1，版本部署差异）

步骤：读取实际注册 DLL，复制到隔离目录，以生产 `IContextMenu` 初始化文件、工作区目录/背景和工作区外目录，查询菜单并按 Unicode 数字编号派发。

结果：旧 DLL 在文件、目录、背景上的 update/checkin/history/version/add/remove 共 18 次派发正确；进入工作区外目录时返回 **0 项**，当前验收要求 **1 项**。诊断为 `Classic menu selection=3 expected=1 actual=0 HRESULT=0`。当前源码重新构建的 DLL 在相同测试及现代接口测试中全部通过，包含工作区外目录、背景和盘符的 `create-workspace`。

影响：用户当前桌面无法获得 ROADMAP 已记录的新入口，也不能拿当前源码的新图标和 Gluon 更新能力推断安装版已包含这些功能。下一步应先安装与验收源码一致的包，再检查真实 Explorer。此次没有替用户安装、重新注册或重启 Explorer。

### GUI-AUDIT-02：重命名窗口的源路径挤掉操作说明（P2，布局缺陷）

步骤：对隔离工作区中的 `standard action 中文.txt` 执行 move；源路径位于较深的 QA 目录；分别查看默认和最小尺寸。无需超过 Windows 260 字符边界即可复现。

预期：用户可以看到完整源路径，并读到“在同一工作区内指定不存在的新路径；不会覆盖已有文件”。实际：源路径换行占据固定行高，该说明在默认和最小窗口均不可见，且标签没有滚动入口。移动和取消操作本身正确。

证据：[默认窗口](images/gui-audit-20260929/rename-normal.png)、[最小窗口](images/gui-audit-20260929/rename-minimum.png)。位置：`src/TortoiseSCM/PathInputForm.cs` 的源路径/提示共用 Label。建议拆分路径与说明，给说明独立高度，并提供完整路径查看方式。

### GUI-AUDIT-03：签出/撤销右键入口缺少直接操作引导（P2，操作体验差异）

`MainForm.InitializeAsync` 对 add/move/remove/ignore 会直接进入相应流程，但 checkout/undo 最终落到“请核对选择范围后点击…”状态文案，窗口仍显示“提交”，没有同名主按钮，需在“操作”下继续寻找。撤销还要求勾选待定项，而行右键撤销使用高亮行。这些操作可以执行，但首次使用者容易误以为点错菜单或功能没有响应。

本轮实证 checkout 初次打开不写入、后续 handler 确认产生 CO；行右键 undo 确认能恢复目标并保留另一勾选项。建议后续提供对应独立窗口或明确的目标操作按钮，同时保留取消默认值和范围审核。

### QA-AUDIT-01：已有 Blame 空白截图不能证明布局（已修正测试）

原 `CheckBlameDialog` 只调用 `CreateControl`，未显示窗口，生成的 `blame-minimum.png` 是空白。移除此伪视觉证据，保留构造检查；新增 `UiTests --blame-live <artifacts> <file> <workspace>`，在真实受控文件上加载、刷新并截图。11 项断言通过。[新的最小窗口](images/gui-audit-20260929/blame-minimum.png) 显示真实行内容。

视觉观察：Blame 的 Refresh 按钮比其他窗口明显偏矮（位于 20 像素表格行），可以后续统一行高；未发现其刷新操作失败。测试 EXE 的通用标题栏图标不用于判断生产程序图标是否正确。

## 截图检查和保留边界

本轮查看了真实提交审核、历史、更新、首次拉取、重命名、分支浏览、仓库预览、Blame、关系图，以及模拟说明模板、标签、暂存集、BC 设置、Partial 冲突与切换预览的截图。完整 UI 运行生成 125 张图；没有将“已生成”写成“逐张目视通过”。窗口表格在最小尺寸允许水平滚动，提交说明、关闭/确认按钮及关键状态可见；上述重命名说明裁切单独列为失败。

未关闭以下验收项：实际经典/现代右键点击、覆盖图标实际显示、Explorer 重启/退出、多工作区桌面选择、深色/高对比度/不同系统语言、真实长路径压力、BC 4/5 已有实例并行、三方编辑保存/放弃/取消及 Pro 不可用、原生文件保存对话框、未在矩阵中标记“真实”的服务器 GUI 写入。多 DPI 和其他用户明确排除范围未重新列为要求。

ROADMAP 中仍未实现的 changelist、补丁创建/应用、批量移动/删除/忽略、拖放、首次稀疏加载、服务器仓库创建等，保持功能缺口，不计为本轮已验收。

## 证据与复现

完整本地证据根目录：`bin/TortoiseSCM/qa/gui-audit-20260929/`。`bin` 为忽略目录，日志/完整截图保留在本机；上面的关键缺陷截图随文档提交。

| 文件或目录 | 内容 |
| --- | --- |
| `environment.json`、`shell-environment.json`、`summary.json` | 源码/安装版、系统、Appx 状态、二进制哈希及结果概要 |
| `build-test.log` | 完整 Release 构建与回归，退出 0 |
| `current-shell-dll-final.log` | 最终 Shell 测试代码对当前生产 DLL 的 537 条 PASS，退出 0 |
| `installed-classic-diagnostic.log` | 当前安装 DLL 的缺失入口复现，预期失败，退出 1 |
| `basic-gui.log` | 256/64/60 三个真实套件及各自 fixture 路径 |
| `file-actions-runner.log`、`file-actions-final.log` | 补充 110 项；正式 runner 和直接执行均通过，统计不重复 |
| `wizard.log`、`wizard-manifest.txt` | 首次创建 22 项与隔离目录 |
| `live-ui.log`、`live-ui-error.log`、`live-ui/` | 完整 GUI 套件及真实工作区读取；退出 0，错误日志为空 |
| `blame-live.log`、`blame/`、`graph/` | 真实 Blame 和真实关系图证据，退出 0 |
| `dirty-update.log` | 38 项后端检查，指向 `integration-20260929-022904-59769afa` |
| `user-files-before.json`、`user-files-after.json`、`preservation-conclusion.json` | 文件保护核对 |

基础套件：`bin/TortoiseSCM/qa/basic-gui-acceptance-20260929-022137-44913522/results.json`；补充 runner：`basic-gui-acceptance-20260929-022811-c5ce70ca/results.json`。首次拉取 fixture：`integration-20260929-022553-922bb7fc`。全部服务器写入均位于 `tortoisescm-autotest-*` 专用分支，隔离工作区保留以供复查。

TestSCM/TestSCM2 的 **4,009 个文件**（含元数据）路径、长度、修改时间和 SHA-256 前后相同；连同已有用户设置/说明历史，共 **4,302 个既有文件未变**。运行过程中新增了 11 个零字节 merge gate 锁文件及后台覆盖层缓存，不能称整个用户配置目录字节级不变；没有更改已有设置或用户工作文件。

复现主要检查：

```powershell
.\build-tortoisescm.ps1 -Test
.\test\TortoiseSCM\Run-BasicGuiAcceptance.ps1
.\test\TortoiseSCM\Run-BasicGuiAcceptance.ps1 -FileActionsOnly
.\test\TortoiseSCM\Run-PartialDirtyUpdateIntegration.ps1
# 此接口要求二进制目录绝对路径。
.\bin\TortoiseSCM\Release\ShellTests.exe --modern-dll (Resolve-Path .\bin\TortoiseSCM\Release).Path
# 只读真实 Blame：使用已验证的隔离受控文件。
.\bin\TortoiseSCM\Release\UiTests.exe --blame-live <截图目录> <受控文件> <工作区根目录>
```

本轮中间失败保留：文件确认测试最初将 `D:`/`d:` 按大小写匹配，修正为 Windows 路径不区分大小写后用新 fixture 通过；一次人工调用遗漏 UTF-8 解码，另一次 Shell 调用传相对目录，均修正调用后通过。它们属于测试/调用问题，不计为产品缺陷，也不掩盖旧安装版的真实菜单差异。Blame 专项在完整回归之后新增并独立编译、执行通过，没有声称新增测试后再次重跑整套后端。

### 2026-09-29 补充：截图标题栏图标

旧截图由未嵌入图标的测试 EXE 生成，`DialogStyle` 从启动程序读取图标，因此显示通用窗口图标。现已给主 UI、工作文件比较 UI 和基础 GUI 验收程序的编译入口添加同一个 `src/Resources/gluon.ico`。重新编译 UiTests，设置 UI 53 项断言通过；[新截图](images/gui-audit-20260929/settings-gluon-icon.png) 已目视确认橘黄色 Gluon 标题栏图标。

提取最新 Release 主程序与 UiTests 的图标后，两者 PNG SHA-256 一致（`39DB9EE700B5DF500C07CE0E0B879EB34172554452469BC6D05E3102606A846F`）。当前安装的 `0.1.0-dev-shell-dispatch-fix-20260928` 主程序提取图标仍为 TortoiseGit；本轮未安装新包或重启 Explorer。原始截图作为当时证据保留。提取图标和检查日志位于 `bin/TortoiseSCM/qa/icon-check-20260929/`。
