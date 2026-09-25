TortoiseSCM — Plastic SCM / Unity Version Control for Windows
===========================================================

本 fork 正在将 TortoiseGit 的 Explorer 工作方式迁移到 Plastic SCM。
新的入口是 **`src/TortoiseSCM.sln`**，包含原生 x64 右键扩展与中文桌面客户端，
通过已安装的 `cm.exe` 操作工作区。原有 `TortoiseGit.sln` 是保留的上游工程，仍然是 Git 客户端。

当前提供：工作区识别、目录范围待定更改与勾选签入、右键历史/丢弃修改、添加、签出、更新、撤销、
差异、分页提交历史与文件明细、历史比较/导出、文件/目录恢复、整仓待提交回滚、快照切换、删除/移动/忽略，以及 Gluon 入口。
`TortoiseSCM.exe --cli` 提供与 GUI 共用后端的无界面模式，支持 JSON 输出及自动化测试。
支持外部 diff/merge 配置、Standard 工作区文件冲突处理、锁管理，以及独立后台缓存驱动的 Explorer 状态图标。
提供带校验清单的 Windows x64 安装 ZIP 和 Windows CI；此版本尚未迁移 TortoiseGit 的全部对话框。
支持 Standard 目录结构冲突逐项选择，以及 Partial 文件内容、同名新增、删除和文件移动冲突处理，包括本地跨目录移动与服务器文件移动；分支图、签名 MSI、自动更新和 Windows 11 一级菜单尚未提供。

```powershell
# Visual Studio 2022+：C++ 桌面开发、Windows SDK、.NET Framework 4.8 targeting pack
.\build-tortoisescm.ps1 -Test
.\contrib\tortoisescm\Register-Shell.ps1
.\bin\TortoiseSCM\Release\TortoiseSCM.exe --path 'D:\Work\Juscent\SCM_Study\TestSCM'
# 无界面查询；管道会等待这个 Windows GUI 子系统程序退出
.\bin\TortoiseSCM\Release\TortoiseSCM.exe --cli --command status --path 'D:\Work\Juscent\SCM_Study\TestSCM' --json | ConvertFrom-Json
```

右键点击 Plastic 工作区里的文件、文件夹或文件夹空白处，选择 **TortoiseSCM**。
Windows 11 先选择“显示更多选项”。默认右键菜单注册仅影响当前用户，不需要管理员权限。
状态图标的 Windows 发现注册需要管理员执行 `Register-Shell.ps1 -EnableMachineOverlays`。
使用 `contrib/tortoisescm/Package.ps1` 打包，解压后运行 `Install.ps1` 可安装到当前用户的独立版本目录。
设置中可修改 `cm.exe`、`gluon.exe` 与外部 diff/merge 工具路径和参数。

详见 [构建、安装与使用说明](doc/TortoiseSCM.md)。许可证沿用 [GPL](LICENSE)，图标来自本仓库。
以下保留上游介绍，便于查阅原工程。

---

TortoiseGit - The coolest Interface to Git Version Control
==========================================================

TortoiseGit is a Windows Shell Interface to Git based on TortoiseSVN. It's open source and can be built entirely with freely available software.

TortoiseGit supports you with regular tasks, such as committing, showing logs, diffing two versions, creating branches and tags, creating patches and so on (see our [Screenshots](https://tortoisegit.org/about/screenshots/) or [documentation](https://tortoisegit.org/docs/)).

* Website: [tortoisegit.org](https://tortoisegit.org)
* Download: [tortoisegit.org/download](https://tortoisegit.org/download)
* Documentation: [tortoisegit.org/docs/](https://tortoisegit.org/docs/)
* Support: [tortoisegit.org/support/](https://tortoisegit.org/support/)
* Issue tracker: [tortoisegit.org/issues](https://tortoisegit.org/issues)
* Contribute: [tortoisegit.org/contribute/](https://tortoisegit.org/contribute/)
* Mailing lists: [tortoisegit-announce](https://groups.google.com/group/tortoisegit-announce),
                 [tortoisegit-users](https://groups.google.com/group/tortoisegit-users) and
                 [tortoisegit-dev](https://groups.google.com/group/tortoisegit-dev)
* StackOverflow tag: [tortoisegit](https://stackoverflow.com/questions/tagged/tortoisegit)

Download
--------

The latest release and language packs are available on the [download page](https://tortoisegit.org/download). There you can also find the system requirements and latest release notes.

The TortoiseGit team also provides [preview releases](https://download.tortoisegit.org/tgit/previews/) on an irregular basis. These versions are used by the TortoiseGit developers and are built from the latest code that represents the cutting edge of the TortoiseGit development.

What to do if things go wrong or a crash happened
--------------------------------------------------

Before reporting an issue, please search whether a similar issue already exists and check that your problem isn't fixed in our latest [preview release](https://download.tortoisegit.org/tgit/previews/).

An important aspect of reporting issues is to have a reproducible example of the issue; it's also important to mention the exact version of your operating system, the version of Git and the version of TortoiseGit (this information can be found on the TortoiseGit about dialog).

TortoiseGit includes a crash reporter (if not disabled on installation), which automatically uploads crash dumps to drdump.com, where the TortoiseGit team can review them. If you have a reproducible example, please also file an issue and link the crash report.

We have a special page describing [steps for debugging](src/Debug-Hints.txt), where the majority of these steps do not require you to build TortoiseGit on your own.

How Can I Contribute?
=====================

You're welcome to contribute to this project! There are several aspects you can help on:

* improving our [documentation](https://tortoisegit.org/docs/) (see [doc/readme.txt](doc/readme.txt) file and [doc](doc) folder),
* [translations](Languages/README.txt),
* testing [preview releases](https://download.tortoisegit.org/tgit/previews/),
* helping other users on the mailing lists,
* improving our UIs, or also
* coding (e.g., fix open issues or implement new features).

Any help is appreciated!

Feel free to report issues and open merge requests.

Please also check the [contribution guidelines](CONTRIBUTING.md) to understand our
workflow.

How to build
------------

Building TortoiseGit is usually not necessary; however, it is easy. All necessary requirements and steps are described in the [build.txt](build.txt) file. Our short description in the [architecture.txt](architecture.txt) file might also be helpful.

License
=======

TortoiseGit is licensed under the [GPLv2](src/gpl.txt).

