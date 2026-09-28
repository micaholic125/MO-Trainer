# tools —— 开发期辅助脚本

**不参与产品构建，不属于交付物。** 产品构建入口是根目录的 `build.ps1` / `build.bat`。

## 现有脚本

### `selfcheck-loop.ps1`

反复编译并运行控制台自检，**发现回归就停下并打印失败行**。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File d:\AIWORK\XLZJ\tools\selfcheck-loop.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File d:\AIWORK\XLZJ\tools\selfcheck-loop.ps1 -MaxRuns 50
powershell -NoProfile -ExecutionPolicy Bypass -File d:\AIWORK\XLZJ\tools\selfcheck-loop.ps1 -MaxRuns 0    # 一直跑
powershell -NoProfile -ExecutionPolicy Bypass -File d:\AIWORK\XLZJ\tools\selfcheck-loop.ps1 -Probe      # 每轮顺带探测游戏
```

两个用途（不是"让机器别闲着"）：

1. **抓回归**——任何一项失败就停止循环并打印失败的检查项。
2. 加 `-Probe` 时，每轮额外跑一次真机探测（`--smoke --autofind`），让游戏内的数字持续落进日志，可以观察其随时间的变化。

**它不修改代码，也不判断"任务完成了"**——那需要人来定。它的职责是"盯着，出问题就停"。

> 脚本位于 `tools\`，但操作的 `src\`、`dist\` 都在上一级目录；脚本内部已自行解析项目根目录，
> 从任何位置调用都可以。

## 约定

- 只放一次性或开发期使用的辅助脚本。
- **使用完毕后应及时删除**，避免与 `src\`、`build.ps1` 混淆。
- 脚本内提示文案**一律使用英文 ASCII**，避免 PowerShell 5.1 无 BOM 读取导致的编码问题
  （本机是 zh-CN 系统，`.ps1` 会被按 GBK 解析，中文字符串会让解析器直接报错）。
