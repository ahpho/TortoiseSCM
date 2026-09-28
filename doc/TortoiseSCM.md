# TortoiseSCM 开发版本

TortoiseSCM 是面向 Windows Explorer 的 Plastic SCM / Unity Version Control 客户端。
这次移植建立了独立的 Plastic 运行入口，没有将 Git 命令简单改名后继续执行。

## 首次安装与日常操作

运行需要 Windows x64、.NET Framework 4.8，以及已安装并完成服务器登录的 Plastic SCM / Unity Version Control 客户端；不需要安装 Visual Studio。含 BC 的安装包已携带 Beyond Compare 运行文件，无需另装程序；不含 BC 的包需自行安装。三方合并需要 Pro 授权。

1. 双击最新 **`TortoiseSCM-…-windows-x64-Setup.exe`**，按向导安装。无需解压或手动执行 PS1、BAT/CMD；安装程序自动安装到当前用户目录并注册经典右键菜单。**升级时直接运行新版安装包，无需先卸载。** 安装完成只提示成功，不自动打开工作区或拉取向导。Beyond Compare 已内置，无需另装或设置路径；原有外部自定义工具路径保留。如果提示 Explorer 仍加载旧菜单，可选择稍后重启，或确认所有复制、移动、删除、解压任务结束后，按向导明确确认重启。重启会关闭文件夹窗口，桌面和任务栏短暂消失，不会自动发生。
2. 在工作区以外的目标父目录空白处右键 → **TortoiseSCM → 拉取仓库…**（Windows 11 可先点“显示更多选项”）。例如在 `D:\` 右键，默认目录为 `D:\TestSCM`、本地工作区名称为 `TestSCM`；已有文件、目录或注册工作区重名时，自动建议 `TestSCM2`、`TestSCM3` 等可用名称，仍可手动修改。填写团队提供的服务器地址（如 `host:8087`、`ssl://host:8088` 或 `组织名@cloud`），点击“查询仓库”，优先选中已有 `TestSCM` 仓库，也可选其他仓库。默认 Gluon 模式，分支通常保持 `/main`；点击“拉取并打开”，核对后等待下载完成。
3. 拉取成功后自动进入待定更改窗口。以后在该目录右键 → TortoiseSCM；Windows 11 先点“显示更多选项”。若菜单尚未出现，在任务管理器中重启“Windows 资源管理器”；也可双击安装目录的 EXE，选择“打开已有工作区…”。
4. 从下表开始日常测试。根目录入口操作整个工作区；文件或子目录的历史只查看对应范围。

| 目的 | 最简操作 |
| --- | --- |
| 更新库 | 工作区根目录右键 → TortoiseSCM → “更新…”；也可在主窗口“操作 → 更新…”，确认整体更新 |
| showLog | 根目录右键 → TortoiseSCM → “历史记录…”；主窗口使用“操作 → 当前范围历史 / 恢复” |
| commit | 修改文件后打开“待处理更改…”或“签入…”；勾选文件，填写说明，点击“提交”，核对预览后确认 |
| 添加新文件 | 在待定列表勾选未版本控制的文件，“操作 → 添加…”，再勾选提交 |
| 比较修改 | 文件右键 → TortoiseSCM → “比较差异…”；使用 Beyond Compare |

查看当前版本：工作区右键 → TortoiseSCM → **“版本信息…”**，可查看安装包版本、Git 提交号、程序目录和架构；主窗口及欢迎窗口也提供入口。开发构建或缺失/损坏的包信息会明确提示，不据此声称最新版。安装新包后若菜单仍显示旧版本，请重启资源管理器后核对；必要时再注销 Windows 并重新登录。

右键“历史记录”直接打开所选文件或目录的日志；“更新”打开独立的更新窗口，核对范围后点击更新，窗口保留执行结果；“签入”打开提交窗口。完整工作区会明确提示整体更新，Partial 工作区按选中范围更新。升级前已经打开的旧窗口不会自动替换，请关闭旧窗口后重新打开。欢迎窗口的“打开已有工作区…”支持直接粘贴目录路径（含“复制为路径”得到的双引号），也可点击浏览选择。

卸载：打开 Windows **设置 → 应用 → 已安装的应用 → TortoiseSCM → 卸载**。工作区和用户设置保留；若存在被占用或修改的程序文件，卸载入口会保留，处理后可再次卸载。已启用系统级图标或需提升权限的现代菜单的旧安装，升级/卸载按提示以管理员身份运行。ZIP、`Install.cmd`、`Uninstall.cmd` 和 PS1 继续保留，供便携使用和维护；正常安装、升级、卸载均无需手动执行这些脚本。
首次可先完成“拉取 → 看日志 → 更新”，再修改一个可提交的测试文件并提交，最后刷新日志确认新变更集。提交失败或结果未确认时先刷新并检查历史，不要直接重复提交。

向导为已有仓库创建本地工作区，默认 **Gluon / Partial**，可选择 **Standard**。Gluon 支持后续按选中文件或目录更新；Standard 后续整体更新，目前跨分支合并、暂存集应用应选择 Standard。Gluon 是工作区模式，不改变服务器仓库。两种模式首次都完整下载所选分支，首次稀疏加载范围选择尚未实现；不创建服务器仓库、不复制服务器历史、不创建动态工作区。目标必须为空或尚不存在，拒绝已有/嵌套工作区和链接目录；会复核仓库及分支身份。查询可取消，确认后的预检/创建/下载期间禁重复操作和关闭；失败会保留目录及阶段信息，不自动删除或重复创建。其他客户端并发操作不属于服务器原子事务。

