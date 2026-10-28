# MO Trainer（心灵终结实时修改器）

> **English summary** — MO Trainer is a standalone, real-time trainer for the Red Alert 2 mod
> **Mental Omega 3.3.6**. It works entirely from outside the game process: it reads and writes
> game memory with `ReadProcessMemory` / `WriteProcessMemory`, and never injects DLLs, patches
> engine code, or modifies any game file. Features: money, power, selected-unit invincibility,
> kill enemies, and instant build. Intended for single-player, skirmish against AI, and
> self-hosted LAN games only — **public CnCNet play is prohibited**. Licensed under GPL-3.0.

一个为《心灵终结》（Mental Omega 3.3.6）开发的 **Windows 免安装实时修改器**。
不用安装、不用配置，双击 `MentalOmegaTrainer.exe` 就能用。

- **只读写游戏内存**，不注入 DLL、不改游戏代码、不改任何游戏文件、不写注册表。
- **零第三方依赖**：只用到 Windows 自带的 .NET Framework，不需要另装运行库。
- **删除整个文件夹 = 完全卸载**，不残留。

> 目前的最新发布是 **v1.0.4**，见 [Releases](https://github.com/micaholic125/MO-Trainer/releases)。

---

## 功能一览

| 功能 | 热键 | 说明 | 风险 |
| --- | --- | --- | --- |
| 金钱 | `Ctrl + Alt + 1` | 金钱低于设定值时自动补回设定值 | 低 |
| 电力 | `Ctrl + Alt + 2` | 提高发电建筑的发电量 | 中（见下方说明） |
| 选中单位无敌 | `Ctrl + Alt + 3` | 对**开启那一刻**选中的单位持续锁血 | 高 |
| 秒杀敌人 | `Ctrl + Alt + 4` | 把敌方单位血量清零 | 高 |
| 瞬间建造 | `Ctrl + Alt + 5` | 建造 / 生产项立即完成 | 高 |

热键是**全局**的，游戏窗口在前台也能用。高风险功能需要先在卡片下勾选「知情确认」才能开启，
这个勾选每次启动都要重勾（防止误触）。

几个必须知道的行为：

- **秒杀敌人**：开启前请先在游戏里选中**自己的**一个单位，程序靠它判断敌我。
  如果和 AI 或队友同队，先「标记友军」，否则队友也会被清掉。
- **选中单位无敌**：只保护**开启那一刻**选中的单位；开启后再选中的单位不在保护范围内。
- **电力**：修改的是「建筑型号」共用的一份数据，**敌方同型号的建筑也会受影响**。
  所以这个功能不会在下次启动时自动恢复，需要你手动开关。
- **瞬间建造**：在局域网对局里单方面开启几乎必然掉线。

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 / 11（64 位） |
| 游戏 | **Mental Omega 3.3.6**（必须是这个版本） |
| 权限 | 管理员（双击后 Windows 会询问，点「是」） |
| 运行库 | 无（使用系统自带的 .NET Framework 4.x） |

游戏版本不一致时，程序会**拒绝写入**并在界面上说明原因 —— 这是保护，不是故障。

---

## 下载与使用

1. 到 [Releases](https://github.com/micaholic125/MO-Trainer/releases) 下载最新的
   `MO-Trainer-v1.0.zip`，解压到任意位置（推荐 `D:\Mental Omega\Trainer`）。
   **请连 `config` 文件夹一起保留**，不要只复制 exe。
2. 双击 `MentalOmegaTrainer.exe`，Windows 弹窗点「是」。
3. 启动游戏并进入一局。修改器顶部的状态条从「未连接」变成「已连接」，就说明连上了。
4. 用界面开关或上面的热键开启 / 关闭功能。

图文版详细说明见 [`docs/使用说明.docx`](docs/使用说明.docx)，发布包里也带了一份。

### 关于 `config\profile.json`

这是发布者机器上**已经校准好**的内存数据：

- 游戏版本相同：通常可以直接用，不需要重新校准。
- 提示「尚未校准」：到「校准向导」按提示做一次。
- 提示「游戏版本不符」：说明游戏不是 3.3.6，程序会拒绝写入。请换回同版本游戏，或重新校准。
- 不需要这份数据：删掉整个 `config` 文件夹即可，程序会重新生成。

---

## 安全与使用范围（请务必读）

- **允许**：单人战役、遭遇战打 AI、**自己当房主**的局域网对局。
- **禁止**：公网 CnCNet 对战。修改内存可能违反游戏服务条款，因此产生的后果由使用者自行承担。
- 程序只读写游戏进程内存：**不改游戏文件、不写注册表、不往 C 盘放任何东西**。
- 日志、配置、校准报告全部写在程序自己所在的文件夹里。
- 程序要求管理员权限，且需要读写游戏内存，**可能被杀毒软件或 Windows SmartScreen 提示**。
  请确认文件来源可信后再选择「仍要运行」；**不要为了它关闭整个杀毒软件**。
- 卸载：删除整个文件夹即可，不残留。

### 已知限制

- **全图视野**：未实现（地址始终没有定位成功）。
- **联机同步模式**：已取消（2026-10-23 决定）。局域网对局只支持单方面使用，且高风险功能容易掉线。
- 只针对 Mental Omega 3.3.6；其它版本或其它 mod 不在支持范围内。

---

## 常见问题

**为什么必须管理员权限？**
如果游戏是以管理员身份启动的，修改器也必须是同级权限，否则系统会拒绝它读写游戏内存。

**杀毒软件报毒 / SmartScreen 拦截？**
这类「读写其它进程内存」的工具普遍会被启发式规则误报。请只从本仓库的 Releases 下载，
确认来源后再放行；不要关闭整个杀毒软件。

**状态条一直是「未连接」？**
依次检查：游戏是否已经启动、游戏是否为 Mental Omega 3.3.6、修改器和游戏是否同为管理员权限。
程序会把具体原因写进 `logs\` 下的日志。

**开了功能但游戏里没反应？**
先看界面状态与日志。程序的原则是：**缺必要数据时拒绝执行并说明缺什么**，不会「开关能点、实际什么都不做」。

**会封号吗？**
公网联机存在风险，本项目明确禁止在公网 CnCNet 对战中使用。单人 / 自建局域网自娱风险最低，但仍请自行判断。

---

## 从源码编译

本工程**不使用 Visual Studio、不依赖 SDK / NuGet / 联网**，直接用 Windows 自带的 .NET Framework 编译器：

```powershell
# 编译主程序
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

# 编译并运行控制台自检（不需要游戏在跑）
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Test -RunTest

# 清理产物后重新编译
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Clean
```

产物：`dist\MentalOmegaTrainer.exe`（要求管理员权限，manifest 已内嵌）。

**给贡献者的两个硬性约束：**

1. **只能用 C# 5 语法**（编译器是 `v4.0.30319\csc.exe`）。禁止字符串插值 `$""`、表达式体成员 `=>`、
   `nameof`、`?.`、`out var`、属性初始化器等新语法。
2. 所有 `.cs` 文件是 UTF-8，编译时必须带 `/codepage:65001`（`build.ps1` 已处理）。

提交前请至少跑一次 `build.ps1 -Test -RunTest`，确认自检全部通过（正常为 0 失败）。

---

## 仓库结构

```
src/            主程序源码（C# / WinForms，C# 5）
  Core/         进程附加、内存读写、扫描器
  Calibration/  校准向导与各类只读探针
  Cheats/       五项功能的实现
  Ui/           界面与热键
  Diagnostics/  自检（四百余项自动检查）
tools/          开发期使用的探针与辅助脚本（C# / Python，均为只读或离线分析）
docs/           需求规格、技术方案、设计规范、开发步骤、校准手册、使用说明
开发日志/        按天记录的开发过程（含失败尝试与回退原因）
build.ps1       编译脚本（无需 SDK / NuGet / 联网）
dist/config/    预校准数据与程序配置
```

`证据归档/`（真机原始日志）与 `发布包/`（对外压缩包）不入库；运行日志与导出报告同样不追踪。

---

## 参与贡献

- **反馈问题**：请开 [Issue](https://github.com/micaholic125/MO-Trainer/issues)，并附上
  「关于」页显示的**构建时间**、现象描述，以及 `logs\` 下对应的日志文件。
  有构建时间才能判断你跑的是不是最新版本。
- **提交代码**：请保持 C# 5 语法、通过自检；涉及内存写入的改动请在说明里写清写入位置与回退方式。
- **不接受**：任何针对公网联机作弊的功能。

---

## 许可证

本项目以 **GNU General Public License v3.0（GPL-3.0）** 发布，许可证全文见 [LICENSE](LICENSE)。

Copyright (C) 2026 micaholic125。版权声明与「允许使用更新版本」的授权表述见
[COPYRIGHT.md](COPYRIGHT.md)。

你可以自由使用、修改、分发本项目；如果分发修改后的版本，必须以同样的协议开源，
并保留版权声明。本程序**不提供任何担保**。

---

## 免责声明

- 本项目是**非官方**作品，与 Electronic Arts、Westwood Studios、Mental Omega 制作组、
  CnCNet 均无关联；文中提及的游戏与 mod 名称归各自权利人所有。
- 本仓库**不包含任何游戏文件**。使用本工具需要你自行拥有合法副本的《红色警戒 2：尤里的复仇》
  与 Mental Omega 3.3.6。
- 本工具仅用于单人游戏与自己当房主的局域网对局。**禁止用于公网 CnCNet 对战。**
  使用本工具产生的一切后果由使用者自行承担。

---

## 联系方式

- GitHub Issue（推荐，便于留存记录）
- 邮箱：2622307388@qq.com
- 抖音号：67664177780
