# 与 TortoiseGit 的功能差距

审计日期：2026-09-27。对象是 `src/TortoiseSCM.sln` 实际编译的 Plastic 产品；仓库保留的 Git 源码不代表这些功能已经移植。对照基于本仓库上游源码，而非对最新 TortoiseGit 发行版的完整认证。

## 日常工作流

| TortoiseGit 工作流 | TortoiseSCM GUI / CLI | 当前边界或缺口 |
| --- | --- | --- |
| Explorer 右键 | 原生经典右键、范围/多选、状态图标；新增 Windows 11 现代菜单接口及可选稀疏包 | 现代菜单为未签名预览，需提升权限；本机未完成真实现代菜单显示验收；正式分发签名尚缺 |
| 检查修改、提交 | 目录范围待定列表、勾选提交、右键历史/差异/撤销；`status/checkin` | 缺少 changelist 分组、提交说明历史/模板、issue tracker 集成；没有按行暂存 |
| 日志与恢复 | 分页历史、提交文件明细、逐行追溯、历史比较/导出、范围回滚；对应 CLI 齐全 | 重命名前历史需查旧路径；尚无完整路径身份跟踪 |
| 差异与三方合并 | 外部工具配置、历史文件/整个快照比较、冲突处理；对应 CLI | 未移植完整 TortoiseGitMerge 编辑器；Partial 冲突与分支操作仍有明确支持范围 |
| 分支管理 | 列表/父子层级、创建、Standard 切换、分支历史、固定头提交合并；对应 CLI | 父子树不是提交/合并关系图；缺少分支重命名、删除、Partial 切换 |
| Stash | shelveset 保存、列表、文件明细、应用、删除、内容比较和导出均有 GUI/CLI | 应用仅允许干净 Standard 工作区；Partial 可保存/比较/导出但不能应用；未实现应用冲突向导和自动 pop；目录/链接导出仍受限 |
| 标签 | 无专用 GUI/CLI | 需实现 Plastic labels 的列表、创建、历史定位与生命周期 |
| Blame | blame CLI, Annotate/Blame GUI, Explorer single-file entry | Implemented Plastic native annotate line, owner, changeset, date, branch and content; read-only, directories and binaries are rejected |
| Repository browser | 固定变更集目录树、目录文件列表、只读文本预览、单文件导出；`repository-list` 与 `export` | 尚无递归整目录导出、跨仓库链接浏览；文本预览限 UTF-8 / 2 MiB |
| Clone / Create repository | 选择已有 Plastic 工作区 | 缺少服务器/仓库浏览、新建工作区/仓库向导 |
| 批量文件操作、拖放 | 多路径提交/撤销/添加；单路径移动、删除、忽略 | 缺少拖放移动、批量删除/忽略及整批预检 |
| Patch | 历史文件导出 | 尚无创建、预览、应用补丁的工作流 |
| 发布体验 | x64 ZIP、校验清单、当前用户安装/卸载、自动构建 | 缺少签名安装、自动更新、语言包、ARM64/32 位 Explorer |

## 推进顺序

1. 已完成暂存集保存/浏览、GUI 应用和删除确认，以及内容比较/导出。保持应用前检查和失败后的显式状态核对。
2. 后续补暂存集应用冲突向导与目录/链接导出；不能以保存成功推断可以安全撤销本地工作。
3. 已补只读仓库浏览：按固定快照逐层读取完整目录树，提供历史文件预览与导出，GUI/CLI 共享后端。后续完善递归目录导出和历史路径身份跟踪。
4. 已实现 Windows 11 现代右键菜单接口及可选预览包，与经典菜单共享命令。后续完成受信任签名和真实 Explorer 现代菜单显示验收；当前非管理员环境阻止未签名部署。
5. 变更集/合并关系图、标签管理，再补分支重命名/删除与 Partial 切换。图必须使用服务器真实父关系和合并边，不能用时间或分支名称猜测。
6. Changelist、提交说明历史/模板、批量操作、补丁及发布平台完善。

每个里程碑都应包含相关自动检查、隔离 Plastic 实测、普通/最小尺寸 GUI 渲染、commit/push 和安装后验证。此列表是优先级，不是已实现承诺。

## 语义差异

Git index/staging、fetch/push、rebase 等不能直接改名为 Plastic 命令。Plastic 的待定更改、changeset、branch、shelveset、复制/同步有不同语义；仅在有明确用户工作流和原生支持后设计对应功能。服务端锁管理已有查询与本人解锁；签出是否获得独占锁由服务器锁规则决定，不能将所有 checkout 都显示为持锁。

## 可核对的源码

- 上游菜单/窗口：`src/TortoiseShell/ContextMenu.cpp`、`src/Resources/TortoiseProcENG.rc`、`src/TortoiseProc/RepositoryBrowser.cpp`、`src/TortoiseGitBlame/`。
- 实际 Plastic 菜单：`src/TortoiseShell/PlasticShell.cpp`；构建选择：`src/TortoiseShell/TortoiseSCMShell.vcxproj`。
- 实际 GUI/CLI：`src/TortoiseSCM/MainForm.cs`、`Program.cs`、`CliRunner.cs`；后端：`src/TortoiseSCM/Core/`。
- 已验证能力与限制：[使用说明](TortoiseSCM.md)、[验证记录](TortoiseSCM-validation.md)。
