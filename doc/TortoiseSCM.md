# TortoiseSCM 开发版本

TortoiseSCM 是面向 Windows Explorer 的 Plastic SCM / Unity Version Control 客户端。
这次移植建立了独立的 Plastic 运行入口，没有将 Git 命令简单改名后继续执行。

## 构建与运行

要求 Windows x64、.NET Framework 4.8、Visual Studio 2022 或更新版本的 C++ 桌面开发组件与 Windows SDK。
使用新的解决方案 `src/TortoiseSCM.sln`。构建不依赖 Git、libgit2、MFC 或 NuGet；保留的上游解决方案仍有其原依赖。

```powershell
cd D:\Work\Juscent\SCM_Study\TortoiseSCM
.\build-tortoisescm.ps1 -Test
.\bin\TortoiseSCM\Release\TortoiseSCM.exe --path 'D:\Work\Juscent\SCM_Study\TestSCM'
```

也可以双击 `TortoiseSCM.exe`，在目录选择器中选择 Plastic 工作区。`--command settings` 打开客户端路径设置。

运行时复用 Plastic 客户端现有的用户配置和认证。新程序不储存密码，也不要求安装 Git。
首次使用会发现标准安装目录和本机 `D:\Program Files\PlasticSCM5\client`；也可在“设置”中指定路径。

### 可安装包

```powershell
.\contrib\tortoisescm\Package.ps1 -Version '0.2.0-preview'
```

产物位于 `bin/TortoiseSCM/packages`：Windows x64 ZIP、SHA-256 校验文件，ZIP 内含逐文件校验清单。
解压后运行 `Install.ps1`，默认安装到 `%LOCALAPPDATA%\Programs\TortoiseSCM` 并注册当前用户右键菜单。
`Install.ps1 -WhatIf` 可预览；需要状态图标时，在管理员 PowerShell 使用 `Install.ps1 -EnableMachineOverlays`。
每次安装使用新版本目录，不覆盖 Explorer 已加载的 DLL。旧版本保留，注册失败时恢复原注册。
运行 `Uninstall.ps1` 卸载当前版本；有机器级图标注册时需管理员执行并添加 `-RemoveMachineOverlays`。
卸载仅清理属于该包且哈希未变化的文件，保留设置、工作区和用户新增/修改的文件。被锁定的文件会保留，注销后可重试。
包当前未数字签名，也不提供自动更新。

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

Explorer 会保持已加载 DLL 的文件锁。如替换 DLL 时失败，先注销当前用户再登录；脚本不会强制结束 Explorer。
卸载注册不会删除工作区、设置或程序文件。

### 状态图标与后台缓存

提供正常、修改、冲突三个图标，目录会汇总受控子项的状态。未版本控制、忽略和未加载项不会被当作正常文件。
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
历史分批加载，单批最多扫描 50 个提交；窗口显示已扫描、已加载及是否还有更早记录。“加载更早”继续查询，“取消加载”保留已有结果。
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
| 历史文件 | 双击历史明细中的文件，输入两个变更集比较，或导出右侧目标版本；支持外部差异工具 |
| 删除 / 重命名 | 生成待提交删除或移动；要求所选范围干净，不覆盖目标；可撤销后再决定是否提交 |
| 加入忽略列表 | 将未版本控制项的精确路径追加到工作区 ignore.conf，保留已有规则 |
| 打开 Gluon | 在官方客户端中打开当前工作区 |
| 设置 | 配置 cm.exe、gluon.exe、命令超时、外部 diff / merge 程序及参数模板 |

