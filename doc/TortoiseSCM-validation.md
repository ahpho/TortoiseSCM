# TortoiseSCM 验证记录

## 用户完整流程自动化（2026-09-29，后续复测）

- [报告及截图](TortoiseSCM-gui-experience-20260929.md)：新增统一入口 `test/TortoiseSCM/Run-GuiExperienceTests.ps1`，17 个 GUI 流程/补充检查阶段最终均通过；真实服务器写入使用专用分支和隔离工作区。
- 完整回归通过；后续小改动完成最终生产构建及重命名 14、Blame 14、首次拉取 26 项专项。修复长源路径遮住重命名说明和 Blame 按钮高度，修正首次拉取测试初始化及截图裁切。
- 原工作区及已有配置共 4313 文件的内容、长度、时间均未变；新增 22 个测试合并锁文件。实际 Explorer/BC 点击仍因桌面控制连接不可用而未验收，安装版本未更新。保留签出/撤销引导及深路径主窗口范围裁切待办。
- [Gluon/标准与锁专项](TortoiseSCM-lock-gluon-test-20260929.md)：隔离 Standard/Gluon 工作区、两方签出及锁管理窗口专项完成。客户端 40 项锁安全/解析断言和 12 项实时 GUI 断言通过；当前 TestSCM 服务器未启用匹配锁规则，因此两个工作区均能签出，正向“第二人被拒绝”场景需服务器部署 `lock.conf` 后复测。

## GUI 与右键完整覆盖审计（2026-09-29）

- [逐项验收报告](TortoiseSCM-gui-audit-20260929.md) 对照官方 TortoiseSVN/TortoiseGit 操作，列出全部 27 个 Shell 命令的真实/模拟/未验收边界。
- 当前源码完整 Release 回归通过；当前生产 Shell DLL 537 条 PASS；真实服务器 GUI 基础/历史/更新/首次拉取/文件操作/Blame 共 523 项断言通过。完整 GUI 套件另在隔离真实工作区运行通过，真实关系图和 Gluon 脏目录后端 38 项补充通过。
- 发现当前 Explorer 安装版仍为 `317a334`，工作区外菜单返回 0 项，缺少当前版本的首次拉取入口；当前源码 DLL 同场景通过。新增重命名长路径说明裁切、签出/撤销入口体验记录；修正既有 Blame 空白截图测试，补真实窗口证据。
- 桌面控制连接不可用；本轮没有真实 Explorer 点击或 BC 人工三方编辑，不关闭这些 ROADMAP 项。原工作区 4,009 个文件及已有用户设置不变；测试新增隔离工作区、缓存及 11 个空锁文件。证据在 `bin/TortoiseSCM/qa/gui-audit-20260929/`。

范围说明（2026-09-28）：用户已排除多 DPI、提交失败草稿跨重启恢复，以及签名/自动更新/ARM64 等发布与平台产品化工作。以下历史记录中的未验收事实保留；历史“下一步/待完成”描述不覆盖 [当前路线图](../ROADMAP.md) 的范围约定。

## Gluon 脏目录更新：文本与二进制（2026-09-29）

- 在专用 `tortoisescm-autotest-*` 分支及独立 producer/consumer/Partial 工作区模拟：选中目录内文本、二进制有本地及服务器修改，另有可直接更新的文件，范围外保留待定修改。真实 Core 场景 37 项通过，证据 `bin/TortoiseSCM/qa/integration-20260929-020031-5bd2dd44/partial-dirty-update-results.json`；可用 `test/TortoiseSCM/Run-PartialDirtyUpdateIntegration.ps1` 重现。
- 证实原生 `partial update --dontmerge` 会保留冲突本地字节，但失败前可已更新目录内无冲突文件。新增 GUI 选定范围只读预检、明确停止及丢弃/冲突处理入口，不自动续跑；范围匹配排除相似前缀兄弟目录，包含可能影响所选子项的祖先结构冲突。结构冲突独立窗口仍显示整个工作区，已明确提示。
- 验证文本独立审核结果应用后待签入；二进制保留本地/采用服务器均字节精确，取消准备不改工作文件并保留备份；已解决但仍待签入的目录可继续更新。显式签入后独立 consumer 内容与修订父节点正确。显式撤销所选目录后可更新到服务器内容，范围外字节、pending、已加载版本和 Gluon 配置保留。
- 修正采用服务器内容时重复写入相同字节引起的伪 CH；完全采用传入内容的决定完成后，从活动会话移除，避免干净文件无可签入内容却被遗留会话阻挡。最新 Core 补充 9 项真实生命周期检查通过（同目录 `incoming-only-lifecycle-results.json`），确认 clean、无活动会话、原始/审核备份仍保留，未准备的其他冲突不受影响。
- 实际 UpdateForm/PartialConflictForm 使用真实 client 的衔接检查 18 项通过（`dirty-gui-proof-results.json`）：预检在任何更新前阻挡，干净 incoming 文件也未下载；二进制文本合并入口拒绝启动，择一准备只生成独立结果，应用仍需确认，取消保留所有文件。普通/最小尺寸截图 `dirty-update-native-{normal,minimum}.png` 与 `dirty-binary-native-{normal,minimum}.png` 已目视核对。
- 新 GUI 回归 39 项、旧启动路由 70 项及既有 Partial 冲突按钮/恢复检查通过，证据 `qa/partial-dirty-ui/ui-tests.log`。原有丢弃确认继续默认取消，不新增自动丢弃操作。
- 全量 `build-tortoisescm.ps1 -Test` 通过（`qa/partial-dirty-regression.log`），包括 Core、2,297 项主 CLI、Shell/现代菜单及 WinForms；最后补充的 incoming-only 会话移除按上述 9 项真实检查复验，随后重建最终 Release。tracked 集成用例新增清理断言后为 38 项，本轮对应证据分别为原 37 项与补充 9 项，未将其冒充整套 38 项重跑。
- 验证边界：桌面控制通道不可用，GUI 由进程内真实窗口驱动；本轮未模拟人工在 Beyond Compare 中编辑文本，而是使用明确的独立审核结果测试真实应用、更新和签入。原生 `cm status` 对同秒同尺寸二进制改写可能漏报，实际 update 仍拒绝覆盖；fixture 使用不同修改时间模拟正常编辑。预检不能承诺目录级原子性或发现全部并发修改。原 TestSCM/TestSCM2、用户配置、当前安装和 Explorer 均未改动。

## Gluon 应用图标（2026-09-29）

- 从本机 Plastic SCM 安装目录原样复制橘黄色 `gluon.ico`，保留 16/32/48/256 图层；替换主程序、Shell DLL 和安装器图标，以及现代菜单包的三张 PNG 标识。所有业务窗口均沿用 `DialogStyle`，历史记录等子窗口无需单独嵌入另一份图标。来源路径与 SHA-256 记录于使用文档。
- Release 构建通过；32 项隔离图标检查通过：实际 EXE/Shell DLL 的四帧图像资源逐字节匹配源 ICO，历史/主窗口/欢迎/设置/版本窗口的图标像素匹配实际 EXE，并验证原生标题栏小图标已设置。这五个窗口普通/最小尺寸截图已生成，历史窗口两种尺寸已目视确认橘黄左上角图标。证据：`bin/TortoiseSCM/qa/gluon-icon/` 下的 `build.log`、`icon-qa.log`、`HistoryForm-{normal,minimum}.png`。
- 现代菜单包检查 27 项通过（`modern-package-tests.log`）；实际 Appx 注册保持不变。桌面控制通道不可用，本轮使用进程内窗口及 PrintWindow 截图，不声称在用户 Explorer 中完成新版注册验收；未改动工作区、用户设置或重启 Explorer。

## Windows 图形安装包（2026-09-29）

- 新增 Inno Setup 中文 `Setup.exe`，内置完整 BC，安装后不启动工作区；固定当前用户应用标识，兼容默认目录的旧脚本安装，新版直接重装升级，Windows 应用列表可卸载。脚本继续保留，但用户无需手动执行。生产安装器只支持默认安装目录，不接管旧脚本的自定义 `InstallRoot`。
- 安装后端专项 38 项通过：完整包校验、ZIP 路径穿越/篡改拒绝、损坏包保留活动版本、旧 DLL 占用时重装、卸载占用保留记录并重试、保留用户文件，以及安装成功后临时文件清理失败仅警告。日志：`bin/TortoiseSCM/qa/setup-20260929/bridge-tests-final.log`。
- 实际编译并执行隔离 `Setup-Test.exe`，28 项通过：首次安装登记应用列表、不同版本升级、同版本重装、中文/空格/符号路径、旧 DLL 占用时升级、卸载失败保留应用列表及支持文件、释放占用后再次卸载成功。日志：`qa/setup-20260929/native-lifecycle-suite.log`；每次运行生成独立 `qa/setup-native-*` 目录，可用 `test/TortoiseSCM/SetupInstallerTests.ps1` 重现。
- 实际中文向导首屏和完成页已截图检查，固定窗口下文本与按钮显示完整，完成时没有启动工作区窗口。截图：`qa/setup-20260929/gui-window-artifacts/welcome.png`、`qa/setup-20260929/gui-window-resume/finished.png`。
- BC 设置迁移 35 项、既有 BC 配置 40 项通过；仅将指向同安装根旧随包 BC 的显式路径恢复为自动发现，外部自定义路径保持不变。包回归 46 项、CMD 启动器 83 项、BC 随包检查 39 项通过。日志分别位于 `qa/installer-bc-20260929/` 和 `qa/setup-20260929/`。
- 边界：实际安装测试使用独立 Test AppId、隔离目录和 `NoRegister`，没有更改用户当前菜单注册、BC 配置、工作区或重启 Explorer。正式菜单注册复用已有事务脚本，本轮不声称已经替用户实装验收。Explorer 重启默认不选，并再次明确提示会中断文件操作。后端已提交但安装器登记失败时保留有效版本并提示重跑，不声称整个封装安装原子回退。未新增签名、自动更新或 ARM64 支持。

## 历史查询提速：原生预览、批量明细与渐进显示（2026-09-29）

- 原生 `cm history --limit=10 --xml` 在最多 4 秒预算内读取候选，并逐项以真实提交路径核对，最多预览 10 条。严格核对仓库、路径、版本号和 XML；缺失路径、不支持或预算耗尽时继续完整查询，预览始终标记未完成。原生修订历史不替代提交发布历史，旧修订回滚重新签入由完整扫描补齐。
- 每页 `find changeset` 后用一次带 `--allbranches` 的区间 `cm log` 获取明细；按 ChangesetId 而非 RevNo 判断发布事件。校验候选覆盖、分支、重复/缺失字段和路径；异常客户端回退精确查询并在 GUI 显示兼容提示。log 不含项目类型，仅可能影响选中路径的祖先记录另查 diff，保留目录移动/删除的后代历史。查询显式固定仓库，防止选择器变化混入同号提交。
- 首次打开先显示核对后的预览，随后自动逐批补齐全部；刷新时也逐批显示新记录，保留原结果至完整查询成功。普通取消/失败恢复刷新前结果，首次加载保留已显示记录；完整状态只由全部扫描决定。跨仓库相同提交编号强制刷新下方明细，恢复操作取消历史查询时保留写入进度及列表一致性。
- 后端历史 77、原生预览 30、渐进加载 UI 29 项通过，涵盖分页空匹配、去重、范围/分支、旧 revision 发布、祖先移动/删除、12 类批量响应回退、取消、错误、仓库变化和写入打断。真实旧 fixture 中 cs44 发布 RevNo42 的回滚记录仍可查询，4 项只读检查通过。证据在 `qa/native-history-preview-20260929/` 和 `qa/history-performance-20260929/`，后者含 `rollback-publication.log`。
- 同一历史 fixture 的自然打开 HistoryForm 完整扫描全部 887 条，Standard 首批 2.17 秒、完成 31.14 秒；Gluon 首批 2.22 秒、完成 31.56 秒。均准确仅显示 886/885/883/882，包含子目录且排除兄弟目录 884，未触发兼容回退；原实现相同查询 330.85 秒。本次为单次本机计时，不代表所有仓库。证据 `qa/history-performance-20260929/{producer,partial}/result.json`、日志和普通/最小截图，已目视检查。
- 完整 `build-tortoisescm.ps1 -Test` 通过，含全部 Core、2,297 项主 CLI、其他 CLI、经典/现代 Shell 及完整 WinForms；历史渐进 UI 29 项、直接 Diff UI 46 项在全量中通过。回归期间补充的写入打断保护已由最终 UI 测试覆盖，之后重新生成 Release，日志为 `qa/history-performance-20260929/regression.log`、`final-build.log`。
- 本轮真实服务器查询均只读，复用隔离 QA 工作区；未安装、注册或重启 Explorer。未引入持久化缓存和增量刷新；完整扫描仍随全仓历史规模增长。包含单引号的仓库规格沿用安全查询限制，明确拒绝。

## 基础 GUI 工作流验收与修复（2026-09-28）

