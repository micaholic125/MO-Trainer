# HANDOFF —— MO-Trainer 项目交接说明

> 交接时间：2026-09-27 收工状态（本文于 2026-09-29 依实际文件核对后撰写）
> 交接人：上一个 AI 助手　接收方：Claude Code
>
> **阅读顺序（重要）**
> 1. `CLAUDE.md`（项目唯一总入口：标准文件路径、日志规则、硬性约束）
> 2. `docs\01-需求规格说明.md` / `02-技术方案.md` / `03-设计规范.md` / `04-开发执行步骤.md` / `05-校准与测试手册.md`
> 3. **本文**：只写「当前真实状态、卡点、坑、未验证项」
>
> 本文与上述文档冲突时，**以 `CLAUDE.md` 与 `docs\` 为准**，并回头修正本文。

---

## 0. 五分钟上手

```powershell
# 构建 + 跑自检（无需游戏在运行）
powershell -NoProfile -ExecutionPolicy Bypass -File d:\AIWORK\XLZJ\build.ps1 -Test -RunTest

# 仅构建
powershell -NoProfile -ExecutionPolicy Bypass -File d:\AIWORK\XLZJ\build.ps1

# 清理后重建
powershell -NoProfile -ExecutionPolicy Bypass -File d:\AIWORK\XLZJ\build.ps1 -Clean
```

| 项目 | 值 |
|---|---|
| 代码根 | `d:\AIWORK\XLZJ`（**不是 git 仓库**，无任何提交历史） |
| 编译器 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` —— **只支持 C# 5**（禁止 `$"{}"`、`=>` 成员体、`nameof`、`?.`、`out var`、属性初始化器、`using static`、`when`）。所有 `.cs` 为 UTF-8，编译**必须**带 `/codepage:65001` |
| 产物 | `dist\MentalOmegaTrainer.exe`（单文件、免安装、**requireAdministrator**） |
| 自检程序 | `dist\_selftest.exe`（控制台，`main = MoTrainer.Diagnostics.SelfTest`）；当前 **149 项通过** |
| 自检源码 | `src\Diagnostics\SelfTest.cs`（约 110 KB） |
| 运行日志 | `dist\logs\trainer-YYYYMMDD.log` |
| 校准配置 | `dist\config\profile.json`（定位信息）、`dist\config\trainer.json`（开关与数值） |
| 导出报告 | `dist\reports\`（由界面「导出日志」生成） |
| 探测循环脚本 | 根目录 `selfcheck-loop.ps1`（2026-09-27 建，**接手前先读一遍**） |
| 工具目录 | `tools\README.md`（340 字节） |

### 关于"运行中的 exe 会锁文件"

`dist\MentalOmegaTrainer.exe` 在运行中被系统锁住，`csc` 会报 CS0016。`build.ps1` 的做法是**先关掉正在运行的实例再重新发布到同一路径**（`-NoKill` 可改为直接失败）。

### ⚠️ 冒烟测试必须用 `--smoke`

修改器是 `requireAdministrator`，**构建脚本的 `Stop-Process` 会被系统拒绝**。若不带着 `--smoke` 启动，就会在用户桌面上留下一个**关不掉的窗口**（已真实发生过，用户当场质问"我没开修改器啊"）。

```powershell
$p = Start-Process .\dist\MentalOmegaTrainer.exe -ArgumentList "--smoke" -PassThru
Start-Sleep -Seconds 7
Write-Output ("self_exited=" + $p.HasExited)   # 期望 True
```

`--smoke` 会在启动 4 秒后记录一次布局诊断并自行 `Close()`。**任何发布前都应跑一次这个**（它曾抓到"启动即崩"）。

---

## 1. 项目是什么

为《心灵终结》（Mental Omega 3.3.6，引擎 `gamemd.exe`）开发的 **Windows 免安装实时内存修改器**。

- 目标用户：**完全不懂代码的普通玩家**，最终交付必须双击即用。
- 使用场景：单人战役 / 遭遇战打 AI / **自己当房主的局域网对局**。
- 功能：金钱、电力、选中单位无敌、秒杀敌人、瞬间建造、全图视野。
- 技术路线：**纯外部内存读写**（`ReadProcessMemory` / `WriteProcessMemory`），**绝不注入游戏进程**。
- 最终产物：单个 `MentalOmegaTrainer.exe`，零依赖、免安装。

---

## 2. 代码地图

| 目录 / 文件 | 职责 |
|---|---|
| `src\Program.cs` | 入口，管理员权限检查、单实例、启动窗 |
| `src\app.manifest` | `requireAdministrator` |
| `src\Core\` | 内存内核：`Win32Api`、`MemoryIO`、`ProcessAttach`、`AobScanner`、`PointerChain`、`PointerScanner`、`ValueScanner`、`RegionSelector`、`IProcessHandle` |
| `src\Calibration\` | `CalibrationProfile`（含 `FieldCalibration` / `FieldLocator`）、`ProfileStore`、`CalibrationWizard`、`CalibrationTarget`、`SelectionProbe`、`ObjectFieldHunter`、`ChainDerivation` |
| `src\Cheats\CheatEngine.cs`（48 KB） | 30Hz 锁定回写线程、`ApplyAll`、各功能的执行路径 |
| `src\Cheats\CheatModels.cs` | `CheatId`（Money/PowerOutput/SelectedInvincible/KillEnemies/InstantBuild/RevealMap）、`CheatFeature` |
| `src\Cheats\TechnoWalker.cs` | **阶段 6 内核**：全对象数组遍历 + 虚表校验 + 按阵营分组 |
| `src\Cheats\TechnoOps.cs` | **阶段 6 内核**：秒杀 / 瞬间建造 / 全图视野三项批量操作（含「缺校准即拒绝执行」） |
| `src\Cheats\TechnoArrayFinder.cs` | 「自动查找总名单」：拿选中单位反查全对象数组 |
| `src\Cheats\TrainerSettings.cs` | `trainer.json` 读写 |
| `src\Ui\MainForm.cs`（36 KB） | 主窗、导航、日志区、布局诊断、`--smoke` |
| `src\Ui\Pages\AdvancedPage.cs`（**76 KB**） | 高级功能页：扫描 / 查找总名单 / 秒杀预演 / 全图视野候选定位 |
| `src\Ui\Pages\UnitPage.cs`（91 KB） | 单位校准页 |
| `src\Ui\Controls\UiFactory.cs` | 按钮工厂（含 DPI 自适应宽度）、`ClampLabelWidths` |
| `src\Util\` | `Logger`、`LogExporter`、`JsonMini`、`HexUtil` |

---

## 3. 当前真实进度

对照 `docs\04-开发执行步骤.md` 的 8 个阶段：

| 阶段 | 状态 | 备注 |
|---|---|---|
| S1 骨架 + 内存内核 | ✅ 完成 | |
| S2 扫描 / 指针链 / 区域枚举 / 配置读写 | ✅ 完成 | 遗留小项：`AobScanner` 350 MB 全堆扫描的真实吞吐未在真机场景验证 |
| S3 校准向导 + 报告导出 | ✅ 完成 | **遗留**：主链（静态根 `0x683D4C`）始终未命中，全靠第 2 条兜底链工作 |
| S4 金钱 / 电力 / 选中无敌 + 30Hz 锁定 | ✅ 完成 | **遗留**：电力 B 方案未实施（见下） |
| S5 深色界面 / 全局热键 / 状态条 / 配置持久化 | ✅ 完成 | **待真机验证**：无敌的反向用例 |
| **S6 高级功能（秒杀 / 瞬间建造 / 全图视野）** | 🔶 **进行中** | 详见第 5 节 |
| S7 联机模式分级 | ⏳ 未开始 | |
| S8 交付（`docs` 成稿 docx、部署到游戏目录、验收） | ⏳ 未开始 | |

**已实现但真机验证程度不同的划分**（务必区分，不要混为一谈）：

- 真机验证过：金钱（消费检验通过）、选中数量 / 选中列表（可定位）、全局对象数组（预演数字合理）
- **未真机验证**：电力产出（`House+0x53A4` 的真机权威性存疑）、无敌反向用例、秒杀（只有只读预演）
- **完全不可执行**：瞬间建造、全图视野（校准数据缺失）

---

## 4. 校准数据现状（`dist\config\profile.json` 实测）

`updatedAt = 2026-09-27 17:41:02`　`moduleBase = 0x00400000`　`moduleSize = 0x00793000`

| 定位项 | calibrated | 地址 / 链 |
|---|---|---|
| `money` | ✅ | 8 条候选链，根 `0x683D4C` + `0x30C`；指纹「测试值 61845 已验证（消费检验通过）」 |
| `powerOutput` | ✅ | 8 条链，`+0x53A4`；指纹「读数核对值 460」 |
| `powerDrain` | ✅ | 8 条链，`+0x53A8`；指纹「读数核对值 200」 |
| `selectedCount` | ✅ | 全局变量 `module + 0x68ECC8`（`hasStaticAddress = true`） |
| `selectedList` | ✅ | 全局变量 `module + 0x68ECA4`（存的是"选中对象指针数组"的起始地址） |
| `technoArray` | ✅ | `staticAddress = 0x37A4E8`（**由「自动查找总名单」落盘**） |
| `technoArrayCount` | ❌ | 未校准 —— 目前只能"按上限 4096 探测，以实际数到的有效对象数为准" |
| `mapShroud` | ❌ | 未校准 |

`fieldOffsets`：

| 字段 | 值 | 状态 |
|---|---|---|
| `technoOwner` | `0x000000F0` | ⚠️ **人工逐个试出**（试过 `+0x0` / `+0xD0` / `+0xE4` / `+0xF0`），自动分析失败：日志为「没有找到符合『阵营』特征的偏移（种类少 + 聚集明显）」。**换局面可能失效** |
| `technoHealth` | `0x0000006C` | 待真机确认 |
| `technoMaxHealth` | `0x0000007D` | 待真机确认 |
| `technoIronCurtain` | `0x00000000` | ❌ 未校准 |
| `productionTimer` | `0x00000000` | ❌ 未校准 |
| `map.shroudSize` | `0x00000000` | ❌ 未校准 |

---

## 5. 阶段 6 的真实卡点（优先看这里）

### S6-1 秒杀敌人 —— **只做完只读预演，尚未接线**

- 已完成：全对象数组已校准落盘（`technoArray.staticAddress = 0x37A4E8`）；界面上有「**预演：秒杀敌人（不写入）**」按钮，实测可算出「将击杀 **1039** 个，放过我方 **309** 个」（以扫到 4094 个对象、跳过 2746 个非我方类型为准）。
- **未接线**：`TechnoOps.ApplyAll` 执行分支 → 三个功能注册 → 全局热键 → 自检断言。
- **⚠️⚠️ 顺序绝对不能反（这是本阶段最大的安全雷）**

  看 `src\Cheats\CheatEngine.cs:221-260` 的 `ApplyAll`：它按功能遍历，
  - `CheatId.SelectedInvincible` 走**专用路径** `ApplySelectedInvincible`（遍历选中对象逐一写血量）
  - **其余全部功能走单地址读-比-写** `ApplyFeature`

  也就是说：**如果先"注册功能"而忘了加执行分支，锁定线程会拿"对象数组的基地址"当单地址去写值** —— 那是往错误的内存反复写。正确顺序固定为：
  `ApplyAll 分支 → 功能注册 → 热键 → 自检断言`。

  唯一的兜底保护是 `ApplyAll` 开头的 `if (!feature.IsCalibrated) { skip }`（`CheatEngine.cs:230`），**它挡不住"已校准但语义错"的情况**。
- 建议的验收标准（沿用 09-29 计划）：开启后 **3 秒内敌方单位归零，且我方单位不受影响**。

### S6-2 瞬间建造 —— 无法执行

`fieldOffsets.productionTimer = 0x00000000`。需要先定位生产/建造队列里的计时字段。

### S6-3 全图视野 —— 无法执行

`mapShroud` 未校准（`calibrated = false`）。2026-09-27 观察到两处「内容一致、全为 `0x00` 的大块」作为**迷雾位图候选**：

- `0x08B6B000`，1 052 672 字节
- `0x08C7F000`，1 576 960 字节

尚未确认与固化。界面上有「**全图视野（点两次）**」入口（`AdvancedPage.cs:103`）。

### S6 其他前置遗留

- `technoArrayCount` 未校准（见第 4 节）。
- **阵营偏移缺自动判据**（见第 4 节 `technoOwner`）。这是秒杀"分清敌我"的地基，不能长期靠人工试。
- **新模块零自检覆盖**：`TechnoWalker` / `TechnoArrayFinder` / `TechnoOps` **均无 `SelfTest` 断言**，自检项数自 2026-09-22 起停在 **149**。接手后建议先补这一块（用合成内存构造假对象数组，验证"虚表校验能识破找错的数组"）。

---

## 6. 已知缺陷、未验证项与技术债

### 6.1 界面被切 —— 花了 6 轮才定位，四个真因（务必引以为戒）

用户曾反馈「框选侧面的字只能看到一半」「日志两个按钮只剩一半」「字只能显示上面一半」。真因**不是**最初猜的任何一条，而是下面四条叠加：

| # | 真因 | 修法 |
|---|---|---|
| 1 | `AutoSize = true` 的 `Label` **不换行、只往宽里长**，把页面 `TableLayoutPanel` 的**期望宽度**顶大 → `AutoScroll` 出横向滚动条 → 右侧被滚出视野 | `UiFactory.ClampLabelWidths()`：递归给所有 `AutoSize` 控件设 `MaximumSize(可用宽度, 0)`；**按"控件相对页面的剩余宽度"算，不能按父容器宽**（父容器正是被撑大的那个）。挂在 `MainForm.OnResize` + 切页时各一次 |
| 2 | 日志标题 `Dock = Fill` 但**先于**按钮添加 → WinForms 按 z 序结算停靠，标题先占满整条，右停靠按钮只剩一个字宽 | 按钮先加、`Fill` 的标题**最后**加 `+ BringToFront()` |
| 3 | **写死的像素不随高 DPI 缩放**：用户显示器 2560×1600 开了缩放，字号放大而 `100` 像素不变 → "打开日志目录"只挤出"打开日" | `UiFactory` 按钮尺寸改为 `max(指定值, TextRenderer.MeasureText(...) + 内边距)` |
| 4 | 同一条的另一半：**写死的高度**（日志标题栏 28、所有者绘制列表行高常量、勾选框 20）→ 字只显示上半截 | 全部改为按 `Font.Height` 实测决定；卡片高度跟着勾选框实际高度走 |

**关键教训**：这类问题的判据是**底部横向滚动条在不在**，以及**用数字量，不要靠截图猜**。`MainForm.LogLayoutDiagnostics()` 会往日志写一行 `[Layout]`（窗口宽 / 内容区 / 日志头 / 按钮坐标 / 页面尺寸与滚动条状态），用户导出日志就能定位。**本地是 96 DPI，用户的显示问题是本地复现不出来的** —— 这是当时反复误判的根本原因。

### 6.2 曾引入并已修复的自身 bug（同类错误不要再犯）

| 现象 | 原因 | 修法 |
|---|---|---|
| **启动即崩**（"未将对象引用设置到对象的实例"，打不开） | 构造函数里 `ClientSize = ...` 会触发 `OnResize`，那一刻日志区控件尚未创建，诊断代码直接取字段 | `LogLayoutDiagnostics()` 开头加空引用保护；并把 **`--smoke` 真启动** 纳入发布流程（正是它后来抓到了这条） |
| 测试在用户桌面留下**关不掉的窗口** | 修改器是管理员权限，脚本 `Stop-Process` 被拒 | 改用 `--smoke` 自退出 |
| 没有任何线索却把"最像的候选"**存进配置** | 排序纯属瞎猜，8 个候选全是垃圾（数量全是 257、命中 0） | 加硬门：**没有线索绝不保存** |
| 用户明明选中了单位，程序报"没有选中任何单位" | 只按一种内存布局解读"指针格/数量格" | 改为**多布局逐一尝试**，并用"对象头 4 字节是否为模块区间内的虚表指针"判定哪一种布局为真 |

### 6.3 其他未验证项

- `[S4-3]` 电力功能 B 方案未实施：`House+0x53A4` 已被真机确认为**影子字段**，需改走"写发电厂产出值"；卡在定位发电厂对象里的产出值字段（**需要用户开局并选中一座发电厂，用连续采样找特征值**）。
- `[S5]` 无敌反向用例待真机验证：① 开启后再点选新单位，该单位**不应**变无敌；② 敌方单位**不应**被记入保护名单并锁血。
- `[S3]` 主链未命中（`0x683D4C`）：2026-09-27 的 52 次回退全部命中第 2 条链 `0x4B4244 / 0x14C / 0x30C`。功能上可用，但主链需重校准，或叠加 AOB 特征做第三重保险。
- **项目没有 git**（无提交历史、无法回滚）。建议接手后先 `git init` 并做一次初始提交。

---

## 7. 用户与环境（影响沟通方式，务必看）

### 用户特征

- **完全不懂代码的普通玩家**，明确说过"术语太多我听不懂"。**沟通规则：只说"点哪个按钮 → 看到什么 → 发我什么"**，不要出现内存偏移、DLL、结构体这类词。
- 用户对**反复截图 / 反复来回**有明显不满（界面那一轮连续 6 次往返是主要摩擦来源）。**凡是能让程序自己写进日志的信息，就不要让用户描述。**
- 用户已明确允许：**我可以自行启动游戏**做验证。但**不能操作游戏界面**（选地图、开局、选中单位、暂停都需要人）—— 这是人工环节的边界。**不要为了省用户 1 分钟去模拟点击游戏窗口**：对局中点错一下就是丢单位/取消选中，且属于"注入输入"，风险收益完全不成比例。

### 环境

| 项 | 值 |
|---|---|
| 游戏目录 | `D:\Mental Omega`（内有 `gamemd.exe`、`Syringe.exe`、`MentalOmegaClient.exe`、`ddraw.ini`、`RA2MO.ini`、`Client\`、`debug\`） |
| 显示器 | **2560×1600 且开了 DPI 缩放** → 本机（96 DPI）复现不出显示类问题 |
| 启动方式 | 用户从 MO 客户端（CnCNet）启动 |
| 规则文件 | `INI` 子目录里**没有** `rulesmo.ini`/`artmo.ini`（打包在 `.mix` 里）→ **字段语义只能靠运行时自证，不能靠读 INI** |

### 已排查过、与本项目无关的一件事

用户反馈过「启动器里启动游戏，但游戏窗口不出现」。已核对结论：

- **与本修改器无关**：修改器最后一次运行是 2026-09-27 17:55:06，而那些异常发生在 22:58 / 23:03 / 23:34；源码中所有写盘路径只指向 `dist\config`、`dist\reports` 与临时目录，三个脚本（`build.bat`/`build.ps1`/`selfcheck-loop.ps1`）中**没有任何** `gamemd`/`Syringe`/游戏路径引用。
- 现象：22:58:31 那次启动 23 秒后结束并留下 `stats.dmp`；23:03:03 那次跑了 31 分钟、退出码 0。
- 可疑配置（**都是游戏/启动器自己写的**）：`ddraw.ini` 里 `width=2552 height=1486`（几乎顶满 2560 宽屏幕）、`posX=4 posY=38`（固定贴左上角）、`windowed=False` 与 `fullscreen=false` 并存、`savesettings=1`；且 `RA2MO.ini` 说 `1280×720` 而 `ddraw.ini` 说 `2552×1486`，**两个文件不一致**。
- **未修改**：以上游戏配置我可佐证未改动过，若要调整应先备份再动，且需用户明确同意。

---

## 8. 建议的下一步（按优先级，取自 09-29 计划）

1. **`[S6-1]` 秒杀接线**，严格按「`ApplyAll` 分支 → 功能注册 → 热键 → 自检断言」顺序。
2. **补自检断言**：`TechnoWalker` / `TechnoArrayFinder` / `TechnoOps`（用合成内存造假对象数组），把 149 项往上推。建议**先做这一步再做接线**，让回归有保护。
3. **需要用户配合的一次真机会话**（人工约 1 分钟摆状态，其余自动化）：
   - 开局并让场上同时有敌我单位 → 验证秒杀（验收：3 s 内敌方归零、我方不受影响）
   - 同一次会话内顺手选中**一座发电厂** → 连续采样找它对象里的产出值字段（`[S4-3]`）
   - 同一次会话内做 `[S5]` 无敌反向用例
   - 同一次会话内重跑一遍金钱定位刷新主链（`[S3]`）
4. `[S6-3]` 确认 `0x08B6B000` / `0x08C7F000` 两处全 `0x00` 大块是否为迷雾位图，是则推进 `mapShroud` 校准。
5. `[S6]` 阵营偏移 `+0xF0` 需要更硬的**自动判据**（当前靠人工试，换局面可能失效）。
6. 之后才是 S7 联机模式分级、S8 交付。

---

## 9. 不可协商的硬规则（摘录，完整版见 `CLAUDE.md` 第 4 节）

- **禁止** DLL 注入、`CreateRemoteThread`、对游戏 `.text` 段做代码 patch。原因：MO 已被 Ares 打入 1450 个 hook，改代码必崩。**只允许写数据段与堆。**
- 写入前**必须**用 `VirtualQueryEx` 确认目标页可写；不可写就跳过并告警。
- 所有 P/Invoke 返回值必须检查，**不得静默忽略**。
- 地址**必须**经运行时值校验后才能固化进配置。**严禁把未实测的地址写进"已确认"**。
- 配置、日志、校准报告**一律写入工具自身目录**（`<exe目录>\logs\`、`<exe目录>\config\`）；**不写注册表、不写 C 盘**。
- 工具定位为**单人 / 局域网自娱**；**公网 CnCNet 联机禁止使用**，界面与文档必须显著标注。
- 联机高风险项（无敌 / 秒杀 / 瞬间建造）**默认关闭**，且必须二次确认。
- **缺必要校准数据时，功能必须拒绝执行并说明缺什么**，不允许"开关能点、程序悄悄什么都不做"。

---

## 10. 交接清单

接手后建议按此顺序确认：

1. 读 `CLAUDE.md` + `docs\04-开发执行步骤.md` 的进度追踪表（**该表最后一次更新是 2026-09-29 00:29**）。
2. 读本文第 4、5 节，把 `dist\config\profile.json` 打开对照一遍。
3. `build.ps1 -Test -RunTest` → 确认 **149 项通过**。
4. `dist\MentalOmegaTrainer.exe --smoke` → 确认 `self_exited=True`。
5. 读根目录 `selfcheck-loop.ps1`，弄清现有自动化探测能力。
6. 建议 `git init` 并做初始提交（当前无任何版本控制）。
7. 继续第 8 节的第 1~2 步（这两步**不需要游戏**）。

> 最后一句提醒：这个项目最容易踩的坑不是内存操作，而是**用"逻辑正确"当成"显示正确"**。
> 用户显示器有 DPI 缩放、我看不到他的屏幕 —— 凡是这类事，**让程序把数字写进日志**，比任何推测都可靠。
