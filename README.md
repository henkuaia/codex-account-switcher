# Codex Account Switcher

Windows 上的 Codex 账号切换工具。发布目录中的 `CodexAccountSwitcher.exe` 依赖已安装的 **.NET 9 Desktop Runtime (x64)**。

## 使用

1. 运行 `CodexAccountSwitcher.exe`，选择 **Add account** 并确认。工具会先安全关闭 Codex，再启动普通浏览器登录；认证成功的新账号会立即成为活动账号，随后工具重新启动 Codex。登录过程中可以安全取消并恢复此前账号。
2. 选择 **Remove account** 只会打开应用自有的账号列表，不会刷新任何额度。活动账号不能删除，必须先切换到其他账号。
3. 选择 **Refresh** 时才会手动查询额度。界面将已识别的长周期显示为 Weekly 或 Monthly，未知长周期使用中性的 Quota 标签，并显示返回的重置时间；不展示五小时额度。该查询使用 unofficial endpoint，可能随服务变化而不可用；`401`、`403` 或不可用结果不应阻止账号切换。
4. 先停止所有正在进行的 Codex 工作，再选择目标账号并确认。工具会安全关闭 Codex、切换账号并重新启动 Codex。

该工具为单实例应用，同一 Windows 用户只能运行一个实例。工具对 codex-auth 的登录、切换和删除命令设置 `CODEX_AUTH_SKIP_SERVICE_RECONCILE=1`，阻止 codex-auth 创建或修改托管自动切换服务。工具本身不创建计划任务。

不提供 automatic 切换或 hot switch。切换期间不要编辑 Codex 的认证文件，也不要同时运行其他账号工具。单实例只防止本工具的两个实例并发，不会阻止 CCSwitch 等外部程序写入认证文件。切换完成后，不要在 CCSwitch 中启用 **OpenAI Official** provider，否则它可能覆盖当前 Codex 认证状态。

## 安全与恢复

- 账号快照位于 `%USERPROFILE%\.codex\accounts`，当前为未加密存储；不要将该目录或 `.codex\auth.json` 分享、上传或提交到版本库。
- 在首次初始化或真实切换前，备份 `.codex\auth.json` 和 `.codex\accounts`。若账号初始化失败，关闭工具后恢复这份 pre-test `.codex` backup，再重新打开 Codex。
- 点击窗口关闭按钮只会隐藏到 tray，工具仍在运行；从 tray 菜单选择 Exit 才会退出。
