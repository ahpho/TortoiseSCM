# Gluon、标准模式与锁流程专项测试（2026-09-29）

## 模式区别

- **Standard** 是完整工作区。工作区树完整加载，更新默认检查整个工作区；跨分支合并和暂存集应用也以 Standard 为主要支持模式。
- **Gluon / Partial** 是部分工作区。服务器仓库、分支和历史不变，但本地可以只加载选定文件或目录，后续更新按加载范围处理；首次拉取当前仍完整下载所选分支，之后才使用部分加载范围。
- 两种模式都使用同一 Plastic 服务器锁规则。Gluon 不会自动开启锁，也不会把本地“签出”状态变成服务器锁。

Plastic 的 `checkout` 只有在服务器配置 `lock.conf` 并且路径匹配锁规则时才会申请独占锁。规则生效时，第一位用户签出后服务器会保留锁，其他用户对同一项的签出会失败；规则未启用时，多个工作区都可以签出，不能靠客户端图标或状态文字推断“已锁定”。

## 测试过程

测试使用专用分支 `/main/tortoisescm-autotest-integration-20260929-070001-6d2b6676`，建立 Producer（Standard）、Consumer（Standard）和 Partial（Gluon）三个隔离工作区。原 `TestSCM`、`TestSCM2` 未作为写入目标。

1. Producer 提交 `lock-focus.txt`，Consumer 更新到同一变更集。
2. Producer 执行 `cm checkout lock-focus.txt`，退出码 0。
3. Consumer 对同一仓库项执行 `cm checkout lock-focus.txt`，也退出码 0；这证明当前服务器没有对该仓库启用匹配的独占锁规则。
4. 应用使用的结构化查询读取所有锁及当前用户/当前工作区锁，均为 0 行。
5. `LockTests.exe --read-only <producer>` 通过 40 项解析、归属核验、只读查询和解锁安全断言。
6. 新增 `UiTests.exe --locks-live <artifacts> <producer>` 通过 12 项 GUI 断言：读取真实锁列表、显示数量、未选择时禁用释放、他人锁不可释放、本人当前工作区锁启用释放，以及最小窗口控件可见。生成的最小窗口截图中保留了“他人锁”和“当前用户”两行合成数据，用于检查按钮启用规则；状态栏仍准确显示真实服务器的 0 个锁。

## 结论

锁管理窗口和锁查询逻辑可以区分他人锁与当前用户/当前工作区锁，也不会直接信任旧列表来释放锁；这些客户端功能专项通过。当前服务器实际没有启用锁策略，所以本次不能证明“签出并获取锁、其他人无法签出”的正向服务器场景。要完成这项验收，需要管理员在测试仓库服务器启用并部署适用于目标路径的 `lock.conf`，然后重复 Producer/Consumer 两次签出，预期第二次失败，并在锁管理窗口看到第一条锁记录。

## 证据

- `bin/TortoiseSCM/qa/integration-20260929-070001-6d2b6676/manifest.json`：隔离工作区和分支。
- `bin/TortoiseSCM/qa/lock-focus/summary.json`：模式、工作区状态和专项结论。
- `bin/TortoiseSCM/qa/lock-focus/lock-list-app-query.txt`：应用使用的结构化全量锁查询。
- `bin/TortoiseSCM/qa/lock-focus-ui/locks-live-minimum.png`：锁管理窗口最小尺寸证据。

该测试仍不等同于 Explorer 鼠标右键验收；桌面 Computer Use 连接此前不可用。实际服务器锁策略由 Plastic 服务器授权和规则决定，TortoiseSCM 不在客户端伪造锁。
