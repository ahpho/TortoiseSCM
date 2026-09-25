# TortoiseSCM 验证记录

日期：2026-09-25。上游基线：`acc10fc20`。运行环境：Windows x64，Plastic `11.0.16.10330`。

本轮新增：文件/目录历史恢复、工作区快照切换、递归目录历史与提交明细、提交列表右键菜单、外部 diff/merge 配置。
功能与命令对应见 [使用说明](TortoiseSCM.md)。

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