- 本轮按用户指定路径验收真实 WinForms 窗口、确认框和按钮，操作真实 Plastic 服务器；所有写入仅在独立 `tortoisescm-autotest-*` 分支及 QA 工作区。桌面 computer-use 接口返回 native pipe unavailable，因此没有把进程内点击或 COM 测试写成实际 Explorer 鼠标验收。Native Shell 361、生产 DLL 537 项通过，新增经典/现代 Add、Remove 的准确 argv 与选择范围捕获；真实桌面菜单显示和覆盖层仍保留后续验收。
- 添加/删除/签入 GUI 集成 256 项通过，覆盖 Standard 和 Gluon：未管理文件添加、签入并由独立 consumer 核对 UTF-8 中文内容；受控文件删除后呈 DE 待签入，再提交并在 consumer 确认删除；目录递归添加/删除；未选私有兄弟文件排除；取消添加/删除/审核、空说明拒绝、刷新及说明选择保留。复现并修复 `--command add` 只打开主窗口而未进入添加确认的缺陷；修复最小签入审核窗口说明和锁列表裁切。证据 `qa/basic-workflow-gui-final.log`、`qa/basic-workflow-gui-final/`，fixture 为 `qa/integration-20260928-230855-2d5c387a/`。
- 更新 GUI 集成 60 项通过：真实入口创建 UpdateForm，点击前不写入；Gluon 选中嵌套目录只接收该目录新增/修改/删除，兄弟目录保持旧内容；Standard 明确展示并执行整工作区更新；根更新、刷新/no-op、繁忙禁重复/关闭、跨工作区拒绝。Standard/Gluon 有冲突时保留本地字节、selector 及 CH 待签入状态。证据 `qa/integration-20260928-230814-1e2279bf/`，普通/最小及冲突截图已查看。
- 历史 GUI 在同一隔离 fixture 续跑 54 项通过：Standard/Gluon 文件历史自动载入全部三个版本；Ctrl+D、双击、标记任意两个版本的真实导出字节准确；取消恢复不改变内容，已有待提交修改时恢复拒绝且保留原内容；干净文件回滚至 cs882 后内容准确、保留待签入状态，selector 和服务器分支头均不变。目录历史及刷新全部仅显示 cs882/883/885/886（含子目录），排除仅修改相似前缀兄弟目录的 cs884。证据 `qa/integration-20260928-231321-41df5d12/history-gui-resume.log` 及截图；早期自动测试器误判原生提示框按钮编号而停滞，修复测试器后才记录通过，产品恢复逻辑无需修改。
- 普通右键对应的不带分支过滤 HistoryForm 另行只读验收通过：自然打开自动扫描全部 887 条提交，330.85 秒后准确仅显示 cs886/885/883/882，包含子目录、排除相似前缀兄弟目录 cs884。普通/最小截图已查看，证据 `qa/directory-history-readonly-20260928/`；查询性能仍列为 ROADMAP 待优化项。
- 另用真实恢复按钮复核精确状态，两种工作区均返回 `CO` 待签入，旧内容 UTF-8 字节正确，服务器头保持 cs886、selector 字节不变；随后只在测试工作区撤销测试更改。证据为同 fixture 下 `producer-exact-rollback.json`、`partial-exact-rollback.json`。
- 真实 HistoryForm 标记 cs856、选中 cs859 文件后直接打开用户提供的 BC，窗口两侧显示正确版本号及 `version one 中文` / `version three 中文`；只读、无中间窗口、输入在窗口关闭前保留，关闭后清理，并恢复历史窗口按钮。15 项检查及两张截图在 `qa/history-real-bc-20260929/`。另一个独立实际 BC 窗口测试复现普通差异窗口关闭返回 13 被误报失败；按 [BC 官方返回码](https://www.scootersoftware.com/v4help/command_line_reference.html)，仅双向比较将 0/1/2/11/12/13 视为正常结果，合并仍严格处理非零码，错误及无法等待仍保留原有保护。修复后真实 BC 窗口关闭验证通过，BC 进程专项 51、历史比较 90、工作文件比较 184、暂存集比较 65 项通过。证据 `qa/gui-acceptance-20260928/real-bc-window-fixed/` 及同目录日志。
- 完整 `build-tortoisescm.ps1 -Test` 通过，包含 Core、2297 项主 CLI、Shell 与完整 WinForms；生产修复完成后再次构建并重跑受影响 BC 专项和完整 WinForms 回归通过。新增 `Run-BasicGuiAcceptance.ps1` 统一创建独立 fixtures 并运行三类基础 GUI 集成，接入 `-Integration`；真实 BC 窗口烟测为显式可选项，避免无人值守时打开编辑器。正常/最小截图均已查看。
- 最终包相关回归：安装/升级回退/卸载 46、双击入口 83、随包 Beyond Compare 39 项通过，均使用隔离目录且不注册真实 Shell。日志在 `qa/gui-acceptance-20260928/`。
- 原 TestSCM、TestSCM2 共 4,009 个文件（包含工作区元数据）的路径、长度、修改时间、SHA-256 前后相同，证据 `qa/gui-acceptance-20260928/user-workspaces-preserved.json`。测试提交说明保存在 QA 专用目录；未安装新版本到用户活动目录、注册 Shell 或重启 Explorer。三方人工编辑/保存、BC 5 和实际桌面菜单点击不属于本轮通过项。

## 右键首次拉取与默认 Gluon 工作区（2026-09-28）

- 安装协调器仅提示安装成功及位置，不启动欢迎窗口；保留旧 `-NoLaunch` 参数兼容。正常安装测试不传该参数，并注入拒绝进程启动的探针，证明成功不依赖启动应用。升级检测旧扩展和用户确认 Explorer 重启的行为保留。
- 经典/现代菜单在工作区外的单目录及目录背景提供“拉取仓库”，传递 `--parent-path`，不复用 `--path` 的精确目的地语义。经典菜单补充 Drive 注册及卸载/失败回退覆盖；现代稀疏包仍使用支持的 Directory/Background 类型，不增加 schema 不支持的 Drive 类型。已有工作区内、文件、多选及元数据目录不开放此入口。
- 默认父目录下 `TestSCM` 与本地同名工作区；本地文件/目录或注册工作区占用时建议 `TestSCM2/3…`，不覆盖用户编辑。异步读取工作区列表，查询失败不把未知状态当无冲突；创建时仍执行原有完整预检。查询服务器后优先选择已有 `TestSCM` 仓库，不创建同名服务器仓库。
- 向导默认 Gluon，可选 Standard；首次均完整下载所选分支。Gluon 按原生创建/下载、`partial configure -/ +/`、`partial update . --report` 顺序初始化，最终验证原生真实 Partial 状态、仓库和分支，不能仅依赖元数据文件中的 Standard 字样。失败保留准确阶段和目录，不自动重复创建；旧后端重载继续创建 Standard。
- Shell 单元 361、实际生产 DLL 435 项通过，包括经典目录/背景/盘符根目录与现代中文空格路径的 argv 捕获，以及已有菜单派发回归；证据 `bin/TortoiseSCM/qa/checkout-shell/`。向导 UI 141、创建后端 196 项通过，涵盖重名建议、异步编辑、参数限制、工作区名单解析、两种模式与转换失败；正常/最小尺寸截图已查看，证据 `qa/checkout-defaults-ui/`、`qa/checkout-context/`。
- 真实隔离创建集成 22 项通过：空分支 Gluon、原接口 Standard、默认 Gluon 向导、Unicode/CRLF/二进制内容、单文件更新不带入旁侧新增文件、根目录更新、GUI 签入和独立 consumer 核对。原 TestSCM 用户文件、selector、status 前后相同，未操作 TestSCM2；写入仅使用隔离 `tortoisescm-autotest-*` 分支。证据 `qa/integration-20260928-224751-814aca97/`。
- Release 构建与完整 WinForms 回归通过；安装/卸载 46、安装入口 83、随包 BC 39、现代稀疏包 27 项通过。未安装到用户活动目录、未修改实际 Explorer 注册或重启 Explorer；真实菜单显示与桌面点击仍需用户安装后验收。首次稀疏范围选择、Partial 跨分支合并和暂存集应用继续保留在 ROADMAP。

## 工作区路径输入、完整历史与直接比较（2026-09-28）

- 打开已有工作区改用可编辑目录输入框，支持粘贴、带引号路径和中文空格路径，并保留浏览按钮。历史窗口首次打开、“刷新全部”和 F5 均自动遍历全部批次，包括没有路径匹配项的中间批次；移除手动加载更早，保留进度、取消、失败重试及刷新失败时的旧结果。
- 历史下方文件列表的 Ctrl+D、双击和比较按钮直接准备版本并调用 Beyond Compare，不经过历史文件设置窗口；导出/自选版本作为独立高级入口保留。默认查询真实父提交，使用服务器比较结果的旧/新路径和新增/删除空侧，本地文件缺失不阻止历史比较。标记版本比较保留明确版本对、仓库身份复核及重复启动保护。
- 专项合并运行：路径选择 UI 36、完整历史 UI 25、直接比较 UI 46 项通过；后端历史分页 39、历史 BC 90、变更集比较 49 项通过。覆盖空页继续、取消/失败保留、同选中提交详情恢复、真实父关系、新增/修改/删除/移动、工具调用参数及仓库变化。日志在 `bin/TortoiseSCM/qa/history-experience/`。
- 真实只读工作区验证 20 项通过：根历史自动加载全部 847 条（9.68 秒），刷新全部 7.02 秒、F5 6.80 秒；本地缺失 `selected 中文.txt` 自动扫描全部 847 个提交，得到 25 条历史（318.19 秒）。cs846 真实父提交为 cs845，历史内容成功导出到 QA 目录。前后 1,997 个用户文件的 SHA-256、路径、长度和修改时间及 selector/workspace/wktree/settings 均一致。证据在 `qa/real-history-20260928/`。
- 普通/最小尺寸的路径输入及历史窗口截图已查看。文件历史逐提交读取明细的性能待优化，已列入 ROADMAP；现有路径历史仍不追溯重命名前的其他路径。本轮 BC 直接调用使用工具替身验证，真实查询/导出单独验证，不将其作为实际 BC 窗口操作验收。
- 最终 Release 构建和完整 WinForms 回归通过（`qa/history-experience/release-build.log`、`full-ui.log`）。安装/卸载 45、双击入口与 Explorer 协调 83、随包 BC 39 项回归通过；使用隔离目录且不注册、不重启实际 Explorer，不修改用户现有安装。

## 安装后检测旧菜单与确认重启 Explorer（2026-09-28）

- 安装器区分磁盘安装成功与 Explorer 实际加载状态，显示旧/新目录；检测到旧扩展后提供重启选项。按用户最终偏好，按 Enter 确认、输入 `N` 或其他非空内容则稍后；提示先完成复制/移动/删除/解压任务，说明可能中断文件操作、关闭文件夹窗口及桌面/任务栏短暂消失，不声称自动检测了文件操作是否完成。
- `-NoPause`、`-NoRegister`、非交互或重定向输入不触发重启，EOF/读取异常不视作确认。重启目标限制当前用户 SID、当前登录会话及系统 Explorer 路径，持有进程句柄并在停止前复核身份；等待自动恢复，必要时启动 Explorer 并限时核对桌面 shell。失败保留已完成安装并提供任务管理器/运行新任务恢复说明。
- 新 helper 随包进入哈希清单，部分卸载保留依赖供双击重试。双击入口及重启分支专项 83 项通过，包含模拟身份变化/其他用户和会话拒绝、选择稍后、停止或恢复失败及重启后版本未确认；日志 `bin/TortoiseSCM/explorer-launcher-full.log`。已有安装/卸载 45 项、随包 BC 39 项、现代菜单包 27 项通过，日志在 `qa/shell-dispatch-fix/`。
- 本机只读检查验证旧扩展检测及桌面进程身份 API；后者发现并修复了局部变量与 PowerShell 只读 `$ShellId` 同名的问题。重启/停止测试使用模拟对象，未替用户实际重启桌面，也未修改实际 Explorer 注册。真实桌面重启及用户文件操作状态仍需用户在确认提示后自行验收。

## Explorer 命令分派与独立更新窗口修复（2026-09-28）

- 复现用户报告：Explorer 设置 Unicode 标志、数字菜单编号放在 `lpVerb` 且 `lpVerbW` 为空时，旧代码将所有菜单项误读为编号 0（status）。新增测试先在旧源码失败，并通过旧生产 DLL 捕获 `Expected: update / Actual: status`。根据 [Microsoft IContextMenu 实现说明](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-implement-the-icontextmenu-interface)，数字编号从 `lpVerb` 读取，Unicode 字符串才使用 `lpVerbW`。
- 修复后 ShellTests 345 项、正式 DLL 路径 392 项通过；后者增加了文件/目录/目录背景中 update、checkin、history、version 共 12 次实际 DLL → 子进程 argv 检查。原生测试不注册 Explorer、不执行 SCM 写入。日志为 `bin/TortoiseSCM/shell-dispatch-{red,build,green}.log` 与 `shell-production-dispatch-{red,green}.log`。
- 程序入口将历史直接路由到原选择范围的日志窗口；更新进入独立窗口，明确 Standard 整体范围或 Partial 原始所选范围，点击更新才写入。主窗口更新菜单复用同一窗口；执行前核对工作区身份/分支/模式，执行中禁止重复或关闭，失败/超时保留结果且不自动重试。
- 更新及路由专项最终 70 项通过；完整 WinForms 回归通过。已查看更新窗口普通/最小尺寸截图 `qa/launch-routing/update-dialog*.png`。最终 Release EXE 分别以 history/update/checkin 启动，验证对应三个窗口并正常关闭，没有点击写操作。日志 `qa/shell-dispatch-fix/{release-build,full-ui,real-window-launches}.log`。
- 只读诊断确认用户活动安装指向新版，但 Explorer 内存仍加载旧 `0.13.0-preview.1` 扩展；安装目录切换无法替换已加载的 DLL。诊断记录在 `qa/shell-dispatch-fix/reported-*.json`。版本信息需从重启后的右键菜单打开核对，安装前已打开的程序窗口也需重新打开。

## 双击安装/卸载与版本信息（2026-09-28）

- 安装包新增 `Install.cmd`、`Uninstall.cmd` 和 PowerShell 协调入口。双击使用 64 位 Windows PowerShell、显示结果并等待回车；安装后打开欢迎窗口，升级保留已有系统级图标选项。卸载先显示并确认当前活动目录，绑定确认时的版本，保留工作区及用户设置；部分卸载保留双击重试入口。CMD 入口结束时关闭其命令窗口，终端自动化继续使用 PS1。
- 经典/现代右键、主窗口操作菜单和欢迎窗口新增版本信息，显示当前启动程序的包版本、完整源码提交、路径和架构，支持复制。独立 `--command version` 不要求工作区；缺失、损坏、过大或不可读的清单显示明确状态。窗口不查询远程最新版，也不声称检测到了 Explorer 已加载扩展的版本。
- Release 构建、完整 WinForms 回归、原生 Shell 与正式现代菜单 DLL 回归通过；版本专项 51 项通过，覆盖清单校验、复制失败和普通/最小布局。已查看版本窗口普通/最小及欢迎窗口最小尺寸截图。日志为 `qa/version-release-build.log`、`version-full-ui.log`、`version-shell.log`、`version-modern-dll.log`，截图在 `qa/version-ui/`。
- 双击入口专项 40 项通过：中文、空格、`& ! ()` 路径、不同工作目录、32/64 位 CMD 宿主、延迟展开、退出码、自删除、隔离安装、取消卸载、部分卸载恢复和损坏包拒绝。既有安装/升级/卸载 45 项、随包 BC 39 项、现代菜单包事务 27 项通过。日志为 `qa/package-launcher-tests.log` 和 `qa/click-version-{PackageTests,BundledBeyondComparePackageTests,ModernPackageTests}.log`；新专项已接入 CI。
- 安装测试使用隔离目录及 `-NoRegister`，实际 Explorer 注册和 Appx 注册保持不变；未替用户升级现有安装，也未把进程内菜单测试作为真实桌面点击验收。本次不改变 SCM 后端，未重跑服务器写入集成。

## 随包携带 Beyond Compare 运行文件（2026-09-28）

- 按用户要求，`Package.ps1 -BeyondCompareDirectory` 可将提供的 BC 运行文件放入 `Tools/BeyondCompare`，纳入 SHA-256 清单及既有版本安装/升级/卸载机制；未指定参数仍生成不含 BC 的包。README 区分两类包，并保留第三方 `License.html`。白名单不复制个人许可证、配置/会话、补丁程序及 BC Shell 扩展。
- 自动发现优先使用当前程序版本目录内完整的 `BComp.exe`/`BCompare.exe` 配对，再检测系统安装；显式自定义路径继续优先。“自动检测”按钮保存空路径，避免升级后仍绑定旧版本目录。当前用户已切换为空路径自动模式；不把本机路径写入产品默认配置。
- BC 配置/发现 40 项及设置 UI 53 项通过，C# 5 `/warnaserror` 编译通过；普通/最小设置页面已查看。新随包专项 39 项、既有包/隔离安装专项 45 项通过，覆盖缺文件、链接/非文件拒绝、完整哈希/字节核对、排除项、安装升级与卸载保留用户文件。新随包专项已加入 CI；本次未改动 SCM 后端，采用相关专项回归。
- 使用用户提供目录中的真实运行文件构建包，在 `qa/bundled-bc-live/` 独立目录 `-NoRegister` 安装。引用该安装版产品的 QA 探针确认自动解析的是本版 `Tools/BeyondCompare/BComp.exe`，通过产品进程等待器执行 `/solo /silent /qc=binary`，相同/不同文件分别返回 1/11，输入文件未改变；安装版两 EXE 的 SHA-256 与用户源文件一致。随后隔离卸载移除包内 BC，保留额外 QA 探针文件，未修改实际 Explorer 注册。
- 日志及截图：`qa/bundled-bc-build.log`、`bundled-bc-profile-tests.log`、`bundled-bc-ui.log`、`bundled-bc-ui/`、`bundled-beyond-compare-package-tests.log`、`bundled-beyond-compare-existing-package-tests.log`；真实调用 `qa/bundled-bc-live/equal.log`、`different.log`、`uninstall.log`。快速比较不能替代三方 Pro 授权、人工保存/放弃和实际 Explorer 点击验收，这些边界仍保留在路线图。

## 首次拉取已有仓库向导（2026-09-28）

- 无参数启动进入欢迎窗口，支持拉取仓库、打开已有工作区和设置；另有 `--command create-workspace [--path 新目录]` GUI 入口及主窗口操作菜单。向导查询服务器仓库，明确选择名称/原生 ID/GUID，创建 Standard 完整工作区后下载指定分支，默认 `/main`；不创建服务器仓库，也不提供 Partial/动态工作区创建。
- 后端 166 项断言通过，C# 5 `/warnaserror` 编译通过。覆盖 SSL 地址、异常仓库响应、身份改变、目录/名称/嵌套/链接拒绝、调用者输入快照、创建前取消、创建/下载超时分阶段保留、写入防取消、创建后新文件保护和跨进程全局创建锁。全局锁测试在元数据创建前拒绝第二请求，未启动原生命令。
- GUI 专项 87 项通过：取消/过期查询、服务器改变、确认取消、繁忙禁关闭/防重复、成功打开、预检失败可修正及创建/下载未确认时禁止重试。欢迎窗口、向导普通/最小尺寸与恢复说明截图已查看，见 `qa/workspace-creation-ui/`。创建与下载使用可配置 Plastic 命令超时，当前显示阶段进度。
- 最终真实集成 `WorkspaceCreationIntegrationTests` 19 项通过，fixture 为 `qa/integration-20260928-200416-d222ddfe/`：实际向导查询与拉取、指定隔离分支、Unicode 路径/CRLF/二进制内容、干净初始工作区、重复创建拒绝、后续更新和日志、实际 MainForm 预览签入、未选文件保留、独立 consumer 核对。原始 TestSCM 的 selector、状态和工作文件未改变。
- 最终 Release `build-tortoisescm.ps1 -Test` 完整回归通过（退出码 0），覆盖全部 Core、2297 项主 CLI、原生 Shell/正式 DLL 及完整 WinForms 测试；新增后端 166 项和 GUI 87 项均已纳入构建脚本。日志为 `qa/workspace-creation-regression.log`；真实向导集成另已接入 `-Integration`。
- 首次集成在签入成功后因测试器使用区分大小写的路径比较而失败（原生返回小写盘符）；修正测试断言，保持产品行为不变，使用上述全新 fixture 重测通过，未重放早期已成功的提交。最终日志 `qa/workspace-creation-live-final.log`，逐命令及断言记录为 fixture 下 `workspace-creation-results.json`，实际向导及主窗口截图也在该目录。
- 45 项包/隔离安装/卸载检查通过，实际 Explorer 注册未改变，日志 `qa/workspace-creation-package-tests.log`。首次安装与常用操作已写入使用说明；真实桌面 Explorer 点击、用户服务器权限/证书配置、BC 保存/放弃和大仓库体验继续列为未验收边界。

## 本机提交说明历史与模板（2026-09-28）

- 主提交窗口新增“说明历史 / 模板…”。按完整仓库标识隔离最近 20 条成功说明及 20 个命名模板，保留 Unicode、CRLF 和完整多行内容；替换编辑内容、覆盖/删除模板及清空历史需要明确确认。刷新和清空历史保留未保存的模板编辑，过期窗口不能覆盖其他进程的新版本。
- 只有 GUI 确认原生签入成功后才写入历史；失败、取消和结果不确定不写入。显式保存模板不等于自动恢复草稿。本机历史保存失败只报告警告，不改变已成功签入的结果，不提示重复提交。说明规则仍仅为非空，格式规则和 changelist 继续列为后续工作。
- 存储专项 176 项通过：仓库隔离、去重/容量、8 个并发子进程、互斥锁超时/遗弃、原子替换失败、损坏/未知版本文件保留及模板条件更新。新增 UI 128 项和既有签入 UI 47 项通过；普通/最小尺寸下主窗口、历史和模板窗口共 6 张截图已查看，位于 `qa/commit-message-ui/`。
- `CommitMessageIntegrationTests` 在新隔离 fixture `qa/integration-20260928-193655-5c14d538/` 上通过 24 项真实检查：Standard/Partial 的实际 MainForm 预览取消和原生签入、准确保存完整说明、成功清空编辑器、未选文件保持待定及独立 consumer 核对仅提交选中文件。原始 TestSCM 的 selector、状态和工作文件保持不变。
- 早期集成测试只调用临时 `Application.DoEvents()`，原生签入成功后 UI 续执行未恢复，导致测试超时；服务器已接受的隔离测试提交未重放。追踪确认后，将测试器改为持续的 STA `Application.Run()` 消息循环，保持产品签入代码不变，使用新 fixture 验证通过。诊断日志保留在 `qa/commit-messages-trace*.log`，最终真实集成日志为 `qa/commit-messages-integration-verified.log`。
- 最终 Release `build-tortoisescm.ps1 -Test` 完整通过，含 2297 项主 CLI、全部 Core、原生 Shell/正式 DLL 及完整 WinForms 回归，日志 `qa/commit-messages-regression.log`；45 项包/隔离安装/卸载测试通过，实际 Explorer 注册未改变，日志 `qa/commit-messages-package-tests.log`。本阶段为真实后端和进程内 WinForms 验证，不包含桌面 Explorer 点击或新增 Beyond Compare 人工验收。

## 提交与合并关系图（2026-09-28）

- 新增只读分页关系图：服务器真实 `PARENT` 作为父提交，原生 `find merge` 的来源、目标、类型和可选基线作为关系数据。父提交用实线、普通 merge 用虚线、其他原生类型用点划线；区间基线只显示在明细，不伪造祖先。页外来源标记为未加载，图形连线绕开中间节点；右侧原生提交列表承担键盘和辅助功能访问。
- 新增 `revision-graph` CLI（JSON 与文本）及主窗口、Explorer 入口。CLI 允许 `--before` 独占游标和 `--limit 1..100`；默认 100。每页最多验证 1000 条 merge 记录，超过则拒绝返回不完整图。固定仓库、selector、Standard/Partial、取消、超时、异常 XML、对象 ID/分支不一致均会失败并清除旧结果。
- RevisionGraphTests 后端 49 项、RevisionGraphCliTests 84 项、RevisionGraphUiTests 45 项通过。UI 普通/最小尺寸图已查看；覆盖长分支单行省略、跳过中间提交的 gutter 路径、重叠关系不同端口、分页替换/返回、根提交、精确文件明细和固定快照。
- `RevisionGraphIntegrationTests.ps1` 最终 225 项通过，使用已有隔离 fixture 的 Standard/Partial 工作区只读核对普通 merge、interval subtractive、基线、真实节点分页和边界；三个工作区的 status、selector、wktree 和全部工作文件 SHA256 保持不变。最终 Release WinForms live 检查由同一构建的 `UiTests.exe` 执行。
- Release `-Test` 完整回归通过：既有 Core/CLI、130 标签后端、200 标签 CLI、原生 Shell/正式 DLL、45 项图 UI；44 项隔离安装/卸载检查通过，Explorer 注册未改变。未进行桌面 Explorer 点击或多 DPI 验收。
- 证据：`qa/graph-final-build.log`、`qa/graph-live-final2.log`、fixture 下 `revision-graph-d41dce476f1f4216b83dcafa218e83c2/results.json`；截图见 `qa/graph-ui-worker/` 与该 fixture 的 Standard/Partial 子目录。

## 仓库标签管理（2026-09-28）

- 新增 GUI 标签列表/筛选、目标提交文件明细、固定快照浏览、创建和删除确认，入口包括主窗口、Explorer 和历史右键。布局参考上游 `IDD_BROWSE_REFS`、`IDD_NEW_BRANCH_TAG`，使用原生控件及既有 DialogStyle。CLI 新增 `labels/label-resolve/label-create/label-delete`，提供 JSON、精确身份及严格参数范围检查。
- 后端 130 项、标签 CLI 200 项检查通过；覆盖特殊名称只读解析、Unicode/多行说明、异常 XML、固定仓库、读取及写入期间 selector/仓库改变、创建冲突、过期删除身份及不确定服务器结果。GUI 专项 69 项通过，覆盖确认取消、异步读取取消、写入防重复/防关闭、失败清除旧选择、删除后的刷新失败提示，以及普通/最小尺寸布局。
- 原生探测确认 `cm label create` 可重新应用已有标签，故创建采用唯一临时名称与重命名发布；服务器实测同名重命名失败且保留双方 ID。探测标签均在隔离分支 cs719 上创建并清理。原生未提供可用的按 ID 条件删除，因此不能承诺跨客户端原子删除；失败后的临时标签名称与仓库均保留在可复制错误提示中。
- `LabelIntegrationTests.ps1` 最终 43 项通过，复用 `qa/integration-20260928-011857-5cfb79d9/`。对 Standard/Partial 创建中文带空格和 `&` 的标签及 CRLF 多行说明，在脏 Standard、干净 Standard、Partial 中查询并浏览固定快照；重复创建和过期 ID/变更集删除均拒绝。真实 WinForms 窗口分别连接 Standard/Partial，选中标签、加载 cs719 提交明细并构造固定仓库/变更集的浏览器。原生标签清单完整恢复，三工作区 status、selector、wktree 及所有非元数据文件 SHA256 不变。
- 最终 Release `-Test` 覆盖全部 Core、既有 CLI、原生 Shell/正式 DLL 接口及完整 WinForms 回归。44 项打包/隔离安装/卸载检查通过，实际 Explorer 注册未改变。正常/最小尺寸渲染已查看；这是进程内 WinForms 验证，不是 Explorer 桌面点击或多 DPI 验收。现代菜单仍保留上一阶段的未签名部署限制。
- 日志：`qa/labels-final-build.log`、`labels-live-final.log`、`labels-package-tests.log`；真实服务器命令与 GUI 输出：隔离 fixture 下 `labels-fb483d2137ae4ce3ad45f5d83b85e0eb/results.json`，截图见其 `producer/` 与 `partial/`。保留原 TestSCM 的干净 `/main`，未替换系统安装。

## Windows 11 现代菜单接口与预览包（2026-09-28）

- 新增独立 CLSID 的 `IExplorerCommand`、`IObjectWithSite` 和子命令枚举器，复用经典菜单的命令表、范围规则和启动器。快速菜单状态查询推迟文件系统探测，不运行 Plastic CLI。枚举 GUID 稳定；点击时重新读取选择，拒绝跨工作区/元数据/无效路径。目录背景通过 Shell site 解析；导航树缺失明确选中项时拒绝操作，避免误用当前文件夹。经典菜单重复项抑制参考保留的上游实现。
- 原生源码测试 321 条 PASS；直接加载正式 DLL 的接口及进程传递检查 203 条 PASS。后者通过 `DllGetClassObject` 创建实际产品 DLL 中的对象，核对图标路径对应的 DLL 版本，并在隔离目录以短命 recorder 代替 GUI EXE 验证真正的 `Invoke` → `CreateProcess` → UTF-8 pathfile，覆盖中文、空格、`&` 及选择改变。测试命令为 `ShellTests.exe --modern-dll <binary-directory>`，已纳入 `build-tortoisescm.ps1 -Test`。
- 可选 `Install.ps1 -EnableModernMenu`、Sparse MSIX、独立注册/注销脚本、图标及文件哈希清单已加入包。同版本迁移仅对 `0x80073CFB` 移除重试，核对实际 external location；失败恢复原注册，独立尝试恢复经典注册，回退失败保留文件。卸载旧版本不注销新版本，独立注册的目录也不能在仍被使用时移除。
- 44 项包/隔离安装/卸载测试、27 项现代包测试通过；后者真实检查 MSIX/manifest/哈希，然后替换 Appx 系统调用边界验证版本迁移、任意失败、移除失败、部署无操作和回退，**不等于真实 Appx 升级已通过**。另有 4 项非管理员真实预检通过，拒绝前不创建安装目录、不改变当前版本指针或经典/Appx 注册。
- 主机为 Windows 11 build 26200，非管理员。真实 `Add-AppxPackage -AllowUnsigned -ExternalLocation` 返回 `0x80073D2B`（未签名包不能包含可执行激活）；独立测试身份的开发注册返回 `0x80073CFF`（开发/旁加载策略未满足）。没有修改信任证书、开发者模式或系统策略；最终没有本任务的 Appx 包残留。因此 **打包 COM 激活和 Explorer 现代菜单实机显示尚未验收**，不应把接口测试当成已显示菜单的证据。已提供 `ShellTests.exe --modern-registered <binary-directory>` 供成功注册后的环境验证。
- 最终 `build-tortoisescm.ps1 -Configuration Release -Test` 退出 0，包含全部 Core、2068 项既有 CLI、5 项 blame CLI、21 项 shelveset 内容 CLI、21 项仓库浏览 CLI、524 条 Shell PASS（321 + 203）及完整本地 WinForms 检查。CI 已加入现代包/注册回退专项测试。
- Computer-use 初始化后 native pipe 不可用，按要求重试、重置后仍失败；没有宣称完成桌面点击或截图验证。现有系统安装/经典注册未替换，TestSCM 保持干净。
- 日志：`qa/modern-menu-full-build.log`、`modern-menu-package-final.log`、`modern-menu-lifecycle-final.log`、`modern-menu-preflight-final.log`；专项原生日志另见 `qa/modern-shell-agent/{test-results,production-results}.txt`。预览包是本地开发测试产物；受信任签名及真实 Explorer 验收仍需后续完成。

## 固定快照仓库浏览器（2026-09-28）

- 新增原生目录树、文件列表、只读预览和单文件导出，入口包括 Explorer、主窗口及历史记录右键；CLI 新增 `repository-list`。默认使用当前分支头，可指定 cs:0。目录读取固定仓库/变更集，不依赖本地加载范围。
- 新增目录后端 37 项、CLI 21 项检查；历史文件 47 项包含查询/下载期间仓库或 selector 改变时拒绝返回和导出、保留已有目标字节，以及显式绑定已浏览仓库。CLI 拒绝混合旧工作区信息与新仓库目录结果。
- GUI 专项 30 项通过，覆盖延迟目录读取、符号链接禁用、快照切换、失败/取消清理、中文预览及换行、正常/最小尺寸；真实隔离 Standard 工作区 GUI 9 项通过，核对默认当前分支头、历史文本预览及子目录/上级导航。截图为程序内 WinForms 渲染并已目视检查，不代表真人 Explorer 点击或多 DPI 验收。
- `RepositoryBrowserIntegrationTests.ps1` 复用 `qa/integration-20260928-011857-5cfb79d9/manifest.json`，35 项真实只读检查通过：Standard 有修改/干净及 Partial 三种工作区目录一致，中文路径身份与大小匹配 native XML、cs:0 空目录、缺失/文件/越界目录拒绝，导出与 native cat 字节哈希一致。待定条目、selector、wktree 保持不变；待定条目按行排序比较，因为原生输出顺序不固定。日志 `qa/repository-browser-live.log`。
- 包/隔离安装/卸载 44 项通过，实际 Explorer 注册未改变（`qa/repository-browser-package.log`）。本轮构建产物位于 `bin/TortoiseSCM/Release`，没有替换系统已安装版本。
- Release 构建及 Core、2068 项既有 CLI、5 项 blame CLI、21 项 shelveset 内容 CLI、新增浏览 CLI 与 Shell 检查通过（`qa/repository-browser-full-build.log`）。带真实工作区的完整 GUI 回归暴露了既有菜单测试仍假设 6 项；更新为包含 Blame 的 7 项后，重新编译并完整运行 GUI，393 条 PASS、退出 0（`qa/repository-browser-ui-final.log`）。未将首次含旧断言的 `-Test` 日志误记为全程成功。
- 最终 Partial GUI 另有 9 项真实检查通过（`qa/repository-browser-partial-ui.log`），验证默认当前分支头、预览、目录导航；最小尺寸截图已检查。原始 TestSCM 最后仍无待定条目，selector 保持 `/main`，隔离 consumer 干净。
- 边界：单文件导出；预览限 UTF-8 且不超过 2 MiB；符号链接只显示，跨仓库链接拒绝；尚无递归整目录导出。

## 原生分支层级浏览（2026-09-27）

- 分支窗口新增列表/层级切换及定位当前分支，沿用原生 TreeView 与既有头提交、历史、创建、切换和合并操作。层级明确只表示服务器 `Parent` 的父子关系，不推断提交继承或合并边。视图切换按完整分支名保留真实对象，筛选清掉旧选择与详情，右键操作使用当前视图的选中分支。
- `branch-tree --path ... [--filter ...]` 提供同样的只读层级。模型按显式父关系排序，筛选保留祖先并标记 `isMatch=false`，缺失父节点不虚构，直接子节点计数保持过滤前语义。循环、重复、混合仓库及坏数据拒绝；GUI 清空列表/树/详情并禁用旧操作。CLI 文本最多缩进 32 层，深层另标实际深度，JSON 保留精确 depth。
- 层级模型 57 项检查通过，包含 20,000 层深链、12,000 个直接子节点、Unicode 筛选、精确父身份和输入不变；CLI 独立完整回归 1809 项通过（`qa/branch-cli/branch-tree-tests.log`），含 100 层文本输出上限。GUI 本地 122 项通过（`qa/branch-hierarchy-ui.log`），覆盖 1500 层、视图切换、祖先操作身份、当前定位、Partial 写操作边界及异常数据处理。
- 复用隔离 fixture `qa/integration-20260927-110239-853c930c/`，没有创建或修改服务器分支。19 项公开 EXE 真实只读检查通过（`qa/branch-hierarchy-live.log`、fixture 下 `branch-hierarchy-results.json`）：逐项对照 native 分支 XML 的父节点、深度顺序、子节点数和头提交；中文子分支筛选精确保留祖先；Standard/Partial 的工作树、selector、wktree 和 fullupdate 元数据保持一致。
- 最终完整 `build-tortoisescm.ps1 -Test -Workspace <producer>` 通过（`qa/branch-hierarchy-full-build.log`），包含 1809 项 CLI、287 项 GUI、57 项层级模型及既有后端/Shell 检查。GUI 同时验证真实当前分支定位、选中对象与头提交明细；普通/最小截图在 `qa/Release/branch-hierarchy*.png`。包/隔离安装/卸载 44 项通过（`qa/branch-hierarchy-package.log`）。界面验证使用程序内原生窗口渲染，不声称真人 Explorer 点击或多 DPI 验收。

## 子分支创建与精确分支历史（2026-09-27）

- 分支列表右键新增创建子分支及本分支历史。创建对话框参考原生分支创建布局，明确父分支、固定起点、短名称和必填说明，完成后选中新分支；不自动切换、签入或加载文件。CLI 新增 `create-branch --branch ... --changeset N --comment ... --yes`，支持 Standard/Partial。
- `history-page --branch` 及历史窗口精确筛选分支自身发布的提交，并与路径范围取交集；不混入继承自祖先的提交。保持每页全仓扫描预算和游标，其他分支不做文件差异查询，空页仍可继续。历史文件右键打开路径历史时保留分支过滤。
- 创建前后固定仓库和 selector；审查补齐首次异步工作区发现前的上下文捕获，以及 GUI 到后端的显式预期上下文传递，防止窗口打开后外部切换仓库而在另一仓库创建分支。网络失败、取消或后置验证失败时明确提示服务器分支可能已经存在，不自动删除或重复提交。
- 原生首次实测发现分支短名禁止单引号、冒号和问号，已在 GUI/后端创建前拒绝；中文、空格及 `&` 可用。通用读取的合成引号名称测试仍保留，用于验证分支过滤不拼接查询表达式。创建说明要求非空，避免本机 `PLASTICEDITOR` 在空说明时打开外部编辑器。
- 独立 fixture `qa/integration-20260927-110239-853c930c/`：历史基线 cs:691、较新父分支 cs:692，公开 EXE 创建子分支后验证旧版本树；子分支提交 cs:693/694，父分支无关提交 cs:695。最终候选 EXE 的 30 项真实检查通过（`qa/branch-creation-final-live.log`、fixture 下 `branch-creation-results.json`），覆盖多行中文说明、重复/非法名称、缺少确认、脏工作树保留、Partial 加载元数据保留、自身提交/空页游标/不存在于当前树的目录过滤。测试结束干净并恢复隔离父分支。
- 完整 `build-tortoisescm.ps1 -Test -Workspace <producer>` 通过（`qa/branch-creation-full-build.log`），含最终 Core 的 83 项分支、39 项历史分页以及其他后端/Shell/GUI；当时 CLI 为 1662 项。其后 CLI 也显式传入首次捕获的上下文，并增加首次 status 期间 selector 改变的拒绝检查；分支历史标题补齐独立的路径范围行。真实子分支的 16 项 UI 定向检查通过（`qa/branch-creation-live-ui.log`），普通/最小截图可见 cs:693/694 与选中提交的文件明细；创建窗口包含成功/失败 mock、不可重复提交及异步上下文检查。
- 最终程序重新构建（`qa/branch-creation-shipping-build.log`）后通过 1668 项 CLI（`qa/branch-creation-shipping-cli.log`）、260 项完整 GUI（`qa/branch-creation-shipping-ui.log`），以及包/隔离安装/卸载 44 项（`qa/branch-creation-package.log`）。创建与历史窗口均使用原生 WinForms 控件并检查普通/最小尺寸；不声称真人 Explorer 点击、多 DPI 或分支图验收。

## 分支浏览、安全切换与合并入口（2026-09-27）

- 新增原生双窗格分支窗口及 `branches`、`branch-head`、`switch-branch` CLI。支持筛选、刷新/取消、当前分支和头提交文件详情；合并入口先解析固定 changeset，再进入既有预检/确认/解决/提交流程。
- Standard 切换要求显式工作区根目录，持有结构与合并锁，前后核对 selector、仓库及干净状态；待定更改、私有/忽略项、嵌套工作区及未完成会话均拒绝。Partial 只浏览。原 TestSCM 的旧式 `br/co` selector 可正确识别 `/main`，未改动原工作区。
- 独立 fixture `qa/integration-20260927-104438-fd4c191d/`，基线 cs:689，子分支 `feature 中文 & space` 的 cs:690。最终 EXE 的 31 项服务器检查通过（`qa/branch-final-live.log`、fixture 下 `branch-results.json`）：中文分支往返及内容、Partial/非根目录/缺少确认拒绝、本地修改/私有/忽略项保护、只读固定来源合并预检、原生未完成合并保护。测试结束干净且恢复原隔离 selector，没有发布合并。
- 初次 live 检查与 GUI 回归共用 producer，干净切换被临时状态拒绝；随后单独运行完整 live 检查通过。后续 GUI 与写测试串行执行，避免测试互相干扰。
- 完整 `build-tortoisescm.ps1 -Test -Workspace <producer>` 通过（`qa/branch-full-build.log`），包含 1508 项 CLI、49 项新分支后端及既有后端/Shell/GUI。交叉审查随后补齐解析来源期间目标 selector 改变的保护：从查询前保留上下文直到构造合并窗口及实际操作，拒绝同仓库目标分支悄然改变；3 项本地竞态检查通过。最终 EXE 已重新构建（`qa/branch-final-build.log`），真实 live 检查使用此最终 EXE。
- 修复后的完整 GUI 回归通过（`qa/branches-final-ui.log`），包括新增目标竞态检查；普通及最小分支窗口在 `qa/branches-final-ui/branches*.png`。最终 EXE 的包/隔离安装/卸载 44 项通过（`qa/branch-final-package.log`）。
- 本阶段尚未提供分支图、创建/删除/重命名分支、Partial 分支切换。界面验证使用程序内 WinForms 渲染，不声称真人 Explorer 点击或多 DPI 验收。

## 完整变更集差异浏览（2026-09-27）

- 新增只读 `diff-changesets --from N --to N`，比较完整仓库两个快照；GUI 从历史列表的比较标记打开独立差异窗口，提供筛选、刷新、取消和文件操作。原生 A/C/D/M 记录保留，目录、链接和 Xlink 仅显示结构项；不累加中间提交，也不展开未修改的目录子项。
- `diff-history --from-item` 及 GUI 支持跨路径内容比较；移动同时修改的 C/M 两行都能定位旧路径，移动目录下的修改文件也映射到原目录。文件窗口可分别导出两端，新增/删除仅允许导出存在的一端。普通历史文件窗口同样固定仓库上下文，避免打开后切换仓库却继续读取同号版本。
- 独立分支 fixture 为 `qa/integration-20260927-103107-af4bb423/`，cs:685 → cs:688（含中间 cs:686/687）。最终公开 EXE 的 31 项服务器检查通过，日志 `qa/changeset-comparison-final-live.log`，详情 `changeset-comparison-results.json`：新增、删除、修改、中文移动、目录移动下修改、空目录、反向/同版本、Partial 未加载内容、二进制、先改后还原内容、删除文件导出。验证前后工作树字节、selector 和 pending 均不变。
- 新后端 49 项断言覆盖格式/路径拒绝、缺失端点、取消、仓库变更、移动多状态、目录后代、同名替换和外部工具参数。首次真实测试准备时发现 Windows PowerShell 5 对无 BOM 脚本的中文解码不一致，测试脚本已保存为 UTF-8 BOM，使用新的上述 fixture 重测；旧试验分支保持隔离。
- UI 首次取消测试暴露的是无 `Application.Run` 的测试器在子窗口关闭后丢失 WinForms 同步上下文。已显式持有该上下文并启用跨线程检查；真实窗口专项含连续 20 次刷新/取消验证。目视检查还发现原生 TextBox 对 LF 差异文本不分行，现只在 GUI 显示时转换 CRLF，CLI 保持原始 diff 文本。
- 最终完整 `build-tortoisescm.ps1 -Test -Workspace <producer>` 通过，日志 `qa/changeset-comparison-final-validation.log`，包含 1209 项 CLI、49 项新后端及既有后端/Shell/GUI。包/隔离安装/卸载 44 项通过（`qa/changeset-comparison-package.log`）；最终 UI 编译产物在真实 cs:685 → cs:688 的 73 项专项通过（`qa/changeset-ui-live-final.log`）。普通/最小差异窗口和移动文件窗口已目视检查，图片分别在 `qa/changeset-ui-live-final/` 与 `qa/Release/changeset-*.png`。这些是程序内渲染，不声称真人 Explorer 点击、多 DPI 或实际外部第三方工具验收。

## 历史窗口操作与状态图标语义修正（2026-09-27）

- 历史列表增加比较起点标记/清除、复制编号和说明；文件列表增加与标记版本比较、显示路径历史、复制当前/原路径，支持 F5 刷新和按列表焦点 Ctrl+C。显式比较版本及用户编辑不再被异步版本建议覆盖；打开子窗口前复核工作区和仓库身份。
- CO 表示签出，显示 Modified，不再误报 Locked。AD/DE 只描述项目本身，父目录汇总为 Modified；PR/IG 不污染受控祖先，并清除被其覆盖的旧 Normal 子树缓存。冲突仍具有最高优先级。
- 安装失败的注册快照补齐全部九个 CLSID 与八个机器级 Overlay 标识，避免升级中途失败时残留新增注册项。
- 本轮测试使用隔离分支工作区 `qa/integration-20260925-151238-8b3f5c34/producer`；原 `TestSCM` 只做只读核对。比较基准功能针对同一路径文件，尚不提供完整变更集目录差异或分支图。
- 完整 `build-tortoisescm.ps1 -Test -Workspace <producer>` 通过：980 项 CLI、226 项 Overlay、27 项历史分页及其他后端、Shell、GUI 回归，日志为 `qa/history-overlay-validation.log`。UI 强制找到真实文件进行比较，覆盖标记版本不被自动建议覆盖、实际同版本内容比较、已删除路径、越界/元数据路径拒绝和 F5 刷新。44 项包/隔离安装/卸载检查通过，日志为 `qa/history-overlay-package.log`。
- 已目视检查重新生成的 `qa/Release/history.png`、`history-minimum.png`、`historical-file.png`、`historical-file-minimum.png`，普通及最小尺寸下列表、版本输入和底部按钮可见。这些是 WinForms 渲染证据，尚不等同于真人 Explorer 菜单点击或多显示器 DPI 验收。
- 真实 Overlay 集成通过：私有文件、内容修改、CO 签出、AD 新增、DE 删除，逐项核对后端状态、后台进程缓存和已注册 COM handler，并核对父目录汇总；最终撤销全部测试更改、恢复字节、正常停止缓存进程，隔离工作区保持干净，无服务器提交。日志为 `qa/history-overlay-live.log`，逐项结果为 fixture 中的 `overlay-integration-results.txt`。测试时已注册 DLL 与本次构建 DLL 的 SHA-256 一致；仍未验证机器级 Overlay 槽位的实际显示。
- `0.8.1-preview` 已由 `6214471d6` 构建包安装到当前用户，安装后真实 COM 激活通过（`qa/history-overlay-installed-shell.log`）。安装版 CLI 查询原 TestSCM 返回空待定列表、`/main` selector；历史文件 `/repeat-second/a.txt` 的 cs:679 → cs:681 比较返回真实文本差异（`qa/history-overlay-installed-diff-changed.json`）。安装路径、文件哈希及包路径记录在 `qa/history-overlay-release-record.json`。
- 后续核对发现首轮集成测试在成功检查之后的 finally 恢复旧时间戳，导致相同内容再次被 Plastic 标为 CH。`qa/overlay-cleanup-diff.json` 证明内容无差异，Undo 前后 SHA-256 相同；已删除时间戳恢复，并将最终干净检查和成功记录移至全部清理结束之后。此为测试清理修正，产品二进制未变化。
- 修正后以安装版 EXE 和已注册 COM 完整重跑，22 项真实 Overlay 断言通过（`qa/history-overlay-live-final.log`），包括所有清理结束后的干净检查；随后独立执行 `cm status` 也为空。原 TestSCM 保持 `/main` 且干净。

日期：2026-09-25。上游基线：`acc10fc20`。运行环境：Windows x64，Plastic `11.0.16.10330`。

## 暂存集应用与删除（2026-09-28）

后续完成 GUI 应用/删除确认和 GUI/CLI 内容比较、导出：

- `build-tortoisescm.ps1 -Configuration Release -Test` 完整通过（Core、2068 项既有 CLI、5 项 blame CLI、Shell、WinForms UI）；新增内容 CLI 套件最终单独通过 21 项。日志为 `bin/TortoiseSCM/qa/shelve-content-final-build.log` 与 `shelve-content-cli-final.log`。
- 比较/导出检查包括父版本文本差异、二进制字节、新增/删除/移动、删除项不下载、manifest 冲突、输出覆盖、嵌套工作区保护、下载失败不改变已有输出。Apply/Delete 捕获确认时的仓库和 selector，测试模拟其变化并确认没有发出原生写命令。
- GUI 检查包括确认拒绝、确认期间切分支拒绝、写入期间关闭/重复提交/取消禁用、删除后刷新、取消和错误仓库比较结果不展示，以及 LF 差异文本转换为 Windows 多行预览。普通及最小尺寸图片位于 `bin/TortoiseSCM/qa/Release/shelves*.png`；最后 UI 日志为 `shelve-content-ui-final.log`。这些是程序内 WinForms 渲染与控件测试，未进行真人 Explorer 桌面点击验收。
- 真实隔离分支 `tortoisescm-autotest-integration-20260928-011857-5cfb79d9`：既有保存/浏览 56 项通过；新增内容流程 16 项通过（包含清理检查）。为 PowerShell 5.1 的两个含中文测试脚本补上 UTF-8 BOM，确保文件名不被按系统代码页误读；Standard/Partial 比较均读取真实 `#sh:` 内容；D 盘导出逐文件哈希吻合并验证覆盖拒绝/确认；CLI apply 恢复选中文件，CLI delete 后编号消失，consumer 最终干净。可重复脚本为 `test/TortoiseSCM/ShelveContentIntegrationTests.ps1`，结构化证据为该 fixture 的 `shelve-content-results.json`，日志为 `bin/TortoiseSCM/qa/shelve-content-live.log`。
- 原始 `TestSCM` 保持 `/main` 且无待定更改。已有目录导出按文件替换，不承诺多文件事务；结构导出、暂存集应用冲突向导尚未交付。本轮更新构建产物，未更新系统安装副本。

- Core 安全门和 CLI 黑盒共 2063 项断言通过：Standard 干净工作区应用成功，Partial/Gluon、待定更改、合并/结构会话和仓库不匹配均在原生写操作前拒绝。
- 应用命令使用仓库限定的 `sh:<id>@<repository>`，执行后重新验证工作区上下文；失败或验证不确定时返回失败并要求检查状态，不自动重试。
- 删除命令先校验 shelveset 属于当前仓库，执行后重新查询确认编号消失；CLI 写命令必须显式 `--yes`。证据：`.omo/evidence/shelves-apply-delete-20260928.txt`。

## 暂存集保存与浏览（2026-09-27）

- 后端 37 项独立检查通过：列表 XML 的 `ObjectId/ShelveId` 身份、仓库限定详情、Standard/Partial 原生命令、评论/路径校验、并发上下文变化、结构项拒绝、合并会话闸门和取消/失败后的不确定结果提示。
- 新鲜隔离 TestSCM 分支工作区的 Standard/Partial CLI 实测共 56 项通过（`bin/TortoiseSCM/qa/shelves-live-success.log`）：各保存两个明确文件，排除目录外文件、私有文件和无关依赖；本地文件哈希、selector 和待定内容保留。对两个干净 consumer 使用官方 `cm shelveset apply` 后，两个选中文件哈希逐项一致，未选文件保持原状；consumer 最终清理干净。
- 安装版 `0.13.0-preview` CLI 对同一隔离仓库的只读列表返回 Standard/Partial shelveset 及真实 `sh:` 编号；安装后重新生成的 ShellTests 通过分支和暂存集 canonical verbs。GUI mock 与最小尺寸检查通过，真实 GUI 读回两个文件的暂存集详情也通过（`bin/TortoiseSCM/qa/shelves-gui-live/`）。
- Partial 原生 `--applychanged` 可能把 `CH` 状态文字变成 `CO`，因此产品只承诺本地内容保留；本阶段不提供应用、删除或 GUI 恢复，恢复由官方客户端完成。原始 `TestSCM` 仍为 `/main` 且 `cm status --short --machinereadable` 为空。

本轮新增：文件/目录历史恢复、工作区快照切换、递归目录历史与提交明细、提交列表右键菜单、外部 diff/merge 配置。
功能与命令对应见 [使用说明](TortoiseSCM.md)。

## Annotate / Blame（2026-09-28）

- Core parser: 13 assertions; rejects malformed rows, foreign repositories, directories and invalid ignore modes.
- CLI black-box: 5 assertions; JSON includes structured line ownership and Unicode content.
- Real read-only check: original `TestSCM` file returned 74 lines including empty lines; workspace remained `/main` and clean.
- GUI: native WinForms dialog has line, author, changeset, date, branch and content columns; Explorer exposes the command for a single file and suppresses it for directories.

## 已执行

```powershell
.\build-tortoisescm.ps1 -Test -Workspace 'D:\Work\Juscent\SCM_Study\TestSCM'
.\build-tortoisescm.ps1 -Integration
.\contrib\tortoisescm\Register-Shell.ps1
.\bin\TortoiseSCM\Release\ShellTests.exe --registered
```

| 检查 | 结果 |
| --- | --- |
| Release x64 EXE + DLL 构建 | 通过 |
| 后端断言（含原 TestSCM 添加/撤销测试） | 47 项通过 |
| 真实 EXE 的无服务器 CLI 黑盒断言 | 490 项通过 |
| 独立分支上的真实服务器 CLI 断言 | 47 项通过 |
| 外部工具真实进程断言 | 18 项通过 |
| 历史解析与删除目录范围断言 | 6 项通过 |
| 恢复 API 的 Standard / Partial 实测 | 各 26 项通过；后来增加的 2 项范围单元断言另计 |
| Shell/COM 单元检查 | 26 项通过 |
| 当前用户注册后的真实 COM DLL 激活 | 20 项通过 |
| UTF-8 多选路径文件交接与消费清理 | 通过 |
| WinForms 工作区加载、范围过滤、勾选、历史加载、筛选、快速切换、分类设置及布局 | 35 项通过 |
| 默认窗口、最小窗口、设置 / 历史 / 合并窗口渲染 | 通过，已查看图片 |
| `git diff --check` | 通过 |
| TestSCM 测试后 `cm status --short --machinereadable` | 空输出，干净 |

原 TestSCM 工作区测试使用独立创建的中文、空格、`&` 文件名：私有 PR → 添加 AD → 撤销添加 PR → 清理。
未改动原文件或其 `/main` selector，测试后保持干净。

服务器测试在 TestSCM 的专用分支中真实提交小型测试文件。最终成功运行：
`bin/TortoiseSCM/qa/integration-20260925-005138-d336c446/`。
该目录包含 `manifest.json` 与 `cli-integration-results.json`，记录各命令的参数、退出码和 JSON 输出。
前期调试的测试分支/工作区保留，未合并进 `/main`；原工作区未切换。

真实服务器验证包含：

- 选择一个文件签入时，另一个已添加文件保持待定，consumer 收不到未选文件。
- consumer 更新后的字节内容与 producer 签入内容一致。
- 含中文、引号和换行的签入说明经服务器历史记录完整返回。
- 本地修改 → 无界面基础差异 → 签出 → 撤销后恢复原内容，其他待定项不受影响。
- Standard 工作区拒绝单文件/多文件更新且不改动内容；显式根目录更新成功。
- 真正 Partial 工作区的添加、签入、签出、撤销及精确单文件更新成功；Standard consumer 能收到 Partial 签入。
- 文件 / 目录恢复后字节与历史版本一致，并产生待提交更改；撤销后恢复当前版本。
- 目录恢复保留范围外的 pending，范围内已有 pending 时拒绝覆盖。
- 目录历史包含仅子文件内容变化的提交，提交明细包含对应文件。
- 目录 status 排除范围外 pending，目录 checkin 只提交选择目录及其后代。
- Standard 根目录切换旧快照保持干净，更新后恢复分支最新内容。
- Partial 历史快照切换、返回最新版本、子目录精确更新均核对实际字节。

额外目录结构恢复验证记录在 `bin/TortoiseSCM/qa/revision-manual-observations.json`，其中明确标注手工命令观察。
Standard 恢复正确产生后来新增文件的删除、旧文件的还原；Partial 原生恢复未完整删除后来新增文件，
所以产品执行前比较目录项路径和 ItemId，涉及增删、移动或未加载项时拒绝 Partial 目录恢复。
自动 API 实测使用原隔离 `integration-20260924-212859-dd4f3aa0` 的 consumer / partial，不涉及原 TestSCM。

这轮测试发现并修正了两处旧假设：`plastic.workspace` 的 `Standard` 标记并不表示当前仍是完整工作树；
原 TestSCM 实际是 Partial。现在通过 Plastic 状态头识别。Standard 工作区过期时不支持单文件更新，
现在 CLI 要求显式根目录，GUI 明确确认整个工作区更新。
差异命令另核对了 `fileinfo` 的基础变更集，以及实际差异工具的基础临时文件与本地文件参数。

后端模拟进程检查包含 Windows 引号/反斜杠参数往返、双输出流各 2 万行、超时、取消、
XML/路径校验、Partial 多路径更新先全部验证再逐项执行、失败即停止且保留已完成输出。
CLI 黑盒测试启动真实 `TortoiseSCM.exe`，验证 UTF-8 stdout/stderr、JSON、退出码、无窗口、
无选中路径/未确认写入的拒绝、工作区模式、结构化历史、无界面差异及超时。

注册态检查通过 `CoCreateInstance` 加载已安装 DLL，并核对接口代码地址来自真实 `TortoiseSCMShell.dll`。
它覆盖文件、目录、背景、多选、命令编号、Unicode/ANSI verb、跨工作区和元数据目录隐藏。

窗口渲染文件位于 `bin/TortoiseSCM/qa/Release/`：
`pending-changes.png`、`pending-changes-minimum.png`、`settings.png`、`history.png`、`history-minimum.png`、`merge-tool.png`。
这些是程序内 WinForms 渲染，不是实际 Explorer 桌面截图。
最终构建和本机回归日志为 `bin/TortoiseSCM/qa/final-validation.log`。

## 原生对话框外观调整

2026-09-25：参考上游资源调整提交、历史、设置、三方合并窗口；共享字体、系统颜色、Explorer 列表主题。
提交窗口改为说明在上、文件在下、底部标准操作按钮；历史为双分隔条三段区域；设置改为左树右页。
原生风格调整后再次通过完整 `-Test -Workspace`，日志为 `bin/TortoiseSCM/qa/native-dialog-validation.log`。
GUI 新增检查覆盖最小尺寸提交按钮、路径优先列表、设置分类切换、历史筛选后清空旧明细和恢复列表，
以及已版本控制 / 未版本控制选择链接不会通过递归父目录选中排除的子项。
新增渲染图为 `settings-diff.png`、`settings-merge-minimum.png`；上列其他窗口图片均已重新生成和查看。
本轮仅改变 GUI，不重复服务器写入流程；CLI 382 项黑盒、后端 47 项、工具 18 项、版本 6 项及 Shell 26 项仍全部通过。

## 尚未验证

- Windows 自动点击服务连接失败，因此未完成真人等价的 Explorer 右键点击到 GUI 操作验收。
- 自动冲突解决与真实网络中断场景尚未端到端测试。外部工具测试使用真实启动的可控测试程序，验证参数、退出码、临时文件生命周期和合并输出，未验收第三方工具本身的界面。
- 未测试其他 Windows 版本、DPI 比例和机器部署。

原上游 UnitTests 基线另执行 585 项，584 项通过；1 项既有 UTF-8 fixture 在本机 CP936 编译环境下失真。
新增 Plastic 工程没有依赖该测试工程，并显式设置 UTF-8 源码编译。

## 阶段 1：历史文件与日常操作

2026-09-25：新增历史导出、两变更集同路径比较、整仓回滚为待提交更改，以及删除、移动、精确路径忽略。

| 检查 | 结果 |
| --- | --- |
| 历史文件独立测试 | 39 项通过，含二进制、覆盖保护、失败不截断、路径验证、外部工具与取消 |
| 整仓回滚独立测试 | 8 项通过；隔离工作区合计 20 项真实验证通过 |
| 文件操作独立测试 | 7 项通过，含已有规则编码保持及 hardlink 保护 |
| 文件操作 Standard / Partial | 各 24 项真实验证通过，结束后干净 |
| 新历史 CLI 端到端场景 | 30 项通过，包含回滚后再次提交及 consumer 验收 |

新 CLI 完整证据：`bin/TortoiseSCM/qa/integration-20260925-033556-cefe929a/cli-integration-results.json`。
cs42 为基线，cs43 包含增删改移动；回滚生成待提交更改、选择器不变，发布为新的 cs44，consumer 更新后逐项核对内容。
文件操作证据：`bin/TortoiseSCM/qa/integration-20260925-033155-88fea7ce/file-operation-results.json`。
整仓回滚 API 证据：`bin/TortoiseSCM/qa/integration-20260925-032653-23081833/workspace-rollback-results.json`。
最终构建日志：`bin/TortoiseSCM/qa/stage1-validation.log`；新增窗口渲染 `historical-file.png` 和 `historical-file-minimum.png` 已检查。

边界：历史比较缺失端点明确报错；重命名前后需分别使用对应路径，目前不自动追踪路径映射。
整仓回滚只支持 Standard 分支最新版本回退父链祖先，拒绝冲突、Partial 和过期工作区；不会自动提交。
文件原生修订历史可能复用旧修订而不列出整仓回滚的发布提交，该提交可在仓库历史与 changeset 明细中查询。
# 第二阶段：原生合并流程与锁管理（2026-09-25）

- `build-tortoisescm.ps1 -Test -Workspace D:\Work\Juscent\SCM_Study\TestSCM` 通过；日志 `bin/TortoiseSCM/qa/stage2-validation.log`。
- 后端 47、外部工具 18、历史恢复 6、整仓回滚 8、文件操作 7、历史文件 39、锁 39、合并安全 35、CLI 黑盒 642 项断言通过；锁另有真实只读查询通过（当前仓库没有锁）。
- GUI 验证包含原有待定更改/历史/设置及新增合并、锁窗口；检查目录冲突阻止开始、会话固定来源、已解决项禁止重复应用、非本人锁禁用释放，以及正常/最小尺寸渲染。图像位于 `qa/Release/merge-conflicts*.png`、`locks*.png`。这是进程内 WinForms 测试，不代表实际 Explorer 点击。
- 独立分支实测 25 项通过，结果 `qa/integration-20260925-035039-a13fe3f7/merge-integration-results.json`：共同基础 cs55，来源 cs56，目标 cs57，人工结果签入 cs58；核验三个贡献文件精确字节、跨进程会话恢复、未解决时拒绝签入、原生合并链接及 consumer 文件内容。
- 中断下载、历史项身份不符、外部修改冲突文件、模糊的原生应用失败/超时均有防护测试；不确定会话阻止签入，整仓撤销后可开始新会话。
- 未实际释放服务器锁；释放归属检查通过模拟命令测试。目录结构冲突和 Partial 传入冲突仍由官方客户端处理。
# 第三阶段：Explorer 状态缓存与图标（2026-09-25）

- 原生 Shell 测试 80 项通过（含已有菜单与新增图标协议、缓存失效、COM 和图标资源）；独立托管缓存测试 49 项通过。证据 `qa/overlay-native-results.txt`、`qa/overlay-managed-results.txt`。
- 原始 TestSCM 只读扫描生成 2,171 条显式受控路径状态；已注册的真实 DLL 可读取托管进程写出的缓存并提取三个嵌入图标。
- `qa/integration-20260925-035039-a13fe3f7/overlay-integration-results.txt` 记录 12 项真实测试：后台进程自动发现修改、文件/父目录状态同步、私有项无正常图标、原生撤销后恢复正常、停止进程清除状态。测试工作区最终干净，无服务器写入。
- `qa/integration-20260925-034524-0a8dec59/conflict-overlay-results.json` 验证现有真实未解决冲突通过实际 COM 组件返回文件及根目录 Conflict 状态；文件、selector、原生 mergeprogress 和保存会话的哈希均未变化。
- GUI 与 CLI 均有单次缓存刷新入口；文件监视只负责触发独立进程的扫描。缓存过期、损坏或扫描失败时不把未知状态当作正常。
- 当前会话非管理员，仅执行了当前用户的 COM 注册与激活测试；未执行机器级 Explorer 图标发现注册，也未声称真实 Explorer 窗口已经显示图标。随附 `Register-Shell.ps1 -EnableMachineOverlays` 需要管理员 PowerShell；图标槽位和 Explorer 重登录行为仍受 Windows 限制。
# 第四阶段：分页历史、完整发布回归与安装包（2026-09-25）

- 最终 `build-tortoisescm.ps1 -Integration -Workspace D:\Work\Juscent\SCM_Study\TestSCM` 通过，日志 `qa/stage4-validation.log`。CLI 黑盒 740、GUI 63、原生 Shell 80 项检查通过。
- 后端 47、外部工具 18、历史恢复 6、整仓回滚 21、文件操作 7、历史文件 39、锁 39、合并保护 40、缓存 49、分页历史 27 项断言通过。
- 全流程真实服务器测试 70 项，另有真实合并 25 项通过；`qa/latest-integration.txt` 和 `latest-merge-integration.txt` 指向本轮独立分支的完整结果。验证整仓回滚可作为待提交更改发布、合并元数据完整、独立 consumer 内容一致。
- 额外只读分页实测确认历史 cs44 的回滚发布出现在 Unicode 文件路径历史中。GUI 检查加载更早、刷新、取消保留记录以及最小尺寸下分页按钮和完整性提示可见。
- 合并/整仓回滚由主界面提交时使用明确的整仓范围确认；未保存会话的原生合并、未完成回滚和被外部改动的原生回滚状态阻止签入。整仓撤销可清理失败状态。
- 安装包 25 项验证通过（`qa/package-final-results.log`）：清单校验、解包 CLI 启动、独立版本安装、卸载保留用户文件/空目录/锁定 DLL、注册回退、跨进程互斥。测试期间实际 Explorer 注册保持不变。
- GitHub Windows CI 已加入源代码；本地验证通过。当前 GitHub API 无认证查询遭遇速率限制，未把远端工作流执行结果作为已通过证据。未验证签名 MSI、ARM64、32 位 Explorer、真实多显示器 DPI 切换或机器级图标安装。

# 第五阶段：目录结构冲突与 Partial 传入内容冲突（2026-09-25）

- 完整服务器回归 `qa/conflicts-final-validation.log` 通过：常规真实 CLI 70 项、分支合并 25 项、Partial 冲突 35 项；最新独立工作区由 `latest-integration.txt`、`latest-merge-integration.txt`、`latest-partial-integration.txt` 指向。安装包检查 `qa/conflicts-package-validation.log` 25 项通过。
- 收尾安全修正后的最终 Release 构建与本地回归见 `qa/conflicts-release-validation.log`：CLI 850、目录合并 47，以及全部原有后端、Shell、GUI 检查通过。最终 EXE 再次通过 9 项真实 Partial CLI 检查，结果保存在 `qa/integration-20260925-135646-a3223bfe/partial-cli-results.json`。
- 新增原生目录规划/逐项选择/应用/取消流程，四类已支持冲突为 EVIL、DIV_MV、CHG_RM、RM_CHG。选择不会改工作文件；稳定公开索引与原生重编号对应，未知类型拒绝执行。
- 目录专用 47 项检查覆盖显式选择、恢复会话、改名边界、忽略项保护、失败保持提交保护、完整撤销恢复，以及不同 settings 不能绕过工作区计划标记。
- `qa/integration-20260925-133640-c3ad7009`：两项同名新增和一项内容冲突，来源 cs77 合并签入 cs82，独立消费者内容和原生合并关系通过；该目录的 `directory-cli-results.json` 另验证 cs87 的双方移动、修改/删除和删除/修改三种 CLI 选择流程。
- `qa/integration-20260925-135119-0764c97f`：真正的同名目录及嵌套后代冲突，来源 cs103、目标 cs104，重命名保留两棵目录并签入 cs105；消费者的双方后代字节及原生合并关系通过。
- Partial 集成覆盖精确基础/本地/传入三方文件、旧结果拒绝、显式重新准备、继续编辑与提交、原生 ParentRevId、选择器和加载规则不变。故意让原生 undo 成功后的 update 失败，验证保留备份、阻止提交、重启显示恢复目录及单文件撤销恢复。
- `qa/integration-20260925-135023-f5c99b3a/partial-cli-results.json`：实际主 EXE 9 项真实 CLI 断言通过，包括 JSON 三方文件、跨进程恢复、显式应用后保持待定、显式签入及消费者字节；缺少 --yes 和未解决签入有失败检查。
- 新窗口正常/最小尺寸均完成渲染和视觉检查：`qa/Release/directory-conflict*.png`、`partial-conflicts*.png`。GUI 强制先准备再应用；新传入不会被旧已解决状态遮盖，应用动作不会静默重新准备并套用旧审核结果。
- 本阶段结束时仍不支持 Partial 传入结构冲突、Xlink，以及未列出的原生目录冲突类型；无冲突路径中的现有目录自动移动/删除仍有保护性限制。后续扩展见第六阶段。正常流程不启动官方 GUI，仍依赖已安装的 cm.exe。

# 第六阶段：扩展结构冲突与失败恢复（2026-09-25）

- 最终 Release 构建与本地回归通过，日志 `qa/structure-release-validation.log`：CLI 920、目录及备份存储保护 58、原有合并保护 40，以及全部后端、Shell、GUI 检查。最终 EXE 的 Partial 内容 CLI 另有 9 项真实测试通过（`qa/structure-release-content-cli.log`，清单由 `latest-release-partial-cli.txt` 指向）。打包、隔离安装和卸载检查 25 项通过，日志 `qa/structure-package-validation.log`。
- 最终源码的结构后端 77 项真实测试通过（`qa/integration-20260925-151824-0b19dd6d/partial-structure-results.json`）；最终主 EXE 的结构 CLI 33 项通过（`qa/integration-20260925-152438-405589f2/partial-structure-cli-results.json`）。主 EXE 在最后一次源码修改后重新构建，构建时间及 SHA-256 另记于 `qa/structure-release-record.json`。
- 完整集成脚本退出码为 0，日志 `qa/structure-final-validation.log`；常规真实 CLI 70、分支合并 25、Partial 内容 35 及内容 CLI 9 项均通过。最终目录矩阵 `qa/integration-20260925-152622-2d542d2a/directory-matrix-results.json` 验证来源选择 23、目标选择 23、无冲突目录处理 13 项，并由独立消费者核验整棵树及原生合并链接。
- Standard 新增 MV_RM、RM_MV、MV_EVIL、ADD_MV、MV_ADD 的来源/目标选择；无冲突目录移动、删除在检查私有/忽略后代、版本、选择器和完整工作树指纹后执行。真实矩阵验证双方选择以及独立消费者的整棵目录和原生合并关系，早期证据为 `qa/integration-20260925-145509-08256c1d/directory-matrix-results.json`。
- Partial 新增文件结构会话：同名新增、传入删除/本地修改、本地删除/传入修改、本地同目录重命名/传入修改。11 种选择均执行真实原生命令，验证准确字节、待定状态、选择器/加载配置和未选中文件不变；同名新增尚未加载时只配置明确选中的文件。准备和应用分离，不自动提交。
- 广矩阵早期完整运行 77 项通过：`qa/integration-20260925-150719-3d909a65/partial-structure-results.json`。包括跨 settings 的工作区会话保护、取消准备、非法重命名、故意让 undo 后 update 失败、恢复时另存新编辑、7 次显式签入及独立消费者验收。`qa/integration-20260925-150208-bcfef1b7/partial-move-content-results.json` 另验证本地重命名同时编辑时保留本地字节。
- 新 GUI 使用原生列表、详情和标准按钮；正常/最小尺寸渲染已查看：`qa/Release/partial-structure.png`、`partial-structure-minimum.png`、`partial-structure-choice.png`、`partial-structure-choice-minimum.png`。检查未备份不能应用、未默认选择处理方式、必填改名、失败会话只能恢复，以及不同冲突的文案与实际处理一致。
- 收尾检查包括文件路径变目录时拒绝递归更新、加载前后身份校验、重命名目标占用检查；新会话禁止将 settings/恢复资料放入工作区，已有会话仍可读取和恢复。
- 剩余边界：Partial 服务器移动、目录结构冲突、跨目录本地移动、路径被另一身份占用、Xlink/符号链接暂不处理。文件结构窗口明确说明不扫描目录级冲突。尚未完成真人等效的 Explorer 点击、机器级图标以及多 DPI 验收。

# 第七阶段：Partial 跨目录移动与服务器文件移动（2026-09-25）

- 新增本地跨目录移动与传入内容修改的处理：接受传入、保留本地位置、指定其他已加载受控目录；纯移动保留传入内容，移动同时编辑可明确保留本地内容。裸文件名相对于本地移动后的目录，完整仓库路径支持另选目录。父目录及祖先的身份、加载状态、待定变化和链接均有检查。
- 新增服务器同目录/跨目录移动与本地内容修改的处理：精确撤销旧文件、卸载旧路径、加载新路径，必要时固定文件版本，保留 ItemId。只提供采用传入或在新位置保留本地内容，不自动提交。准备与恢复包含旧、新两个路径；失败后新增的本地字节先另存，再恢复为固定传入版本。
- 原生实验比较了双文件 update、直接加载新路径、同向本地 move、父目录 update 和先卸载再加载。前三种存在拒绝、重复身份或残留移动；父目录更新会改变其他已加载文件。精确卸载/加载的同目录和跨目录实验通过：`qa/integration-20260925-170458-c70dd90f/incoming-move-native-probe.json`。仅加载文件形态及空父目录保留另见 `qa/integration-20260925-171659-a4f0d3a7/`；实验脚本为 `test/TortoiseSCM/Probe-PartialIncomingMove.ps1`。
- 发现原生精确文件更新也会处理其他传入目录删除，故内容及结构冲突执行前同时核对当前分支头和实际固定版本的已加载目录。无关私有目录不会被误判；目录移动、删除、替换或链接会在修改前指出并拒绝。新增目录的精确文件更新实验见 `qa/integration-20260925-165759-d24c059a/exact-file-new-directory-proof.json`。
- 审查修正包括：撤销前重验完整文件范围，撤销后只接受基础/传入版本的合法字节；更新、删除、替换前再次校验；已加载的新路径出现编辑时不以再次更新覆盖。较新服务器版本需要回到固定版本时，先核对该较新版本的原始字节。移动同时编辑的 MV/CH 两条状态由结构预检统一按原身份处理，避免在未提交新路径上错误查询历史。
- 最终本地构建及回归日志为 `qa/move-final-release-validation.log`，包括 CLI 920、目录及存储保护 58，以及原有后端、Shell 和 GUI 测试。内容冲突 35 项真实回归通过（`qa/move-final-content.log`）。安装包 25 项通过（`qa/move-package-validation.log`）。
- 正常及最小尺寸的 `qa/Release/partial-cross-directory-choice*.png`、`partial-incoming-move-choice*.png` 已生成并查看；检查未明确选择不能应用、仓库路径原样传递、相对目录说明、最小尺寸文本与按钮可见，以及传入移动的保留本地语义。
- 本阶段仍不处理 Partial 目录级冲突、服务器移动叠加本地移动/删除、仅大小写变化的传入改名、不同身份替换、Xlink/符号链接。目录级操作需要单独展示递归影响范围和对应恢复方案，当前不会隐式扩大到父目录。
- 结构后端 77 项（`qa/integration-20260925-172057-c6d8904b/partial-structure-results.json`）、跨目录移动 50 项（`qa/integration-20260925-172218-2fe5af2d/partial-cross-directory-results.json`）及公开 CLI 55 项（`qa/integration-20260925-172258-a225f598/partial-structure-cli-results.json`）通过，包含显式提交与独立工作区内容验收。
- 收尾审查另发现：原文件版本未变时，本地移动目标仍可能被服务器新增的另一身份占用。结构预检现在先判断该碰撞，再决定是否跳过无传入修改的移动；无法验证版本或身份的移动也会明确报告为不支持，防止 MV/CH 去重漏掉提交保护。
- 服务器移动 54 项真实断言通过（`qa/integration-20260925-172257-47313b71/partial-incoming-move-results.json`）：同目录及跨目录双方选择、未加载兄弟保持未加载、加载前后故障恢复、服务器版本推进后的固定版本恢复、加载后出现编辑时拒绝覆盖，以及显式提交和独立工作区验收。
- 目标碰撞修复后再次完整构建并通过本地 `-Test -Workspace`，最终程序时间及 SHA-256 记录在 `qa/move-release-record.json`。前述 77/50/54/55 项矩阵在此额外保护之前通过；该保护使用专门的真实碰撞回归复核，避免把早期矩阵误记为最终二进制测试。
- 最终源码的目标碰撞回归 12 项通过（`qa/integration-20260925-173257-9bf1bd95/partial-move-collision-results.xml`）：原文件版本不变的碰撞仍显示在两种预检中，准备和提交拒绝且不改变本地字节、选择器及服务器内容；无碰撞的移动并编辑可正常提交，独立工作区收到正确内容。该测试已加入 `build-tortoisescm.ps1 -Integration`。
- 最终主 EXE 另通过 5 项公开 CLI 碰撞验证（同目录 `partial-move-collision-cli-results.json`）：两种预检显示冲突，准备和提交退出码为 2，全部本地字节和 selector 保持不变。

# 第八阶段：Partial 目录变化的完整范围审核（2026-09-25）

- 新增独立目录预检/准备/应用/取消/恢复流程及 GUI/CLI 入口。支持显式完整加载子树和默认全量加载工作区的服务器移动，以及采用服务器目录删除；移动时可在新位置保留本地已修改文件内容，未修改文件使用传入内容。影响清单包括目录和全部后代，准备整树备份后才允许明确选择。
- 原生证据：`qa/integration-20260925-174102-418f12fd/partial-directory-native-probe.json` 验证完整子树移动保留所有身份及范围外加载状态；`qa/integration-20260925-174138-0c15f9ac/` 证明目录加载会扩大部分加载范围，且卸载保留私有项；`qa/integration-20260925-174327-5f8d4fb1/` 证明配置选中目录时也可能改变其他已删除目录的加载规则，因此这些情况须预先拒绝。可复现实验脚本为 `test/TortoiseSCM/Probe-PartialDirectoryIncoming.ps1`。
- 首批拒绝部分加载子树、私有/忽略项、嵌套工作区、链接、本地结构变更、目录身份替换、后代结构变化，以及同时存在其他已加载目录的服务器结构变化。全量加载的移动/删除证据分别为 `qa/integration-20260925-174521-1c00cfa8/`、`qa/integration-20260925-174626-069831ae/`；实现对全量标记、目录卸载后的临时规则及完成后的原模式分别建模。逐已加载文件重建研究见 `qa/integration-20260925-174637-11d0f796/`，本阶段尚未开放这种部分加载范围。
- 写操作共用结构互斥锁，目录会话使用独立工作区标记，避免与 Standard 目录合并标记混淆。新增保护覆盖普通添加/签出/签入/撤销/更新、文件移动/删除/忽略、历史恢复/切换，以及输出到该工作区的历史导出和外部合并工具。更换 settings 不会绕过会话标记。
- 纯目录移动不会必然改变子文件的内容版本号。已修正普通内容/文件结构预检使用本地受控身份与当前分支树核对，备份通过历史树的 ItemId 查找当时路径，避免新路径在旧内容版本中不存在导致后续提交或内容冲突准备失败。目录后代分次提交的版本实验见 `qa/integration-20260925-175229-2c0624ab/partial-directory-revision-probe.json`。
- 全量模式的原生加载规则使用不透明 GUID 命名空间，并非仓库 GUID。准备时记录受控目录 ItemId 集合；卸载后的中间态要求合法单一命名空间、无重复或未知 ID、范围外目录成员不变，并核对本地/服务器身份及范围外字节后才保存该命名空间。完成后必须恢复原 fullupdate 标记及空显式规则。
- 补齐连续纯目录移动的历史路径处理：目录本身也可能保留旧版本号，预检按历史根 ItemId 映射相对层级，而非假设当前路径在旧版本中存在。后端全量模式连续移动 14 项通过（`qa/integration-20260925-182143-3024050d/partial-directory-repeated-full-results.json`）。物理缺失 LD 使用仍保留的本地受控身份。DE 的最初历史父路径方案在后续安全审查中被替换，见下方身份歧义验证。
- 目录公开 CLI 的显式/全量模式各 49 项通过（`qa/integration-20260925-181351-2efead46/`、`qa/integration-20260925-181358-5d351c5d/`），涵盖 prepare/status/cancel、两种移动选择、删除、提交/独立消费者，以及移动后的再次内容冲突。两组使用历史父路径最后修正之前的候选主 EXE，其 SHA-256 单独记于 `qa/partial-directory-main-matrix-record.json`；目录历史路径修正由后续 EXE 的连续纯移动聚焦用例复核，DE 身份安全修正另见下方记录。
- 全量模式 148 项真实后端检查通过（`qa/integration-20260925-180623-46d8652b/partial-directory-full-results.json`），涵盖无关结构/配置变化拒绝、卸载前后/加载后的故障恢复、加载规则命名空间篡改拒绝、已知路径新字节另存和未知项保护。原文件结构 77 项通过（`qa/integration-20260925-181248-ae21688c/partial-structure-results.json`）；内容冲突 35 项通过（`qa/partial-directory-content-regression.log`）。这些矩阵使用最后历史路径修正之前的 Core；后续专项测试单独记录。
- 显式加载模式完整 199 项真实后端检查通过（`qa/integration-20260925-180853-4e688f69/partial-directory-results.json`），包含 15 类写入口阻断、不同 settings、完整/不完整子树边界、配置/服务器版本/新编辑变化及三个故障边界。目录历史路径修正后的 EXE 连续纯移动公开 CLI 在显式/全量模式各 14 项通过（`qa/integration-20260925-182106-3ef30e8b/`、`qa/integration-20260925-182106-c763d91d/`）。
- 末次身份审查真实复现了同版本 A/B 交换文件名再删除的误认（`qa/integration-20260925-182632-84cd5e6c/deleted-swap-identity-probe.json`）。最终 DE 实现读取原生已加载版本与内容 Hash，在该版本完整历史树中只接受唯一的同仓库常规文件身份；多项同版本同内容时明确拒绝。原反例使用新 Core 复测已拒绝（同目录 `deleted-swap-fixed-preview.log`），没有执行错误的恢复或覆盖。纯目录移动后的 LD 仍使用本地 ItemId。
- 完整本地 `-Test -Workspace` 通过（`qa/partial-directory-shipping-validation.log`），包括后端、980 项 CLI、Shell 和 GUI。其后仅将无法确定身份的 DE 转为该路径的“不支持”记录，使无关范围不被阻断；主 EXE 已重新构建，并再次通过 980 项 CLI（`qa/partial-directory-cli-final.log`）和 25 项安装包测试（`qa/partial-directory-package-final.log`）。最终程序 SHA-256 和源码/构建时间见 `qa/partial-directory-release-record.json`。
- 新窗口正常和最小尺寸均已生成并目视检查：`qa/Release/partial-directory.png`、`partial-directory-minimum.png`。沿用原生列表、上下窗格、标准按钮和明确选择；整树备份路径、完整影响范围及中断恢复入口可见。尚未增加真人等效的 Explorer 点击、多显示器 DPI 或机器级图标安装验证。
- DE/LD 专项覆盖纯目录移动后的接受/保留删除/中断恢复、单文件独立更新和根目录删除，共八种正常场景，公开 EXE 与独立消费者结果均通过（`qa/deleted-identity-shipping.log`，fixture `qa/integration-20260925-182938-5bed2083/`）。该候选 EXE 的记录单独保存在 `qa/partial-directory-deleted-matrix-record.json`。旧测试末尾直接比较 XML 的失败仅由 PrintableLastModified 的“4分钟前→5分钟前”引起，原始失败证据保留；测试已改用稳定的短机器格式比较。
- 最后范围修正后的主 EXE 14 项真实专项通过（同 fixture 的 `partial-deleted-identity-ambiguity-results.json`、`qa/deleted-identity-scope.log`）：歧义 DE 在 GUI/CLI 预检中作为单条不支持记录显示；准备及该项签入拒绝且无会话/无字节修改；无关 sentinel 使用唯一文件路径提交成功，消费者核对服务器内容且本地 DE 仍保留。原 TestSCM 保持无待定变更，selector 仍为 `/main`；全部服务器测试均位于独立测试分支。

# 第九阶段：保留服务器已删除的本地目录树（2026-09-25）

- `incoming-directory-delete` 新增 `keep-local`：先接受旧身份的删除，再从整树备份重建全部原路径文件和空目录，逐项加入为待提交新身份；不自动签入。GUI 在选择后逐项显示删除或新添加的结果，明确旧身份/历史不恢复。CLI 保留既有命令，并在 `resolutionDetails` 中说明语义及 `readdsAsNewItems`。
- 原生探针验证了显式和 fullupdate 模式下逐项 add/undo 的实际行为：`qa/integration-20260925-195954-ba25eb3f/`、`qa/integration-20260925-200134-b45e99ec/`。`partial undo <path> --added` 保留私有字节与空目录；生产恢复先备份，再撤销本次添加，最后仅移除已审核路径，范围外加载规则和字节保持不变。恢复遇到未知后代、已发布正身份或替换的临时身份会拒绝。
- 重建阶段及每个 add 意图、原生负 ItemId 持久保存；add 前后中断都能进入恢复。恢复目标仍为服务器删除状态，不能误解为继续保留或自动提交。每次原生命令前重核本地范围、加载配置和服务器旧路径仍为空；服务器重建同名目录时拒绝覆盖。
- 同名目录重现的原生实验发现：仅依赖 `cm partial checkin` 会将本地添加并入服务器新目录。已有整目录冲突保护仍可被“只提交一个子文件”绕过（`qa/integration-20260925-200323-4a1ffc0f/public-directory-reappearance-checkin.json`）；已扩展检查，待添加父目录存在冲突时，其选中后代也必须拒绝，其他范围仍可提交。
- 最终主 EXE 的本地 `-Test -Workspace` 通过（`qa/keep-deleted-release-validation.log`）：980 项 CLI、后端、Shell 和 GUI。安装包/隔离安装/卸载 25 项通过（`qa/keep-deleted-package-validation.log`）。正常及最小尺寸的 `qa/Release/partial-directory-keep-deleted*.png` 已生成并目视检查：结果路径、身份说明和全部操作均可见。
- 目录碰撞保护使用最终主 EXE 与 Core 的真实回归 23 项通过（`qa/integration-20260925-200943-a5d4d840/directory-readd-collision-results.json`）：整目录、子文件、子目录、空目录四种提交选择均拒绝，字节/目录/待定状态/加载规则/服务器 HEAD 不变；无关文件精确提交成功，独立消费者未收到冲突目录中的本地项。
- 原有连续纯目录移动回归 14 项通过（`qa/integration-20260925-200719-60b05566/partial-directory-repeated-full-results.json`），覆盖新会话字段读写后的 take/keep、提交与消费者。早期候选 EXE 的 keep-deleted 公共 CLI 在显式/全量模式各 15 项通过（`qa/integration-20260925-200455-604c1190/`、`qa/integration-20260925-200455-debb0151/`）；最终 EXE 全流程证据另见下方。
- 新保留删除目录的后端矩阵显式/全量模式各 83 项通过（`qa/integration-20260925-200043-ae549e9e/partial-directory-keep-deleted-results.json`、`qa/integration-20260925-200049-f279dea7/partial-directory-keep-deleted-full-results.json`）：正常重建/空目录/新身份签入、添加前/部分添加/全部添加后中断、跨客户端恢复、未知私有后代拒绝及已知路径新编辑另存、服务器同名身份重现拒绝、目录外规则与内容保持。末次 AD 身份一致性检查使用最新 Core 的全部添加后故障定向回归，显式/全量各 19 项通过（`qa/integration-20260925-200952-676dc1af/`、`qa/integration-20260925-201012-4dc31efc/`），避免将较早矩阵误称为最终源码验证。
- 旧会话兼容实测 21 项通过（`qa/keep-deleted-legacy-schema.txt`）：原存档缺少新增重建字段时按未重建状态加载，档案字节不变；临时读取标记已清理。
- 最终主 EXE 的完整公开目录 CLI 在显式/全量模式各 78 项通过（`qa/integration-20260925-200955-7481ae51/partial-directory-cli-results.json`、`qa/integration-20260925-200955-9e215329/partial-directory-cli-results.json`），覆盖移动双选择、删除双选择、跨进程会话/取消、空目录与新身份、再次内容冲突、连续两次纯移动，以及真实签入和独立消费者；日志为 `qa/keep-deleted-final-cli-*.log`。最终程序 SHA-256 与构建/源码时间见 `qa/keep-deleted-release-record.json`。原 TestSCM 仍干净且保持 `/main`。

# 内置文本比较与三方手工编辑器首版（2026-09-28）

- 最终 `build-tortoisescm.ps1 -Test -Workspace D:\Work\Juscent\SCM_Study\TestSCM` 通过，完整日志 `qa/builtin-final-release-validation.log`，包括 2,076 项 CLI 断言、全部后端、Shell/现代菜单及 GUI 回归。
- 新增可选内置左右 Diff、三方只读贡献文件及独立结果编辑、同步滚动、差异导航、长行完整内容查看。保留原外部工具配置；CLI 明确启动外部工具的语义保持不变。合并保存不改变 Plastic 冲突状态，确认应用及签入仍是独立动作。
- 文本引擎 120 项、内置路由 58 项、外部工具 18 项及历史文件 47 项检查通过。文本检查覆盖 UTF-8/UTF-16/UTF-32、BOM、CRLF/LF/CR、混合行尾、空文件、二进制/无效编码、大小/行数上限、有界粗略对齐、取消、外部修改、只读、硬链接、junction 和长目录保存。最终磁盘校验到原子替换之间仍存在文件系统未提供 compare-and-swap 的竞争窗口，不声称能阻止所有跨进程极短竞态。
- 新编辑器 61 项进程内 UI 检查通过，包括后台线程请求实际模态窗口、窗口关闭前任务保持未完成、保存/不保存结果区分、独立插入行对齐、首次上一差异，以及万字长行尾部查看。普通/最小渲染已查看：`qa/editors/text-diff*.png`、`text-merge*.png`、`text-line-inspector-minimum.png`；设置截图为 `qa/Release/settings-builtin-diff.png`、`settings-merge-minimum.png`。这些不是实际 Explorer 点击或多 DPI 验收。
- 最终 Core 的真实服务器集成 37 项通过：`qa/integration-20260928-113850-1c5d8779/builtin-tools-results.json`，日志 `qa/builtin-final-live-validation.log`。覆盖工作文件/重命名/历史比较、Standard 贡献文件、结果保存后未解决、重新打开会话、明确应用、签入及独立消费者；Partial 覆盖加载基线、传入冲突、独立编辑结果、应用、selector/加载规则不变以及独立消费者精确字节。
- 两轮早期实测分别发现深层会话目录临时文件名超长，以及转换后的 Partial 工作区仍保留 `Standard` 元数据而被历史读取误拒绝。已缩短原子保存临时名；历史读取保留仓库、工作区根/名称和 selector 验证，移除对不可靠模式提示的比较，写操作继续使用原生状态和加载规则保护。早期失败证据保留，未计入最终通过结果。
- 安装包回归 44 项通过，日志 `qa/builtin-package-validation.log`；原 TestSCM 保持无待定更改和 `/main` selector。所有服务器写入位于独立 `tortoisescm-autotest-*` 分支。
- 旧 Partial 内容冲突的真实回归 35 项通过，日志 `qa/builtin-partial-regression.log`，fixture 由 `qa/latest-builtin-partial-regression.txt` 指向；包括传入推进后重新准备、继续编辑、原生父修订、undo 后 update 故障恢复、未选中文件保护和同名添加拒绝。程序哈希与构建时间记录于 `qa/builtin-release-record.json`，发布包为 `packages/TortoiseSCM-0.1.0-dev-builtin-editor-20260928-final-windows-x64.zip`。
- 当前边界：内置工具只处理支持的 Unicode 文本，最多 2 MiB / 20,000 行；尚无自动三方合并、按块采用或统一冲突列表编辑窗格。复杂行差异明确提示粗略对齐，新增/删除工作文件比较及更多编码仍在路线图中。

# 三方自动合并草稿与逐块核查（2026-09-28）

- 新增显式自动合并草稿、合并块列表、基线/本地/远程逐块采用、手工标记核查和源/结果定位。非重叠修改自动组合；相同修改保留一份；冲突块默认保留本地候选并保持待处理。相邻替换可独立合并，插入触及替换边界时保守地合为一个待审核范围。近似行对齐或仅格式变化使用整文件核查回退，不假定可自动消除冲突。
- 算法套件 6,542 项通过，包括 250 组随机重复行/空行输入、选择全部基线/本地/远程时完整重建原文、源坐标与结果范围、长度变化、空文件、末尾换行、删除/修改、相同与不同插入、相邻变化、编码/BOM/行尾差异、大范围近似回退和取消。
- 新增逐块 GUI 套件 56 项及原有编辑器检查通过，日志 `qa/block-merge-ui.log`。操作真实 WinForms 按钮验证自动组合、冲突/自动/已核查状态、待审核草稿保存、逐块采用、手工编辑失效、重新生成保护、重启结果恢复、长度变化后的选区、格式专用回退和未保存提示。已目视检查 `qa/block-merge-ui/text-merge-plan.png`、`text-merge-plan-minimum.png`、`text-merge-plan-manual.png`，普通/最小窗口均无按钮遮挡；这些为进程内渲染，未声称实际 Explorer 或多 DPI 验收。
- 当前核查状态仅在编辑窗口存活期间有效；自由编辑全文或替换整份贡献内容会立即停用旧块映射，重新生成须明确确认。重新打开已有结果保留字节，但不推断之前的核查状态。保存待处理冲突时明确标为草稿；自动采用、已核查以及 Plastic 原生解决状态分别显示。任何编辑器动作都不会自动应用或签入。
- 最终 Core 的真实服务器集成 45 项通过，日志 `qa/block-merge-live.log`，fixture 由 `qa/latest-block-merge.txt` 指向。Standard 和 Partial 分别包含双方独立修改及同一范围冲突：验证自动组合、显式采用远程冲突块、保存仍未解决、确认应用、提交与独立消费者精确字节，并保留工作区 selector/加载规则。
- 完整 `build-tortoisescm.ps1 -Test -Workspace` 回归通过（`qa/block-merge-release.log`），包括 2,076 项 CLI、后端、Shell/现代菜单及整套 GUI 检查。该轮主 EXE 构建后补充了界面提示及格式专用块导航修正；最终 GUI 源码已由逐块套件及完整 UI 编译检查覆盖，并重新构建发布程序（`qa/block-merge-final-build.log`）。安装包、隔离安装及卸载 44 项检查通过（`qa/block-merge-package.log`）。
- 最终程序 SHA-256、构建时间与源码提交记录于 `qa/block-merge-release-record.json`；发布包为 `packages/TortoiseSCM-0.1.0-dev-block-merge-20260928-windows-x64.zip`。原 TestSCM 仍无待定更改且保持 `/main`，服务器写入均位于独立测试分支。
- 该阶段仍不持久化跨会话核查状态，不支持自由编辑后自动重建安全块映射，也未完成实际 Explorer 点击、多 DPI 或整套上游 TortoiseMerge 功能对齐。

# Beyond Compare 统一工具配置与会话（2026-09-28）

- 产品方向改为统一 BC，停止内置编辑器扩展；路线图审视已单独提交 `b623dfd7d`。新旧应用配置均使用固定 BC profile，旧字段留存但不参与产品路由。显式旧 BC 路径迁移、共用新路径、自动发现、BCompare→BComp 归一化、缺失配置和固定参数 34 项通过（`qa/beyond-compare-profile-final.log`）。CLI 使用 `--beyond-compare`；旧工具/参数选项明确提示迁移。
- 新进程契约 22 项通过（完整回归日志内）：真实辅助进程验证三方顺序、中文/空格路径、只读参数、工作文件改名按身份读取、固定历史端点、源文件不变、正常退出后清理、超短 SCM 超时不影响编辑、取消等待工具关闭、非零退出及 102 等待失败保留贡献目录。辅助进程不是 BC 产品，不能替代 BC 桌面验收。
- 设置窗口共用路径、固定角色、Pro 提示、程序缺失状态及普通/最小尺寸 52 项通过（`qa/beyond-compare-settings-ui.log`）。`qa/beyond-compare-settings/settings-diff*.png`、`settings-merge*.png` 已生成并目视检查，无按钮遮挡；这些仍属于进程内 WinForms 检查。
- 完整 `build-tortoisescm.ps1 -Test -Workspace` 通过（`qa/beyond-compare-regression.log`）：2,082 项 CLI、后端、Shell/现代菜单和整套 GUI。完整回归后仅补充旧 BC 显式路径迁移，并通过最终 34 项配置检查及重新构建（`qa/beyond-compare-final-build.log`）；工具进程和 UI 实现未在此后改动。
- 最终 Core 的真实 Plastic + BC 辅助进程集成 35 项通过（`qa/beyond-compare-live-final.log`），fixture 为 `qa/integration-20260928-124354-206bc33b/manifest.json`，结果为相邻 `bc-contract-results.json`。覆盖受控改名、Partial 加载基线、历史固定端点、Standard/Partial 三方字节、保存仍未解决、明确应用及签入、独立消费者精确内容、selector/加载规则。首轮辅助测试 XML 换行归一化使 CRLF 期望值不匹配，已改用 Base64 传输精确字节；失败 fixture 保留且不计入通过。
- 实际已安装 BC 4 经发布 EXE 启动三方窗口，命令行角色和独立实例参数正确；配置 SCM 超时 1 秒，进程仍等待 72 秒且未自动生成结果。证据 `qa/bc-desktop-20260928-124541/launch-evidence.json`。Computer Use 原生管道缺失，按重试/重置流程仍不可用，故未确认窗口内只读表现、保存/放弃、已有实例并行或 DPI；验证后只终止了命令行确认为本次隔离 fixture 的测试进程，不计为正常关闭验收。
- 安装包/隔离安装/卸载 44 项通过（`qa/beyond-compare-package.log`）。源码提交、最终 EXE 和 ZIP 哈希记录于 `qa/beyond-compare-release-record.json`；发布包 `packages/TortoiseSCM-0.1.0-dev-beyond-compare-20260928-windows-x64.zip`。原 TestSCM 保持干净及 `/main`，服务器写入均位于隔离测试分支。
- 后续边界：历史主“比较”及暂存集文本预览尚待改为 BC 入口，新增/删除空侧比较尚未实现；BC 5、BC 4 保存/放弃与多 DPI 仍需实机验收。等待异常 102 时不能推断子窗口状态，贡献文件保留并要求用户先关闭对应窗口；不根据工具退出码认定 Plastic 已解决。

# Beyond Compare 历史与暂存集逐文件比较（2026-09-28）

- 历史主按钮与暂存集文件按钮、双击、右键统一使用 BC；旧统一文本预览仍供 CLI 和内部回归使用。固定快照与暂存集新增/删除项在重新读取原生状态后建立空侧；下载错误不作为不存在。移动兼修改使用原生旧路径，标题显示实际 cs/sh 与路径。
- 新历史后端 79 项、暂存集后端 65 项进程契约检查通过：精确文本/二进制字节、中文/空格/符号路径、只读属性、移动与修改双记录、伪造/重复记录、工作区或暂存集身份变化、下载失败、异常退出，以及当前会话目录的清理/保留。历史取消检查确认 BC 未结束前保留输入；退出 102 不推断子窗口已经关闭。
- 真实 Plastic 集成 84 项通过，fixture 为 `qa/integration-20260928-145001-83636750/manifest.json`，证据为相邻 `bc-browsing-results.json`。Standard/Partial 均覆盖暂存集与历史 A/C/D/M+C、重命名前后端点、精确贡献字节、只读参数、标题和等待后清理。所有浏览前后工作文件、待定状态、selector 不变，原 TestSCM 内容哈希/selector/状态也保持不变。结构暂存集由原生命令创建，产品“保存暂存集”仍仅支持已明确支持的内容更改。
- 首轮集成 fixture 的原生 checkin 未指定 `--all`，导致测试准备只提交移动、未提交内容；已修正 fixture 并在新的隔离分支完整通过。早期失败证据保留，不计入通过。BC 使用独立辅助进程验证实际接收的参数和字节，本轮不声称验收 BC 桌面保存/放弃或多 DPI。
- 新历史 GUI 49 项通过，实际点击按钮验证普通/固定比较路由、增删空侧说明、导出范围、参数快照、异步锁定、失败恢复与关闭保护。暂存集 GUI 另覆盖文件选择、真实按钮路由、取消后等待、重复启动/关闭拦截和 selector 变化拒绝。
- 最终完整 `build-tortoisescm.ps1 -Test -Workspace` 通过（`qa/bc-browsing-regression.log`），包括新增后端/GUI、2,082 项 CLI、原有后端、Shell/现代菜单和全部窗口回归。已目视检查 `qa/Release/shelves.png`、`shelves-minimum.png`、`history-beyond-compare.png` 与新增项最小窗口截图；控件均可见，无重叠。这些是进程内 WinForms 渲染，未声称真实 Explorer 或多 DPI 验收。
- 安装/升级回滚/卸载检查 44 项通过（`qa/bc-browsing-package.log`），未改变真实 Explorer 注册。下一阶段继续提交窗口的失败保留与重试预检；工作文件增删空侧、工具并行会话、BC 实际保存/放弃及多 DPI 仍列为未完成。

# 提交预览、执行前复核与失败保留（2026-09-28）

- 新增提交预览后端 25 项和 GUI 47 项通过。预览复制并固定仓库、工作区名称、selector、Standard/Partial 模式、请求路径、实际递归文件、锁提示、私有/忽略排除数量及文件状态/内容指纹；UI 只读显示完整说明和实际范围，普通/最小窗口截图为 `qa/checkin-review*.png` 与 `qa/checkin-main-success.png`。
- 原生执行前在现有 Standard 合并和 Partial 冲突 gate 内重新读取工作区、状态与内容，检查移动旧路径、目录新增后代、selector/名称/模式变化；回调发生在最终原生命令前，不绕过已有冲突会话保护。客户端文件哈希是紧邻执行前的复核，不宣称跨进程原子冻结。
- 真实 Plastic 集成 37 项通过，最终 fixture 为 `qa/integration-20260928-151219-b6c567ee/manifest.json`，证据为相邻 `checkin-preflight-results.json`。Standard/Partial 覆盖预览后内容变化、状态变化、目录新增受控子项、显式文件范围、目录范围和排除私有项；拒绝时服务器分支头、工作文件、状态和 selector 均不变，原 TestSCM 也保持不变。
- GUI 失败、超时、非零退出、抛出异常和刷新失败均保留说明/勾选/列表顺序及操作记录，不自动重试；成功刷新解锁预览但不跳过不确定结果确认，只有明确成功才清空说明。关闭挂起提交会被拦截；取消预览不发送签入。完整回归日志为 `qa/checkin-regression-final2.log`，包含原有 2,082 项 CLI、Core、Shell 和窗口检查；新增 UI 测试最终 47 项通过。
- 本阶段不把锁提示当作权限结论，也不保证与其他客户端并发修改之间的服务器原子事务。跨重启失败草稿恢复已按用户要求排除；后续保留进度/取消和更细的服务端结果核对。

# 工作文件 Beyond Compare 空侧（2026-09-28）

- `PlasticTools` 现按 Plastic 原生 `AD`、`DE`、`LD`、修改和移动状态准备工作文件比较。新增使用只读空基线；受控删除和本地删除使用只读空工作侧；普通修改/重命名继续按 ItemId 下载并在启动前复核身份。下载失败、状态不一致、重建删除路径、私有/忽略/目录/链接和删除基线身份不唯一时拒绝启动，不把错误伪装为空文件。
- 聚焦回归 `WorkingBeyondCompareTests` 184 项通过；既有 `BuiltInToolTests` 58 项、`BeyondCompareProcessTests` 22 项通过。覆盖取消、非零退出、不确定工具生命周期、selector/状态/ItemId/内容与文件存在性变化。
- 隔离 Plastic 实测 `WorkingBeyondCompareIntegrationTests` 78 项通过（Standard 与 Partial），覆盖修改、添加、二进制添加、受控删除、本地删除、重命名后修改、中文路径、重复历史基线和删除路径重新出现；原 `TestSCM` selector、文件及待定状态保持不变。证据目录：`qa/integration-20260928-160035-89e17358/`，结果为 `bc-working-results.json`。
- 主窗口路由测试在 Standard 与 Partial 各 80 项通过，使用延迟工具宿主验证忙碌/关闭拦截、临时输入生命周期、空侧只读属性和普通/最小尺寸截图；这是工具宿主测试，不声称真实 Beyond Compare 窗口内保存/放弃验收。产物位于同一隔离目录下的 `ui-producer/` 和 `ui-partial/`。
- 全量 `build-tortoisescm.ps1 -Test` 通过，包含新增 184 项后端套件和原有 CLI/Core/Shell/WinForms 回归；x64 包构建与安装/卸载检查 45 项通过，包为 `bin/TortoiseSCM/packages/TortoiseSCM-0.1.0-dev-bc-working-20260928-windows-x64.zip`。实际 BC 保存/放弃、已有实例并行行为仍是后续验收项。

# 非当前叶子分支重命名（2026-09-28）

- 新增分支列表/层级树的“重命名…”按钮和右键入口，以及 `rename-branch` CLI。对话框固定分支原生 ID、GUID、父分支和头提交，显示新完整名称并明确确认。CLI 要求提供已审阅的 ID、GUID、头提交与 `--yes`。仅支持名称与父关系一致的非当前叶子分支；根分支、当前 selector/checkout 引用、有子分支、名称冲突和身份不完整时拒绝操作。
- `BranchRenameTests` 141 项、既有 `BranchTests` 83 项及 `BranchHierarchyTests` 57 项通过。覆盖执行前身份/头提交/selector/工作区变化、调用方修改快照、两类本地操作锁、Partial/本地待定内容、取消和执行后不确定结果；无自动反向重命名或自动重试。
- `BranchRenameUiTests` 62 项通过，普通/最小尺寸目视检查无控件重叠或裁切，截图为 `qa/branch-rename-ui/branch-rename*.png`。覆盖输入、拒绝确认、写入期间禁止关闭、身份快照、失败后禁止旧窗口重试、过滤后的子分支保护、Partial 列表/层级树入口。
- 真实服务器 CLI 集成 39 项通过：`qa/integration-20260928-162349-60a9dc2b/branch-rename-results.json`。在隔离分支创建实际提交后分别经 Standard 与 Partial 重命名，同一 ID/GUID、父关系和头提交保持不变；旧名消失。当前/根/带子分支及旧身份等拒绝测试通过，两个工作区已有的私有文件、状态及 selector 未改变，原 TestSCM 文件与 selector 亦保持不变。
- 原生 `cm branch rename` 的第二参数为短名称；本机探测不支持 `brid:`/`br:brid:` 作为首参数。实现使用仓库限定名称，在执行前后核对身份，但不承诺原子条件写入或更新其他用户保存的名称引用。分支删除、当前/父分支重命名和 Partial 分支切换仍待后续实现。
- 全量 `build-tortoisescm.ps1 -Test` 通过（`qa/branch-rename-regression.log`），包含 2,187 项 CLI、Core、Shell/现代菜单和完整 WinForms 回归，新增重命名 UI 62 项亦在全量运行中通过。安装/升级回滚/卸载检查 45 项通过（`qa/branch-rename-package-tests.log`），实际 Explorer 注册保持不变。版本包路径：`bin/TortoiseSCM/packages/TortoiseSCM-0.1.0-dev-branch-rename-20260928-windows-x64.zip`。

# 受保护的空叶分支删除（2026-09-28）

- 分支列表/层级树右键新增“删除空分支…”和 `delete-branch` CLI，确认固定原生 ID、GUID、父关系与继承头提交。只允许非当前空叶分支，执行前两次核对完整分支记录、自身 changeset、分支属性与继承头暂存引用；任一引用查询失败或返回异常数据均不写入。暂存保护是保守的：即使暂存集来自其他分支，只要以该继承头为父版本也会阻止删除。
- `BranchDeleteTests` 157 项通过，C# 5 `/warnaserror` 编译通过；覆盖身份/头/父关系/selector/工作区模式竞态、两类本地操作锁、引用查询错误、迟到的提交/属性/暂存引用、取消、失败、不确定结果和可变调用参数快照。继承 cs:0 合法；删除后原名称或 ID/GUID 仍存在、或列表身份字段缺失时不能报告成功。证据 `qa/branch-delete-core/results.log`。
- `BranchDeleteUiTests` 48 项通过，验证默认取消、取消不写入、异步关闭保护、失败后禁止旧窗口重试、身份快照、过滤前完整子分支检查及 Partial 入口。普通/最小窗口、失败状态和层级树截图为 `qa/branch-delete-ui/branch-delete*.png`，已目视检查文字和按钮完整，无重叠；这些是进程内 WinForms 检查。
- 首轮真实 CLI 集成 54 项通过；最终后端另补“写后列表缺失身份时不报告成功”的保护，最终构建在新 fixture 重做服务器集成。执行使用 `cm branch delete br:<name>@<repo>`；实测 `brid:<id>` 和 `br:brid:<id>` 不可用。原生按名称写入存在跨客户端并发窗口，无法保证原子条件删除或检查其他用户保存的 selector 引用。
- 不删除已发布的 changeset、标签、暂存集或其他分支，不自动重建或重试；有历史的分支不提供级联删除。测试只在独立 `tortoisescm-autotest-*` 分支操作，原 TestSCM 保持不变。
- 最终构建的真实服务器 CLI 集成 54 项通过（`qa/branch-delete-integration-final.log`），fixture 为 `qa/integration-20260928-172006-dbd57e69/manifest.json`，结果在相邻 `branch-delete-results.json`。覆盖中文空分支、根/当前/子分支保护、真实分支属性、已发布提交、继承头暂存引用、Standard/Partial 成功删除、保留父提交、旧身份拒绝同名替代分支。两个工作区已有私有文件、状态及 selector 和原 TestSCM 内容均保持不变。
- 全量回归的 Core 套件全部通过（`qa/branch-delete-regression.log`）。首轮新增 CLI 多路径拒绝测试误用了同一路径，CLI 按既有规则去重后进入工作区查询，导致测试期望不匹配；已改成两个不同路径，并从 CLI 阶段继续执行完整 CLI、Shell/现代菜单及 GUI 检查，未重复运行已通过的 Core 套件。
- 后续全量回归通过（`qa/branch-delete-regression-resumed.log`）：2,262 项主 CLI 检查及全部其他 CLI、Shell/现代菜单和 WinForms 检查，新增删除 UI 48 项亦通过。安装/升级回滚/卸载 45 项通过（`qa/branch-delete-package.log`），未改变实际 Explorer 注册。版本包：`bin/TortoiseSCM/packages/TortoiseSCM-0.1.0-dev-branch-delete-20260928-windows-x64.zip`。

# 保留加载配置的 Partial 分支切换（2026-09-28）

- `switch-branch` CLI 与分支窗口“切换工作区…”现支持干净 Partial 工作区。保留单仓库根映射、结构/合并互斥锁、未完成会话和嵌套工作区保护；待定修改、签出、私有或忽略项均拒绝。执行前再次核对分支 ID/GUID/父关系/头提交、工作区身份、原生模式及加载树和配置，执行后核对当前分支、干净状态及加载配置。
- 原生 11.0.16.10330 实测采用工作区根目录作为 CWD，命令为 `cm partial switch br:<name>@<repo> --report`；本机实际拒绝帮助中列出的 `--workspace`。不传 `--configure`、强制选项或会跳过询问的 `--noinput`。工作区 `.plastic/plastic.workspace` 在 Partial 下仍可能写 Standard，后端和 GUI 均以原生 status XML 判断模式。
- `PartialBranchSwitchTests` 133 项及原有 `BranchTests` 83 项通过，C# 5 `/warnaserror` 编译通过（`qa/partial-branch-switch-core/`）。覆盖配置缺失/异常、完整及稀疏规则、目标身份/头提交竞态、selector/模式/工作区/加载树变化、待定与会话保护、超时、执行和验证期间取消、失败不重试或写回旧元数据。切换后允许 selector/wktree 正常改变；加载规则和 fullupdate 的存在性/字节要求保持一致。
- `PartialBranchSwitchUiTests` 63 项通过：真实按钮路线、拒绝确认、挂起期间关闭/重复操作保护、成功/不确定结果刷新、selector 和模式变化，以及 Standard 行为。Partial 测试均使用写着 Standard 的工作区元数据，确认原生模式决定提示和流程。`qa/partial-branch-switch-ui/partial-branch-switch*.png` 普通/最小尺寸截图已目视检查，Partial 提示和按钮完整；这些是进程内窗口验证。
- 首轮真实 CLI 集成 38 项通过（`qa/partial-branch-switch-integration.log`，fixture `qa/integration-20260928-173359-31356963/`）：完整加载目录的新增/修改/删除、切回源分支、排除文件和单文件选择保持稀疏、拒绝修改/签出/私有/忽略文件和未完成会话。Producer 和原 TestSCM 均保持不变。最终模式后检补强后另以新 fixture 验证最终构建。
- 已知边界：原生按名称切换不是原子条件事务；其他客户端仍可能在预检与命令间改变目标/工作区。新增或移动加载目录等结构变化可能改变原生加载规则，程序会报告结果未确认，要求刷新状态并在 Gluon 核对配置，不自动恢复旧元数据或反向切换。结构预览/人工恢复向导和 Partial 跨分支合并仍未完成。
- 最终构建真实 CLI 集成仍为 38 项通过（`qa/partial-branch-switch-integration-final.log`），fixture `qa/integration-20260928-173815-82bee097/manifest.json`，相邻 `partial-branch-switch-results.json` 记录全部命令及断言。安装/升级回滚/卸载 45 项通过（`qa/partial-branch-switch-package.log`），实际 Explorer 注册未改变。
- 原有真实分支流程回归 31 项通过（`qa/partial-switch-branch-regression.log`），保留 Standard 切换、待定/忽略/私有项与原生合并保护；旧 Partial 全面禁用断言改为验证有私有文件时拒绝且保留字节和 selector。
- 全量 `build-tortoisescm.ps1 -Test` 回归通过（`qa/partial-branch-switch-regression.log`），包括 2,262 项主 CLI、Core、其他 CLI、Shell/现代菜单和完整 WinForms 回归，新增 Partial UI 63 项通过。运行期间最终补强了模式后检和界面原生模式提示；最终生产程序已重新构建（`qa/partial-branch-switch-final-build.log`），补强后的后端专项、最终真实集成和后续完整 GUI 均通过。版本包：`bin/TortoiseSCM/packages/TortoiseSCM-0.1.0-dev-partial-branch-switch-20260928-windows-x64.zip`。

# Partial 分支切换目录结构预览（2026-09-28）

- 分支窗口在 Partial 切换前显示只读结构预览；新增 `partial-switch-preview` CLI。按固定目标 head 的真实 ItemId/路径识别已加载目录保留、移动、删除、同路径替换和完整加载范围内新增目录，后四类在原生命令前阻止。普通文件变化和不影响已选项的未加载区域变化继续允许。CLI 读取成功但不可切换时仍返回成功，调用方须检查 `data.canSwitch`。
- GUI 执行使用内部保存的目标身份、头提交、selector、工作区身份、加载树和加载配置证据，修改公开显示模型不能改写执行目标或把被拒绝的预览变成授权；确认后变化须重新预览。直接 `switch-branch` 同样执行结构保护。目录层级缺失、重复身份/路径、异常 XML、加载规则归属异常和链接等情况拒绝，不自动卸载、重配置、恢复元数据或反向切换。
- 交叉审查补齐单文件稀疏加载边界：已选文件移入目标新目录或未加载父目录，即使没有完整目录加载规则也会阻止；目标文件每级父目录须与已加载路径和身份一致。已加载目录之间的普通文件移动继续允许。`PartialBranchSwitchPreviewTests` 最终 205 项及既有切换 133 项通过，C# 5 `/warnaserror` 编译通过，日志为 `qa/partial-switch-preview-core/preview-tests.log`。
- 新预览 GUI 57 项与既有切换 GUI 63 项通过，覆盖默认取消、阻止时无执行入口、等待期间关闭保护、身份/模式变化与 Standard 行为。普通/最小尺寸截图为 `qa/partial-switch-preview-ui/partial-switch-preview*.png`，已目视检查；最小尺寸的长表格列使用水平滚动和工具提示。这是进程内 WinForms 验证。
- 首轮真实测试曾将显式切回分支前的 selector 字节作为后续预览基线，原生切回添加空白导致断言失败；已改为比较每次预览紧邻前后的状态。修正后真实预览集成 61 项通过（`qa/integration-20260928-181132-81bc944e/`）；补齐稀疏文件新父目录保护后另用新 fixture 验证最终构建。
- 原有 Partial 分支切换真实回归 38 项通过（`qa/partial-switch-preview-legacy-integration.log`，fixture `qa/integration-20260928-181132-cc33f189/`），包含文件增删改、切回及稀疏加载。原 TestSCM 与 Standard producer 保持不变。安装/升级回滚/卸载 45 项通过（`qa/partial-switch-preview-package-tests.log`），实际 Explorer 注册未改变。
- 边界：预览不是完整文件差异清单；独立 CLI 预览 JSON 不绑定后续切换，后者会重新检查当前目标。原生按名称执行仍非服务器原子条件事务。当前读取目标完整树，部分匹配为二次复杂度，大仓库性能和长时间预览取消仍需优化，未声称已通过压力验收。受控目录结构处理、人工恢复向导及 Partial 跨分支合并仍未完成。
- 最终构建真实预览集成 66 项通过（`qa/partial-switch-preview-integration-final2.log`，fixture `qa/integration-20260928-181447-a52cc2f5/`，相邻 `partial-switch-preview-results.json`）。覆盖目录移动/删除/替换/新增、未加载区域放行、内容更新、单文件稀疏范围、新父目录移动拦截及完整加载模式；拒绝时文件、selector 和加载规则不变，原 TestSCM 保持不变。
- 全量 `build-tortoisescm.ps1 -Test` 通过（`qa/partial-switch-preview-regression.log`），包含最终 205 项预览核心、2,297 项主 CLI、其他 Core/CLI、Shell/现代菜单及完整 WinForms 回归。运行期间补齐稀疏文件保护，最终程序另行构建（`qa/partial-switch-preview-build-final2.log`），随后核心、CLI、GUI 和最终真实集成均验证了该版本。版本包：`bin/TortoiseSCM/packages/TortoiseSCM-0.1.0-dev-partial-switch-preview-20260928-windows-x64.zip`。

# Partial 目录预览索引与取消（2026-09-28）

- 将逐项遍历目标/已加载树替换为 ItemId、大小写不敏感路径和精确路径索引；解析时验证并关联父目录，范围和最近异常祖先以缓存沿父链计算，不因加载规则数或深层目录重复扫描整树。目录匹配整体按节点处理，最终预览行仍按路径稳定排序。保留同路径替换、大小写移动、稀疏文件新父目录、链接、跨仓库和异常层级的既有拒绝语义。
- XML 分块读取、节点解析、目录规则、范围/身份匹配和排序检查取消；XML 仍禁止 DTD 且不解析外部资源。核心专项 210 项通过，包含读取原生命令输出时取消、不发出切换、释放工作区互斥锁后重新预览；C# 5 `/warnaserror` 编译通过。
- 新增 `PartialBranchSwitchScaleTests` 22 项：每树 11,001 / 110,001 个项目、1,000 / 10,000 条加载规则，目标树父节点逆序，末尾规则新增目录、范围外相似前缀目录、大小写重复/移动/父链校验、读取中确定性取消和 DTD 拒绝。首轮本机解析与匹配为 95 / 777 ms，新增目录匹配为 5 / 53 ms；门槛为解析与匹配 30 秒、纯匹配 10 秒，用于发现二次复杂度回退，不是服务器响应时限。
- 分支窗口在初始模式查询和 Partial 只读预览时复用“取消预览”按钮；关闭/Escape 也请求取消并等待异步结束，窗口保留以供重试。迟到的成功结果不能打开确认或发出写入；确认切换后恢复既有不可取消写入与关闭保护。GUI 专项 130 项及原切换 GUI 63 项通过；普通/最小尺寸截图为 `qa/partial-switch-preview-cancel-ui/partial-switch-preview-pending*.png`，已目视确认按钮和状态可见。
- 安装/升级回滚/卸载检查 45 项通过（`qa/partial-switch-scale-package-tests.log`），实际 Explorer 注册未改变。新增规模套件已加入标准 `build-tortoisescm.ps1 -Test`。
- 边界：仍需传输并保存目标完整树及 XML，尚未验证真实十万项服务器端到端性能与超大预览列表呈现；工作区元数据哈希等既有同步操作不承诺立即中断。结构处理/恢复向导、Partial 跨分支合并继续列为未完成。
- 最终构建真实 Plastic 结构预览/切换集成 66 项通过（`qa/partial-switch-scale-integration.log`，fixture `qa/integration-20260928-182548-a20ddf1d/`，相邻 `partial-switch-preview-results.json`）。目录移动/删除/替换/新增及稀疏文件新父目录仍在写前拒绝，允许的内容更新与稀疏切换保持原行为；原 TestSCM、producer、selector 与加载配置检查通过。
- 最终全量 `build-tortoisescm.ps1 -Test` 通过（`qa/partial-switch-scale-regression.log`）：210 项预览核心、22 项规模/取消、2,297 项主 CLI、既有 Core/CLI、Shell/现代菜单和完整 WinForms 检查；预览 GUI 130 项与既有切换 UI 63 项在全量中通过。该次规模测量为 94 / 855 ms，新增目录匹配 2 / 47 ms。版本包：`bin/TortoiseSCM/packages/TortoiseSCM-0.1.0-dev-partial-switch-scale-20260928-windows-x64.zip`。

## 2026-09-29：右键菜单常用操作优先

经典 IContextMenu 和现代 IExplorerCommand 共用显示顺序：更新、签入、待处理更改、比较差异、历史记录置顶，随后为文件操作、分支与合并等仓库操作，设置和版本信息置底。显示位置与稳定命令标识分离，保留 canonical verb、现代菜单 GUID 和选择范围规则；编译期检查保证每项恰好出现一次。

- `build-tortoisescm.ps1` 和 Release x64 ShellTests 构建通过。
- ShellTests 498 项断言通过；使用实际构建 DLL 的 `--modern-dll` 测试 707 项断言通过，包含经典及现代命令派发。
- 覆盖文件、目录、目录背景、多选和工作区外的菜单排序/过滤，核对显示文字、canonical verb、稳定 GUID，以及真实进程启动器收到的命令和路径。
- 日志：`bin/TortoiseSCM/qa/menu-order-20260929/` 下的 `build.log`、`test-build.log`、`shell.log`、`production-dll.log`。
- 本轮未更新已安装的 Explorer 扩展，也未完成桌面右键点击验收；需安装更新版本并让 Explorer 重新加载扩展后，实际菜单才会采用新顺序。