目录操作递归包含子项。签入/撤销一个有更改的目录前应核对其子项。
勾选目录会勾选其可见后代；取消某个子项会取消父目录的递归选择，避免把该子项隐式提交。
列表右键提供显示历史、查看差异和丢弃修改，操作对象为高亮行；签入按钮使用勾选项。
待提交列表按当前路径过滤，移动到范围外的条目不会显示在原目录内。
CLI 命令根据实际退出码报告结果，GUI 错误可在“操作 → 操作记录”查看；失败时不会清空签入说明。
Gluon 与默认官方差异窗口独立运行，TortoiseSCM 仅确认进程启动。自定义外部工具等待进程退出，报告其退出码。
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
# 可增加 --external 调用设置的差异工具
& $exe --cli --command export --path 'D:\workspace' --item '/Assets/file.txt' --changeset 42 --output 'D:\temp\file.txt' --yes --json | ConvertFrom-Json
# 覆盖已存在的导出目标必须额外指定 --overwrite
```

`--item` 是以 `/` 开头的仓库路径，与本机工作区是否已加载该文件无关；可导出当前已被删除的文件的旧版本。
比较双方目前使用同一路径；重命名前请使用旧路径。缺失端点明确报错，不伪装为空文件。
导出先下载并验证，再替换目标，服务器错误不会截断旧文件。目录、元数据和符号链接不作为文件导出。

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

### 外部 diff / merge

设置窗口可选择可执行文件，并指定参数模板。CLI 支持同样配置：

```powershell
& $exe --cli --command settings --json | ConvertFrom-Json
& $exe --cli --command settings --diff-tool 'C:\Tools\diff.exe' --diff-args '"{base}" "{local}"' --yes --json | ConvertFrom-Json
& $exe --cli --command settings --merge-tool 'C:\Tools\merge.exe' --merge-args '"{base}" "{local}" "{remote}" "{merged}"' --yes --json | ConvertFrom-Json
& $exe --cli --command diff --path 'D:\workspace\file.txt' --external --json | ConvertFrom-Json
& $exe --cli --command merge --base 'D:\tmp\base.txt' --local 'D:\tmp\local.txt' --remote 'D:\tmp\remote.txt' --output 'D:\tmp\merged.txt' --yes --json | ConvertFrom-Json
```

工具参数必须符合所选工具的实际命令格式。应使用工具的等待选项（若有），以便临时基础文件在比较结束后才清理。
diff 留空时 GUI 使用官方查看器；普通 CLI `diff` 仍输出文本，只有 `--external` 才启动工具。
merge 留空时明确报错。设置窗口的“打开合并工具”允许选择四个文件，此入口只编辑文件。
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

Partial 工作区通过“操作 → Partial 传入冲突…”处理已加载文件的本地内容修改与服务器新版本冲突。预检后准备基础、本地、传入三方文件，调用用户配置的合并工具；审核结果后单独确认应用。后端只更新该文件的基础修订，再写入审核结果，保持 Partial 模式、分支和加载规则，不自动提交。

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
| 本地同目录重命名，服务器修改 | 接受传入，或保留本地重命名/另选新名称；纯重命名保留服务器内容，本地同时编辑时显式采用本地内容 |

准备会固定文件身份、服务器变更集和本地内容，并保存备份。应用前重新核验，过期准备会拒绝执行。未完成会话会阻止普通写操作，避免与更新、撤销、回滚交错。未应用准备可取消；失败后使用“恢复为传入版本”，将受影响文件恢复到固定传入状态，原始备份及恢复时的文件内容均保留。此恢复不会重新提交本地内容，可从备份取回后另行处理。

```powershell
& $exe --cli --command partial-structure-preview --path 'D:\workspace' --json | ConvertFrom-Json
& $exe --cli --command partial-structure-prepare --path 'D:\workspace' --item '/file.txt' --yes --json | ConvertFrom-Json
& $exe --cli --command partial-structure-status --path 'D:\workspace' --json | ConvertFrom-Json
# 选择必须来自 preview/session 的 resolutionOptions
& $exe --cli --command partial-structure-resolve --path 'D:\workspace' --resolution rename --rename 'local-copy.txt' --yes --json | ConvertFrom-Json
# 其他选择为 keep-local / take-incoming；成功后回到常规待定更改检查与提交
& $exe --cli --command partial-structure-cancel --path 'D:\workspace' --yes --json | ConvertFrom-Json
# 仅用于应用中断；采用会话固定的传入状态，保留备份
& $exe --cli --command partial-structure-recover --path 'D:\workspace' --yes --json | ConvertFrom-Json
```

当前结构处理限普通文件和同目录本地重命名。服务器移动文件需要原生命令更新父目录，暂不扩大范围执行；目录级 Partial 结构冲突、跨目录本地移动、路径被不同身份文件替换、Xlink 与符号链接仍会拒绝或显示不支持原因。
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
                 ├─ SettingsForm.cs           客户端与外部工具设置
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

### 本轮五项需求对照

| 需求 | GUI | CLI | 限制 |
| --- | --- | --- | --- |
| 文件 / 目录更新、历史、恢复 | 更新、历史 / 范围历史内恢复按钮 | `update`、`history`、`rollback --changeset` | 精确范围更新需要 Partial；Standard 明确要求整体更新 |
| 整仓更新、历史、历史版本 | 根目录打开，历史内分别回滚待提交和切换快照 | 根路径 `update`、`history`、`rollback`、`switch` | 整仓待提交回滚仅 Standard；Partial 快照仅处理已加载项 |
| 目录范围提交、勾选及右键 | 按范围显示，勾选签入，右键历史 / 差异 / 丢弃 | `status --path` 获取范围，重复 `--path` 提交所选项，`history` / `undo` | 目录递归操作包含后代；私有文件需先添加 |
| 上下窗格历史明细 | 上方分页提交、下方完整提交文件清单 | `history-page` / `history` + `changeset --changeset` | 路径过滤分页可能为空，需继续查询直到 hasMore=false |
| 自定义 diff / merge | 设置窗口、差异按钮、结构选择、三方编辑与确认解决 | `settings`、`diff --external`、`merge`、`merge-*`、`partial-conflict-*` | 分支合并仅 Standard；Partial 内容冲突限同一文件身份 |

这是可继续演进的开发版本，不是 TortoiseGit 全功能等价移植。
尚未提供：分支浏览、Partial 目录级及传入移动结构冲突处理、拖放移动、仓库创建、
签名 MSI、自动更新、语言包、ARM64 与 32 位 Explorer。当前拒绝符号链接、junction 和跨嵌套工作区的递归写操作。
高级操作通过官方客户端完成。
部分工作区使用 `cm partial` 的对应命令，完整与部分工作区的行为不能混同；实际测试结果见交付说明。

参考：[Unity Gluon 签入说明](https://docs.unity.com/unity-version-control/gluon/check-in-changes)、
[Unity Gluon 添加新项](https://docs.unity.com/en-us/unity-version-control/gluon/upload-new-items)、
[Plastic CLI 指南](https://docs-plasticscm.azurewebsites.net/cli/plastic-scm-version-control-cli-guide)。
