# 与 TortoiseGit 的功能差距

审计日期：2026-09-28。对象是 `src/TortoiseSCM.sln` 实际编译的 Plastic 产品；仓库保留的 Git 源码不代表这些功能已经移植。对照基于本仓库上游源码，而非对最新 TortoiseGit 发行版的完整认证。

完成范围以 [ROADMAP](../ROADMAP.md) 为准：多 DPI、提交失败草稿跨重启恢复，以及签名/自动更新/ARM64 等发布与平台产品化工作已由用户排除。下表保留能力差异，范围外项目不计入待完成清单。

## 日常工作流

| TortoiseGit 工作流 | TortoiseSCM GUI / CLI | 当前边界或缺口 |
| --- | --- | --- |
| Explorer 右键 | 原生经典右键、范围/多选、状态图标；新增 Windows 11 现代菜单接口及可选稀疏包 | 现代菜单为未签名预览，需提升权限；本机未完成真实现代菜单显示验收；正式签名分发在范围外 |
| 检查修改、提交 | 目录范围待定列表、勾选提交、实际范围/说明/锁预览、执行前状态与内容复核、失败保留及刷新后明确重试、本机仓库隔离的成功说明历史和命名模板；`status/checkin` | GUI 新增预检，CLI 仍按原有显式参数执行；缺少 changelist 分组、可配置说明规则和 issue tracker 集成；跨重启失败草稿在范围外；没有按行暂存 |
| 日志与恢复 | 分页历史、提交文件明细、逐行追溯、历史比较/导出、范围回滚；对应 CLI 齐全 | 重命名前历史需查旧路径；尚无完整路径身份跟踪 |
| 差异与三方合并 | 工具统一 Beyond Compare：共用配置、固定角色、独立等待、工作文件身份基线、历史/暂存集逐文件比较、经原生状态确认的工作文件/历史/暂存集增删空侧、Standard/Partial 独立结果及明确应用 | 三方文本合并需 BC Pro；工作文件受控删除的历史身份不唯一时拒绝比较；实际 BC 保存/放弃未验收；多 DPI 在范围外；Partial 仍受原生流程支持范围限制 |
| 分支管理 | 列表/父子层级、创建、非当前叶子分支重命名、受保护空叶分支删除、Standard/Partial 干净工作区切换、分支历史、固定头提交合并；对应 CLI | 重命名/删除要求原生身份、头版本与父关系复核；删除拒绝自身提交、属性及继承头暂存引用；Partial 切换提供可取消的目录结构预览，使用身份/路径索引，在执行前阻止加载目录移动/删除/替换及加载范围新目录；核验加载配置，规则改变时报告结果未确认；尚不支持当前/根/有子分支重命名、Partial 跨分支合并及加载结构恢复向导，不提供历史级联删除 |
| Revision graph | 原生分页图与提交列表、真实父提交及带类型的合并边、提交文件明细与固定快照浏览；`revision-graph` CLI | 每页最多 100 提交/1000 合并边；页外端点明确未加载；尚无全仓自动布局、分支过滤、图像导出 |
| Stash | shelveset 保存、列表、文件明细、应用、删除、内容比较和导出均有 GUI/CLI | 应用仅允许干净 Standard 工作区；Partial 可保存/比较/导出但不能应用；未实现应用冲突向导和自动 pop；目录/链接导出仍受限 |
| 标签 | 标签列表/筛选、创建、按 ID/变更集核对后删除、目标提交文件明细和固定快照浏览；`labels/label-resolve/label-create/label-delete` | 尚无用户重命名、移动或按标签切换；原生按名称删除不能原子防止跨客户端竞争 |
| Blame | blame CLI, Annotate/Blame GUI, Explorer single-file entry | Implemented Plastic native annotate line, owner, changeset, date, branch and content; read-only, directories and binaries are rejected |
| Repository browser | 固定变更集目录树、目录文件列表、只读文本预览、单文件导出；`repository-list` 与 `export` | 尚无递归整目录导出、跨仓库链接浏览；文本预览限 UTF-8 / 2 MiB |
| Clone / Create repository | 选择已有 Plastic 工作区 | 缺少服务器/仓库浏览、新建工作区/仓库向导 |
| 批量文件操作、拖放 | 多路径提交/撤销/添加；单路径移动、删除、忽略 | 缺少拖放移动、批量删除/忽略及整批预检 |
| Patch | 历史文件导出 | 尚无创建、预览、应用补丁的工作流 |
| 发布体验 | x64 ZIP、校验清单、当前用户安装/卸载、自动构建 | 签名安装、自动更新、语言包、ARM64/32 位 Explorer 在范围外；维护现有 x64 安装能力 |

## 推进顺序

1. 已完成暂存集保存/浏览、GUI 应用和删除确认，以及内容比较/导出。保持应用前检查和失败后的显式状态核对。
2. 后续补暂存集应用冲突向导与目录/链接导出；不能以保存成功推断可以安全撤销本地工作。
3. 已补只读仓库浏览：按固定快照逐层读取完整目录树，提供历史文件预览与导出，GUI/CLI 共享后端。后续完善递归目录导出和历史路径身份跟踪。
4. 已实现 Windows 11 现代右键菜单接口及可选预览包，与经典菜单共享命令。在现有未签名预览能力内继续真实 Explorer 显示验收并记录部署限制；不新增签名交付要求。
5. 已补标签列表、创建、核验删除，以及使用服务器真实父关系与带类型合并边的分页提交关系图、非当前叶子分支重命名与空叶分支删除。已补保留加载配置的 Partial 切换；后续完善图的导航与筛选及被拦截加载结构变化的处理与恢复体验；不能用时间或分支名称猜测祖先关系。
6. 提交说明历史/模板已完成；后续补 Changelist、说明规则、批量操作、补丁和工作区管理。首次工作区拉取向导已提升为 P0，详见 ROADMAP。

每个里程碑都应包含相关自动检查、隔离 Plastic 实测、普通/最小尺寸 GUI 渲染、commit/push 和安装后验证。此列表是优先级，不是已实现承诺。

## 语义差异

Git index/staging、fetch/push、rebase 等不能直接改名为 Plastic 命令。Plastic 的待定更改、changeset、branch、shelveset、复制/同步有不同语义；仅在有明确用户工作流和原生支持后设计对应功能。服务端锁管理已有查询与本人解锁；签出是否获得独占锁由服务器锁规则决定，不能将所有 checkout 都显示为持锁。

## 可核对的源码

- 上游菜单/窗口：`src/TortoiseShell/ContextMenu.cpp`、`src/Resources/TortoiseProcENG.rc`、`src/TortoiseProc/RepositoryBrowser.cpp`、`src/TortoiseGitBlame/`。
- 实际 Plastic 菜单：`src/TortoiseShell/PlasticShell.cpp`；构建选择：`src/TortoiseShell/TortoiseSCMShell.vcxproj`。
- 实际 GUI/CLI：`src/TortoiseSCM/MainForm.cs`、`Program.cs`、`CliRunner.cs`；后端：`src/TortoiseSCM/Core/`。
- 已验证能力与限制：[使用说明](TortoiseSCM.md)、[验证记录](TortoiseSCM-validation.md)。
