# 提交窗口与多文件提交验证（2026-09-30）

## 界面

- 顶部只保留 `Commit to: repository@server:/路径 · 模式`，删除重复的“范围”行。
- `Recent messages` 按钮靠左。
- 临时“使用 stdin 提交（测试）”开关：未勾选使用路径参数，勾选使用 UTF-8 stdin。提交期间禁用切换。
- 正常和最小尺寸已检查；提交窗口回归 103 项通过，包括 Partial 目录删除/移动的勾选范围。

## 实际定位

1. 默认 cm 的 194 文件提交失败，原始错误是远程连接被关闭。官方 REST 提交对照也在约 63 秒失败，上传字节仍为 0。调试日志进一步定位到旧服务器拒绝 `TryCheckIn` V3 后处理大请求的协议降级阶段；标准提交也能在确认阶段遇到同类失败。此前“64 个文件是命令行参数上限”的解释没有证据支持，已删除。
2. 临时副本 `client.conf` 开启 `PlasticProtoEnableLz4=yes` 后，194 文件的一次原生提交成功，cs:1096，原生命令 510 ms。日志确认服务器接受 LZ4，版本降级后的 RPC 正常完成。软件只为当前提交复制配置，保留其他设置，结束后清理；不修改用户全局配置、权限检查或官方 DLL。
3. 独立对照中，默认 Standard/Partial 的 65 空文件、相同内容、不同内容六场有五场失败、一场成功；LZ4 的 Standard/Partial × 空文件/不同内容四场全部成功，443–524 ms，各一个 changeset、pending 0。保留默认模式偶尔成功这一事实。4 MB 网络探针上传 156 ms、下载 125 ms，不能用文件总大小或网络吞吐解释失败。
4. English cm 在中文 Windows 上使用 CP936 输入。强制 UTF-8 会损坏中文路径；仅改为 CP936 后，175 条长路径仍在固定缓冲边界损坏字符。只读 `fileinfo` 连续八次复现，证明与服务器提交无关。官方 `cm shell --encoding=utf-8` 的 reader 连续三次正确读取全部路径。
5. stdin 提交采用上述官方 UTF-8 reader，一进程只执行一个 checkin。说明写入临时 UTF-8 文件；路径全部规范化为工作区内绝对路径，提前失败时也无法被识别成 shell 命令。仅接受唯一明确的 `CommandResult`，缺失或重复按未确认失败处理，保留说明与勾选。

官方资料：[LZ4 元数据压缩](https://docs.unity.com/en-us/unity-version-control/release-notes/9#9.0.16.4292)、[历史协议降级/60 秒关闭修复](https://docs.unity.com/en-us/unity-version-control/release-notes/6#6.0.16.1735)。后者记录的是历史同类机制；当前服务端版本未知，不能断言就是该历史缺陷。具体配置开关由本机客户端定义与现场实验确认。

## 最终真实窗口验收

`CheckinAtomicGuiIntegrationTests.cs` 打开可见的生产 MainForm，使用真实加载、勾选事件、临时开关及提交按钮处理流程，调用真实 cm/服务器。所有写入在新分支 `/main/tortoisescm-autotest-integration-20260930-232546-92bbb293`，没有修改 TestSCM/TestSCM2。

| 模式 | 输入 | 单文件 | 194 文件 | 源码 194 项 | 只选源码 175 文件 |
|---|---|---|---|---|---|
| Standard | Paths | cs1126 | cs1127 | cs1128 | — |
| Standard | stdin | cs1129 | cs1130 | cs1131 | cs1132 |
| Gluon/Partial | Paths | cs1133 | cs1134 | cs1135 | — |
| Gluon/Partial | stdin | cs1136 | cs1137 | cs1138 | cs1139 |

14 场全部通过，2127 项断言。每场仅一个 changeset，核对服务器选定路径、中文多行/引号说明、选定文件不再 pending、未选 AD/private 兄弟项保持状态；独立 consumer 更新后逐文件核对原始字节。XML 读取按规范将 CRLF 规范化为 LF，说明内容完整。

源码场景只读取并复制 `TestSCM2/Source - 副本`：175 个文件、19 个目录（含根目录），共 194 项。只选文件的两场用于强制长 stdin，不依赖目录合并缩短输入。所有批量提交均为单次 checkin，没有拆成多条历史。

本机 Computer Use native pipe 不可用；这些是可见窗口的进程内 GUI 自动化，不能表述成桌面鼠标操作。正常/最小尺寸截图已目视检查。

## 回归与证据

- Backend 88、Preflight 34、提交 UI 103、说明 UI 129 项通过；其余完整后端/CLI/Shell 回归及完整 UI 套件通过，最后重新构建 Release。
- 完整回归第一次停在新增 UI 测试未创建控件句柄的测试问题；修正测试窗口初始化后完整 UI 套件通过。
- 早期失败的真实只读 shell 探针确认残留绝对路径不会执行 undo/checkin。
- 可复现入口：`New-TestWorkspace.ps1`、`CheckinAtomicGuiIntegrationTests.cs`、`CheckinTransportMatrixTests.ps1`、`CheckinApiLiveTests.ps1`。旧的弱覆盖直接 CLI 脚本已归档，不作为产品验收依据。
- 大型原始证据留在本地 ignored `bin/TortoiseSCM/qa/`：`atomic-gui-utf8-reviewed-20260930/results.json`、各场日志/截图/server.json；`transport-20260930-225734-b634d842/transport-comparison.json`；`cm-debug-20260930/cm.log.txt`；`long-stdin-readonly-20260930/`。

安装文件版本：`0.1.0-dev-20260930-checkin3`，生成 Windows x64 Setup.exe。