工作区创建成功而下载未完成时，按向导提示在官方客户端核对仓库、分支和本地状态后继续更新，不要删除目录或重新创建。下载使用设置中的 Plastic 命令超时，默认 5 分钟；大仓库首次拉取前可在欢迎窗口“设置…”调长。认证失败或 SSL 证书尚未信任时，先通过官方客户端完成配置。官方对工作区与仓库的区别见 [创建工作区说明](https://docs.unity.com/en-us/unity-version-control/workflow/create-workspace)。

入口也支持 `TortoiseSCM.exe --command create-workspace`，可选 `--path 'D:\Workspaces\MyProject'` 预填精确目标目录，或 `--parent-path 'D:\'` 在父目录下建议新子目录；两者不能混用。这是 GUI 向导命令。工作区外目录、目录背景可右键拉取，磁盘节点通过经典菜单提供；已有工作区内不显示此入口，避免嵌套创建。已打开工作区的“操作 → 拉取仓库…”会在完成后打开独立窗口，保留原工作区。

## Gluon 目录更新遇到本地修改

右键所选目录 → 更新 → 核对范围 → 更新。程序先预检所选范围中的传入冲突；发现冲突时，先停止本次更新并列出路径。通过以下入口明确处理，再返回刷新范围、重新点击更新，不自动续跑：

- **保留修改**：点“处理传入冲突”。文本选择 Beyond Compare 三方合并；二进制选择“保留本地版本”或“采用服务器版本”，不启动文本合并。点击“准备结果”只生成独立结果文件；核对后再点“确认应用结果”。文本合并和保留本地的结果作为待定更改，需另行签入；完全采用服务器内容时无需再次签入。
- **丢弃修改**：点“检查 / 丢弃修改”，仅选择确实不需要的修改行，再执行“丢弃所选行的修改”并确认。选择目录撤销会包含其全部子项；取消确认不会丢弃内容。
- **暂不处理**：直接关闭窗口，或取消未应用准备；工作文件不变，准备时的备份和独立结果保留。结构冲突需另点“结构冲突”，该窗口显示整个工作区，应再次核对范围。

预检基于 Plastic 报告的状态，不是目录级原子事务。并发编辑、预检后到达的新版本、原生状态未识别的修改仍可能由实际更新拒绝；拒绝前无冲突文件可能已更新，必须核对状态和日志。不会自动重试、丢弃或签入。合并前后会复核文件身份、版本和原始内容；中断时保留恢复副本并阻止不确定的继续应用。

## 应用图标

主程序、所有业务窗口（包括历史记录）、Explorer 菜单和安装器统一使用 `src/Resources/gluon.ico`。图标原样取自本机 Plastic SCM 客户端 `D:\Program Files\PlasticSCM5\theme\avalonia\icons\gluon.ico`，SHA-256 为 `556946ab179e30309e726b3eb8ffee3e5e9c1f84c2340682a54dc388205597f7`，保留 16、32、48、256 像素图层。现代菜单包的 PNG 标识由该 ICO 的 256 像素图层按目标尺寸导出。子窗口通过公共 `DialogStyle` 读取当前 EXE 的图标，后续新增窗口应沿用此入口。

## 构建图形安装包

在已有 Release 构建上运行：

```powershell
.\contrib\tortoisescm\Build-Setup.ps1 -Version '0.1.0-dev-setup-20260929' -BeyondCompareDirectory 'D:\Work\Juscent\SCM_Study\Tool\BeyondCompare'
```

输出单个 `…-Setup.exe`、SHA-256 文件和作为内部负载的 ZIP。也可用 `-PackageArchive` 指定已经包含 BC 的 ZIP；图形安装包拒绝缺少 BC 的负载。首次构建自动获取固定版本的官方 Inno Setup 便携编译器，核对固定 SHA-256 和厂商签名；也可传 `-IsccPath` 使用已安装的编译器。此下载仅发生在开发机，用户安装时不下载工具。

安装器采用固定应用标识及原有版本目录升级机制，保留旧版被 Explorer 占用的 DLL。新版本注册失败沿用后端回退；若文件和注册已成功、但安装器自身登记失败，会明确提示重跑以补齐卸载入口。卸载同时清理可确认归属的旧版本，保留被修改、占用和用户自行加入的文件。正常自动工具模式优先使用当前版本的随包 BC；识别到同安装根旧版本内的显式 BC 路径时，安全切换回自动模式，不覆盖用户选择的外部工具。

## 构建与运行

要求 Windows x64、.NET Framework 4.8、Visual Studio 2022 或更新版本的 C++ 桌面开发组件与 Windows SDK。
使用新的解决方案 `src/TortoiseSCM.sln`。构建不依赖 Git、libgit2、MFC 或 NuGet；保留的上游解决方案仍有其原依赖。

```powershell
cd D:\Work\Juscent\SCM_Study\TortoiseSCM
.\build-tortoisescm.ps1 -Test
.\bin\TortoiseSCM\Release\TortoiseSCM.exe --path 'D:\Work\Juscent\SCM_Study\TestSCM'
```

也可以双击 `TortoiseSCM.exe`，从欢迎窗口拉取新工作区或打开已有 Plastic 工作区。`--command settings` 打开客户端路径设置。

运行时复用 Plastic 客户端现有的用户配置和认证。新程序不储存密码，也不要求安装 Git。
首次使用会发现标准安装目录和本机 `D:\Program Files\PlasticSCM5\client`；也可在“设置”中指定路径。

### 历史仓库浏览器

Explorer 单选文件、目录或工作区背景右键的“仓库浏览器”，以及主窗口操作菜单，可打开原生目录树/文件列表窗口。历史窗口右键选中提交，可浏览该提交的整个仓库快照。没有指定版本时，使用当前分支头变更集；无法确定当前分支时需输入编号。

浏览器显示服务器在固定变更集下的目录内容，与本地是否加载该目录无关，适用于 Standard 和 Partial 工作区。切换快照会清空旧列表及预览。双击目录进入下一级；选中文件后可只读预览 UTF-8 文本（最多 2 MiB）或导出原始字节。导出同名文件前确认覆盖。符号链接仅显示，不跟随；跨仓库链接明确拒绝。当前仅支持单文件导出。

```powershell
.\bin\TortoiseSCM\Release\TortoiseSCM.exe --command repository-browser --path 'D:\Work\Juscent\SCM_Study\TestSCM' --changeset 1
.\bin\TortoiseSCM\Release\TortoiseSCM.exe --cli --json --command repository-list --path 'D:\Work\Juscent\SCM_Study\TestSCM' --changeset 1 --item /
.\bin\TortoiseSCM\Release\TortoiseSCM.exe --cli --json --command export --path 'D:\Work\Juscent\SCM_Study\TestSCM' --changeset 1 --item /path/file.txt --output 'D:\Temp\file.txt' --yes
```

`repository-list` 必须显式指定变更集，`--item` 默认为 `/`，返回直接子项的仓库路径、ItemId、类型及字节数。目录或文件在该快照不存在时报告错误。所有浏览操作不会切换分支、更新、签入或回滚工作区；导出仅写入指定目标。

### 提交与合并关系图

Explorer 的“提交关系图”和主窗口操作菜单可打开整个仓库的分页关系图。左侧绘制提交及真实关系，右侧是可通过键盘选择的原生提交列表；下方显示选中提交的说明、关系明细和更改文件，可继续浏览该提交的固定快照。此窗口只读，不切换分支或修改文件，适用于 Standard 和 Partial 工作区。

父提交来自服务器 `PARENT` 字段，合并来源、目标与类型来自原生 merge 记录。实线表示父提交，虚线表示普通合并，点划线表示其他原生关系；挑选合并和反向合并保留实际类型，区间基线仅在明细中显示，不作为父子边。图中的分支列只负责排版，不据此推断祖先关系。

默认显示最新 100 个提交，可跳转到某个编号之前、查看更早一页或返回。仅查询以本页提交为目标的关系，页面外来源标明“未加载”；没有绘出的后续提交或关系不代表不存在。每页最多接收 1000 条合并记录，超限会报错而非悄悄截断；CLI 可减少 `--limit` 后重试。文件/目录参数仅用于定位工作区，当前不提供按路径或分支过滤的图。

```powershell
$exe = '.\bin\TortoiseSCM\Release\TortoiseSCM.exe'
$workspace = 'D:\Work\Juscent\SCM_Study\TestSCM'
& $exe --command revision-graph --path $workspace
& $exe --command revision-graph --path $workspace --before 140
& $exe --cli --json --command revision-graph --path $workspace --before 140 --limit 20
```

`--before` 是不包含该编号的游标；`--before 0` 返回空页。JSON 包含 `nodes`、`edges`、`hasMore` 和 `nextBeforeChangeset`，节点保留编号及对象 ID，关系保留类型、来源/目标、可选基线和端点是否已加载。分页结果不是完整仓库关系图。目前不提供图像导出、全仓图自动布局或分支筛选。

### 标签管理

Explorer 单选文件、目录或工作区背景右键的“标签”，以及主窗口操作菜单，可打开仓库标签列表。标签覆盖整个仓库，不按所选目录裁剪。上方列表支持筛选；选中标签后，下方显示目标提交说明及更改文件，双击可浏览该固定变更集的完整快照。历史记录右键“创建标签”会固定选中提交，也可在标签窗口输入目标变更集。

GUI 和 CLI 均支持列表、查询、创建和删除，适用于 Standard / Partial 工作区，不更新或切换工作区。创建要求名称、明确变更集与非空说明；删除需确认标签名称、ID 和变更集。

现有特殊名称标签仍可列出、精确查询和浏览。为避免原生对象规格歧义，创建/删除拒绝 `@ # : " / \`、控制字符、开头短横线及首尾空白；这些名称的标签需使用官方客户端管理。CLI 筛选匹配名称和说明；GUI 还可按变更集、分支、作者或日期筛选。

```powershell
$exe = '.\bin\TortoiseSCM\Release\TortoiseSCM.exe'
$workspace = 'D:\Work\Juscent\SCM_Study\TestSCM'
& $exe --command labels --path $workspace
& $exe --cli --json --command labels --path $workspace --filter release
& $exe --cli --json --command label-create --path $workspace --label release-example --changeset 1 --comment 'Release snapshot' --yes
$resolved = (& $exe --cli --json --command label-resolve --path $workspace --label release-example | ConvertFrom-Json).data.label
& $exe --cli --json --command repository-list --path $workspace --changeset $resolved.changeset
# 核对查询结果后再删除；以实际查询到的 ID 和变更集为准。
& $exe --cli --json --command label-delete --path $workspace --label $resolved.name --label-id $resolved.id --changeset $resolved.changeset --yes
```

创建不会覆盖或移动已有同名标签：先使用唯一临时名称创建并核验，再通过原生重命名发布；同名冲突会失败。网络中断、取消或发布失败可能留下临时标签，错误会给出名称和仓库；请刷新核对后处理，程序不会自动删除。底层 `cm label delete` 仅支持按名称删除，因此 ID/变更集检查与删除之间的跨客户端竞争无法做到原子保护。暂不提供用户标签重命名、移动标签或直接按标签切换工作区；可以解析标签后使用固定变更集的现有浏览/恢复操作。

### 可安装包

```powershell
.\contrib\tortoisescm\Package.ps1 -Version '0.2.0-preview'
```

可将自备的 BC 运行文件随包携带，例如：

```powershell
.\contrib\tortoisescm\Package.ps1 -Version '0.2.0-preview-with-bc' -BeyondCompareDirectory '..\Tool\BeyondCompare'
```

运行文件安装到本版本的 `Tools\BeyondCompare`，进入逐文件校验清单并随版本安装/升级/卸载；不会注册 BC 自己的 Explorer 扩展。打包要求 `BCompare.exe`、`BComp.exe` 和 `License.html`，并按白名单携带运行所需辅助文件；不复制补丁程序、个人许可证、会话和配置。BC 使用独立许可，三方合并仍需 Pro。

使用含 BC 的包时，在“设置 → 差异查看器”保留空路径（自动模式）即可。已有自定义路径会继续优先使用；需要改用包内 BC 时点击“自动检测”并保存。卸载保留用户新增/修改文件，包括使用 BC 后生成的文件。

产物位于 `bin/TortoiseSCM/packages`：Windows x64 ZIP、SHA-256 校验文件，ZIP 内含逐文件校验清单。
普通使用双击 `Install.cmd`，安装成功后仅提示结果；需要命令行参数时使用 `Install.ps1`。默认安装到 `%LOCALAPPDATA%\Programs\TortoiseSCM` 并注册当前用户右键菜单。
`Install.ps1 -WhatIf` 可预览；需要状态图标时，在管理员 PowerShell 使用 `Install.ps1 -EnableMachineOverlays`。
每次安装使用新版本目录，不覆盖 Explorer 已加载的 DLL。旧版本保留，注册失败时恢复原注册。
双击 `Uninstall.cmd` 并确认卸载当前活动版本；高级用法可运行 `Uninstall.ps1`；有机器级图标注册时需管理员执行并添加 `-RemoveMachineOverlays`。
卸载仅清理属于该包且哈希未变化的文件，保留设置、工作区和用户新增/修改的文件。被锁定的文件会保留；等待文件操作结束后重启资源管理器，再重试卸载，必要时注销后重试。
双击入口自动调用 64 位 Windows PowerShell，`ExecutionPolicy Bypass` 仅对本次进程生效，不修改系统执行策略；没有自动提权。`.cmd` 入口结束时会关闭其命令窗口；在已有终端或自动化中请使用底层 `.ps1`，它们保留高级选项。包当前未数字签名，也不提供自动更新。

### Windows 11 现代右键菜单（预览）

新增 `IExplorerCommand` 菜单实现，与经典菜单共享命令及选择范围规则：文件单选、目录/背景和同工作区多选；工作区外仅普通单目录/背景提供拉取入口，`.plastic` 元数据和跨工作区混选不提供操作。打开菜单时不启动 `cm.exe`，点击命令后仍由独立主程序执行。

安装包包含可选的应用身份稀疏包，默认安装仍使用经典菜单。当前提供的是未签名测试包；本机实测普通权限注册被 Windows 拒绝，因此预览安装需要管理员 Windows PowerShell：

```powershell
.\Install.ps1 -EnableModernMenu
```

该选项要求 Windows 11 x64。已经启用现代菜单的安装在升级时保留该选项；安装失败会尝试恢复先前注册，旧版本目录保留以便恢复。卸载核对 Windows 实际记录的外部程序目录，避免移除属于新版本的菜单。脚本不安装信任证书、不启用开发者模式；组织策略仍可能拒绝未签名包。受信任签名分发不在当前项目范围内，现代菜单保持未签名预览。

注册后的 Explorer 可能需要注销再登录以加载新扩展。当前已验证原生接口及命令传递；本机未签名部署被策略/权限阻止，尚未完成真实现代菜单显示验收。经典菜单仍可通过“显示更多选项”使用。

实现依据：[Microsoft 的 Explorer 菜单扩展文档](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer)；未签名包仅用于开发测试，参见[未签名 MSIX 要求](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package)。

GitHub Actions 的 Windows 2022 工作流执行无服务器构建/测试、隔离安装测试并上传 ZIP 工件；真实 Plastic 服务器集成测试仍需本地授权的测试仓库。

## Explorer 集成

```powershell
.\contrib\tortoisescm\Register-Shell.ps1
# 卸载右键菜单
.\contrib\tortoisescm\Unregister-Shell.ps1
```

默认注册使用当前用户的 `HKCU\Software\Classes`，不修改 TortoiseGit 的 CLSID。
可通过 `-BinaryDirectory` 指向另一个同时含 EXE 和 DLL 的目录。
菜单显示于工作区内的文件、目录与背景；多选必须来自同一个工作区。
`.plastic` 元数据目录不显示菜单。Windows 11 使用经典“显示更多选项”菜单。

经典和现代菜单均优先显示常用操作：更新、签入、待处理更改、比较差异、历史记录；随后是文件操作、分支与合并等仓库操作，设置和版本信息位于末尾。不适用于当前选择的条目隐藏后，其余条目保持此顺序。工作区外仍仅显示“拉取仓库…”。

Explorer 会保持已加载 DLL 的文件锁。如替换 DLL 时失败，先注销当前用户再登录；脚本不会强制结束 Explorer。
卸载注册不会删除工作区、设置或程序文件。

### 状态图标与后台缓存

提供正常、修改、冲突、新增、删除、忽略和未版本控制七类工作区状态图标。受控子项的新增、删除或修改在父目录汇总为“修改”，冲突汇总为“冲突”；忽略和未版本控制子项不会把受控父目录标成忽略或未版本控制。签出不等于服务器锁，因此签出状态仍显示为修改；锁定图标资源已预留，服务器锁请从锁管理窗口查询。未加载项不会被当作正常文件；Windows 的 overlay 槽位限制可能导致部分图标不显示。
Explorer 只读取本地缓存，不在图标查询中执行 cm、访问服务器或扫描工作区。
打开工作区窗口会启动独立后台进程，监视文件变化并定期刷新；过期、损坏或失败的缓存不显示图标。
Windows 图标槽位有限，其他扩展已占满时，即使注册成功也可能无法显示。

Windows 的图标覆盖发现使用机器级注册。需要在管理员 PowerShell 中执行：

```powershell
.\contrib\tortoisescm\Register-Shell.ps1 -EnableMachineOverlays
# 移除本产品的机器级图标注册
.\contrib\tortoisescm\Unregister-Shell.ps1 -RemoveMachineOverlays
```

该选项同时配置当前用户登录时启动缓存进程；不会移除或重新排列其他产品的图标注册。
GUI 的“操作 → 刷新 Explorer 状态图标”可手动刷新；CLI 也可单次生成缓存：

```powershell
& $exe --cli --command cache-refresh --path 'D:\workspace' --yes --json | ConvertFrom-Json
& $exe --cache-worker   # 独立后台刷新进程（每用户一个）
& $exe --cache-stop
```

缓存位于 `%LOCALAPPDATA%\TortoiseSCM`，使用显式路径和时间戳；不以“没有发现更改”推断受控文件。
当前后台监视最多 64 个已打开的本地工作区，缓存最多 200,000 条 / 32 MiB；超过限制会停止显示对应状态，而非截断后显示正常。
Partial 工作区仅缓存实际加载项。原生合并存在但无法确定冲突文件时，工作区根目录显示需检查的冲突状态。

## 使用

主窗口沿用 Tortoise 提交对话框布局：上方提交说明，下方带复选框的路径列表，底部刷新、操作、提交、关闭。
添加、更新、历史、差异、设置和操作记录位于底部“操作”菜单。文件列表的右键菜单用于所选行操作。
历史窗口使用“提交列表 / 提交说明 / 文件明细”三段布局，两个分隔条均可拖动；顶部筛选框过滤已加载记录。
历史打开时先尝试原生快速预览（最多核对 10 条、预算 4 秒），再按提交区间批量读取明细，自动补齐所选范围的全部记录。预览不代表完整历史；窗口持续显示扫描进度，无需手动加载更早记录。“刷新全部”或 `F5` 也逐批显示新记录，同时保留原列表，全部成功后替换为完整结果；取消或失败会恢复原列表，首次加载则保留已显示记录。只有完整扫描结束才显示完成。旧客户端不支持批量查询或响应不完整时，自动使用兼容查询并在进度处提示，可能较慢。
下方文件明细选中一项后，按 `Ctrl+D`、双击或点击比较按钮，直接打开 Beyond Compare，对比该提交与服务器记录的父版本，不再先打开版本选择窗口。新增/删除比较使用空的一侧，移动使用旧/新历史路径；不依赖该文件当前仍存在于本地。
提交列表右键可复制变更集编号或提交说明，以及“标记为比较起点”。选择另一条提交后，在下方文件明细右键选择与标记版本比较，即可直接比较该文件的两个版本；也可清除标记。导出与手动选择版本保留为单独入口。
比较整个仓库时，在提交列表标记起点，再选择目标提交，右键选择“与标记版本比较整个仓库”。新窗口列出两个快照之间的新增、删除、修改和移动项，可按状态、路径、原路径或类型筛选；支持取消加载、刷新和 F5。目录、符号链接和 Xlink 仅显示结构变更，目录移动不展开未修改子文件。
双击文件可比较内容、调用 Beyond Compare，或分别导出起点/目标版本。移动项和移动目录下的修改文件使用旧、新两条历史路径；新增只可导出目标版本，删除只可导出起点版本，不把不存在的端点伪装成空文件。该窗口比较范围为整个仓库，包括在文件或子目录历史窗口中发起的比较。
文件明细右键可打开该路径的历史、复制路径或移动前路径；已从本地删除的历史路径仍可查询。`Ctrl+C` 复制当前焦点列表的变更集编号或路径，`F5` 刷新历史。
文件/目录页面按路径事件筛选提交，能显示复用旧修订的回滚发布。重命名前的历史需查询旧路径；路径曾被删除再创建时，本页面会包含该路径的多次使用。
设置使用左侧分类树和右侧设置页。所有窗口共用系统对话框字体、颜色和 Explorer 列表主题。

布局参考上游 `src/Resources/TortoiseProcENG.rc` 的 `IDD_COMMITDLG`、`IDD_LOGMESSAGE`、
`IDD_SETTINGSPROGSDIFF`、`IDD_SETTINGSPROGSMERGE`。目前界面仍由 WinForms 承载原生 Windows 控件，
没有复用上游 MFC 对话框类；Git 专用控件、完整主题系统与编辑器功能并未因此移植。

| 操作 | 行为 |
| --- | --- |
| 待定更改 | 显示原右键范围内的更改；支持中文、空格和多选路径 |
| 签入 | 勾选待定项并填写说明；确认后提交所选项。私有项需先添加 |
| 添加 / 签出 | 操作勾选项；没有勾选时使用原右键范围，执行前显示范围 |
| 更新 | 完整工作区明确确认整体更新；Partial 工作区使用勾选项或原右键范围。不传入强制覆盖本地更改的参数 |
| 撤销 | 只操作勾选项，确认提示会明确说明本地更改将丢失 |
| 差异 | 选择一个受控文件，比较工作区本地版本与其基础版本 |
| 历史 | 上半部提交记录，下半部选中提交的完整文件明细；文件、目录（含子项内容修改）与整个仓库均可查询 |
| 范围历史 / 恢复 | 固定使用打开窗口时的文件或目录范围；根目录分别提供“整仓回滚为待提交”和“切换历史快照” |
| 历史文件 | 双击历史明细中的文件，输入两个变更集并用 Beyond Compare 比较，或分别导出起点/目标版本 |
| 删除 / 重命名 | 生成待提交删除或移动；要求所选范围干净，不覆盖目标；可撤销后再决定是否提交 |
| 加入忽略列表 | 将未版本控制项的精确路径追加到工作区 ignore.conf，保留已有规则 |
| 打开 Gluon | 在官方客户端中打开当前工作区 |
| 设置 | 配置 cm.exe、gluon.exe、Plastic 命令超时及共用 Beyond Compare 路径 |

目录操作递归包含子项。签入/撤销一个有更改的目录前应核对其子项。
勾选目录会勾选其可见后代；取消某个子项会取消父目录的递归选择，避免把该子项隐式提交。
列表右键提供显示历史、查看差异和丢弃修改，操作对象为高亮行；签入按钮使用勾选项。

点击“提交”会先生成只读预览，列出实际签入文件、完整说明、仓库/分支/工作区和相关锁。目录范围包含受控的待定后代，私有/忽略文件会显示排除数量；显式勾选私有项须先添加。文件依赖未选中的待定结构父目录时，需重新选择目录后核对范围。锁信息仅作提示，查询失败会说明原因，最终锁和权限判断仍由 Plastic 完成。

确认后会再次检查工作区、模式、待定范围和文件内容指纹；发现预览后内容变化或目录内新增受控更改时停止签入。此复核保留现有 Standard 合并与 Partial 冲突检查，CLI `checkin` 继续使用原有参数流程。文件系统复核和服务器签入不是原子事务，确认期间应避免其他客户端或编辑器同时修改提交范围。

失败、超时或异常后保留说明、原勾选及“操作 → 操作记录”中的可复制详情，不自动刷新或重试。先点击“刷新”并查看历史确认上次结果；刷新成功后才能重新提交，预览中还需确认当前剩余更改确实需要提交。刷新失败继续禁止提交，原列表和说明保持可检查。仅明确成功才清空说明；关闭程序后的失败草稿恢复已排除在项目范围之外。

“提交说明”区域的“说明历史 / 模板…”打开当前仓库的本机说明库。“最近成功提交”只记录此程序 GUI 确认成功的说明，保留最近 20 条并将完全相同的说明移到最前；不会从服务器历史导入，也不保存失败或未确认提交的草稿。CLI 和其他客户端的提交不写入此历史。选择条目后可查看完整多行内容，再点击“使用此说明”；已有编辑内容时须确认替换，关闭说明库不会替换当前说明。

“命名模板”支持新建、载入当前说明、保存、修改同名模板、另存新名称和删除，每仓库最多 20 个。模板只有显式保存才持久化；修改名称会另存。覆盖和删除均需确认，并发修改导致所审阅内容过期时会拒绝写入。使用模板仍需经过正常提交预览，不会自动签入。说明最多 32,768 字符，模板名最多 80 字符；提交仍要求非空说明，尚无可配置格式规则。

说明库保存在当前用户 `%LOCALAPPDATA%\TortoiseSCM\commit-messages`，按完整仓库标识隔离，本机多个该仓库工作区可复用。清空历史只清除此仓库的本机记录，不删除模板或服务器提交；模板可单独删除。文件损坏或不可写时会提示并保留原文件，提交已成功但历史保存失败不会变成提交失败，也不需要重新提交。

待提交列表按当前路径过滤，移动到范围外的条目不会显示在原目录内。
CLI 命令根据实际退出码报告结果，GUI 错误可在“操作 → 操作记录”查看；失败时不会清空签入说明。
Gluon 独立运行，TortoiseSCM 仅确认进程启动。Beyond Compare 则等待本次窗口关闭，期间保留贡献文件且不受 Plastic 命令超时限制。
命令超时后请刷新核对工作区状态。

## 无界面 CLI

`TortoiseSCM.exe` 同时承担主 GUI 和命令入口的角色。加入 `--cli` 后，在初始化 WinForms 前分流；
参数错误、服务器错误和超时都不会打开对话框。GUI 与 CLI 共用 `PlasticClient`，CLI 不是绕过产品直接调用 cm 的测试脚本。

```powershell
$exe = '.\bin\TortoiseSCM\Release\TortoiseSCM.exe'
& $exe --cli --help | Out-String
& $exe --cli --command workspace --path 'D:\Work\Juscent\SCM_Study\TestSCM' --json | ConvertFrom-Json
& $exe --cli --command status --path 'D:\Work\Juscent\SCM_Study\TestSCM' --json | ConvertFrom-Json
& $exe --cli --command history --path 'D:\Work\Juscent\SCM_Study\TestSCM\README.md' --json | ConvertFrom-Json
& $exe --cli --command diff --path 'D:\Work\Juscent\SCM_Study\TestSCM\README.md' --json | ConvertFrom-Json
```

工作区写操作支持 `add`、`checkout`、`checkin`、`undo`、`update`、`rollback`、`switch`、`remove`、`move`、`ignore`，必须显式给 `--yes` 和至少一个 `--path`。
重复 `--path` 可传递多选；必须来自同一工作区，空选择不表示全库操作。
签入必须有 `--comment <说明>` 或 `--commentsfile <UTF-8文件>`。
添加、签出和撤销可带 `--recursive`，签入本身遵循 Plastic 的递归目录语义。
`--timeout <秒数>` 控制每个 cm 进程的超时，`--cm <绝对路径>` 可临时指定客户端。

完整工作区仅允许显式以工作区根路径执行更新，单文件/子目录更新会在执行前拒绝；Partial 可精确更新单项或多项。
实际类型通过 Plastic 状态头识别，不以可能仍显示 `Standard` 的本地 metadata 标记作为最终依据。
CLI `diff` 输出基础版本与本地版本的文本差异，二进制变化单独标记，不启动查看器。
`gluon` 仅支持 GUI 模式。`settings` 可从 CLI 查询或修改工具配置。

### 历史与恢复

```powershell
& $exe --cli --command history --path 'D:\workspace\Assets' --json | ConvertFrom-Json
& $exe --cli --command changeset --path 'D:\workspace' --changeset 42 --json | ConvertFrom-Json
# 恢复文件或目录为待提交更改，不改写服务器历史
& $exe --cli --command rollback --path 'D:\workspace\Assets' --changeset 42 --yes --json | ConvertFrom-Json
# 将整个工作区切换到历史快照，不产生回滚提交
& $exe --cli --command switch --path 'D:\workspace' --changeset 42 --yes --json | ConvertFrom-Json
# 在当前分支产生整仓回滚待提交更改，检查后再用 checkin 提交
& $exe --cli --command rollback --path 'D:\workspace' --changeset 42 --yes --json | ConvertFrom-Json
# 从历史快照回到当前分支最新版本
& $exe --cli --command update --path 'D:\workspace' --yes --json | ConvertFrom-Json
```

`changeset.data` 包含 `changeset` 元信息和 `files`（`status/path/oldPath/itemType`）。
仓库根历史包含各分支提交；目录历史通过提交文件路径筛选，也包括只有子文件内容变化的提交。
当前目录查询需要逐个读取仓库提交明细，大仓库首次查询可能较慢。
`rollback` 要求目标范围无待定更改，保留范围外更改；`switch` 要求整个工作区干净。
根目录 `rollback` 使用原生减法合并：仅支持干净且位于分支最新版本的 Standard 工作区，目标必须是父链祖先。
它保留分支选择器，生成待提交更改，不自动提交；发生冲突或无法识别的预检输出时拒绝执行。
`switch` 则切换历史快照；Partial 的切换只影响已加载内容，保留加载规则。
Partial 目录恢复仅支持目录结构一致的历史内容；涉及增删或移动时会在执行前拒绝，请使用 Standard 工作区完成这类恢复。
恢复语义遵循原生 [Unity Version Control REVERT](https://docs.unity.com/zh-cn/unity-version-control/uvcs-cli/revert)。
整仓回滚使用 [MERGE 的 subtractive interval](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/merge)。

### 历史文件比较与导出

```powershell
& $exe --cli --command diff-history --path 'D:\workspace' --item '/Assets/file.txt' --from 40 --to 42 --json | ConvertFrom-Json
# 重命名前后的文件使用不同路径
& $exe --cli --command diff-history --path 'D:\workspace' --from-item '/Assets/old.txt' --item '/Assets/new.txt' --from 40 --to 42 --json | ConvertFrom-Json
# 两个完整仓库快照之间的目录差异，不改变工作区
& $exe --cli --command diff-changesets --path 'D:\workspace' --from 40 --to 42 --json | ConvertFrom-Json
# 可增加 --external 调用设置的差异工具
& $exe --cli --command export --path 'D:\workspace' --item '/Assets/file.txt' --changeset 42 --output 'D:\temp\file.txt' --yes --json | ConvertFrom-Json
# 覆盖已存在的导出目标必须额外指定 --overwrite
```

`--item` 是以 `/` 开头的仓库路径，与本机工作区是否已加载该文件无关；可导出当前已被删除的文件的旧版本。
文件比较默认双方使用同一路径；`--from-item` 可单独指定起点路径，也适用于 `--external`。缺失端点明确报错，不伪装为空文件。
GUI 从“比较整个仓库”的固定快照差异列表打开新增/删除项时，Beyond Compare 可以显示明确空侧；启动前会重新读取该版本对的原生差异确认状态。移动项使用旧路径和新路径，标题标明各自版本。直接输入两个版本的普通历史比较仍要求两侧存在，不能据下载失败推断新增或删除。
`diff-changesets.data.files` 提供 `status/path/oldPath/itemType`；状态 A/C/D/M 分别为新增、修改、删除、移动，移动兼修改可返回两行，修改行也携带推导出的旧路径。`--path` 仅定位工作区，不限制差异范围；该命令只读，支持 Standard/Partial，无需下载工作树或 `--yes`。
比较使用 [原生 cm diff 的两个变更集参数](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/diff)，不是把期间每次提交明细相加：中间新增后删除的项不会出现。C 表示修订发生变化，最终文件字节仍可能相同，以打开文件后的内容比较为准。起点可晚于目标；同一变更集返回空列表。无法安全解析的路径或输出会报错，不返回不完整列表。
导出先下载并验证，再替换目标，服务器错误不会截断旧文件。目录、元数据和符号链接不作为文件导出。

### 分支浏览、切换与合并入口

从 Explorer “TortoiseSCM → 分支…”或主窗口“操作 → 分支…”打开分支列表，也可运行 `TortoiseSCM.exe --command branches --path D:\workspace`。
列表显示当前分支、最新变更集、作者、创建日期和说明，可筛选、刷新、取消加载；“头提交详情”读取选中分支最新一次提交的说明和修改文件，不表示分支的全部历史。分支右键菜单的“本分支历史”打开上下窗格历史窗口，仅显示该分支自身发布的提交，不包含继承自父分支的提交；仍可查看明细、比较及导出文件。
可在列表与“层级”视图之间切换。层级使用服务器返回的父分支关系，支持展开/折叠和定位当前分支；筛选时保留可见祖先作为上下文，并区分实际匹配项。祖先节点仍是可操作的真实分支，头提交、历史、创建、切换和合并均作用于当前选中的分支。缺失父分支时显示独立根节点及提示，不虚构可操作的父分支；重复或循环关系会报错。
该层级描述分支的父子组织关系，不表示提交之间的继承或合并边；创建时指定其他历史起点不会改变这一区别。
选择其他分支后，“合并到当前…”解析其最新变更集并打开现有合并窗口，固定来源版本。打开窗口只读取状态，仍须预检、确认开始合并、解决冲突及单独提交。

“切换工作区…”在确认后切换 Standard 或 Partial 工作区，包括从文件或子目录入口打开分支窗口的情况。要求工作区无待定更改、签出项、私有或忽略项，且没有未完成的合并/冲突会话；不会强制丢弃内容。Standard 更新整个工作区；Partial 使用原生 `partial switch` 更新加载范围，完整加载的目录会接收目标分支的新文件，排除文件和未选中的目录仍保持未加载。Partial 跨分支合并仍不开放。

Partial 切换前显示只读目录结构预览，按固定目标头提交的真实 ItemId/路径核对已加载目录。移动、删除、同路径替换，以及完整加载范围内新增目录会阻止继续；已加载单个文件移入未加载父目录同样拒绝。未加载范围且不涉及已选文件的新目录，以及普通文件内容变化仍可切换。预览只说明目录结构和加载范围，不是完整文件变更清单。被拦截时请取消，在 Gluon 中核对目标与加载范围后重新预览，程序不会自动卸载或重配置。

读取工作区模式和准备只读预览期间，可点击“取消预览”；此时点击关闭也会请求取消，窗口会等待查询结束后保持打开，允许重新预览。取消后的迟到结果不会打开确认或执行切换。确认开始切换后，须等待写入与结果核对完成。

GUI 确认固定预览时的目标 ID/GUID/父关系/头提交、selector、加载树和配置，变化后要求重新预览；直接 CLI 切换也会执行结构检查。加载规则缺失、身份或目录层级数据无法验证时拒绝执行。切换后再次核对目标分支、干净状态和加载规则。原生写入仍有并发窗口；即使命令已经执行，后检不一致也会报告结果未确认。应先刷新当前分支与状态，并在 Gluon 检查加载配置，确认后再明确决定下一步。程序不自动恢复旧元数据、重试、暂存、撤销或反向切换。参见 [Unity Partial switch](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/partial-switch)。

选择非当前叶子分支后，可使用“重命名…”按钮或右键菜单填写新的短名称，并确认仓库、原/新完整名称、父分支和头提交。Standard/Partial 均支持；不切换、更新或签入工作区，保留本地待定内容。根分支、当前 selector 引用的分支、有子分支、身份缺失和名称与父关系不一致的分支不可重命名。过滤列表不会隐藏子分支保护。重命名会影响其他用户按名称保存的引用。

执行前核对原生分支 ID、GUID、父关系及头提交，执行后验证同一身份出现在新名称且旧名消失。原生命令按名称执行，预检与服务器写入不能组成原子事务；其他客户端并发修改仍可能导致结果不确定。失败或取消后应刷新核对，不自动重试或反向重命名。命令契约见 [Unity branch CLI](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/branch)。

从选中分支的右键菜单创建子分支：默认起点为打开对话框时解析的固定头提交，可指定历史变更集；填写子分支短名称和非空说明后创建。创建只修改服务器分支信息，不签入、不切换工作区、不加载文件，Standard 和 Partial 均可使用。创建成功后可单独执行“切换工作区…”。若网络故障、取消或验证失败，服务器分支可能已经创建，应先刷新列表核对，不自动删除或重试。

列表和层级树右键的“删除空分支…”可删除非当前空叶分支，支持 Standard/Partial。确认窗口显示仓库、名称、父分支及继承头提交，默认取消。空分支仍有继承的头提交，程序通过原生查询确认它没有自身发布的提交；有子分支、分支属性或身份不完整时拒绝操作。任何以该继承头提交为父版本的暂存集都会阻止删除，包括在其他分支创建的暂存集，这是保守保护。名称或仓库含单引号等无法安全构造查询的字符时不支持删除。

删除只操作选定分支，不删除提交、标签、暂存集或其他分支，不切换或清理工作区。执行前再次核对身份与引用，成功后核对名称、ID、GUID 均已消失；失败或取消后需刷新核对结果，不自动重试或重建。其他用户保存的名称引用无法检查，删除后可能失效；原生按名称写入仍有并发窗口。原生命令见 [Unity branch delete](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/branch-delete)。

```powershell
& $exe --cli --command branches --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command branch-tree --path 'D:\workspace' --filter 'task' --json | ConvertFrom-Json
$head = & $exe --cli --command branch-head --path 'D:\workspace' --branch '/main/task' --json | ConvertFrom-Json
& $exe --cli --command merge-preview --path 'D:\workspace' --changeset $head.data.changeset --json | ConvertFrom-Json
# Standard/Partial 分支切换均需干净工作区、显式根目录和确认
& $exe --cli --command switch-branch --path 'D:\workspace' --branch '/main/task' --yes --json | ConvertFrom-Json
# Partial 专用只读目录预览，返回 canSwitch 和 directories；不接受 --yes
& $exe --cli --command partial-switch-preview --path 'D:\workspace' --branch '/main/task' --json | ConvertFrom-Json
# 创建子分支，固定历史起点；不自动切换，说明也可从 --commentsfile 读取
& $exe --cli --command create-branch --path 'D:\workspace' --branch '/main/new-task' --changeset 123 --comment '新任务' --yes --json | ConvertFrom-Json
# 先查看并确认分支身份和头提交，再重命名；--new-name 只接受短名称
$rows = (& $exe --cli --command branches --path 'D:\workspace' --json | ConvertFrom-Json).data.branches
$branch = $rows | Where-Object name -eq '/main/new-task'
& $exe --cli --command rename-branch --path 'D:\workspace' --branch $branch.name --branch-id $branch.branchId --branch-guid $branch.guid --changeset $branch.headChangeset --new-name 'renamed-task' --yes --json | ConvertFrom-Json
# 删除前重新列出并审阅空分支身份；有自身提交时拒绝，不删除历史
$rows = (& $exe --cli --command branches --path 'D:\workspace' --json | ConvertFrom-Json).data.branches
$emptyBranch = $rows | Where-Object name -eq '/main/unused-empty-task'
& $exe --cli --command delete-branch --path 'D:\workspace' --branch $emptyBranch.name --branch-id $emptyBranch.branchId --branch-guid $emptyBranch.guid --changeset $emptyBranch.headChangeset --yes --json | ConvertFrom-Json
# 可与文件/目录 --path 叠加；按 nextBeforeChangeset 继续分页直到 hasMore=false
& $exe --cli --command history-page --path 'D:\workspace' --branch '/main/new-task' --limit 50 --json | ConvertFrom-Json
```

分支列表的 `data.branches` 提供 `name/branchId/guid/parent/owner/creationDate/comment/repository/headChangeset/isCurrent`。旧版输出缺少身份时仍可浏览，但不能重命名或删除。
`partial-switch-preview` 返回 `headChangeset/canSwitch/loadedDirectoryCount/loadingRuleCount/isFullyLoaded/directories`；每个目录含 `path/targetPath/itemId/change/reason`。成功读取但被结构保护拦截时仍返回成功退出码，必须检查 `canSwitch=false`；查询错误或工作区变化则报错。CLI 预览与后续切换是两个独立调用，切换会重新检查当前目标，不把上一份 JSON 当作执行授权。
`branch-tree` 只读，支持 Standard 和 Partial；`data.nodes` 是父节点在前的扁平序列，在分支字段之外包含 `depth/isMatch/parentMissing/childCount`，`data.filter` 为过滤条件。`childCount` 为未过滤数据中的直接子分支数；保留祖先的 `isMatch=false`，缺失父分支的根节点 `parentMissing=true`。`--filter` 按名称、作者、日期、说明或头提交匹配；不接受写操作确认 `--yes`。
分支历史沿用有界扫描和游标，每次最多扫描 `--limit` 个仓库提交，再按分支与路径取交集。空页不代表结束：只要 `hasMore=true`，就可使用 `nextBeforeChangeset` 继续读取；`data.branch` 标明当前过滤条件。分支名精确匹配，不解释为查询表达式。
`--branch` 使用完整分支名，不含 `br:` 前缀或仓库后缀；切换使用 [原生 cm switch](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/switch)，不创建提交。切换失败或超时后应先刷新核对状态，不自动回滚或撤销可能已完成的部分操作。

### 删除、移动与忽略

```powershell
& $exe --cli --command remove --path 'D:\workspace\old.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command move --path 'D:\workspace\old.txt' --destination 'D:\workspace\renamed.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command ignore --path 'D:\workspace\generated' --yes --json | ConvertFrom-Json
```

删除 / 移动仅用于已提交的受控项，禁止工作区根目录、跨工作区、目标覆盖和有待定更改的范围。
GUI 在“操作”菜单或待定列表右键提供同名入口；干净文件可通过 Explorer 从该文件打开后操作。
忽略配置采用 [Plastic 原生 ignore.conf 精确路径规则](https://docs.unity.com/en-us/unity-version-control/config-files/filter-pattern)，
目录规则覆盖后代，已有受控文件不会因此取消跟踪。需要共享规则时可自行添加、提交 ignore.conf。

### Beyond Compare 比较与合并

图形比较与合并工具统一使用 Windows Beyond Compare 4/5。三方文本合并需要 Pro 授权。安装包可通过 `-BeyondCompareDirectory` 携带用户提供的 BC 运行文件及其独立许可说明，个人授权文件不随包分发。程序不修改 BC 全局设置；自动模式先查找当前程序目录的 `Tools\BeyondCompare`，再查找常见安装目录和注册表，也可在设置的“差异查看器”或“合并工具”中选择 BComp.exe；两页共用同一路径，留空或点击“自动检测”保持自动模式，升级后使用新版本目录中的 BC。显式自定义路径仍优先，失效时会提示。选择 BCompare.exe 时会转为同目录的 BComp.exe。找不到程序会明确提示，不回退到其他编辑器；状态、历史查询和提交等不依赖 BC 的操作仍可使用。

工作文件基线按 Plastic ItemId 定位，支持受控文件本地改名后的比较；启动前重新核对工作区 selector、仓库和文件身份。历史主“Beyond Compare”按钮下载固定端点，暂存集逐文件比较下载父版本和暂存版本。比较两侧只读；三方左侧为本地、右侧为远程、祖先为基线，输出为独立结果文件。参数由程序固定管理，无需填写模板。输入角色及等待方式依据 [Beyond Compare 官方集成说明](https://www.scootersoftware.com/kb/vcs) 和 [命令行文档](https://www.scootersoftware.com/v5help/command_line_reference.html)。

每次启动采用 BComp.exe 与独立实例选项，等待本次窗口关闭后才清理比较临时文件。人工编辑不受 Plastic 命令超时限制；若请求取消，先在 BC 中保存或放弃并关闭窗口，TortoiseSCM 才结束等待，避免杀掉未保存编辑。等待器异常（102）会报告并保留贡献目录；此时先关闭对应 BC 窗口再重试，不要同时应用或重新编辑结果。

保存并关闭 BC 后，必须回到 Standard 或 Partial 冲突窗口核查并明确应用，最后另行签入。工具正常退出只表示窗口关闭，不证明已保存或已解决冲突；不根据退出码自动应用。原有备份、身份校验、输入/输出别名保护和恢复流程继续有效。

重新启动 BC 三方合并会从三个贡献文件重新组合输出，不会像旧内置编辑器那样直接载入此前手工保存的结果。已有满意结果可直接在冲突窗口确认应用；再次启动并保存之前，请另存需要保留的手工结果。此行为见 [Scooter Software 关于合并输出恢复的说明](https://forum.scootersoftware.com/forum/beyond-compare-discussion/general-aa/92039-text-merge-potential-improvements-options-for-vcs-purposes)。

工作文件比较支持已添加（AD）、受控删除（DE）和本地删除（LD）的空侧：新增时左侧为空，删除时右侧为空，窗口标题标明对应状态。启动前重新核对原生状态、基线版本、工作区和文件身份。DE 已从本地树移除，只有已加载版本与内容哈希能唯一定位历史文件时才比较；同版本同内容的多个文件、待定父目录结构变化、删除路径重新出现文件等情况会拒绝并提示核对。私有、忽略、目录和链接不作为文件比较；下载失败不会被解释为空侧。Standard 与 Partial 均使用这些规则。

BC 自身负责文本编码、行尾、差异显示和交互合并；旧内置编辑器的 2 MiB / 20,000 行限制不套用到 BC，Plastic 内容准备及操作范围限制仍然有效。内置编辑器源码和回归资产保留，但设置、历史主比较和暂存集逐文件比较入口均使用 BC。真实 BC 保存/放弃及工具并行会话验收见路线图；多 DPI 已排除。

旧配置加载后统一使用 BC，原内置/外部字段保留为停用配置。已有明确 BC 路径优先迁移（先旧合并路径、再旧比较路径），失效路径会报告，不偷偷替换安装位置。CLI `settings` 报告 `toolProvider=BeyondCompare`；`--beyond-compare` 配置共用路径，旧的独立工具/参数选项会提示迁移，不能切回其他工具。普通 CLI `diff` 继续输出文本，`--external` 才打开 BC：

```powershell
& $exe --cli --command settings --json | ConvertFrom-Json
& $exe --cli --command settings --beyond-compare 'C:\Program Files\Beyond Compare 5\BComp.exe' --yes --json | ConvertFrom-Json
& $exe --cli --command diff --path 'D:\workspace\file.txt' --external --json | ConvertFrom-Json
& $exe --cli --command merge --base 'D:\tmp\base.txt' --local 'D:\tmp\local.txt' --remote 'D:\tmp\remote.txt' --output 'D:\tmp\merged.txt' --yes --json | ConvertFrom-Json
```

设置窗口的“打开合并工具”允许选择三个输入和独立输出，此入口只编辑文件。
待定更改窗口的“操作 → 合并变更集 / 解决冲突”提供完整工作区的分支合并流程：预检、开始、三方编辑、确认解决，然后返回待定更改提交。工具退出本身不会标记冲突解决。
工具通过独立参数调用，不经过命令解释器。`--settings-file <文件>` 可使用隔离配置；进行分支合并或准备 Partial 冲突时，该配置文件及相邻会话目录必须放在工作区外，避免操作影响自身的恢复资料或将备份误加入提交。

### 分支合并与锁

```powershell
& $exe --cli --command merge-preview --path 'D:\workspace' --changeset 123 --json | ConvertFrom-Json
& $exe --cli --command merge-start --path 'D:\workspace' --changeset 123 --yes --json | ConvertFrom-Json
& $exe --cli --command merge-status --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command merge-prepare --path 'D:\workspace' --changeset 123 --item '/file.txt' --yes --json | ConvertFrom-Json
# 将 base/local/remote 交给已配置的工具；该命令不会标记解决
& $exe --cli --command merge-conflict-tool --path 'D:\workspace' --changeset 123 --item '/file.txt' --yes --json | ConvertFrom-Json
# 检查结果后，单独应用；随后使用常规 checkin 提交
& $exe --cli --command merge-resolve --path 'D:\workspace' --changeset 123 --item '/file.txt' --result 'D:\tmp\reviewed.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command locks --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command unlock --path 'D:\workspace' --lock-id '00000000-0000-0000-0000-000000000000' --yes --json | ConvertFrom-Json
```

合并开始要求干净的 Standard 工作区，并在原生 Plastic 合并状态下处理文件冲突。会话保存在当前用户的本地应用数据目录，可以跨进程恢复。
合并结果需保存在独立文件；应用前核验冲突文件是否被其他程序修改。未解决或中断状态不能直接由本程序签入。
合并和整仓回滚必须整体提交：点击“提交”后会明确确认整个工作区的受控更改范围；CLI 使用根目录 `checkin`。
放弃这类操作时，可用“操作 → 撤销整个合并 / 回滚”，或根目录 `undo --yes`。失败的整仓回滚同样保留会话，不能绕过检查直接发布。
Standard 目录结构冲突先建立规划会话，在“结构冲突…”中逐项选择来源或目标；同名新增冲突还可重命名目标以保留双方。记录选择不会修改工作区文件，全部选择后点击“应用结构方案”，然后处理剩余内容冲突并整体提交。未应用的方案可单独取消，取消不会撤销用户文件。
当前逐项处理同名新增（EVIL）、双方移动到不同位置（DIV_MV）、修改/删除（CHG_RM、RM_CHG），以及移动/删除（MV_RM、RM_MV）、移动到同名项（MV_EVIL）、新增/移动（ADD_MV、MV_ADD）。新类型提供采用来源或目标；重命名保留双方仍限定已验证的 EVIL。无冲突目录移动和删除也支持，执行前检查待定、私有和被忽略的后代以及工作区是否发生变化；未知原生冲突类型明确拒绝。

```powershell
& $exe --cli --command merge-directory-resolve --path 'D:\workspace' --changeset 123 --conflict 1 --resolution rename --rename 'local-copy.txt' --yes --json | ConvertFrom-Json
# --conflict 使用当前会话返回的稳定 index；其他选择为 src / dst
& $exe --cli --command merge-continue --path 'D:\workspace' --changeset 123 --yes --json | ConvertFrom-Json
& $exe --cli --command merge-directory-cancel --path 'D:\workspace' --yes --json | ConvertFrom-Json
```

Partial 工作区通过“操作 → Partial 传入冲突…”处理已加载文件的本地内容修改与服务器新版本冲突。预检后准备基础、本地、传入三方文件，调用 Beyond Compare；审核结果后单独确认应用。后端只更新该文件的基础修订，再写入审核结果，保持 Partial 模式、分支和加载规则，不自动提交。

```powershell
& $exe --cli --command partial-conflicts --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command partial-conflict-prepare --path 'D:\workspace' --item '/file.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command partial-conflict-tool --path 'D:\workspace' --item '/file.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command partial-conflict-resolve --path 'D:\workspace' --item '/file.txt' --result 'D:\tmp\reviewed.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command partial-conflict-status --path 'D:\workspace' --json | ConvertFrom-Json
# 仅取消尚未应用的准备；不改动工作区内容
& $exe --cli --command partial-conflict-cancel --path 'D:\workspace' --yes --json | ConvertFrom-Json
```

Partial 内容处理中断时会保留原始和审核结果备份，并阻止提交；`partial-conflict-status` 返回恢复目录。保留需要的备份后，可显式撤销受影响文件、刷新并重新处理。准备期间本地内容、服务器版本或文件身份变化会拒绝旧结果。成功应用后可继续编辑，再按常规勾选提交。

### Partial 文件结构冲突

从“操作 → Partial 文件结构冲突…”或内容冲突窗口中的“结构冲突…”进入。先预检、准备备份，再显式选择处理方式。单次会话处理一个文件；不会自动提交。同名新增的传入项尚未加载时，仅加载明确选中的文件，不更新父目录。

| 冲突 | 支持的选择 |
| --- | --- |
| 双方新增同名文件 | 采用传入、本地内容覆盖传入，或本地另存新名称保留双方 |
| 服务器删除，本地修改 | 采用删除，或将本地内容作为新增重新加入；可另存新名称 |
| 本地删除，服务器修改 | 接受传入，或在新的基础上继续删除 |
| 本地重命名或跨目录移动，服务器修改 | 接受传入，或保留本地位置/另选位置；纯移动保留服务器内容，本地同时编辑时显式采用本地内容 |
| 服务器移动文件，本地修改内容 | 跟随服务器的新路径，选择采用服务器内容或在新位置保留本地内容 |

准备会固定文件身份、服务器变更集和本地内容，并保存备份。应用前重新核验，过期准备会拒绝执行。未完成会话会阻止普通写操作，避免与更新、撤销、回滚交错。未应用准备可取消；失败后使用“恢复为传入版本”，将受影响文件恢复到固定传入状态，原始备份及恢复时的文件内容均保留。此恢复不会重新提交本地内容，可从备份取回后另行处理。

本地跨目录移动要求源目录、目标目录及其祖先已加载、受版本控制，且身份与服务器一致；不会自动创建目录或更新父目录。选择新位置时，单独文件名相对于本地移动后的目录，也可填写 `/目录/文件名`。服务器移动则仅卸载原文件、加载服务器新路径，保持文件身份、选择器和加载规则的含义；原路径不再保留，规则记录的行顺序可能由原生客户端重排。

```powershell
& $exe --cli --command partial-structure-preview --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command partial-structure-prepare --path 'D:\workspace' --item '/file.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command partial-structure-status --path 'D:\workspace' --json | ConvertFrom-Json
# 选择必须来自 preview/session 的 resolutionOptions
& $exe --cli --command partial-structure-resolve --path 'D:\workspace' --resolution rename --rename 'local-copy.txt' --yes --json | ConvertFrom-Json
# 仅本地移动冲突可选择其他已加载受控目录
& $exe --cli --command partial-structure-resolve --path 'D:\workspace' --resolution rename --rename '/Assets/Reviewed/local.txt' --yes --json | ConvertFrom-Json
# 其他选择为 keep-local / take-incoming；成功后回到常规待定更改检查与提交
& $exe --cli --command partial-structure-cancel --path 'D:\workspace' --yes --json | ConvertFrom-Json
# 仅用于应用中断；采用会话固定的传入状态，保留备份
& $exe --cli --command partial-structure-recover --path 'D:\workspace' --yes --json | ConvertFrom-Json
```

此文件窗口处理普通文件；目录变化使用下述独立目录窗口。服务器移动同时叠加本地移动/删除、仅大小写变化的服务器改名、路径被不同身份文件替换、Xlink 与符号链接仍会拒绝或显示不支持原因。
原生已删除状态（DE）的文件不再出现在本地身份列表中。本程序仅在已加载版本和内容校验值能唯一确定历史文件身份时处理；同一版本有多个内容相同的文件时会拒绝自动处理，保留待定删除。仅从磁盘删除的缺失状态（LD）仍可读取受控身份，不受此限制。
即使选中单文件，原生 Partial 更新也可能处理已删除目录。因此准备和应用冲突前会检查已加载目录在目标版本中的身份；遇到目录删除、移动或替换，会指出具体目录并要求先单独处理，不自动扩大更新范围。内容冲突处理也使用此检查。原生目录更新的递归行为见 [PARTIAL UPDATE](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/partial-update)。

### Partial 目录冲突

从“操作 → Partial 目录冲突…”进入。上栏列出服务器移动或删除的已加载目录，下栏显示该目录及全部已加载后代的原路径、传入路径和本地修改标记。单次处理一棵目录，必须先准备整树备份，再明确选择处理方式；不会默认选择或自动提交。

- 服务器移动：可采用服务器目录和内容，或跟随新位置并保留本地已修改文件的内容。原来未修改的文件仍采用服务器版本，目录与后代的 ItemId 保持。
- 服务器删除：可采用删除，或在原路径保留整棵本地树。选择保留时会从备份重新添加全部文件和空目录，形成新身份的待提交项；旧身份及其历史不会恢复，必须在待提交界面检查后另行提交。原始内容保留在恢复备份中。
- 本轮要求目录子树完整加载，支持工作区全量加载模式和显式目录加载规则，且服务器移动保持相同后代结构。含未加载后代、私有/忽略项、待定增删移动、链接、嵌套工作区、不同身份替换或仅大小写移动时拒绝。同次存在其他已加载目录结构变化也暂不支持；预检会显示具体原因，不自动扩大加载范围。

```powershell
& $exe --cli --command partial-directory-preview --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command partial-directory-prepare --path 'D:\workspace' --item '/Assets/OldFolder' --yes --json | ConvertFrom-Json
& $exe --cli --command partial-directory-status --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command partial-directory-resolve --path 'D:\workspace' --resolution keep-local --yes --json | ConvertFrom-Json
# 另一选择为 take-incoming；prepare 返回的 resolutionOptions 决定该目录可用的选择。
# 对服务器删除的目录，keep-local 表示在原路径重新添加本地树，不自动提交。
# JSON 的 resolutionDetails 说明每个选项的效果，readdsAsNewItems 标明是否创建新身份。
# 未应用的准备可取消；已中断的处理必须显式恢复：
& $exe --cli --command partial-directory-cancel --path 'D:\workspace' --yes --json | ConvertFrom-Json
& $exe --cli --command partial-directory-recover --path 'D:\workspace' --yes --json | ConvertFrom-Json
```

目录会话持久记录在工作区元数据中，换配置文件或重启不会绕过保护。准备和应用分离，旧选择不能覆盖后续编辑；中断后阻止其他修改操作，状态命令返回恢复目录。显式恢复会先另存已知路径的新编辑；新出现的其他文件或变化的服务器结构会阻止恢复并保留现状，需要先处理这些变化。整树卸载/加载使用原生 [PARTIAL CONFIGURE](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/partial-configure)，不会隐式更新父目录。
保留已删除目录的重建过程若中断，“恢复为传入版本”会先备份已知路径的新内容，再撤销本次添加并移除重建的树，回到服务器删除状态；不会继续签入或恢复旧身份。服务器已在原路径创建新项、出现未知后代或其他结构变更时，会保留会话并拒绝覆盖。

### 锁管理

锁列表限定当前仓库；只有当前用户、当前工作区持有的锁才允许释放，执行前再次读取核验。
锁获取遵循服务器规则：普通签出并不保证获得锁。本程序不修改服务器锁规则，服务器权限仍决定能否释放。

`--json` 的 stdout 为无 BOM UTF-8 单个对象，包括失败：

```json
{"schemaVersion":1,"command":"status","success":true,"exitCode":0,"output":"","error":"","data":{"workspace":{},"entries":[]}}
```

状态条目包含 `path/oldPath/status/description/isDirectory`；历史包含 `changeset/owner/branch/comment/revisionSpec`；
差异包含 `path/baseRevision/diffText/isBinary/hasChanges`。
退出码为 `0` 成功、`1` SCM/运行错误、`2` 参数或范围错误、`124` 超时。
本程序为 Windows GUI 子系统：PowerShell 使用管道等待输出；自动化程序使用 `ProcessStartInfo` 重定向 stdout/stderr 并等待退出即可。

分页历史用于大仓库和路径事件历史；原 `history` 命令保留其兼容行为：

```powershell
& $exe --cli --command history-page --path 'D:\workspace\directory' --limit 50 --json | ConvertFrom-Json
# 使用上一页返回的 nextBeforeChangeset 作为 --before；编号为排除上界
& $exe --cli --command history-page --path 'D:\workspace\directory' --before 123 --limit 50 --json | ConvertFrom-Json
```

`history-page` 返回 `entries/scannedChangesets/hasMore/nextBeforeChangeset`。某页没有路径匹配记录并不意味着历史结束，必须检查 `hasMore`。
单页限制是扫描的提交数，不保证有等量的匹配记录。`--limit` 范围 1–100；查询可取消，GUI 内复用有容量限制的不可变提交明细缓存。

## 代码结构

```
Explorer
  └─ src/TortoiseShell/PlasticShell.cpp       原生 COM 菜单，仅做本地工作区探测
       └─ UTF-8 临时路径文件
            └─ src/TortoiseSCM/Program.cs      独立程序入口
                 ├─ MainForm.cs               中文待定更改与操作窗口
                 ├─ HistoryForm.cs            提交 / 文件明细与历史恢复
                 ├─ SettingsForm.cs           客户端与 Beyond Compare 设置
                 ├─ ToolLaunchForm.cs         外部三方合并入口
                 ├─ MergeForm.cs              分支合并预检与文件冲突处理
                 ├─ LocksForm.cs              仓库锁列表与自己持有锁的释放
                 ├─ CliRunner.cs              无界面命令、JSON 与退出码
                 └─ Core/                     Plastic 命令、XML 状态与进程后端
                      └─ cm.exe / gluon.exe
```

菜单保留 TortoiseGit/TortoiseSVN 的“薄 shell + 独立 GUI 进程”方式，复用仓库图标。
新 shell 不把 .NET 或 Plastic 命令执行装入 Explorer；不会在右键菜单展开时运行服务器请求。
Git 后端、Git 状态缓存和原 GUI 暂留在上游工程，不能用于 Plastic 工作区。

## 验证

基础 GUI 服务器验收可运行 `test/TortoiseSCM/Run-BasicGuiAcceptance.ps1`（也已接入 `build-tortoisescm.ps1 -Integration`）。它分别创建隔离 `tortoisescm-autotest-*` 分支，测试 Standard/Gluon 的添加和删除后签入、历史比较与回滚、目录历史和更新，并保留截图及原生命令证据；需要已配置并登录的 Plastic 客户端。可选 `-BeyondComparePath '路径\BComp.exe'` 验证真实只读比较窗口正常打开和关闭。真实桌面 Explorer 鼠标点击、状态覆盖图标和三方人工编辑仍需单独验收。

`build-tortoisescm.ps1 -Test` 编译并运行无服务器的后端测试、真实 EXE 的 CLI 黑盒测试、Shell 测试和设置窗口渲染。
加入 `-Workspace 'D:\Work\Juscent\SCM_Study\TestSCM'` 会执行实际 cm 集成测试和待定更改窗口渲染。
集成测试仅创建专用临时文件，测试添加/撤销后清理，不提交服务器，也不修改现有文件。
渲染图片位于 `bin/TortoiseSCM/qa/Release`。
注册后可运行 `bin/TortoiseSCM/Release/ShellTests.exe --registered` 验证真实 COM DLL 加载。
本次结果与未验证范围见 [验证记录](TortoiseSCM-validation.md)。

```powershell
# 完整服务器测试：会在 TestSCM 中创建专用测试分支和三个独立工作区，并真实签入小型测试文件
.\build-tortoisescm.ps1 -Integration
```

该选项从 TestSCM 的空 `cs:0` 创建唯一测试分支，建立 producer、consumer、partial 工作区；
不会切换原有 TestSCM 的分支。仓库规格从原工作区解析，脚本严格限定仓库名 `TestSCM`。
每次运行在 `bin/TortoiseSCM/qa/integration-*` 保存 manifest、命令记录及 `cli-integration-results.json`；
`latest-integration.txt` 指向最近一次的 manifest。测试分支和工作区保留便于复查，不合并到 `/main`。

## 当前边界

### 暂存集（Shelvesets）

在待定更改窗口勾选受控文件，选择“操作 → 保存勾选项为暂存集”，填写说明并确认。
暂存集保存在服务器，保存后本地文件修改内容仍保留，也不会产生签入。Partial 工作区的原生 `--applychanged` 可能把待定状态从 `CH` 改成 `CO`，所以这里只承诺内容保留，不承诺状态文字不变。当前只接受明确勾选的文件，不接受目录递归选择、私有/忽略项或未选择的结构依赖；不要把保存成功当作已经丢弃本地工作。

Explorer “TortoiseSCM → 暂存集…”及主窗口“操作 → 暂存集…”打开原生上下窗格：上方为当前仓库的暂存集，下方为选中项的更改文件，含移动前路径。列表显示整个仓库，不按入口目录筛选。支持说明/作者/编号筛选、刷新与取消加载。

```powershell
& $exe --cli --command shelve-create --path 'D:\workspace\src\a.cs' --path 'D:\workspace\src\b.cs' --comment '保存进行中的修改' --yes --json | ConvertFrom-Json
& $exe --cli --command shelves --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command shelve-details --path 'D:\workspace' --shelve 12 --json | ConvertFrom-Json
& $exe --cli --command shelve-apply --path 'D:\workspace' --shelve 12 --yes --json | ConvertFrom-Json
& $exe --cli --command shelve-delete --path 'D:\workspace' --shelve 12 --yes --json | ConvertFrom-Json
& $exe --cli --command shelve-diff --path 'D:\workspace' --shelve 12 --json | ConvertFrom-Json
& $exe --cli --command shelve-export --path 'D:\workspace' --shelve 12 --output 'D:\exports\shelve-12' --yes --json | ConvertFrom-Json
```

`--commentsfile` 可代替 `--comment`。创建支持 Standard 和 Partial 对应原生命令；失败或超时后，服务器可能已经保存，请先刷新列表确认，程序不会自动重试。
列表返回 `data.shelves`（`shelveId` 为公开的 `sh:` 编号，`objectId` 为服务器内部对象编号），详情返回 `data.files`。
加载文件明细后，GUI 底部按钮和右键菜单提供应用、删除、比较、导出。写入必须确认，进行中不能关闭或重复提交。应用前要求仓库及选择器匹配、Standard 工作区、无待定更改（包括私有/忽略项）；Partial/Gluon 应用明确拒绝。GUI 确认显示目标工作区；CLI 写操作必须显式 `--yes`，失败或验证不确定时要求检查状态。应用不会自动删除暂存集，也不会自动解决冲突。

GUI 选择下方的普通文件或二进制文件后，点击“比较”、双击或使用右键即可打开 Beyond Compare。左侧为暂存集父变更集，右侧为暂存内容；新增/删除以原生状态证明的空侧显示，移动兼修改使用旧/新路径。目录和链接不能作为单个文件比较。启动前复核暂存集身份、父版本、文件明细和工作区；工具关闭前不能关闭所属窗口，取消也要等待工具结束。CLI 的文本差异输出保留，Standard/Partial 均支持只读访问。

导出只保存暂存集中存在的更改文件，保留仓库相对路径，并写入 `shelveset.manifest`（含删除记录）；不是完整仓库快照或可直接应用的补丁。目录新增/移动、链接等尚不支持导出。输出目录必须位于 Plastic 工作区之外，父目录须存在；已有文件默认拒绝覆盖，CLI 必须额外提供 `--overwrite`，GUI 会再次说明覆盖。下载完成后才写目标；新目录整体移动，已有目录逐文件替换，中途失败可能已写出部分文件，需检查后再重试。

### 本轮五项需求对照

| 需求 | GUI | CLI | 限制 |
| --- | --- | --- | --- |
| 文件 / 目录更新、历史、恢复 | 更新、历史 / 范围历史内恢复按钮 | `update`、`history`、`rollback --changeset` | 精确范围更新需要 Partial；Standard 明确要求整体更新 |
| 整仓更新、历史、历史版本 | 根目录打开，历史内分别回滚待提交和切换快照 | 根路径 `update`、`history`、`rollback`、`switch` | 整仓待提交回滚仅 Standard；Partial 快照仅处理已加载项 |
| 目录范围提交、勾选及右键 | 按范围显示，勾选签入，右键历史 / 差异 / 丢弃 | `status --path` 获取范围，重复 `--path` 提交所选项，`history` / `undo` | 目录递归操作包含后代；私有文件需先添加 |
| 上下窗格历史明细 | 上方自动完整加载提交、下方完整提交文件清单 | `history-page` / `history` + `changeset --changeset` | GUI 自动查询至全部；CLI 路径过滤分页可能为空，需继续至 hasMore=false |
| Beyond Compare 比较 / 合并 | 设置窗口、差异按钮、结构选择、三方编辑与确认解决 | `settings`、`diff --external`、`merge`、`merge-*`、`partial-conflict-*` | 三方文本合并需 BC Pro；分支合并仅 Standard；Partial 内容冲突限同一文件身份 |

这是可继续演进的开发版本，不是 TortoiseGit 全功能等价移植。
更完整的逐项差距与推进顺序见 [TortoiseGit 功能对照](TortoiseSCM-parity.md)。
尚未提供：有历史分支的级联删除、当前/根/有子分支的重命名、Partial 跨分支合并及被拦截加载结构变化的处理/恢复向导、上述范围之外的 Partial 目录冲突处理、拖放移动、仓库创建。

范围外：签名 MSI、自动更新、语言包、ARM64 与 32 位 Explorer，以及多 DPI、跨重启提交失败草稿恢复；这些不作为完成条件。现有 x64 安装与卸载继续维护。当前拒绝符号链接、junction 和跨嵌套工作区的递归写操作。
高级操作通过官方客户端完成。
部分工作区使用 `cm partial` 的对应命令，完整与部分工作区的行为不能混同；实际测试结果见交付说明。

参考：[Unity Gluon 签入说明](https://docs.unity.com/unity-version-control/gluon/check-in-changes)、
[Unity Gluon 添加新项](https://docs.unity.com/en-us/unity-version-control/gluon/upload-new-items)、
[Plastic CLI 指南](https://docs-plasticscm.azurewebsites.net/cli/plastic-scm-version-control-cli-guide)。

### Annotate / Blame

The Explorer `TortoiseSCM -> Annotate / Blame...` command and the GUI operations menu accept one existing controlled file. The read-only view shows line number, owner, changeset, date, branch and content; double-click or the context menu opens that file's history. Directories, missing files and Plastic binary files are rejected.

```powershell
& $exe --cli --command blame --path 'D:\workspace\src\a.cs' --json | ConvertFrom-Json
& $exe --cli --command blame --path 'D:\workspace\src\a.cs' --ignore eol --json | ConvertFrom-Json
```

`data.lines` contains `line/owner/changeset/date/branch/content` plus revision and repository metadata. The command never changes the workspace; comments that cannot be represented unambiguously by Plastic's native format are rejected rather than assigned to the wrong line.
