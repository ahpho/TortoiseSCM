# TortoiseSCM 验证记录

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
