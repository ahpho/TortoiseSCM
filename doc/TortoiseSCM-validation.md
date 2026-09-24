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
