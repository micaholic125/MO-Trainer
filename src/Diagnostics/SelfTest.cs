using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using MoTrainer.Calibration;
using MoTrainer.Cheats;
using MoTrainer.Core;
using MoTrainer.Ui.Controls;
using MoTrainer.Ui.Pages;   // 页面排版自检要真造出「高级功能」页来量控件位置
using MoTrainer.Util;

// 布局自检要用真实控件量尺寸，所以需要 WinForms 与绘图类型
using System.Drawing;
using System.Windows.Forms;

namespace MoTrainer.Diagnostics
{
    /// <summary>
    /// 控制台自检程序。**不参与主程序编译**（由 build.ps1 用 /main: 单独指定入口）。
    ///
    /// 目的：在没有游戏运行时也能验证内存内核链路是否可用；
    /// 游戏运行时可额外验证附加、模块枚举、映像头读取与区域画像。
    ///
    /// 退出码：0 = 全部通过，1 = 存在失败项。
    /// </summary>
    internal static class SelfTest
    {
        private const uint PAGE_READWRITE_TEST = 0x04;
        private const uint MEM_COMMIT_RESERVE = 0x3000;
        private const uint MEM_RELEASE = 0x8000;

        private static int _pass;
        private static int _fail;
        private static int _warn;

        public static int Main(string[] args)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Logger.Init(baseDirectory);
            Logger.SetMinimumLevel(LogLevel.Debug);

            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }

            Write("=================================================");
            Write(" MO Trainer - 内存内核自检");
            Write("=================================================");
            Write("");

            Section("1. 运行环境");
            CheckEnvironment();

            Section("2. P/Invoke 结构体布局");
            CheckStructLayout();

            Section("3. 日志系统");
            CheckLogging(baseDirectory);

            Section("4. 内存读写往返（自身进程）");
            CheckSelfMemoryRoundTrip();

            Section("5. 区域枚举与可写页判定（自身进程）");
            CheckSelfRegions();

            Section("6. 目标进程附加");
            CheckTargetProcess();

            Section("7. 扫描与配置（S2 模块）");
            CheckJson();
            CheckScanPattern();
            CheckAobScanner();
            CheckPointerChain();
            CheckValueScanner();
            CheckProfileStore();

            Section("8. 指针回溯与校准向导（S3 模块）");
            CheckPointerScanner();
            CheckWizard();

            Section("9. 功能开关与锁定引擎（S4 模块）");
            CheckTrainerSettings();
            CheckCheatEngine();
            CheckFeatureCardInteraction();
            CheckUnavailableFeature();

            Section("10. 单位校准（S5 模块）");
            CheckStaticAddressLocator();
            CheckScanRegionCoverage();
            CheckChainDerivation();
            CheckObjectFieldHunter();
            CheckMergeByOffset();
            CheckLogExporter();
            CheckSelectionProbe();

            // 界面布局单独成节：这两条都是"真机上被切了才发现"的回归，
            // 靠截图来回猜了两轮，改成用真实控件量尺寸 —— 一次钉死。
            Section("11. 界面布局（防切边回归）");
            CheckLogHeaderDocking();
            CheckLabelClamp();

            // S6 的批量操作是**唯一会一次性改写大量游戏状态**的代码（秒杀会写下上百个对象），
            // 一旦判据出错就是把自己的部队一起清空。所以这一节的重点不是"能跑通"，
            // 而是"该拒绝的时候一定拒绝、该跳过的一定跳过"。
            Section("12. 对象遍历与批量操作（S6 模块）");
            CheckTechnoWalker();
            CheckTechnoWalkerPartialRead();
            CheckTechnoArrayFinder();
            // 选中读取的共享件（2026-10-08）："你选中的是哪些单位"必须走金钱锚定 + 自动更正，
            // 它是「自动查找」的线索来源 —— 线索错了，找到的名单必然是错的（真机三次定位全败的根因）。
            CheckSelectionResolver();
            CheckTechnoOps();
            // 引擎层的端到端接线回归（2026-10-07 [S6-1]）：名单从哪来、我方是谁、
            // 什么时候必须拒绝 —— 这些参数链路的风险只有到引擎层才测得到。
            CheckKillWiring();
            // 「高级功能」页的排版（2026-10-08 晚）：用户真机截图里按钮挤成一堆、压住提示文字。
            // 这类问题**本机 96 DPI 复现不出来**（用户显示器有缩放），所以只能靠"用数字量"：
            // 直接造出页面、在几档宽度下量每个控件的位置，断言互不重叠、不出页面。
            CheckAdvancedPageLayout();
            // 环境（游戏版本）检查：防止"换了引擎还照旧写"。它失效时表面完全看不出来，
            // 所以算法、三种结论、以及"引擎是否真的停写"都要有断言兜着。
            CheckEnvironmentCheck();
            // 「电力试探」是全程序唯一往游戏里写**实验值**的地方：中心是"无论如何都还原"与"不该写就一个字节不写"。
            CheckPowerProbe();
            // 「电力侦查」是只读工具：中心是"能认出类型对象"与"一个字节都不写"。
            CheckPowerScout();

            // 这一条格外重要：采集要靠**用户开一局游戏**才能跑，
            // 而用户的时间是本项目最稀缺的东西。采集本身若有 bug，
            // 用户白开一局、白等三分钟，还会以为"数据本来就是这样"。
            // 所以必须在没有游戏的情况下先证明它整条链路都通。
            Section("13. 一键采集（校准辅助）");
            CheckHarvestSession();

            Section("14. 自动巡检（找类型对象 · 主链反查）");
            CheckAutoInspection();

            Write("");
            Write("=================================================");
            Write(string.Format(CultureInfo.InvariantCulture,
                " 结果：{0} 项通过，{1} 项失败，{2} 项提示", _pass, _fail, _warn));

            // 机器可读的 ASCII 汇总行，专供脚本解析。
            //
            // 为什么必须有这一行：本程序把标准输出设成 UTF-8，而 PowerShell 5.1 抓取
            // 子进程输出时按系统 ANSI 代码页（本机 GBK）解码，中文标点会变成乱码 ——
            // 于是"用正则匹配「149 项通过」"这种写法会失配，转而去匹配日志里的十六进制
            // 地址串（真实踩过：脚本一直报 0 通过 0 失败）。
            // 而 .ps1 自身又必须保持纯 ASCII（写了中文会让解析器直接报错），
            // 所以两侧只能靠纯 ASCII 的标记行对接。**改动本行会同时影响 tools\selfcheck-loop.ps1。**
            Write(string.Format(CultureInfo.InvariantCulture,
                "SELFTEST_RESULT pass={0} fail={1} warn={2}", _pass, _fail, _warn));

            Write(" 日志：" + Logger.CurrentFile);
            Write("=================================================");

            Logger.Info("SelfTest", string.Format(CultureInfo.InvariantCulture,
                "result pass={0} fail={1} warn={2}", _pass, _fail, _warn));

            return _fail > 0 ? 1 : 0;
        }

        // ============================================================ 检查项

        private static void CheckEnvironment()
        {
            bool elevated = IsElevated();

            Write("  操作系统   : " + Environment.OSVersion.VersionString);
            Write("  CLR 版本   : " + Environment.Version);
            Write("  进程位数   : " + (Environment.Is64BitProcess ? "x64" : "x86"));
            Write("  进程权限   : " + (elevated ? "管理员" : "普通用户"));
            Write("  运行目录   : " + AppDomain.CurrentDomain.BaseDirectory);
            Write("  构建时间   : " + BuildInfo.Text);

            if (!elevated)
            {
                WarnItem("当前不是管理员权限",
                    "若游戏以管理员启动，本节第 6 部分的附加检查会因权限不足而跳过；请用管理员身份运行本自检");
            }

            // 构建标识要能读出来，否则"确认手里这份是不是最新"就无从谈起
            string build = BuildInfo.Text;
            Check("构建标识可读（能确认手里这份是不是最新编译的）",
                build.Length > 0 && build.IndexOf("未知", StringComparison.Ordinal) < 0,
                build);

            bool is64 = Environment.Is64BitProcess;
            if (is64)
            {
                Check("自检进程为 x64（与主程序一致）", true, "x64");
            }
            else
            {
                WarnItem("自检进程位数", "当前为 x86，与主程序 x64 不一致；结构体布局检查将按 x86 判定");
            }
        }

        private static void CheckStructLayout()
        {
            int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            int miSize = Marshal.SizeOf(typeof(MODULEINFO));
            int intPtrSize = IntPtr.Size;

            int expectedMbi = intPtrSize == 8 ? 48 : 28;
            int expectedMi = intPtrSize == 8 ? 24 : 12;

            Write(string.Format(CultureInfo.InvariantCulture,
                "  IntPtr.Size = {0}，MEMORY_BASIC_INFORMATION = {1} 字节（期望 {2}），MODULEINFO = {3} 字节（期望 {4}）",
                intPtrSize, mbiSize, expectedMbi, miSize, expectedMi));

            Check("MEMORY_BASIC_INFORMATION 尺寸正确", mbiSize == expectedMbi,
                mbiSize + " / " + expectedMbi);
            Check("MODULEINFO 尺寸正确", miSize == expectedMi,
                miSize + " / " + expectedMi);
        }

        private static void CheckLogging(string baseDirectory)
        {
            Logger.Debug("SelfTest", "debug 级样本，仅用于验证级别过滤");
            Logger.Info("SelfTest", "info 级样本");
            Logger.Warn("SelfTest", "warn 级样本");
            Logger.Error("SelfTest", "error 级样本（并非真实错误）");

            string expectedDir = Path.Combine(baseDirectory, "logs");
            bool dirOk = Directory.Exists(expectedDir);
            Check("日志目录位于工具自身目录内", dirOk, expectedDir);

            bool fileOk = !string.IsNullOrEmpty(Logger.CurrentFile) && File.Exists(Logger.CurrentFile);
            Check("日志文件已创建", fileOk, Logger.CurrentFile == null ? "null" : Logger.CurrentFile);

            Check("日志未写入系统盘 C:", !(Logger.CurrentFile != null
                && Logger.CurrentFile.Length > 1
                && Logger.CurrentFile.Substring(1, 1) == ":"
                && char.ToUpperInvariant(Logger.CurrentFile[0]) == 'C'), "ok");
        }

        private static void CheckSelfMemoryRoundTrip()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("打开自身进程句柄", false, "OpenProcess 失败");
                return;
            }

            try
            {
                MemoryIO io = new MemoryIO(self);

                // 需要在 4GB 以下分配测试块，才能同时覆盖 uint 地址 API。
                // 固定地址可能已被占用，故依次尝试多个候选，最后再退化为不带地址提示。
                IntPtr block = IntPtr.Zero;
                uint usedHint = 0;
                uint[] hints = new uint[]
                {
                    0x10000000, 0x20000000, 0x30000000, 0x40000000,
                    0x50000000, 0x60000000, 0x70000000
                };
                for (int i = 0; i < hints.Length; i++)
                {
                    block = Win32Api.VirtualAllocEx(
                        self.Handle, new IntPtr((long)hints[i]), (IntPtr)0x1000,
                        MEM_COMMIT_RESERVE, PAGE_READWRITE_TEST);
                    if (block != IntPtr.Zero)
                    {
                        usedHint = hints[i];
                        break;
                    }
                }
                if (block == IntPtr.Zero)
                {
                    block = Win32Api.VirtualAllocEx(
                        self.Handle, IntPtr.Zero, (IntPtr)0x1000, MEM_COMMIT_RESERVE, PAGE_READWRITE_TEST);
                }
                if (block == IntPtr.Zero)
                {
                    Check("VirtualAllocEx 分配测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (usedHint != 0)
                {
                    Write(string.Format(CultureInfo.InvariantCulture,
                        "  使用低地址候选 0x{0:X8} 分配成功", usedHint));
                }

                long blockAddr = block.ToInt64();
                Write(string.Format(CultureInfo.InvariantCulture,
                    "  测试内存块地址 = 0x{0:X}", blockAddr));

                try
                {
                    bool lowAddress = blockAddr > 0 && blockAddr < 0x100000000L;
                    if (!lowAddress)
                    {
                        WarnItem("测试块位于 4GB 以上", "uint 地址 API 无法覆盖，将只测试 IntPtr 版本");
                    }

                    // 1) IntPtr 版本的 32 位读写往返
                    bool w1 = io.WriteUInt32Raw(block, 0xDEADBEEF);
                    uint r1;
                    bool rr1 = io.ReadUInt32Raw(block, out r1);
                    Check("IntPtr 版本 32 位读写往返", w1 && rr1 && r1 == 0xDEADBEEF,
                        string.Format(CultureInfo.InvariantCulture, "写入 0xDEADBEEF，读回 0x{0:X8}", r1));

                    // 2) uint 地址版本（主程序实际使用的入口）
                    if (lowAddress)
                    {
                        uint addr = (uint)blockAddr;
                        bool w2 = io.WriteUInt32(addr, 0x12345678);
                        uint r2;
                        bool rr2 = io.ReadUInt32(addr, out r2);
                        Check("uint 地址版本 32 位读写往返", w2 && rr2 && r2 == 0x12345678,
                            string.Format(CultureInfo.InvariantCulture, "写入 0x12345678，读回 0x{0:X8}", r2));

                        ushort r3v;
                        bool rr3 = io.ReadUInt16(addr, out r3v);
                        Check("16 位读取字节序正确", rr3 && r3v == 0x5678,
                            string.Format(CultureInfo.InvariantCulture, "期望 0x5678，读回 0x{0:X4}", r3v));

                        byte[] payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
                        bool w4 = io.Write(addr + 0x10, payload, 0, payload.Length);
                        byte[] back = io.ReadBytes(addr + 0x10, payload.Length);
                        bool same = back != null && back.Length == payload.Length;
                        if (same)
                        {
                            for (int i = 0; i < payload.Length; i++)
                            {
                                if (back[i] != payload[i]) { same = false; break; }
                            }
                        }
                        Check("字节数组批量读写往返", w4 && same,
                            back == null ? "读回为 null" : HexUtil.BytesToText(back, 0, back.Length));

                        Check("写入后回读校验（WriteUInt32Verified）",
                            io.WriteUInt32Verified(addr + 0x20, 0xCAFEBABE),
                            "0xCAFEBABE");

                        Check("可写页判定（应为 true）", io.IsWritable((ulong)addr), "allocated RW page");
                    }
                    else
                    {
                        WarnItem("uint 地址版本读写", "跳过：测试块不在 4GB 以下");
                    }

                    // 3) 只读页判定（自身模块映像头所在页）
                    Process current = Process.GetCurrentProcess();
                    ProcessModule mainModule = current.MainModule;
                    if (mainModule != null)
                    {
                        ulong moduleBase = (ulong)mainModule.BaseAddress.ToInt64();
                        bool writable = io.IsWritable(moduleBase);
                        Check("只读映像页判定（应为 false）", !writable,
                            string.Format(CultureInfo.InvariantCulture, "0x{0:X} writable={1}", moduleBase, writable));

                        byte[] header = io.ReadRawBytes(mainModule.BaseAddress, 2);
                        Check("读取自身映像头（MZ）",
                            header != null && header.Length == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z',
                            header == null ? "读取失败" : HexUtil.BytesToText(header, 0, 2));
                    }
                }
                finally
                {
                    Win32Api.VirtualFreeEx(self.Handle, block, IntPtr.Zero, MEM_RELEASE);
                }
            }
            finally
            {
                self.Dispose();
            }
        }

        private static void CheckSelfRegions()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("自身进程区域枚举", false, "OpenProcess 失败");
                return;
            }

            try
            {
                MemoryIO io = new MemoryIO(self);
                Stopwatch watch = Stopwatch.StartNew();
                List<MemoryRegion> regions = io.EnumRegions(0x00010000ul, 0x7FFFFFFFul, true, false, 0);
                watch.Stop();

                Check("可枚举到已提交区域", regions.Count > 0,
                    regions.Count + " 个，耗时 " + watch.ElapsedMilliseconds + " ms");

                if (regions.Count > 0)
                {
                    ulong total = 0;
                    int writable = 0;
                    for (int i = 0; i < regions.Count; i++)
                    {
                        total += regions[i].Size;
                        if (regions[i].Writable) { writable++; }
                    }
                    Check("区域合计大小合理", total > 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "合计 {0:N0} MB，其中可写区域 {1} 个", total / 1048576.0, writable));

                    MemoryRegion first = regions[0];
                    bool sane = first.State == Win32Api.MEM_COMMIT && first.Base >= 0x10000ul;
                    Check("区域字段解析合理", sane, first.ToString());
                }
            }
            finally
            {
                self.Dispose();
            }
        }

        private static void CheckTargetProcess()
        {
            ProcessAttach attach = new ProcessAttach();
            try
            {
                bool attached = attach.TryAttach();
                Write("  附加状态   : " + attach.State);
                Write("  描述       : " + attach.Describe());

                Process[] found = Process.GetProcessesByName(ProcessAttach.TargetProcessName);
                int runningCount = found == null ? 0 : found.Length;
                if (found != null)
                {
                    for (int i = 0; i < found.Length; i++)
                    {
                        try { found[i].Dispose(); }
                        catch { }
                    }
                }

                if (!attached)
                {
                    if (runningCount == 0)
                    {
                        WarnItem("game process not running",
                            "gamemd.exe 未在运行，跳过附加相关检查（这是正常的，游戏启动后可再跑一次自检）");
                    }
                    else if (attach.LastErrorWasAccessDenied)
                    {
                        // 权限不足属于环境限制，不是代码缺陷：游戏可能以管理员启动，
                        // 而本自检是普通权限。用管理员身份重跑即可验证。
                        WarnItem("附加 gamemd.exe 被拒绝（权限不足）",
                            "进程存在（" + runningCount + " 个），但 OpenProcess 返回拒绝访问。"
                            + "请用管理员身份运行本自检，或让游戏以普通权限启动。");
                    }
                    else
                    {
                        Check("附加 gamemd.exe", false,
                            "进程存在（" + runningCount + " 个）但附加失败：" + attach.LastError);
                    }
                    return;
                }

                Check("附加 gamemd.exe", true, attach.Describe());
                Check("主模块基址与预期一致", attach.ImageBaseAsExpected,
                    string.Format(CultureInfo.InvariantCulture,
                        "实际 0x{0:X8}，预期 0x{1:X8}", attach.ModuleBase, ProcessAttach.ExpectedImageBase));

                MemoryIO io = new MemoryIO(attach);

                byte[] header = io.ReadBytes(attach.ModuleBase, 2);
                Check("读取游戏映像头（MZ）",
                    header != null && header[0] == (byte)'M' && header[1] == (byte)'Z',
                    header == null ? "读取失败" : HexUtil.BytesToText(header, 0, 2));

                uint peOffset;
                bool peOk = io.ReadUInt32(attach.ModuleBase + 0x3C, out peOffset);
                if (peOk && peOffset > 0 && peOffset < attach.ModuleSize - 4)
                {
                    byte[] signature = io.ReadBytes(attach.ModuleBase + peOffset, 2);
                    Check("解析 PE 头偏移并校验签名",
                        signature != null && signature[0] == (byte)'P' && signature[1] == (byte)'E',
                        string.Format(CultureInfo.InvariantCulture,
                            "e_lfanew=0x{0:X}，signature={1}",
                            peOffset, signature == null ? "读取失败" : HexUtil.BytesToText(signature, 0, 2)));
                }
                else
                {
                    Check("解析 PE 头偏移", false,
                        string.Format(CultureInfo.InvariantCulture, "e_lfanew=0x{0:X} 不合理", peOffset));
                }

                Stopwatch watch = Stopwatch.StartNew();
                List<MemoryRegion> regions = io.EnumRegions(0x00010000ul, Win32Api.MaxUserAddress32, true, false, 0);
                watch.Stop();

                ulong writableTotal = 0;
                int writableCount = 0;
                int privateCount = 0;
                for (int i = 0; i < regions.Count; i++)
                {
                    MemoryRegion r = regions[i];
                    if (r.Writable) { writableTotal += r.Size; writableCount++; }
                    if (r.Type == Win32Api.MEM_PRIVATE) { privateCount++; }
                }

                Check("游戏进程区域画像可获取", regions.Count > 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "已提交 {0} 个（私有 {1} 个）｜可写 {2} 个 / {3:N0} MB｜耗时 {4} ms",
                        regions.Count, privateCount, writableCount, writableTotal / 1048576.0, watch.ElapsedMilliseconds));

                WriteLargestPrivateRegions(regions, 5);
            }
            finally
            {
                attach.Dispose();
            }
        }

        private static void WriteLargestPrivateRegions(List<MemoryRegion> regions, int top)
        {
            List<MemoryRegion> privates = new List<MemoryRegion>();
            for (int i = 0; i < regions.Count; i++)
            {
                MemoryRegion r = regions[i];
                if (r.Type == Win32Api.MEM_PRIVATE && r.Writable) { privates.Add(r); }
            }
            privates.Sort(delegate(MemoryRegion a, MemoryRegion b)
            {
                return b.Size.CompareTo(a.Size);
            });

            Write("  最大的私有可写区域（后续扫描的优先目标区）：");
            int count = privates.Count < top ? privates.Count : top;
            for (int i = 0; i < count; i++)
            {
                Write("    " + privates[i].ToString());
            }
        }

        // ============================================================ S2 模块

        private const int TestBlockSize = 0x10000;

        private static readonly uint[] LowHints = new uint[]
        {
            0x20000000, 0x21000000, 0x22000000, 0x30000000,
            0x40000000, 0x50000000, 0x60000000, 0x70000000
        };

        /// <summary>在自身进程申请一块 4GB 以下的测试内存（覆盖生产代码的 uint 地址路径）。</summary>
        private static IntPtr AllocLowBlock(IntPtr handle)
        {
            for (int i = 0; i < LowHints.Length; i++)
            {
                IntPtr block = Win32Api.VirtualAllocEx(
                    handle, new IntPtr((long)LowHints[i]), (IntPtr)TestBlockSize,
                    MEM_COMMIT_RESERVE, PAGE_READWRITE_TEST);
                if (block != IntPtr.Zero) { return block; }
            }
            return IntPtr.Zero;
        }

        private static bool IsLowAddress(IntPtr address)
        {
            long value = address.ToInt64();
            return value > 0 && value < 0x100000000L;
        }

        private static MemoryRegion MakeRegion(uint start, int size)
        {
            return MakeTypedRegion(start, size, Win32Api.MEM_PRIVATE);
        }

        private static MemoryRegion MakeTypedRegion(uint start, int size, uint type)
        {
            MemoryRegion region = new MemoryRegion();
            region.Base = start;
            region.Size = (ulong)size;
            region.Protect = Win32Api.PAGE_READWRITE;
            region.Type = type;
            region.State = Win32Api.MEM_COMMIT;
            return region;
        }

        private static bool ContainsAddress(List<ScanCandidate> candidates, uint address)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Address == address) { return true; }
            }
            return false;
        }

        private static void CheckJson()
        {
            JsonValue root = JsonValue.NewObject();
            root.Set("name", "心灵终结 修改器");
            root.Set("moduleBase", 0x00400000);
            root.Set("enabled", true);
            root.Set("escape", "引号\" 反斜杠\\ 换行\n 制表\t 结束");
            root.Set("empty", JsonValue.Null());

            JsonValue chain = JsonValue.NewArray();
            chain.Add("0xA8B230");
            chain.Add("0x30");
            root.Set("chain", chain);

            string text = root.ToJson(true);
            JsonValue parsed;
            string error;
            if (!JsonValue.TryParse(text, out parsed, out error))
            {
                Check("JSON 序列化与解析往返", false, error);
                return;
            }

            bool same = parsed.Get("name").AsString("") == "心灵终结 修改器"
                && parsed.Get("moduleBase").AsUInt(0) == 0x00400000
                && parsed.Get("enabled").AsBool(false)
                && parsed.Get("escape").AsString("") == "引号\" 反斜杠\\ 换行\n 制表\t 结束"
                && parsed.Get("empty").IsNull
                && parsed.Get("chain") != null
                && parsed.Get("chain").Count == 2
                && parsed.Get("chain").GetAt(0).AsString("") == "0xA8B230";

            Check("JSON 往返（含中文 / 转义 / 数组 / null）", same, text.Length + " 字符");

            Check("JSON 缺失键返回 null 而不是抛异常", parsed.Get("不存在的键") == null, "ok");

            JsonValue bad;
            string badError;
            bool rejected = !JsonValue.TryParse("{\"a\": }", out bad, out badError);
            Check("JSON 非法输入被拒绝", rejected, badError == null ? "(无错误信息)" : badError);
        }

        private static void CheckScanPattern()
        {
            ScanPattern pattern = ScanPattern.TryCreate("8B 0D ?? ?? ?? ?? 89");
            bool parsed = pattern != null && pattern.Length == 7
                && pattern.HasWildcards && pattern.AnchorIndex == 0;
            Check("特征码模板解析（含通配）", parsed,
                pattern == null ? "解析失败" : pattern.Text + " len=" + pattern.Length);

            byte[] buffer = new byte[] { 0x90, 0xEA, 0x8B, 0x0D, 0x11, 0x22, 0x33, 0x44, 0x89, 0xC8 };
            bool at2 = pattern != null && HexUtil.MatchPattern(buffer, 2, pattern.Bytes);
            bool notAt0 = pattern != null && !HexUtil.MatchPattern(buffer, 0, pattern.Bytes);
            Check("特征码匹配位置正确", at2 && notAt0,
                at2 ? "偏移 2 命中、偏移 0 未命中" : "匹配结果异常");

            Check("非法特征码字符串被拒绝", ScanPattern.TryCreate("ZZ ZZ") == null, "ok");
            Check("全通配特征码被拒绝", ScanPattern.TryCreate("?? ??") == null, "ok");

            ScanPattern plain = ScanPattern.TryCreate("4D 5A");
            Check("无通配特征码解析", plain != null && !plain.HasWildcards && plain.Length == 2,
                plain == null ? "解析失败" : "len=2");
        }

        private static void CheckAobScanner()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("AOB 扫描器", false, "OpenProcess 失败");
                return;
            }

            IntPtr block = IntPtr.Zero;
            try
            {
                block = AllocLowBlock(self.Handle);
                if (block == IntPtr.Zero)
                {
                    Check("AOB 扫描器测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(block))
                {
                    WarnItem("AOB 扫描器", "测试块不在 4GB 以下，跳过 uint 地址路径检查");
                    return;
                }

                uint blockAddress = (uint)block.ToInt64();
                MemoryIO io = new MemoryIO(self);

                const string anchor = "MOTRAINER-AOB-ANCHOR-8F3C1D2E";
                byte[] anchorBytes = Encoding.ASCII.GetBytes(anchor);

                uint alignedAt = blockAddress + 0x1000;
                uint misalignedAt = blockAddress + 0x2001;
                io.Write(alignedAt, anchorBytes, 0, anchorBytes.Length);
                io.Write(misalignedAt, anchorBytes, 0, anchorBytes.Length);

                AobScanner scanner = new AobScanner(self);
                List<MemoryRegion> regions = new List<MemoryRegion>();
                regions.Add(MakeRegion(blockAddress, TestBlockSize));

                ScanPattern pattern = ScanPattern.TryCreate(
                    HexUtil.BytesToText(anchorBytes, 0, anchorBytes.Length));

                ScanOptions alignedOptions = new ScanOptions();
                alignedOptions.FourByteAligned = true;
                alignedOptions.ThreadCount = 2;

                ScanReport aligned = scanner.Scan(regions, pattern, alignedOptions, CancellationToken.None, null);
                bool alignedOk = aligned.Hits.Count == 1 && aligned.Hits[0] == alignedAt;
                Check("AOB 扫描命中 4 字节对齐的目标", alignedOk,
                    "命中 " + aligned.Hits.Count + " 处"
                    + (aligned.Hits.Count > 0
                        ? "，首个 0x" + aligned.Hits[0].ToString("X8", CultureInfo.InvariantCulture) : ""));

                ScanOptions looseOptions = new ScanOptions();
                looseOptions.FourByteAligned = false;
                looseOptions.ThreadCount = 3;

                ScanReport loose = scanner.Scan(regions, pattern, looseOptions, CancellationToken.None, null);
                bool bothFound = loose.Hits.Contains(alignedAt) && loose.Hits.Contains(misalignedAt);
                Check("AOB 扫描（不对齐模式）同时命中两处", bothFound, "命中 " + loose.Hits.Count + " 处");

                byte[] readBack = io.ReadBytes(alignedAt, anchorBytes.Length);
                bool bytesMatch = readBack != null && Encoding.ASCII.GetString(readBack) == anchor;
                Check("命中地址回读内容与特征码一致", bytesMatch, anchor);

                ScanPattern absent = ScanPattern.TryCreate("DE AD BE EF DE AD BE EF DE AD");
                ScanReport none = scanner.Scan(regions, absent, looseOptions, CancellationToken.None, null);
                Check("不存在的特征码返回 0 命中", none.Hits.Count == 0, "命中 " + none.Hits.Count + " 处");

                Check("AOB 扫描统计可获取",
                    aligned.BytesScanned > 0 && aligned.ChunksScanned > 0, aligned.Summary());
            }
            catch (Exception ex)
            {
                Check("AOB 扫描器", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (block != IntPtr.Zero)
                {
                    Win32Api.VirtualFreeEx(self.Handle, block, IntPtr.Zero, MEM_RELEASE);
                }
                self.Dispose();
            }
        }

        private static void CheckPointerChain()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("指针链", false, "OpenProcess 失败");
                return;
            }

            IntPtr blockA = IntPtr.Zero;
            IntPtr blockB = IntPtr.Zero;
            try
            {
                blockA = AllocLowBlock(self.Handle);
                blockB = AllocLowBlock(self.Handle);
                if (blockA == IntPtr.Zero || blockB == IntPtr.Zero)
                {
                    Check("指针链测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(blockA) || !IsLowAddress(blockB))
                {
                    WarnItem("指针链", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint addressA = (uint)blockA.ToInt64();
                uint addressB = (uint)blockB.ToInt64();
                MemoryIO io = new MemoryIO(self);

                // 构造 [[base+A]+0x40]+0x18 = B+0x18
                io.WriteUInt32(addressA, addressB);
                io.WriteUInt32(addressB + 0x40, addressB);
                io.WriteUInt32(addressB + 0x18, 0xCAFEBABE);

                PointerChain chain = new PointerChain(new uint[] { addressA, 0x40, 0x18 });
                uint resolved;
                string error;
                bool ok = chain.TryResolve(io, 0, out resolved, out error);
                Check("指针链解析两级间接寻址", ok && resolved == addressB + 0x18,
                    ok ? string.Format(CultureInfo.InvariantCulture, "解析到 0x{0:X8}", resolved) : error);

                uint value = 0;
                bool valueOk = ok && io.ReadUInt32(resolved, out value) && value == 0xCAFEBABE;
                Check("通过指针链读到目标值", valueOk,
                    string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", value));

                string formatted = chain.Format(0);
                Check("指针链可读表达式生成", formatted.IndexOf("0x", StringComparison.Ordinal) >= 0, formatted);

                PointerChain parsed = PointerChain.Parse("0xA8B230, 0x30, 0x18");
                Check("指针链文本解析", parsed != null && parsed.LevelCount == 3 && parsed.Depth == 2,
                    parsed == null ? "解析失败" : parsed.Serialize());

                PointerChain single = PointerChain.Parse("0xA8B230");
                Check("单级静态地址链（不解引用）",
                    single != null && single.Depth == 0 && single.ResolveOrZero(io, 0x400000) == 0x00E8B230,
                    single == null ? "解析失败" : single.Format(0x400000));

                uint[] offsets = chain.Offsets;
                Check("偏移链返回副本而非内部引用",
                    offsets.Length == 3 && offsets[0] == addressA && offsets[2] == 0x18, chain.Serialize());

                // 断链必须失败，绝不能返回"看起来能用"的野地址
                io.WriteUInt32(addressA, 0);
                uint brokenAddress;
                string brokenError;
                bool brokenOk = chain.TryResolve(io, 0, out brokenAddress, out brokenError);
                Check("断链返回失败而不是脏地址",
                    !brokenOk && !string.IsNullOrEmpty(brokenError), brokenError);
            }
            catch (Exception ex)
            {
                Check("指针链", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (blockA != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, blockA, IntPtr.Zero, MEM_RELEASE); }
                if (blockB != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, blockB, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
            }
        }

        private static void CheckValueScanner()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("数值搜索", false, "OpenProcess 失败");
                return;
            }

            IntPtr block = IntPtr.Zero;
            try
            {
                block = AllocLowBlock(self.Handle);
                if (block == IntPtr.Zero)
                {
                    Check("数值搜索测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(block))
                {
                    WarnItem("数值搜索", "测试块不在 4GB 以下，跳过 uint 地址路径检查");
                    return;
                }

                uint blockAddress = (uint)block.ToInt64();
                MemoryIO io = new MemoryIO(self);
                ValueScanner scanner = new ValueScanner(io);

                const uint marker = 0x00ABCDEF;
                uint at1 = blockAddress + 0x800;
                uint at2 = blockAddress + 0x1200;
                uint at3 = blockAddress + 0x2000;
                io.WriteUInt32(at1, marker);
                io.WriteUInt32(at2, marker);
                io.WriteUInt32(at3, marker);

                List<MemoryRegion> regions = new List<MemoryRegion>();
                regions.Add(MakeRegion(blockAddress, TestBlockSize));

                ValueScanOptions options = new ValueScanOptions();
                options.FourByteAligned = true;

                ValueScanReport report = scanner.Scan(regions, marker, options, CancellationToken.None, null);
                bool foundAll = ContainsAddress(report.Candidates, at1)
                    && ContainsAddress(report.Candidates, at2)
                    && ContainsAddress(report.Candidates, at3);
                Check("数值首轮扫描命中全部三处标记",
                    foundAll && report.Candidates.Count == 3, report.Summary());

                io.WriteUInt32(at2, 0x00001234);
                int changed = scanner.Refilter(report.Candidates, ValueMatch.Changed, 0);
                Check("变化筛选（Changed）只保留改动过的那一处",
                    changed == 1 && report.Candidates[0].Address == at2, "保留 " + changed + " 个");

                int equals = scanner.Refilter(report.Candidates, ValueMatch.EqualTo, 0x00001234);
                Check("按指定值筛选（EqualTo）", equals == 1, "保留 " + equals + " 个");

                io.WriteUInt32(at2, marker);
                ValueScanReport fresh = scanner.Scan(regions, marker, options, CancellationToken.None, null);
                Check("重新扫描仍得到三处", fresh.Candidates.Count == 3,
                    "候选 " + fresh.Candidates.Count + " 个");

                io.WriteUInt32(at2, 0x00001234);
                int unchanged = scanner.Refilter(fresh.Candidates, ValueMatch.Unchanged, 0);
                Check("未变筛选（Unchanged）保留未改动的两处", unchanged == 2, "保留 " + unchanged + " 个");

                int changedAgain = scanner.Refilter(fresh.Candidates, ValueMatch.Changed, 0);
                Check("对剩余候选再次筛选（Changed）返回 0", changedAgain == 0, "保留 " + changedAgain + " 个");

                // 上一轮筛选已清空候选，这里重新取一份完整列表再验证变小筛选
                io.WriteUInt32(at2, marker);
                io.WriteUInt32(at1, marker);
                ValueScanReport fresh2 = scanner.Scan(regions, marker, options, CancellationToken.None, null);
                io.WriteUInt32(at1, marker - 1);
                int decreased = scanner.Refilter(fresh2.Candidates, ValueMatch.Decreased, 0);
                Check("变小筛选（Decreased）只保留变小的那一处",
                    decreased == 1 && fresh2.Candidates[0].Address == at1, "保留 " + decreased + " 个");

                int none = scanner.Refilter(fresh2.Candidates, ValueMatch.EqualTo, 0xDEADBEEF);
                Check("按不存在的值筛选返回 0", none == 0, "保留 " + none + " 个");

                // ---------- 2 字节（short）字段 ----------
                // 真机教训：电力这类字段可能是 short，按 4 字节比时高 2 位是邻居字段的数据，
                // 数值永远对不上，表现就是"界面上明明有数字却一个候选都搜不到"。
                uint narrowAt = blockAddress + 0x3000;
                io.WriteUInt16(narrowAt, 300);
                io.WriteUInt16(narrowAt + 2, 0x1234);   // 邻居非零，4 字节比较一定失败

                ValueScanOptions narrowOptions = new ValueScanOptions();
                narrowOptions.FourByteAligned = true;
                narrowOptions.MatchUInt16 = true;

                ValueScanReport narrowReport = scanner.Scan(
                    regions, 300, narrowOptions, CancellationToken.None, null);
                Check("2 字节模式能找到 short 字段",
                    ContainsAddress(narrowReport.Candidates, narrowAt),
                    "候选 " + narrowReport.Candidates.Count + " 个");

                ValueScanReport wideReport = scanner.Scan(
                    regions, 300, options, CancellationToken.None, null);
                Check("4 字节模式找不到该 short 字段（复现真机现象）",
                    !ContainsAddress(wideReport.Candidates, narrowAt),
                    "候选 " + wideReport.Candidates.Count + " 个");

                int narrowKept = scanner.Refilter(narrowReport.Candidates, ValueMatch.EqualTo, 300);
                Check("2 字节候选按自身宽度读取后仍能筛中",
                    narrowKept >= 1 && narrowReport.Candidates[0].Width == 2,
                    "保留 " + narrowKept + " 个");

                bool wrote = scanner.WriteValue(at1, 0x11223344);
                uint readBack;
                bool readOk = io.ReadUInt32(at1, out readBack) && readBack == 0x11223344;
                Check("候选地址可写入并回读校验", wrote && readOk,
                    string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", readBack));

                // RegionSelector 行为检查
                RegionSelector heapSelector = RegionSelector.ForHeapData();
                List<MemoryRegion> allRegions = io.EnumRegions(0x00010000ul, 0x7FFFFFFFul, true, false, 0);
                List<MemoryRegion> filtered = heapSelector.Apply(allRegions);
                bool filterOk = filtered.Count > 0 && filtered.Count < allRegions.Count;
                for (int i = 0; i < filtered.Count && filterOk; i++)
                {
                    if (filtered[i].Type != Win32Api.MEM_PRIVATE || !filtered[i].Writable)
                    {
                        filterOk = false;
                    }
                }
                Check("RegionSelector 只保留私有可写区域", filterOk,
                    allRegions.Count + " → " + filtered.Count + "（" + heapSelector.Describe() + "）");
            }
            catch (Exception ex)
            {
                Check("数值搜索", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (block != IntPtr.Zero)
                {
                    Win32Api.VirtualFreeEx(self.Handle, block, IntPtr.Zero, MEM_RELEASE);
                }
                self.Dispose();
            }
        }

        private static void CheckProfileStore()
        {
            string tempRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_selftest_tmp");
            try
            {
                if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, true); }

                ProfileStore store = new ProfileStore(tempRoot);
                CalibrationProfile profile = new CalibrationProfile();
                profile.GameVersion = "3.3.6";
                profile.ExeCrc32 = "0x1B499086";
                profile.ModuleBase = 0x00400000;
                profile.ModuleSize = 0x793000;
                profile.Money.SetOffsets(new uint[] { 0xA8B230, 0x30 });
                profile.Money.Fingerprint = "10000";
                profile.OffsetTechnoHealth = 0x6C;
                profile.DefaultMoney = 250000;

                string saveError;
                bool saved = store.Save(profile, out saveError);
                Check("校准配置可保存", saved && File.Exists(store.ProfilePath), saveError);

                CalibrationProfile loaded = store.Load();
                uint[] loadedOffsets = loaded.Money.Chain.Offsets;
                bool same = loaded.GameVersion == "3.3.6"
                    && loaded.ModuleBase == 0x00400000
                    && loaded.ModuleSize == 0x793000
                    && loaded.Money.IsCalibrated
                    && loadedOffsets.Length == 2
                    && loadedOffsets[0] == 0xA8B230
                    && loadedOffsets[1] == 0x30
                    && loaded.Money.Fingerprint == "10000"
                    && loaded.OffsetTechnoHealth == 0x6C
                    && loaded.DefaultMoney == 250000;
                Check("校准配置读回且字段一致", same, loaded.Describe());

                Check("配置写在工具自身目录内",
                    store.ProfilePath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                    store.ProfilePath);

                Check("未校准项被正确判定",
                    !loaded.HasSelection && !loaded.HasTechnoArray
                    && loaded.CalibratedItemCount() == 1
                    && loaded.MissingItems().Count == 4,
                    "已校准 " + loaded.CalibratedItemCount() + " 项，待校准 " + loaded.MissingItems().Count + " 项");

                string reportPath = store.WriteReport(loaded, null, "自检样本环境", new string[] { "自检样本明细行" });
                bool reportOk = !string.IsNullOrEmpty(reportPath) && File.Exists(reportPath);
                Check("校准报告可生成", reportOk,
                    reportPath == null ? "生成失败" : Path.GetFileName(reportPath));

                if (reportOk)
                {
                    string content = File.ReadAllText(reportPath, Encoding.UTF8);
                    bool hasAnchor = content.IndexOf("A8B230", StringComparison.Ordinal) >= 0;
                    bool hasTitle = content.IndexOf("MO Trainer 校准报告", StringComparison.Ordinal) >= 0;
                    Check("报告内容包含环境、偏移链与表达式", hasAnchor && hasTitle, content.Length + " 字符");
                }

                Check("环境指纹一致时判定为匹配",
                    loaded.MatchesEnvironment(0x00400000, 0x793000, "0x1B499086"), "ok");
                Check("exe CRC 变化时判定为不匹配",
                    !loaded.MatchesEnvironment(0x00400000, 0x793000, "0xDEADBEEF"), "ok");

                CalibrationProfile blank = new CalibrationProfile();
                Check("空配置的未校准列表完整",
                    !blank.IsCalibratedAtAll && blank.MissingItems().Count == 5, blank.Describe());
            }
            catch (Exception ex)
            {
                Check("校准配置持久化", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, true); } }
                catch { }
            }
        }

        // ============================================================ S3 模块

        /// <summary>
        /// 合成校准目标：用一块 RW 内存冒充"模块映像"，另一块冒充"堆对象"。
        /// 这样整套校准逻辑（数值搜索 / 指针回溯 / 向导状态机）都能在没有游戏的情况下跑通。
        /// </summary>
        private sealed class SyntheticTarget : ICalibrationTarget
        {
            private readonly MemoryIO _memory;
            private readonly uint _moduleBase;
            private readonly uint _moduleSize;
            private readonly List<MemoryRegion> _heap;
            private readonly List<MemoryRegion> _pointer;

            public SyntheticTarget(
                MemoryIO memory, uint moduleBase, uint moduleSize,
                List<MemoryRegion> heap, List<MemoryRegion> pointer)
            {
                _memory = memory;
                _moduleBase = moduleBase;
                _moduleSize = moduleSize;
                _heap = heap;
                _pointer = pointer;
            }

            public bool IsReady { get { return true; } }
            public MemoryIO Memory { get { return _memory; } }
            public uint ModuleBase { get { return _moduleBase; } }
            public uint ModuleSize { get { return _moduleSize; } }
            public List<MemoryRegion> HeapRegions { get { return _heap; } }
            public List<MemoryRegion> PointerRegions { get { return _pointer; } }
            public string DescribeEnvironment() { return "合成环境（自检）"; }
        }

        /// <summary>
        /// 「自动巡检」的自检（2026-10-09 新增）。
        ///
        /// 为什么必须在这里先证明它是对的：这一整套东西的产出是**一份报告**，
        /// 而报告是拿去离线分析、进而**改真机地址**的依据。它要是悄悄算错了，
        /// 后果不是"这次没采到"，而是"我照着错的结论把地址固化下来"——
        /// 那正是 `CLAUDE.md` 7.3 里"没有任何线索却把最像的候选存进配置"那一类事故。
        ///
        /// 场景刻意做成"真机该有的样子"：
        ///   · 6 个同类型表对象，其中 +0x0E4 是**共用的类型对象指针**、+0xF0 是**共用的阵营指针**、
        ///     +0x10 是**各自不同**的指针（按对象，不是按类型）；
        ///   · 类型对象里放一个建造 ID 字符串（GAPOWR），阵营对象在金钱偏移处放你的金钱。
        /// 期望：认出 +0x0E4 并给出 ID 字符串；把 +0xF0 标成"像房子"排除；
        ///       偏移 0（类型表本身）与 +0x10（按对象）都不该出现在候选里。
        /// </summary>
        private static void CheckAutoInspection()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("自动巡检", false, "OpenProcess 失败");
                return;
            }

            IntPtr moduleBlock = IntPtr.Zero;
            IntPtr heapBlock = IntPtr.Zero;
            try
            {
                moduleBlock = AllocLowBlock(self.Handle);
                heapBlock = AllocLowBlock(self.Handle);
                if (moduleBlock == IntPtr.Zero || heapBlock == IntPtr.Zero)
                {
                    Check("自动巡检测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(moduleBlock) || !IsLowAddress(heapBlock))
                {
                    WarnItem("自动巡检", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint moduleBase = (uint)moduleBlock.ToInt64();
                uint moduleSize = TestBlockSize;
                uint heap = (uint)heapBlock.ToInt64();
                MemoryIO io = new MemoryIO(self);

                const uint TypeOffset = 0x0E4;      // 该被认出来的那个
                const uint OwnerOffset = 0xF0;      // 应该被"像房子"排除掉
                const uint PerObjectOffset = 0x10;  // 按对象，不该出现在候选里
                const uint MoneyOffset = 0x30C;

                uint vtable = moduleBase + 0x2000;          // 类型表（偏移 0），必须被跳过
                uint typeObject = heap + 0x9000;            // "说明书"：同型号共用一份
                uint idString = heap + 0x9400;
                uint house = heap + 0xA000;                 // 阵营房子
                const uint MyMoney = 5000;

                // 对象之间刻意隔开 0x1000：否则各自的 0x800 头部窗口会互相重叠，
                // 读到的"共用值"其实来自邻居 —— 那是场景没搭好，不是被测代码有问题。
                uint[] objects = new uint[6];
                for (int i = 0; i < objects.Length; i++)
                {
                    objects[i] = heap + 0x2000 + (uint)(i * 0x1000);
                    io.WriteUInt32(objects[i], vtable);
                    io.WriteUInt32(objects[i] + TypeOffset, typeObject);      // 共用的"说明书"
                    io.WriteUInt32(objects[i] + OwnerOffset, house);          // 共用的"阵营"
                    io.WriteUInt32(objects[i] + PerObjectOffset, heap + 0xC000 + (uint)(i * 0x10)); // 各自不同
                }

                // 类型对象：里面有一个指向建造 ID 字符串的指针
                io.WriteUInt32(typeObject + 0x10, idString);
                byte[] name = Encoding.ASCII.GetBytes("GAPOWR\0");
                io.Write(idString, name, 0, name.Length);

                // 阵营房子：金钱偏移处正好是"你的金钱" —— 这就是把阵营指针认出来的判据
                io.WriteUInt32(house + MoneyOffset, MyMoney);

                List<HarvestObjectRow> roster = new List<HarvestObjectRow>();
                for (int i = 0; i < objects.Length; i++)
                {
                    HarvestObjectRow row = new HarvestObjectRow();
                    row.Address = objects[i];
                    row.VTable = vtable;
                    roster.Add(row);
                }

                TypePointerAnalysis analysis = AutoInspection.FindTypePointers(
                    io, moduleBase, moduleSize, roster, MoneyOffset, MyMoney);

                Check("自动巡检：统计到了对象（没把名单读空）",
                    analysis.Groups == 1 && analysis.MembersScanned == objects.Length,
                    analysis.Groups + " 组 / " + analysis.MembersScanned + " 个对象");

                TypePointerHit type = null;
                TypePointerHit owner = null;
                bool sawOffset0 = false;
                bool sawPerObject = false;
                for (int i = 0; i < analysis.Hits.Count; i++)
                {
                    if (analysis.Hits[i].Offset == TypeOffset) { type = analysis.Hits[i]; }
                    if (analysis.Hits[i].Offset == OwnerOffset) { owner = analysis.Hits[i]; }
                    if (analysis.Hits[i].Offset == 0) { sawOffset0 = true; }
                    if (analysis.Hits[i].Offset == PerObjectOffset) { sawPerObject = true; }
                }

                Check("自动巡检：认出「多个对象共用同一取值」的字段（+0x0E4）",
                    type != null, type == null ? "没找到" : "找到");
                Check("自动巡检：在候选里找到了建造 ID 字符串（GAPOWR）",
                    type != null && type.IdString == "GAPOWR",
                    type == null ? "—" : "「" + type.IdString + "」");
                Check("自动巡检：把「阵营指针」认出来并标注为像房子（不能当类型对象）",
                    owner != null && owner.LooksLikeHouse,
                    owner == null ? "没找到 +0xF0" : "像房子=" + owner.LooksLikeHouse);

                // 偏移 0 是类型表本身：它同样"同类相同"，但它是**假阳性**，
                // 不排掉就会排在候选第一位，把人引到完全错误的结论上去。
                Check("自动巡检：偏移 0（类型表本身）没有混进候选",
                    !sawOffset0, sawOffset0 ? "混进来了" : "已排除");

                // 每个对象各不相同的指针 = 按对象的东西，不是按类型
                Check("自动巡检：按对象的指针（各自不同）没有混进候选",
                    !sawPerObject, sawPerObject ? "混进来了" : "已排除");

                Check("自动巡检：结论指向正确的那个字段（+0x0E4）",
                    analysis.Best != null && analysis.Best.Offset == TypeOffset,
                    analysis.Best == null ? "没有结论"
                        : "结论 +0x" + analysis.Best.Offset.ToString("X", CultureInfo.InvariantCulture));

                string text = AutoInspection.FormatTypePointers(analysis, true);
                Check("自动巡检：报告段落写得出结论（含字段偏移与 ID）",
                    text.IndexOf("结论：") >= 0 && text.IndexOf("GAPOWR") >= 0,
                    "段落 " + text.Length + " 字符");

                // 名单读空时必须**说得出为什么**，不能悄悄给一份空分析
                TypePointerAnalysis empty = AutoInspection.FindTypePointers(
                    io, moduleBase, moduleSize, new List<HarvestObjectRow>(), MoneyOffset, MyMoney);
                Check("自动巡检：名单为空时如实说明（不静默给空结果）",
                    empty.Note.Length > 0 && empty.Hits.Count == 0, empty.Note);

                // ---- 主链反查（[S3]）----
                // 真链路：在内存里造一条"静态根 → 中转 → 房子"的指针链，
                // 让反查必须能把它找出来。只断言"不报错"是不够的 ——
                // 那样把扫描整个删掉也照样绿。
                uint hop = heap + 0x8000;
                io.WriteUInt32(hop + 0x8, house);              // 中转 +0x8 → 房子
                io.WriteUInt32(moduleBase + 0x100, hop);       // 静态根 → 中转

                List<MemoryRegion> regions = new List<MemoryRegion>();
                regions.Add(MakeRegion(moduleBase, TestBlockSize));
                regions.Add(MakeRegion(heap, TestBlockSize));
                SyntheticTarget target = new SyntheticTarget(io, moduleBase, moduleSize, regions, regions);

                CalibrationProfile moneyProfile = new CalibrationProfile();
                moneyProfile.Money.SetOffsets(new uint[] { 0x100, 0x8, MoneyOffset });

                string refit = AutoInspection.RefitMainChain(target, moneyProfile, 5);
                Check("自动巡检：主链反查能从真实房子地址反推出静态根",
                    refit.IndexOf("反查成功", StringComparison.Ordinal) >= 0
                        && refit.IndexOf(
                            "0x" + house.ToString("X8", CultureInfo.InvariantCulture),
                            StringComparison.Ordinal) >= 0,
                    refit.Replace("\n", " ").Replace("\r", ""));

                // 缺前置时必须说得出缺什么，不能悄悄返回一段空话
                string noMoney = AutoInspection.RefitMainChain(target, new CalibrationProfile(), 5);
                Check("自动巡检：金钱未校准时主链反查如实说明（不静默什么都不做）",
                    noMoney.IndexOf("无法反查", StringComparison.Ordinal) >= 0,
                    noMoney.Replace("\n", " ").Replace("\r", ""));
            }
            catch (Exception ex)
            {
                Check("自动巡检", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (moduleBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, moduleBlock, IntPtr.Zero, MEM_RELEASE); }
                if (heapBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, heapBlock, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
            }
        }

        private static void CheckPointerScanner()
        {            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("指针回溯", false, "OpenProcess 失败");
                return;
            }

            IntPtr moduleBlock = IntPtr.Zero;
            IntPtr houseBlock = IntPtr.Zero;
            try
            {
                moduleBlock = AllocLowBlock(self.Handle);
                houseBlock = AllocLowBlock(self.Handle);
                if (moduleBlock == IntPtr.Zero || houseBlock == IntPtr.Zero)
                {
                    Check("指针回溯测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(moduleBlock) || !IsLowAddress(houseBlock))
                {
                    WarnItem("指针回溯", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint moduleAddress = (uint)moduleBlock.ToInt64();
                uint houseAddress = (uint)houseBlock.ToInt64();
                MemoryIO io = new MemoryIO(self);

                // 合成布局：模块 .data 里的全局指针 → 堆对象；堆对象 +0x30 是"金钱"
                uint staticSlot = moduleAddress + 0x100;
                uint moneyAddress = houseAddress + 0x30;
                io.WriteUInt32(staticSlot, houseAddress);
                io.WriteUInt32(moneyAddress, 10000);

                List<MemoryRegion> regions = new List<MemoryRegion>();
                regions.Add(MakeRegion(moduleAddress, TestBlockSize));
                regions.Add(MakeRegion(houseAddress, TestBlockSize));

                PointerScanner scanner = new PointerScanner(io);
                PointerScanOptions options = new PointerScanOptions();

                PointerScanReport report = scanner.Scan(
                    moneyAddress, moduleAddress, TestBlockSize, regions, options, CancellationToken.None, null);

                bool unique = report.Paths.Count == 1
                    && report.Paths[0].Offsets.Length == 2
                    && report.Paths[0].Offsets[0] == 0x100
                    && report.Paths[0].Offsets[1] == 0x30;
                Check("指针回溯找到唯一静态路径", unique,
                    report.Summary() + (report.Paths.Count > 0
                        ? "｜" + report.Paths[0].Format(moduleAddress) : ""));

                if (unique)
                {
                    uint resolved;
                    string error;
                    bool resolveOk = report.Paths[0].ToChain().TryResolve(
                        io, moduleAddress, out resolved, out error) && resolved == moneyAddress;
                    Check("回溯出的偏移链能解析回原始地址", resolveOk,
                        resolveOk
                            ? string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", resolved)
                            : error);
                }

                // 没有任何指针指向的地址必须返回 0 条路径，否则会给出假配置
                uint orphan = houseAddress + 0x9000;
                io.WriteUInt32(orphan, 42);
                PointerScanReport none = scanner.Scan(
                    orphan, moduleAddress, TestBlockSize, regions, options, CancellationToken.None, null);
                Check("没有静态指针指向的目标返回 0 条路径", none.Paths.Count == 0, none.Summary());

                Check("指针回溯留有逐层轨迹", report.Trace.Count >= 2,
                    report.Trace.Count + " 行");
            }
            catch (Exception ex)
            {
                Check("指针回溯", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (moduleBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, moduleBlock, IntPtr.Zero, MEM_RELEASE); }
                if (houseBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, houseBlock, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
            }
        }

        // ============================================================ S4 模块

        private static void CheckTrainerSettings()
        {
            string tempRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_selftest_settings");
            try
            {
                if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, true); }

                TrainerSettings settings = new TrainerSettings();
                settings.SetValue(CheatId.Money, 888888);
                settings.SetEnabled(CheatId.Money, true);
                settings.SetValue(CheatId.PowerOutput, 30000);
                settings.SetEnabled(CheatId.PowerOutput, false);

                string path = settings.PathIn(tempRoot);
                Check("运行设置可以保存", settings.Save(path) && File.Exists(path), path);

                TrainerSettings reloaded = new TrainerSettings();
                bool loaded = reloaded.Load(path);
                Check("运行设置可以重新加载", loaded, reloaded.Describe());
                Check("数值与开关状态都被正确还原",
                    reloaded.GetValue(CheatId.Money, 0) == 888888
                    && reloaded.GetEnabled(CheatId.Money)
                    && reloaded.GetValue(CheatId.PowerOutput, 0) == 30000
                    && !reloaded.GetEnabled(CheatId.PowerOutput),
                    reloaded.Describe());

                // 配置坏掉不该让程序起不来
                File.WriteAllText(path, "{ 这不是合法 JSON ", new UTF8Encoding(true));
                TrainerSettings broken = new TrainerSettings();
                Check("损坏的运行设置文件被安全忽略（不抛异常）",
                    !broken.Load(path) && broken.GetValue(CheatId.Money, 123) == 123, "ok");

                TrainerSettings missing = new TrainerSettings();
                Check("运行设置文件缺失时安全忽略",
                    !missing.Load(Path.Combine(tempRoot, "nope.json")), "ok");
            }
            catch (Exception ex)
            {
                Check("运行设置", false, ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, true); } }
                catch { }
            }
        }

        /// <summary>
        /// 锁定引擎：在合成环境验证"读-比-写"闭环、宽度感知、以及绑定配置时的禁用规则。
        /// 这一层是"功能到底有没有真的生效"的核心，必须在没有游戏的情况下也能回归。
        /// </summary>
        private static void CheckCheatEngine()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("锁定引擎", false, "OpenProcess 失败");
                return;
            }

            IntPtr moduleBlock = IntPtr.Zero;
            IntPtr houseBlock = IntPtr.Zero;
            try
            {
                moduleBlock = AllocLowBlock(self.Handle);
                houseBlock = AllocLowBlock(self.Handle);
                if (moduleBlock == IntPtr.Zero || houseBlock == IntPtr.Zero)
                {
                    Check("锁定引擎测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(moduleBlock) || !IsLowAddress(houseBlock))
                {
                    WarnItem("锁定引擎", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint moduleAddress = (uint)moduleBlock.ToInt64();
                uint houseAddress = (uint)houseBlock.ToInt64();
                MemoryIO io = new MemoryIO(self);

                uint moneyAddress = houseAddress + 0x30;
                uint narrowAddress = houseAddress + 0x50;

                io.WriteUInt32(moduleAddress + 0x100, houseAddress);
                io.WriteUInt32(moneyAddress, 5000);
                io.WriteUInt16(narrowAddress, 7);

                List<MemoryRegion> heap = new List<MemoryRegion>();
                heap.Add(MakeRegion(houseAddress, TestBlockSize));
                List<MemoryRegion> pointer = new List<MemoryRegion>();
                pointer.Add(MakeRegion(moduleAddress, TestBlockSize));
                pointer.Add(MakeRegion(houseAddress, TestBlockSize));

                SyntheticTarget target = new SyntheticTarget(
                    io, moduleAddress, (uint)TestBlockSize, heap, pointer);

                CalibrationProfile profile = new CalibrationProfile();
                profile.Money.SetOffsets(new uint[] { 0x100, 0x30 });
                profile.PowerOutput.SetOffsets(new uint[] { 0x100, 0x50 });
                profile.PowerOutput.ValueWidth = 2;

                TrainerSettings settings = new TrainerSettings();
                CheatEngine engine = new CheatEngine(target, settings);

                CheatFeature money = engine.Add(new CheatFeature(
                    CheatId.Money, "金钱", "测试用", SynchRisk.Medium, true));
                money.TargetValue = 123456;
                money.Enabled = true;                       // 故意先开启，验证绑定时的兜底

                CheatFeature power = engine.Add(new CheatFeature(
                    CheatId.PowerOutput, "电力", "测试用", SynchRisk.Low, true));
                power.TargetValue = 100;
                power.Enabled = true;

                CheatFeature uncalibrated = engine.Add(new CheatFeature(
                    CheatId.SelectedInvincible, "未校准项", "测试用", SynchRisk.High, false));
                uncalibrated.Enabled = true;

                engine.BindProfile(profile);

                Check("绑定配置后未校准的功能被自动关闭",
                    !uncalibrated.Enabled && !uncalibrated.IsCalibrated,
                    "IsCalibrated=" + uncalibrated.IsCalibrated);
                Check("已校准的功能保持开启", money.Enabled && money.IsCalibrated, "ok");
                Check("引擎登记了全部功能项", engine.Features.Count == 3,
                    engine.Features.Count + " 项");

                // ---- 刻意停用（2026-10-09）：**校准数据齐全也不许碰内存** ----
                //
                // 电力就是这种形态：`profile.json` 里是"已校准"（当年读数核对通过），
                // 真机实测却证明那个位置只是界面显示值。只判 IsCalibrated 的写法
                // 会一路放行到写入 —— 所以这里给它一份**完全合法**的定位信息，
                // 只把停用位挂上，验证引擎确实一个字节都不写。
                uint disabledAddress = houseAddress + 0x60;
                io.WriteUInt32(disabledAddress, 4242);

                CalibrationProfile disabledProfile = new CalibrationProfile();
                disabledProfile.PowerOutput.SetOffsets(new uint[] { 0x100, 0x60 });

                CheatEngine disabledEngine = new CheatEngine(target, new TrainerSettings());
                CheatFeature disabled = disabledEngine.Add(new CheatFeature(
                    CheatId.PowerOutput, "停用项", "测试用", SynchRisk.Low, true));
                disabled.TargetValue = 999;
                disabled.Enabled = true;                      // 故意先开启，验证绑定时的兜底
                disabled.UnavailableReason = FeatureAvailability.ReasonFor(CheatId.PowerOutput);
                disabledEngine.BindProfile(disabledProfile);

                Check("刻意停用的功能在绑定时被自动关闭（即使它是「已校准」的）",
                    !disabled.Enabled && disabled.IsCalibrated,
                    "Enabled=" + disabled.Enabled + "｜IsCalibrated=" + disabled.IsCalibrated);

                // 再强行打开一次：模拟"有人绕过界面、直接改标志"或"旧配置里存着开启状态"
                disabled.Enabled = true;
                disabledEngine.Start();
                Thread.Sleep(250);
                disabledEngine.Stop();

                Check("刻意停用的功能即使被强行打开，也一个字节都不写",
                    io.ReadUInt32Or(disabledAddress, 0) == 4242 && disabled.WriteCount == 0,
                    "当前 " + io.ReadUInt32Or(disabledAddress, 0)
                    + "（期望 4242）｜写入 " + disabled.WriteCount + " 次");

                // 无敌的必要条件里不再有"满血值"。
                // 满血值因单位类型而异（动员兵 125、坦克几百），拿某个单位的满血值去锁别的单位，
                // 会被"绝不往下压"的安全判断全部跳过 —— 表现为"开了无敌完全没用"。
                // 锁定值改由运行时按"各单位见过的最高血量"决定，所以这里**故意不给满血值**。
                // 用一个独立引擎实例验证，免得影响上面"功能项个数"这类断言。
                CalibrationProfile invincibleProfile = new CalibrationProfile();
                invincibleProfile.SelectedCount.SetOffsets(new uint[] { 0x300 });
                invincibleProfile.SelectedList.SetOffsets(new uint[] { 0x304 });
                invincibleProfile.OffsetTechnoHealth = 0x6C;
                invincibleProfile.TechnoMaxHealth = 0;

                CheatEngine invincibleEngine = new CheatEngine(target, new TrainerSettings());
                CheatFeature invincible = invincibleEngine.Add(new CheatFeature(
                    CheatId.SelectedInvincible, "无敌", "测试用", SynchRisk.High, false));
                invincibleEngine.BindProfile(invincibleProfile);
                Check("缺满血值也能启用无敌（锁定值改由运行时按各单位最高血量决定）",
                    invincible.Locator != null,
                    invincible.Locator == null ? "被判为不可用" : "ok");

                engine.Start();
                Check("引擎线程已启动", engine.IsRunning, "ok");

                bool locked = WaitUntil(delegate { return io.ReadUInt32Or(moneyAddress, 0) == 123456; }, 2000);
                Check("锁定线程把字段写成目标值", locked,
                    "当前 " + io.ReadUInt32Or(moneyAddress, 0));

                // 关键回归：模拟游戏自己改掉数值，引擎必须在一两轮内写回
                io.WriteUInt32(moneyAddress, 42);
                bool restored = WaitUntil(delegate { return io.ReadUInt32Or(moneyAddress, 0) == 123456; }, 1500);
                Check("数值被游戏改动后立即写回（这就是「锁定」）", restored,
                    "当前 " + io.ReadUInt32Or(moneyAddress, 0));

                bool narrowOk = WaitUntil(delegate
                {
                    byte[] raw = io.ReadBytes(narrowAddress, 2);
                    return raw != null && raw.Length >= 2 && raw[0] == 100 && raw[1] == 0;
                }, 1500);
                Check("2 字节字段被按短整型写入（宽度信息生效）", narrowOk, "ok");

                Check("写入次数被统计（界面靠它证明功能在工作）",
                    money.WriteCount > 0, "写入 " + money.WriteCount + " 次");
                Check("引擎持续运行", engine.Ticks > 0, engine.Ticks + " 轮");

                // 关闭后必须彻底停手，否则玩家以为关了其实还在改
                money.Enabled = false;
                Thread.Sleep(120);
                io.WriteUInt32(moneyAddress, 777);
                Thread.Sleep(200);
                Check("关闭功能后不再回写",
                    io.ReadUInt32Or(moneyAddress, 0) == 777,
                    "当前 " + io.ReadUInt32Or(moneyAddress, 0));

                engine.Stop();
                Check("停止后线程不再运行", !engine.IsRunning, "ok");

                // 指针链全部失效时必须停手，不能往垃圾地址写
                CalibrationProfile brokenProfile = new CalibrationProfile();
                brokenProfile.Money.SetOffsets(new uint[] { 0xDEAD0, 0x30 });
                engine.BindProfile(brokenProfile);
                money.Enabled = true;
                money.TargetValue = 555;
                engine.Start();
                Thread.Sleep(250);
                Check("指针链失效时不写入、并给出可读的错误",
                    money.Address == 0 && money.LastError.Length != 0,
                    "Address=0x" + money.Address.ToString("X") + "｜" + money.LastError);
                engine.Stop();
            }
            catch (Exception ex)
            {
                Check("锁定引擎", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (moduleBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, moduleBlock, IntPtr.Zero, MEM_RELEASE); }
                if (houseBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, houseBlock, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
            }
        }

        /// <summary>
        /// 功能卡片的「点击 → 用户意图」链路。
        ///
        /// 这条断言是被真机打脸之后补的：开关状态在抛出 Toggled 之前被刷新回了旧值，
        /// 主窗口读到的是"状态没变"，于是点开关毫无反应、开关自己弹回原位。
        /// 纯界面逻辑自检本来覆盖不到，所以加一条让它在合成环境里就能暴露。
        /// </summary>
        private static void CheckFeatureCardInteraction()
        {
            FeatureCard card = new FeatureCard();
            FeatureCard uncalibratedCard = new FeatureCard();
            try
            {
                CheatFeature feature = new CheatFeature(
                    CheatId.Money, "金钱", "测试用", SynchRisk.Medium, true);
                feature.TargetValue = 12345;
                feature.Locator = new FieldLocator();
                feature.Locator.SetOffsets(new uint[] { 0x100, 0x30 });
                card.Bind(feature);

                int toggled = 0;
                card.Toggled += delegate { toggled++; };

                Check("已校准的功能卡片开关可操作", !card.Toggle.ReadOnly, "ok");

                card.Toggle.Toggle();
                Check("点击开关后用户意图被正确记录（不会被刷新回旧值）",
                    card.DesiredEnabled && toggled == 1,
                    "意图=" + card.DesiredEnabled + "｜事件 " + toggled + " 次");

                // 主窗口裁决通过后同步视觉
                feature.Enabled = true;
                card.RefreshState();
                Check("裁决通过后开关保持开启", card.Toggle.Checked, "ok");

                card.Toggle.Toggle();
                Check("再次点击记录为「关闭」意图", !card.DesiredEnabled && toggled == 2,
                    "意图=" + card.DesiredEnabled + "｜事件 " + toggled + " 次");

                CheatFeature raw = new CheatFeature(
                    CheatId.SelectedInvincible, "未校准", "测试用", SynchRisk.High, false);
                uncalibratedCard.Bind(raw);
                uncalibratedCard.Toggle.Toggle();
                Check("未校准的功能开关点不动",
                    !uncalibratedCard.Toggle.Checked && !uncalibratedCard.DesiredEnabled,
                    "Checked=" + uncalibratedCard.Toggle.Checked);
            }
            catch (Exception ex)
            {
                Check("功能卡片交互", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                card.Dispose();
                uncalibratedCard.Dispose();
            }
        }

        /// <summary>
        /// 「刻意停用」的功能必须**处处都点不动**（2026-10-09 新增）。
        ///
        /// 背景：电力原来的做法（锁游戏界面的显示值）被真机实测推翻，可 `profile.json` 里
        /// 它仍然是"已校准"的 —— 于是 `IsCalibrated` 这一条**根本拦不住它**：
        /// 开关照旧能打开、引擎照旧往那块内存里写。这正是 `CLAUDE.md` §4.2 点名禁止的
        /// "开关能点、实际没用"（而且比"没用"更糟：它真的在改内存）。
        ///
        /// 这里钉死三件事：
        ///   ① 电力**确实**被声明为停用（依据是 `FeatureAvailability`，不是某处一句界面文案）；
        ///   ② 卡片上写明原因、开关点不动；
        ///   ③ 别的功能没有被误伤。
        /// 引擎层"一个字节都不写"在 `CheckCheatEngine` 里单独验。
        /// </summary>
        private static void CheckUnavailableFeature()
        {
            FeatureCard card = null;
            FeatureCard control = null;
            try
            {
                string reason = FeatureAvailability.ReasonFor(CheatId.PowerOutput);
                Check("电力被显式声明为「暂不可用」（依据是代码里的声明，不是界面文案）",
                    reason.Length > 0, "原因：" + reason);

                // 状态行只有一行位置：太长会被卡片底边压掉。
                // 布局这类事一律用数字量，别靠看（CLAUDE.md 7.2）。
                Check("停用原因足够短、放得进卡片状态行",
                    reason.Length <= 40, reason.Length + " 字");

                int wrongly = 0;
                CheatId[] all = (CheatId[])Enum.GetValues(typeof(CheatId));
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == CheatId.PowerOutput) { continue; }
                    if (FeatureAvailability.ReasonFor(all[i]).Length > 0) { wrongly++; }
                }
                Check("其它功能没有被误标为停用", wrongly == 0, "误标 " + wrongly + " 项");

                // 卡片：**故意给它一份完整合法的定位信息**，模拟电力现在的真实处境
                //（校准数据看着是好的）。若哪天有人把"停用"判断挪到"未校准"后面，
                // 这条会立刻转红 —— 那正是本轮要防的回归。
                CheatFeature power = new CheatFeature(
                    CheatId.PowerOutput, "电力", "测试用", SynchRisk.Low, true);
                power.TargetValue = 30000;
                power.Locator = new FieldLocator();
                power.Locator.SetOffsets(new uint[] { 0x100, 0x53A4 });
                power.UnavailableReason = reason;

                Check("（前提）被停用的功能确实是「已校准」状态 —— 否则这条断言测不到东西",
                    power.IsCalibrated && !power.IsUsable,
                    "IsCalibrated=" + power.IsCalibrated + "｜IsUsable=" + power.IsUsable);

                card = new FeatureCard();
                card.Bind(power);
                Check("已校准但被停用的功能：开关置灰",
                    card.Toggle.ReadOnly && !card.Toggle.Checked, "ok");

                card.Toggle.Toggle();
                Check("已校准但被停用的功能：点了也开不起来",
                    !card.Toggle.Checked && !card.DesiredEnabled,
                    "Checked=" + card.Toggle.Checked + "｜意图=" + card.DesiredEnabled);

                // 状态行必须**说得出原因**。只断言"开关点不动"是不够的 ——
                // 玩家看到灰开关却不知道下一步该干什么，一样是白搭。
                Check("停用功能的状态行写明了原因（不是一句空话）",
                    card.StatusText.IndexOf("暂不可用") >= 0
                    && card.StatusText.IndexOf(reason) >= 0,
                    card.StatusText);

                // 顺序回归：一个功能"又被停用、又没校准"时，必须先说"暂不可用"。
                // 说成"尚未校准"，玩家会跑到校准页反复折腾一个根本修不好的东西。
                // （这条是改坏验证逼出来的：原来只测"已校准+停用"，把停用分支挪到
                //   未校准分支之后照样全绿 —— 因为那时前一个分支根本不会命中。）
                FeatureCard bothCard = new FeatureCard();
                try
                {
                    CheatFeature both = new CheatFeature(
                        CheatId.PowerOutput, "又停用又未校准", "测试用", SynchRisk.Low, true);
                    both.UnavailableReason = reason;
                    bothCard.Bind(both);
                    Check("「又停用、又没校准」时，状态行说的是停用（而不是让人白跑一趟校准）",
                        bothCard.StatusText.IndexOf("暂不可用") >= 0
                        && bothCard.StatusText.IndexOf("尚未校准") < 0,
                        bothCard.StatusText);
                }
                finally
                {
                    bothCard.Dispose();
                }

                // 对照组：同一份定位信息，只把停用位摘掉，开关就该恢复正常。
                // 没有这一条，"开关打不开"就无法证明是**因为停用**，而不是因为别的原因。
                control = new FeatureCard();
                power.UnavailableReason = "";
                control.Bind(power);
                Check("对照组：摘掉停用位后开关恢复可操作（证明上面确实是停用挡住的）",
                    !control.Toggle.ReadOnly && power.IsUsable, "IsUsable=" + power.IsUsable);
            }
            catch (Exception ex)
            {
                Check("刻意停用功能", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (card != null) { card.Dispose(); }
                if (control != null) { control.Dispose(); }
            }
        }

        // ============================================================ S5 模块

        /// <summary>
        /// 静态全局变量定位：地址 = 模块基址 + 偏移，**绝不能解引用**。
        ///
        /// 真机实测「选中数量」就是这一类 —— 模块 .data 里的全局变量
        /// （实测地址 0x00B08EC8，模块基址 0x00400000，偏移 0x708EC8）。
        /// 如果拿它去套指针链，解析出来会变成"变量内容"而不是变量本身，后续全错。
        /// </summary>
        private static void CheckStaticAddressLocator()
        {
            FieldLocator locator = new FieldLocator();
            locator.SetStaticAddress(0x708EC8);

            uint resolved = locator.ResolveOrZero(null, 0x400000);
            Check("静态地址解析为「模块基址 + 偏移」且不解引用",
                locator.IsCalibrated && resolved == 0x00B08EC8,
                string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", resolved));

            Check("静态地址的描述里标明是全局变量",
                locator.Describe(0x400000).IndexOf("全局变量", StringComparison.Ordinal) >= 0,
                locator.Describe(0x400000));

            // 跨会话恒定：换个模块基址时结果应随之平移，而不是读到别的内存
            Check("静态地址随模块基址平移",
                locator.ResolveOrZero(null, 0x00400000 + 0x1000) == 0x00B09EC8,
                string.Format(CultureInfo.InvariantCulture, "0x{0:X8}",
                    locator.ResolveOrZero(null, 0x00400000 + 0x1000)));

            // JSON 往返
            FieldLocator reloaded = FieldLocator.FromJson(locator.ToJson());
            Check("静态地址能被写入配置并读回",
                reloaded.HasStaticAddress && reloaded.StaticOffset == 0x708EC8
                && reloaded.ResolveOrZero(null, 0x400000) == 0x00B08EC8,
                reloaded.Describe(0x400000));

            FieldLocator cleared = FieldLocator.FromJson(locator.ToJson());
            cleared.Clear();
            Check("清空后静态地址不再生效",
                !cleared.IsCalibrated && cleared.ResolveOrZero(null, 0x400000) == 0, "ok");
        }

        /// <summary>
        /// 回归：数值搜索默认的区域集**只含私有页**，游戏模块的 .data 是 image 类型、
        /// 会被整体排除在外。于是"某个值是不是全局变量"这种问题，
        /// 无论把搜索范围放多大都搜不到 —— 因为那一大块从一开始就不在搜索区域里。
        ///
        /// 这个坑在真机上让"选中数量"卡了好几轮：看着像"字段不存在"，
        /// 实际是搜索范围漏了整整一个段。
        /// </summary>
        private static void CheckScanRegionCoverage()
        {
            List<MemoryRegion> all = new List<MemoryRegion>();
            all.Add(MakeTypedRegion(0x10000000, 0x1000, Win32Api.MEM_PRIVATE));
            all.Add(MakeTypedRegion(0x20000000, 0x1000, Win32Api.MEM_IMAGE));

            List<MemoryRegion> heap = RegionSelector.ForHeapData().Apply(all);
            Check("堆数据选择器会排除模块映像页（.data）",
                heap.Count == 1 && heap[0].Type == Win32Api.MEM_PRIVATE,
                "覆盖 " + heap.Count + " 个区域");

            int imageWritable = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Type == Win32Api.MEM_IMAGE && all[i].Writable) { imageWritable++; }
            }
            Check("模块数据段能被单独取出（全局变量住在那里）",
                imageWritable == 1, "找到 " + imageWritable + " 个可写映像页");
        }

        private static void CheckChainDerivation()
        {
            FieldLocator baseLocator = new FieldLocator();
            baseLocator.SetOffsets(new uint[] { 0x100, 0x30 });

            long delta;
            List<PointerChain> derived = ChainDerivation.Derive(
                baseLocator, 0x21000030, 0x21000600, out delta);

            Check("链推导：相对偏移计算正确", delta == 0x5D0,
                "+0x" + delta.ToString("X", CultureInfo.InvariantCulture));
            Check("链推导：只改写尾段偏移",
                derived.Count == 1
                && derived[0].Offsets.Length == 2
                && derived[0].Offsets[0] == 0x100
                && derived[0].Offsets[1] == 0x600,
                derived.Count > 0 ? derived[0].Serialize() : "无");

            long unused;
            Check("链推导：基准未校准时返回空",
                ChainDerivation.Derive(new FieldLocator(), 0x1000, 0x2000, out unused).Count == 0, "ok");
            Check("链推导：地址为 0 时返回空",
                ChainDerivation.Derive(baseLocator, 0, 0x2000, out unused).Count == 0, "ok");
        }

        private static void CheckObjectFieldHunter()
        {
            byte[] before = new byte[64];
            byte[] after = new byte[64];

            HexUtil.WriteUInt32(before, 0x10, 500);
            HexUtil.WriteUInt32(after, 0x10, 380);            // 变小 120：像血量
            HexUtil.WriteUInt32(before, 0x18, 1000000);
            HexUtil.WriteUInt32(after, 0x18, 100);            // 暴降 999900：更像指针被释放
            HexUtil.WriteUInt32(before, 0x20, 0x12345678);
            HexUtil.WriteUInt32(after, 0x20, 0x12345678);     // 没变
            HexUtil.WriteUInt32(before, 0x30, 1000);
            HexUtil.WriteUInt32(after, 0x30, 999900);         // 变大

            ObjectSnapshot snapBefore = new ObjectSnapshot();
            snapBefore.BaseAddress = 0x10000000;
            snapBefore.Bytes = before;
            ObjectSnapshot snapAfter = new ObjectSnapshot();
            snapAfter.BaseAddress = 0x10000000;
            snapAfter.Bytes = after;

            List<FieldChange> wide = ObjectFieldHunter.FindDecreased(
                snapBefore, snapAfter, 1, 2000000, 1, 2000000, 60);
            Check("字段猎手只挑出「变小」的字段（变大/未变的不出现）",
                wide.Count == 2 && wide[0].Offset == 0x10 && wide[1].Offset == 0x18,
                wide.Count > 0 ? wide[0].Describe() : "0 个");

            // 收紧变化幅度上限：999900 那种暴降会被排除，只留下像血量那一处
            List<FieldChange> tight = ObjectFieldHunter.FindDecreased(
                snapBefore, snapAfter, 1, 2000000, 1, 20000, 60);
            Check("变化幅度超过上限的字段被过滤",
                tight.Count == 1 && tight[0].Offset == 0x10,
                "候选 " + tight.Count + " 个");

            // 受伤前数值超出合理血量区间也必须被过滤（0x10 变成 9000 万，那个量级不可能是一击的伤害）
            HexUtil.WriteUInt32(before, 0x10, 90000000);
            HexUtil.WriteUInt32(after, 0x10, 80000000);
            List<FieldChange> rangeFiltered = ObjectFieldHunter.FindDecreased(
                snapBefore, snapAfter, 1, 2000000, 1, 2000000, 60);
            Check("受伤前数值超出合理区间时被过滤",
                rangeFiltered.Count == 1 && rangeFiltered[0].Offset == 0x18,
                "候选 " + rangeFiltered.Count + " 个");

            ObjectSnapshot empty = new ObjectSnapshot();
            Check("快照无效时返回空而不是抛异常",
                ObjectFieldHunter.FindDecreased(empty, snapAfter, 1, 100, 1, 100, 10).Count == 0, "ok");
        }

        /// <summary>
        /// 日志导出：必须真的能落盘、内容完整。
        ///
        /// 这个功能的全部价值就是"把界面放不下的日志带出来"。
        /// 一旦路径解析错、或写入被拒，用户在排查现场是看不出问题的 ——
        /// 只会拿到一个空文件或者干脆没文件，然后又得重跑一轮。所以必须有断言盯着。
        /// </summary>
        private static void CheckLogExporter()
        {
            List<LogEntry> entries = new List<LogEntry>();
            entries.Add(new LogEntry(new DateTime(2026, 9, 21, 21, 30, 0),
                LogLevel.Info, "Unit", "监视起始：选中数量 1，附近找到 2 个疑似对象数组槽位"));
            entries.Add(new LogEntry(new DateTime(2026, 9, 21, 21, 30, 5),
                LogLevel.Warn, "Unit", "对象归属校验：2 个跟踪对象里有 1 个引用了你的阵营对象"));

            string path = null;
            try
            {
                path = LogExporter.Export(entries, "自检");

                Check("日志导出：文件真的落盘了",
                    !string.IsNullOrEmpty(path) && File.Exists(path), path);
                Check("日志导出：落在 reports 目录下（不会被 7 天清理规则删掉）",
                    !string.IsNullOrEmpty(path)
                    && path.IndexOf("reports", StringComparison.OrdinalIgnoreCase) >= 0, path);

                string content = File.ReadAllText(path, Encoding.UTF8);
                Check("日志导出：正文完整（中文与内容都在）",
                    content.IndexOf("附近找到 2 个疑似对象数组槽位", StringComparison.Ordinal) >= 0
                    && content.IndexOf("对象归属校验", StringComparison.Ordinal) >= 0,
                    content.Length + " 字符");
            }
            finally
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try { File.Delete(path); } catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// 布局：日志标题栏的**停靠顺序**。
        ///
        /// 这一条是真机踩出来的：日志标题 `Dock=Fill`、右侧两个按钮 `Dock=Right`。
        /// 若标题**先于**按钮添加，标题会先占满整条宽度，把两个按钮挤成一个字宽 ——
        /// 用户截图里只剩「打开日」「导出日」。**不是按钮被窗口切掉，是被标题挤没了。**
        /// WinForms 的停靠按 z 序结算，正确写法是：按钮先加，Fill 控件最后加并 BringToFront。
        /// 这里用真实控件跑一遍，把"按钮完整落在标题栏内、标题不覆盖按钮、两者不重叠"钉死。
        /// </summary>
        private static void CheckLogHeaderDocking()
        {
            Form host = new Form();
            host.ClientSize = new Size(1000, 660);

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 28;
            host.Controls.Add(header);

            Button openDir = MoTrainer.Ui.Controls.UiFactory.MakeButton("打开日志目录", 100);
            openDir.Dock = DockStyle.Right;
            header.Controls.Add(openDir);

            Button export = MoTrainer.Ui.Controls.UiFactory.MakeButton("导出日志", 80);
            export.Dock = DockStyle.Right;
            header.Controls.Add(export);

            Label title = new Label();
            title.Text = "运行日志";
            title.AutoSize = false;
            title.Dock = DockStyle.Fill;
            header.Controls.Add(title);
            title.BringToFront();

            host.PerformLayout();
            header.PerformLayout();

            string geometry = string.Format(CultureInfo.InvariantCulture,
                "标题栏宽 {0}｜打开目录 {1}+{2}｜导出日志 {3}+{4}｜标题 {5}+{6}",
                header.ClientSize.Width, openDir.Left, openDir.Width,
                export.Left, export.Width, title.Left, title.Width);

            Check("日志停靠：两个按钮完整落在标题栏内（不被挤扁）",
                openDir.Width >= 100 && export.Width >= 80
                && openDir.Right <= header.ClientSize.Width
                && export.Right <= header.ClientSize.Width,
                geometry);

            Check("日志停靠：标题不覆盖按钮",
                title.Right <= export.Left,
                string.Format(CultureInfo.InvariantCulture, "标题右 {0} ≤ 导出左 {1}", title.Right, export.Left));

            // 两个按钮都是 Dock=Right：**先添加的停靠在外层**（贴窗口右边缘），
            // 后添加的排在它左边。断言方向据此来，别写反。
            Check("日志停靠：两个按钮互不重叠",
                openDir.Right <= export.Left,
                string.Format(CultureInfo.InvariantCulture, "打开目录右 {0} ≤ 导出左 {1}", openDir.Right, export.Left));

            host.Dispose();
        }

        /// <summary>
        /// 布局：超长文本不得把页面顶宽。
        ///
        /// 页面用 `TableLayoutPanel + AutoSize + AutoSize 的 Label` 承载内容，
        /// 而 AutoSize 的 Label 只往宽里长、不换行 —— 一句长文案就能把整张表的
        /// **期望宽度**顶大，让 AutoScroll 面板冒出横向滚动条（用户实测右边缘被滚出视野）。
        /// `UiFactory.ClampLabelWidths` 给长标签设 MaximumSize（只限宽、不限高）后，
        /// 文本改为就地折行，页面的期望宽度不再被文案绑架。
        /// </summary>
        private static void CheckLabelClamp()
        {
            Panel page = new Panel();
            page.ClientSize = new Size(400, 200);

            Label longLabel = new Label();
            longLabel.AutoSize = true;
            longLabel.Text = new string('宽', 200);
            page.Controls.Add(longLabel);

            int before = longLabel.PreferredWidth;
            MoTrainer.Ui.Controls.UiFactory.ClampLabelWidths(page);
            int after = longLabel.PreferredWidth;

            Check("超长标签被限宽：不再顶宽页面",
                after <= page.ClientSize.Width,
                string.Format(CultureInfo.InvariantCulture,
                    "原期望宽 {0} → 限宽后 {1}（页面可用 {2}）", before, after, page.ClientSize.Width));

            Check("限宽只限宽、不限高（长文本仍能完整折行显示）",
                longLabel.MaximumSize.Height == 0,
                string.Format(CultureInfo.InvariantCulture, "MaximumSize={0}", longLabel.MaximumSize));

            page.Dispose();
        }

        /// <summary>
        /// 多对象变化按偏移汇总 —— 本次真机 bug 的核心逻辑。
        ///
        /// 真机教训：挨打的往往不是第一个选中的单位。玩家为了让某个单位挨打，
        /// 必须先取消选中、换别的单位强制攻击、再把它选回来（用户实测反馈），
        /// 所以"第 2 步那一刻的第一个对象"很可能压根没挨打 ——
        /// 实际现象是对比了 8KB 内存却"一个字节都没变"。
        /// 正确做法是**把所有选中单位都比一遍**，再按偏移汇总：
        /// 多个单位同时变化的那个偏移，才是所有单位共有的字段（血量）。
        /// </summary>
        private static void CheckMergeByOffset()
        {
            // 单位 A：偏移 0x20 掉血，另有 0x40 也变小
            List<FieldChange> a = new List<FieldChange>();
            a.Add(MakeFieldChange(0x20, 500, 380));
            a.Add(MakeFieldChange(0x40, 9000, 8000));

            // 单位 B：只有 0x20 掉血 —— B 才是真正挨打的那个
            List<FieldChange> b = new List<FieldChange>();
            b.Add(MakeFieldChange(0x20, 500, 100));

            // 单位 C：完全没变化
            List<FieldChange> c = new List<FieldChange>();

            List<List<FieldChange>> perObject = new List<List<FieldChange>>();
            perObject.Add(a);
            perObject.Add(b);
            perObject.Add(c);

            List<FieldChangeSummary> merged = ObjectFieldHunter.MergeByOffset(perObject);

            Check("多对象变化按偏移汇总：同一偏移自动合并", merged.Count == 2,
                merged.Count + " 个偏移");

            FieldChangeSummary top = merged[0];
            Check("吻合单位数最多的偏移排第一（那才是共有字段）",
                top.Offset == 0x20 && top.AffectedObjects == 2,
                string.Format(CultureInfo.InvariantCulture,
                    "+0x{0:X} 吻合 {1} 个单位", top.Offset, top.AffectedObjects));

            Check("汇总保留差异最大的那次作为代表值",
                top.Before == 500 && top.After == 100, top.Before + " → " + top.After);

            Check("只有一个单位变化的偏移排在后面",
                merged[1].Offset == 0x40 && merged[1].AffectedObjects == 1,
                string.Format(CultureInfo.InvariantCulture, "+0x{0:X}", merged[1].Offset));

            Check("空输入返回空列表而不是抛异常",
                ObjectFieldHunter.MergeByOffset(null).Count == 0
                && ObjectFieldHunter.MergeByOffset(new List<List<FieldChange>>()).Count == 0, "ok");

            // 同为一个单位变化时，按"掉幅比例"排：
            // 血量是按"打惨"来操作的，跌幅必然大；坐标 / 计时器这类跌幅零碎。
            // 早先按"原始值越小越靠前"排，反而把 4、23 这种小数值噪声顶到前面。
            List<FieldChange> noise = new List<FieldChange>();
            noise.Add(MakeFieldChange(0x80, 9000, 8900));   // 掉 1%
            List<FieldChange> health = new List<FieldChange>();
            health.Add(MakeFieldChange(0xD0, 400, 23));     // 掉 94%

            List<List<FieldChange>> two = new List<List<FieldChange>>();
            two.Add(noise);
            two.Add(health);

            List<FieldChangeSummary> ranked = ObjectFieldHunter.MergeByOffset(two);
            Check("同为单对象变化时，掉幅大的排前面",
                ranked[0].Offset == 0xD0 && ranked[0].DropPermille > 900,
                string.Format(CultureInfo.InvariantCulture,
                    "第一 +0x{0:X}（-{1}%）", ranked[0].Offset, ranked[0].DropPermille / 10));

            // 归属优先：真机实测，只按掉幅排会让**非本方结构**里 6312 → 170（-97%）
            // 顶到第一，而真正的血量是动员兵 125 → 23（-82%）。归属标记必须参与排序。
            FieldChange foreign = MakeFieldChange(0x400, 6312, 170);   // 掉 97%，但非本方
            foreign.Owned = false;
            FieldChange mine = MakeFieldChange(0x4C4, 125, 23);        // 掉 82%，本方
            mine.Owned = true;

            List<FieldChange> foreignList = new List<FieldChange>();
            foreignList.Add(foreign);
            List<FieldChange> mineList = new List<FieldChange>();
            mineList.Add(mine);

            List<List<FieldChange>> mix = new List<List<FieldChange>>();
            mix.Add(foreignList);
            mix.Add(mineList);

            List<FieldChangeSummary> byOwner = ObjectFieldHunter.MergeByOffset(mix);
            Check("本方对象上的变化优先于非本方的更大跌幅",
                byOwner[0].Offset == 0x4C4 && byOwner[0].OwnedObjects == 1,
                string.Format(CultureInfo.InvariantCulture,
                    "第一 +0x{0:X}（本方{1}个，-{2}%）",
                    byOwner[0].Offset, byOwner[0].OwnedObjects, byOwner[0].DropPermille / 10));
        }

        private static FieldChange MakeFieldChange(uint offset, uint before, uint after)
        {
            FieldChange change = new FieldChange();
            change.Offset = offset;
            change.Before = before;
            change.After = after;
            return change;
        }

        /// <summary>
        /// 选中列表探测：在合成内存里摆一个"动态容器"（数量 + 数组指针 + 对象数组），
        /// 验证程序能认出它，且不会在普通内存上误报。
        /// </summary>
        private static void CheckSelectionProbe()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("选中列表探测", false, "OpenProcess 失败");
                return;
            }

            IntPtr block = IntPtr.Zero;
            uint savedHintBase = SelectionProbe.ModuleBaseHint;
            uint savedHintSize = SelectionProbe.ModuleSizeHint;
            try
            {
                block = AllocLowBlock(self.Handle);
                if (block == IntPtr.Zero)
                {
                    Check("选中列表探测测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(block))
                {
                    WarnItem("选中列表探测", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint blockAddress = (uint)block.ToInt64();
                MemoryIO io = new MemoryIO(self);

                const int count = 3;
                uint arrayAddress = blockAddress + 0x8000;
                uint countAddress = blockAddress + 0x1040;

                // 声明一块"假模块映像"并**真的把模块区间灌进去**。
                //
                // 早先这里没设模块区间，于是"对象头必须落在模块区间内"那条硬校验被跳过 ——
                // 测试是靠"校验没跑"通过的，等于没测到它。而且留下一个地雷：
                // 只要别的测试先设过模块区间，这一节就会莫名其妙地挂。
                // 现在对象头写成模块区间内的地址，校验真的跑起来。
                uint fakeModuleBase = blockAddress + 0x8000;
                uint fakeModuleSize = 0x4000;
                SelectionProbe.SetModuleRange(fakeModuleBase, fakeModuleSize);

                uint[] objects = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    objects[i] = blockAddress + 0x2000 + (uint)(i * 0x100);
                    // 对象头 = 模块区间内的"类型表地址"，这样它才算个真对象
                    io.WriteUInt32(objects[i], fakeModuleBase + 0x1000 + (uint)(i * 4));
                    io.WriteUInt32(arrayAddress + (uint)(i * 4), objects[i]);
                }
                io.WriteUInt32(countAddress, count);
                io.WriteUInt32(countAddress + 8, arrayAddress);           // 数组指针与数量相邻

                SelectionProbeResult probe = SelectionProbe.Probe(io, countAddress, count, 0x40);
                Check("选中列表探测：认出指针数组结构",
                    probe.Found && probe.ArraySlotAddress == countAddress + 8,
                    probe.Found ? "数组槽 0x" + probe.ArraySlotAddress.ToString("X8") : probe.Error);
                Check("选中列表探测：取出的对象地址与构造一致",
                    probe.ObjectAddresses.Count == count
                    && probe.ObjectAddresses[0] == objects[0]
                    && probe.ObjectAddresses[count - 1] == objects[count - 1],
                    "共 " + probe.ObjectAddresses.Count + " 个");

                // 反向：附近只有一堆 0 时绝不能报"找到了"
                uint lonely = blockAddress + 0x3000;
                io.WriteUInt32(lonely, count);
                SelectionProbeResult none = SelectionProbe.Probe(io, lonely, count, 0x40);
                Check("选中列表探测：普通内存不误报", !none.Found, none.Error);

                // 数量为 0 / 越界时必须拒绝，而不是去读一段垃圾
                Check("选中列表探测：数量为 0 时拒绝",
                    !SelectionProbe.Probe(io, countAddress, 0, 0x40).Found, "ok");
                Check("选中列表探测：数量越界时拒绝",
                    !SelectionProbe.Probe(io, countAddress, 9999, 0x40).Found, "ok");

                // ---- 真机事故回归（2026-09-29 用户采集）----
                //
                // 那次采集的"选中对象"里有 8 个是**模块内的代码地址**：
                // 它们的头 4 字节读出来是 0x8BF18B56 这类值，拆开是 MOV/PUSH EBX/PUSH ESI
                // 这些机器指令 —— 那是代码，不是对象。
                // 根因是"模块区间"提示字段留了 0，第 ④ 条硬校验被**静默跳过**。

                // ① 头不在模块区间内的"对象"，必须被拒
                uint codeArray = blockAddress + 0x4000;
                uint codeCount = blockAddress + 0x5000;
                uint[] codeHeads = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    codeHeads[i] = blockAddress + 0x2400 + (uint)(i * 0x100);
                    io.WriteUInt32(codeHeads[i], 0x8BF18B56u + (uint)i);   // 像机器指令的"类型表"
                    io.WriteUInt32(codeArray + (uint)(i * 4), codeHeads[i]);
                }
                io.WriteUInt32(codeCount, count);
                io.WriteUInt32(codeCount + 8, codeArray);

                SelectionProbeResult junk = SelectionProbe.Probe(io, codeCount, count, 0x40);
                Check("选中列表探测：头不在模块区间内的「代码地址」被拒（真机混进来过 8 个）",
                    !junk.Found, junk.Found ? "误报为选中列表！" : junk.Error);

                // ② 不知道模块区间时必须拒绝探测，而不是悄悄跳过校验
                SelectionProbe.SetModuleRange(0, 0);
                SelectionProbeResult blocked = SelectionProbe.ProbeWithEscalation(io, countAddress, count);
                Check("选中列表探测：不知道模块区间时拒绝探测（不静默跳过安全检查）",
                    !blocked.Found && blocked.Error.Length > 0, blocked.Error);

                // ③ 反过来：模块区间给对了，同一批对象必须能被认出来
                SelectionProbe.SetModuleRange(fakeModuleBase, fakeModuleSize);
                SelectionProbeResult ok = SelectionProbe.ProbeWithEscalation(io, countAddress, count);
                Check("选中列表探测：模块区间正确时正常认出（拒绝不是把好数据也挡了）",
                    ok.Found && ok.ObjectAddresses.Count == count,
                    ok.Found ? "共 " + ok.ObjectAddresses.Count + " 个" : ok.Error);
            }
            catch (Exception ex)
            {
                Check("选中列表探测", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // 本节改了全局提示字段，收工时恢复原值，免得影响后面的检查
                SelectionProbe.SetModuleRange(savedHintBase, savedHintSize);
                if (block != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, block, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
            }
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) { return true; }
                Thread.Sleep(20);
            }
            return condition();
        }

        private static void CheckWizard()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady)
            {
                Check("校准向导", false, "OpenProcess 失败");
                return;
            }

            IntPtr moduleBlock = IntPtr.Zero;
            IntPtr houseBlock = IntPtr.Zero;
            string tempRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_selftest_wizard");

            try
            {
                moduleBlock = AllocLowBlock(self.Handle);
                houseBlock = AllocLowBlock(self.Handle);
                if (moduleBlock == IntPtr.Zero || houseBlock == IntPtr.Zero)
                {
                    Check("校准向导测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(moduleBlock) || !IsLowAddress(houseBlock))
                {
                    WarnItem("校准向导", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint moduleAddress = (uint)moduleBlock.ToInt64();
                uint houseAddress = (uint)houseBlock.ToInt64();
                MemoryIO io = new MemoryIO(self);

                uint moneyAddress = houseAddress + 0x30;
                uint powerAddress = houseAddress + 0x50;
                uint noiseAddress = houseAddress + 0x70;
                uint hugeAddress = houseAddress + 0x80;

                io.WriteUInt32(moduleAddress + 0x100, houseAddress); // 静态指针变量
                // 第二个指向同一对象的全局：实测游戏里就是这种情况，
                // 且不同会话"能被找到的那几个"会变，所以要验证多候选链机制
                io.WriteUInt32(moduleAddress + 0x200, houseAddress);
                io.WriteUInt32(moneyAddress, 10000);                 // 金钱
                io.WriteUInt32(powerAddress, 100);                   // 电力消耗
                io.WriteUInt32(noiseAddress, 0);                     // 干扰项
                io.WriteUInt32(hugeAddress, 0);                      // 超大值干扰项

                if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, true); }
                ProfileStore store = new ProfileStore(tempRoot);
                CalibrationProfile profile = new CalibrationProfile();

                List<MemoryRegion> heap = new List<MemoryRegion>();
                heap.Add(MakeRegion(houseAddress, TestBlockSize));
                List<MemoryRegion> pointer = new List<MemoryRegion>();
                pointer.Add(MakeRegion(moduleAddress, TestBlockSize));
                pointer.Add(MakeRegion(houseAddress, TestBlockSize));

                SyntheticTarget target = new SyntheticTarget(
                    io, moduleAddress, (uint)TestBlockSize, heap, pointer);
                CalibrationWizard wizard = new CalibrationWizard(target, store, profile);

                // ---------- 金钱 ----------
                bool started = wizard.Start();
                Check("向导开始并进入金钱定位",
                    started && wizard.Step == WizardStep.MoneyScan, wizard.StepTitle);

                bool scanned = wizard.SubmitMoneyValue(10000);
                Check("金钱首轮扫描收敛到唯一候选并进入验证",
                    scanned && wizard.Step == WizardStep.MoneyVerify && wizard.MoneyAddress == moneyAddress,
                    wizard.Status);

                bool probed = wizard.WriteProbe();
                uint probeRead;
                io.ReadUInt32(moneyAddress, out probeRead);
                Check("写入测试值生效",
                    probed && probeRead == wizard.ProbeValue,
                    string.Format(CultureInfo.InvariantCulture, "读回 {0}，期望 {1}", probeRead, wizard.ProbeValue));

                Check("测试值只比当前金额高一点点（避免游戏里数字滚半天）",
                    wizard.ProbeValue > 10000 && wizard.ProbeValue <= 10000 + 50000,
                    "当前 10000 → 测试值 " + wizard.ProbeValue);

                bool confirmed = wizard.ConfirmProbe();
                Check("确认测试值后进入消费检验步骤",
                    confirmed && wizard.Step == WizardStep.MoneySpendTest, wizard.StepTitle);

                // 模拟玩家花掉"增量的一半"：这是旧门槛（中点）刚好会误判的临界量，
                // 新门槛（原值 + 增量/4）必须仍然判定为权威字段
                uint probeValue = wizard.ProbeValue;
                uint probeDelta = probeValue - 10000;
                io.WriteUInt32(moneyAddress, probeValue - probeDelta / 2);
                bool spendOk = wizard.VerifySpend();
                uint[] moneyOffsets = profile.Money.Chain.Offsets;
                bool moneyChainOk = spendOk && moneyOffsets.Length == 2
                    && moneyOffsets[0] == 0x100 && moneyOffsets[1] == 0x30;
                Check("消费检验通过并锁定金钱指针链", moneyChainOk, profile.Money.Describe(moduleAddress));

                Check("多条静态链被全部保留（对抗会话差异）",
                    profile.Money.ChainCount == 2, "保留 " + profile.Money.ChainCount + " 条候选链");

                // 主链失效时必须能自动回退到备用链
                FieldLocator fallback = new FieldLocator();
                fallback.SetOffsets(new uint[] { 0xDEAD0, 0x30 });  // 指向模块外的空地址，必然失败
                List<PointerChain> moneyChains = profile.Money.Chains;
                fallback.AddChain(moneyChains[0]);
                uint fallbackAddress = fallback.ResolveOrZero(io, moduleAddress);
                Check("主链失效时自动回退到备用链", fallbackAddress == moneyAddress,
                    string.Format(CultureInfo.InvariantCulture, "回退解析到 0x{0:X8}", fallbackAddress));

                FieldLocator allBroken = new FieldLocator();
                allBroken.SetOffsets(new uint[] { 0xDEAD0, 0x30 });
                Check("所有候选链都失效时不返回脏地址", allBroken.ResolveOrZero(io, moduleAddress) == 0, "ok");

                uint restored;
                io.ReadUInt32(moneyAddress, out restored);
                Check("检验通过后自动把金钱还原为原值", restored == 10000, "当前值 " + restored);

                bool moneyResolves = profile.Money.ResolveOrZero(io, moduleAddress) == moneyAddress;
                Check("金钱指针链能解析回原字段地址", moneyResolves,
                    string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", profile.Money.ResolveOrZero(io, moduleAddress)));

                Check("向导进入电力定位步骤", wizard.Step == WizardStep.PowerScan, wizard.StepTitle);

                // ---------- 电力：读数精确匹配 ----------
                // 合成环境里"负载"= 0x50 处的 4 字节 100。
                // "电力"刻意放在 0x600 处，且是 **short 250**、邻居字段非零 ——
                // 这同时复现了真机上的两个现象：
                //   ① short 字段按 4 字节比搜不到 → 验证自动换编码；
                //   ② 字段在对象内偏移 0x600 > 指针回溯上限 0x400 → 验证改用金钱链推导。
                io.WriteUInt16(houseAddress + 0x600, 250);
                io.WriteUInt16(houseAddress + 0x602, 0x1234);

                bool powerFirst = wizard.SubmitPowerValue(100);
                Check("电力首轮按读数精确匹配命中唯一候选",
                    powerFirst && wizard.Step == WizardStep.PowerVerify
                    && wizard.PowerCandidates.Count == 1
                    && wizard.PowerCandidates[0].Address == powerAddress,
                    "候选 " + wizard.PowerCandidates.Count + " 个｜" + wizard.Status);

                bool powerConfirmed = wizard.ConfirmPowerField(powerAddress, 100);
                uint[] powerOffsets = profile.PowerDrain.Chain.Offsets;
                bool powerChainOk = powerConfirmed && powerOffsets.Length == 2
                    && powerOffsets[0] == 0x100 && powerOffsets[1] == 0x50;
                Check("「负载」字段拿到静态指针链", powerChainOk, profile.PowerDrain.Describe(moduleAddress));

                Check("确认负载后自动进入「电力」定位",
                    wizard.Step == WizardStep.PowerScan && wizard.PowerFieldName == "电力",
                    wizard.StepTitle + " · " + wizard.PowerFieldName);

                bool outputFirst = wizard.SubmitPowerValue(250);
                Check("「电力」读数匹配命中唯一候选（4 字节搜不到时自动改用 2 字节）",
                    outputFirst && wizard.Step == WizardStep.PowerVerify
                    && wizard.PowerCandidates.Count == 1
                    && wizard.PowerCandidates[0].Address == houseAddress + 0x600,
                    "候选 " + wizard.PowerCandidates.Count + " 个｜" + wizard.Status);

                bool outputConfirmed = wizard.ConfirmPowerField(houseAddress + 0x600, 250);
                uint[] outputOffsets = profile.PowerOutput.Chain.Offsets;
                bool outputChainOk = outputConfirmed && outputOffsets.Length == 2
                    && outputOffsets[0] == 0x100 && outputOffsets[1] == 0x600;
                Check("「电力」字段在对象深处时改由金钱链推导出指针链", outputChainOk,
                    profile.PowerOutput.Describe(moduleAddress));

                Check("推导出的链能真正解析回该字段地址",
                    profile.PowerOutput.ResolveOrZero(io, moduleAddress) == houseAddress + 0x600,
                    string.Format(CultureInfo.InvariantCulture, "0x{0:X8}",
                        profile.PowerOutput.ResolveOrZero(io, moduleAddress)));

                Check("推导来源被记进配置备注（便于日后排查）",
                    profile.PowerOutput.Note.IndexOf("推导", StringComparison.Ordinal) >= 0,
                    profile.PowerOutput.Note);

                Check("自动识别出的数值宽度被写进配置（short → 2 字节）",
                    profile.PowerOutput.ValueWidth == 2 && profile.PowerDrain.ValueWidth == 4,
                    "电力 " + profile.PowerOutput.DescribeEncoding()
                    + "｜负载 " + profile.PowerDrain.DescribeEncoding());

                Check("向导进入完成状态", wizard.Step == WizardStep.Completed, wizard.Status);
                Check("校准报告已生成",
                    !string.IsNullOrEmpty(wizard.ReportPath) && File.Exists(wizard.ReportPath),
                    wizard.ReportPath);

                if (!string.IsNullOrEmpty(wizard.ReportPath) && File.Exists(wizard.ReportPath))
                {
                    string content = File.ReadAllText(wizard.ReportPath, Encoding.UTF8);
                    bool hasMoney = content.IndexOf("[金钱]", StringComparison.Ordinal) >= 0;
                    bool hasPower = content.IndexOf("[电力消耗]", StringComparison.Ordinal) >= 0;
                    bool hasEnv = content.IndexOf("MO Trainer 校准报告", StringComparison.Ordinal) >= 0;
                    Check("报告含环境、金钱与电力三个部分", hasMoney && hasPower && hasEnv,
                        content.Length + " 字符");

                    Check("报告列出全部候选链",
                        content.IndexOf("候选链 2", StringComparison.Ordinal) >= 0, "ok");
                }

                CalibrationProfile reloaded = store.Load();
                uint[] reloadedMoney = reloaded.Money.Chain.Offsets;
                Check("向导保存的配置可被重新加载",
                    reloaded.HasMoney && reloaded.HasPowerDrain
                    && reloadedMoney.Length == 2 && reloadedMoney[0] == 0x100,
                    reloaded.Describe());

                // ---------- 影子字段必须被消费检验排除 ----------
                // 影子字段的特征：写入测试值能让显示变化，但一消费就回落到"原值 - 消费额"
                uint shadowAddress = houseAddress + 0xB0;
                io.WriteUInt32(shadowAddress, 33333);
                CalibrationWizard shadow = new CalibrationWizard(target, store, new CalibrationProfile());
                shadow.Start();
                bool shadowScanned = shadow.SubmitMoneyValue(33333);
                bool shadowProbed = shadow.WriteProbe();
                bool shadowConfirmed = shadow.ConfirmProbe();
                // 影子字段：消费后回落到"原值 - 消费额"，而不是从写入的测试值扣减
                io.WriteUInt32(shadowAddress, 33333 - 500);
                bool shadowPassed = shadow.VerifySpend();
                Check("影子字段被消费检验识别并排除",
                    shadowScanned && shadowProbed && shadowConfirmed && !shadowPassed
                    && shadow.Step == WizardStep.MoneyScan,
                    shadow.Status);

                // ---------- 失败路径：候选被排除后应退回输入 ----------
                CalibrationWizard second = new CalibrationWizard(target, store, new CalibrationProfile());
                second.Start();
                second.SubmitMoneyValue(10000);
                second.RejectProbe();
                Check("排除候选后退回金钱输入步骤", second.Step == WizardStep.MoneyScan, second.Status);

                // ---------- 未连接游戏时必须拒绝开始 ----------
                CalibrationWizard offline = new CalibrationWizard(
                    new OfflineTarget(), store, new CalibrationProfile());
                bool offlineOk = !offline.Start() && offline.Step == WizardStep.Failed;
                Check("未连接游戏时向导拒绝开始", offlineOk, offline.Status);
            }
            catch (Exception ex)
            {
                Check("校准向导", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (moduleBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, moduleBlock, IntPtr.Zero, MEM_RELEASE); }
                if (houseBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, houseBlock, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
                try { if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, true); } }
                catch { }
            }
        }

        /// <summary>模拟"游戏没开"的目标，用于验证向导的拒绝路径。</summary>
        private sealed class OfflineTarget : ICalibrationTarget
        {
            public bool IsReady { get { return false; } }
            public MemoryIO Memory { get { return null; } }
            public uint ModuleBase { get { return 0; } }
            public uint ModuleSize { get { return 0; } }
            public List<MemoryRegion> HeapRegions { get { return new List<MemoryRegion>(); } }
            public List<MemoryRegion> PointerRegions { get { return new List<MemoryRegion>(); } }
            public string DescribeEnvironment() { return "未连接"; }
        }

        // ============================================================ 输出

        private static void Section(string title)
        {
            Write("--- " + title + " ---");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                _pass++;
                Write("  [PASS] " + name + (string.IsNullOrEmpty(detail) ? "" : "  |  " + detail));
                Logger.Debug("SelfTest", "PASS " + name + " | " + detail);
            }
            else
            {
                _fail++;
                Write("  [FAIL] " + name + "  |  " + detail);
                Logger.Error("SelfTest", "FAIL " + name + " | " + detail);
            }
        }

        private static void WarnItem(string name, string detail)
        {
            _warn++;
            Write("  [WARN] " + name + "  |  " + detail);
            Logger.Warn("SelfTest", "WARN " + name + " | " + detail);
        }

        private static void Write(string line)
        {
            Console.WriteLine(line);
        }

        /// <summary>当前进程是否以管理员身份运行。</summary>
        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        // ============================================================ S6 模块

        /// <summary>
        /// S6 三项批量操作共用的合成场景：三块 RW 内存分别冒充
        /// "游戏模块映像 / 游戏堆 / 游戏数据段"，于是整套对象遍历与批量操作
        /// 都能在没有游戏的情况下跑通。
        ///
        /// 为什么连"模块区间"都要造假：遍历器唯一的硬判据就是
        /// "对象头 4 字节必须落在模块区间内"。不造出这个区间，
        /// **"名单地址找错时能不能识破"**这条最关键的断言就无从验证 ——
        /// 而那正是"往错误内存反复写值"这类事故的第一道防线。
        /// </summary>
        private sealed class TechnoScene : IDisposable
        {
            /// <summary>对象内字段偏移，与真机 profile 的取值一致，让测试贴近实际。</summary>
            public const uint OwnerOffset = 0xF0;
            public const uint HealthOffset = 0x6C;
            public const uint ProgressOffset = 0x100;

            /// <summary>
            /// 假模块区间取 16MB。必须大到能容下一个"4 字节全是可打印字符"的值
            /// （见 <see cref="AsciiVTable"/>），否则"文本被当成类型表"那条判据永远走不到。
            /// </summary>
            public const uint FakeModuleSize = 0x1000000;

            /// <summary>「一大批对象」用的紧凑布局：步长 0x80，字段偏移相应缩小。</summary>
            public const uint CrowdOwnerOffset = 0x40;
            public const uint CrowdHealthOffset = 0x6C;
            public const int CrowdCount = 300;

            public MemoryIO Io;
            public uint ModuleBase;
            public uint ModuleSize;
            public uint Heap;
            public uint Arrays;

            /// <summary>两个"阵营对象"的地址。真实引擎里同一阵营的所有单位都指向同一处。</summary>
            public uint MyOwner;
            public uint EnemyOwner;

            /// <summary>六个正常对象：0/1 属我方，2/3/4 属敌方，5 的阵营字段读不出来。</summary>
            public uint[] Team = new uint[6];

            /// <summary>类型表只被自己引用的对象 —— 用来验证"孤例类型被当作假对象剔除"。</summary>
            public uint RareObject;

            public uint CrowdArray;
            public uint CrowdOwner;

            private readonly List<IntPtr> _blocks = new List<IntPtr>();
            private IntPtr _handle;

            /// <summary>
            /// 一个"落在模块区间内、4 字节又全是可打印字符"的值 —— 真机实测抓到过
            /// `0x00747261` 这种（ASCII 就是 "art"），是普通数据不是类型表。
            ///
            /// **必须相对模块基址算，不能写死 0x20202020**：分配到的基址是哪一块
            /// 取决于哪块内存当时空闲，写死只在基址恰好是 0x20000000 时才成立。
            /// </summary>
            public uint AsciiVTable
            {
                get { return (ModuleBase & 0xFF000000) | 0x00202020; }
            }

            public static TechnoScene Create(SelfHandle self)
            {
                if (self == null || !self.IsReady) { return null; }

                IntPtr moduleBlock = AllocLowBlock(self.Handle);
                IntPtr heapBlock = AllocLowBlock(self.Handle);
                IntPtr arrayBlock = AllocLowBlock(self.Handle);
                if (moduleBlock == IntPtr.Zero || heapBlock == IntPtr.Zero || arrayBlock == IntPtr.Zero)
                {
                    return null;
                }
                if (!IsLowAddress(moduleBlock) || !IsLowAddress(heapBlock) || !IsLowAddress(arrayBlock))
                {
                    return null;
                }

                TechnoScene scene = new TechnoScene();
                scene._handle = self.Handle;
                scene._blocks.Add(moduleBlock);
                scene._blocks.Add(heapBlock);
                scene._blocks.Add(arrayBlock);
                scene.Io = new MemoryIO(self);

                scene.ModuleBase = (uint)moduleBlock.ToInt64();
                scene.ModuleSize = FakeModuleSize;
                scene.Heap = (uint)heapBlock.ToInt64();
                scene.Arrays = (uint)arrayBlock.ToInt64();

                // 阵营对象：只是被比较的"标签值"，不会被解引用，所以随便放一块安全地址即可。
                scene.MyOwner = scene.Heap + 0xF000;
                scene.EnemyOwner = scene.Heap + 0xF400;
                scene.CrowdOwner = scene.Heap + 0xF800;

                uint vtableA = scene.ModuleBase + 0x2000;      // 被 4 个对象共用
                uint vtableB = scene.ModuleBase + 0x3000;      // 被 2 个对象共用
                uint vtableRare = scene.ModuleBase + 0x4000;   // 只被 1 个对象引用

                // 对象按 0x200 间隔摆放，留出 +0xF0 / +0x100 这些字段的位置。
                for (int i = 0; i < 6; i++) { scene.Team[i] = scene.Heap + (uint)(0x200 + i * 0x200); }
                scene.RareObject = scene.Heap + 0xE00;

                scene.WriteObject(scene.Team[0], vtableA, scene.MyOwner, 100);
                scene.WriteObject(scene.Team[1], vtableA, scene.MyOwner, 100);
                scene.WriteObject(scene.Team[2], vtableA, scene.EnemyOwner, 100);
                scene.WriteObject(scene.Team[3], vtableA, scene.EnemyOwner, 100);
                scene.WriteObject(scene.Team[4], vtableB, scene.EnemyOwner, 100);
                scene.WriteObject(scene.Team[5], vtableB, 0, 100);   // 阵营读不出来
                scene.WriteObject(scene.RareObject, vtableRare, scene.EnemyOwner, 100);

                scene.WriteArray(scene.Arrays, new uint[]
                {
                    scene.Team[0], scene.Team[1], scene.Team[2],
                    scene.Team[3], scene.Team[4], scene.Team[5], scene.RareObject
                });

                return scene;
            }

            /// <summary>主名单的地址（就是上面那 7 项）。</summary>
            public uint ArrayMain { get { return Arrays; } }

            public void WriteObject(uint address, uint vtable, uint owner, uint health)
            {
                Io.WriteUInt32(address, vtable);
                Io.WriteUInt32(address + OwnerOffset, owner);
                Io.WriteUInt32(address + HealthOffset, health);
            }

            public void WriteArray(uint address, uint[] entries)
            {
                byte[] buffer = new byte[entries.Length * 4];
                for (int i = 0; i < entries.Length; i++)
                {
                    uint value = entries[i];
                    buffer[i * 4] = (byte)(value & 0xFF);
                    buffer[i * 4 + 1] = (byte)((value >> 8) & 0xFF);
                    buffer[i * 4 + 2] = (byte)((value >> 16) & 0xFF);
                    buffer[i * 4 + 3] = (byte)((value >> 24) & 0xFF);
                }
                Io.Write(address, buffer, 0, buffer.Length);
            }

            /// <summary>读一个字段。读不到返回 0xFFFFFFFF（调用方的断言会因此失败，正是想要的效果）。</summary>
            public uint ReadField(uint address, uint offset)
            {
                uint value;
                if (!Io.ReadUInt32(address + offset, out value)) { return 0xFFFFFFFF; }
                return value;
            }

            /// <summary>把一批对象的血量加起来 —— 用来证明"一个字节都没被写过"。</summary>
            public uint SumHealth(uint[] addresses)
            {
                uint sum = 0;
                for (int i = 0; i < addresses.Length; i++)
                {
                    uint value;
                    if (Io.ReadUInt32(addresses[i] + HealthOffset, out value)) { sum += value; }
                }
                return sum;
            }

            /// <summary>摆 300 个同阵营对象，用来验证"单拍写入有上限"。</summary>
            public uint[] PlantCrowd()
            {
                uint vtable = ModuleBase + 0x6000;
                uint[] addresses = new uint[CrowdCount];
                for (int i = 0; i < CrowdCount; i++)
                {
                    addresses[i] = Heap + 0x2000 + (uint)(i * 0x80);
                    Io.WriteUInt32(addresses[i], vtable);
                    Io.WriteUInt32(addresses[i] + CrowdOwnerOffset, CrowdOwner);
                    Io.WriteUInt32(addresses[i] + CrowdHealthOffset, 100);
                }
                CrowdArray = Arrays + 0x7000;
                WriteArray(CrowdArray, addresses);
                return addresses;
            }

            public void Dispose()
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    Win32Api.VirtualFreeEx(_handle, _blocks[i], IntPtr.Zero, MEM_RELEASE);
                }
                _blocks.Clear();
            }
        }

        private static void CheckTechnoWalker()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("对象遍历", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("对象遍历测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;
                TechnoWalkResult walk = TechnoWalker.WalkArray(
                    io, scene.ArrayMain, 7, scene.ModuleBase, scene.ModuleSize);

                Check("对象遍历：认得出全部真对象，并剔掉孤例类型",
                    walk.Ok && walk.Scanned == 7 && walk.Count == 6
                        && walk.RareTypeRemoved == 1 && walk.Skipped == 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "扫 {0}｜认 {1}｜跳过 {2}｜孤例剔除 {3}｜重复 {4}",
                        walk.Scanned, walk.Count, walk.Skipped, walk.RareTypeRemoved, walk.Duplicates));

                List<KeyValuePair<uint, int>> groups =
                    TechnoWalker.GroupByOwner(io, walk.Objects, TechnoScene.OwnerOffset);
                Check("对象遍历：按阵营分组，人数最多的排第一（阵营读不出的不计入）",
                    groups.Count == 2
                        && groups[0].Key == scene.EnemyOwner && groups[0].Value == 3
                        && groups[1].Key == scene.MyOwner && groups[1].Value == 2,
                    string.Format(CultureInfo.InvariantCulture, "{0} 个阵营｜首位 {1} 个",
                        groups.Count, groups.Count > 0 ? groups[0].Value : 0));

                // ---- 「名单地址找错」——整个 S6 最危险的失败模式 ----
                // 造一块**可读、但内容不是对象指针**的内存当名单。必须被全部识破：
                // 只要放过去一个，秒杀就会往那儿写血量。
                uint decoy = scene.Heap + 0x7000;
                // 两种假值交替出现，各自堵住一条路 —— 真机里"找错的名单"里本来就是一堆杂值：
                //   ① 落在模块**起点之前**的（对齐、非文本、非 0，只能靠"落在模块区间内"拦下）
                //   ② 落在模块**末尾之后**的（同上，专门覆盖区间的上边界）
                // 这两条是逐个改坏验证过的：删掉区间检查，下面这条断言立刻转红。
                // （早先只放了 ①，而且取的值还更小，被"跳过模块头部"那条先拦下了 ——
                //   结果是删掉区间检查也照样全绿，等于没测。）
                io.WriteUInt32(decoy, 0x00004000);
                io.WriteUInt32(decoy + 4, scene.ModuleBase + scene.ModuleSize + 0x1000);
                uint arrayWrong = scene.Arrays + 0x0400;
                uint[] wrong = new uint[64];
                for (int i = 0; i < wrong.Length; i++) { wrong[i] = decoy + (uint)((i % 2) * 4); }
                scene.WriteArray(arrayWrong, wrong);

                TechnoWalkResult bad = TechnoWalker.WalkArray(
                    io, arrayWrong, 64, scene.ModuleBase, scene.ModuleSize);
                Check("对象遍历：名单地址找错时一个都不认（不会拿垃圾当单位）",
                    bad.Ok && bad.Count == 0 && bad.Skipped == 64,
                    string.Format(CultureInfo.InvariantCulture,
                        "扫 {0}｜认 {1}｜跳过 {2}｜孤例剔除 {3}｜重复 {4}",
                        bad.Scanned, bad.Count, bad.Skipped, bad.RareTypeRemoved, bad.Duplicates));

                // ---- 三条"假类型表"判据，逐条单独触发 ----
                uint objText = scene.Heap + 0x1000;
                uint objUnaligned = scene.Heap + 0x1200;
                uint objLowVtable = scene.Heap + 0x1400;
                scene.WriteObject(objText, scene.AsciiVTable, scene.EnemyOwner, 100);
                scene.WriteObject(objUnaligned, scene.ModuleBase + 0x2002, scene.EnemyOwner, 100);
                scene.WriteObject(objLowVtable, scene.ModuleBase + 0x800, scene.EnemyOwner, 100);

                uint arrayFilter = scene.Arrays + 0x0800;
                scene.WriteArray(arrayFilter, new uint[] { objText, objUnaligned, objLowVtable });
                TechnoWalkResult filtered = TechnoWalker.WalkArray(
                    io, arrayFilter, 3, scene.ModuleBase, scene.ModuleSize);
                Check("对象遍历：文本 / 未对齐 / 落在模块头部的假类型表全部被剔",
                    filtered.Ok && filtered.Count == 0 && filtered.Skipped == 3,
                    string.Format(CultureInfo.InvariantCulture,
                        "扫 {0}｜认 {1}｜跳过 {2}｜孤例剔除 {3}",
                        filtered.Scanned, filtered.Count, filtered.Skipped, filtered.RareTypeRemoved));

                uint arrayDup = scene.Arrays + 0x0900;
                scene.WriteArray(arrayDup, new uint[] { scene.Team[0], scene.Team[0], scene.Team[1] });
                TechnoWalkResult dup = TechnoWalker.WalkArray(
                    io, arrayDup, 3, scene.ModuleBase, scene.ModuleSize);
                Check("对象遍历：同一对象重复出现只算一次",
                    dup.Ok && dup.Count == 2 && dup.Duplicates == 1,
                    string.Format(CultureInfo.InvariantCulture, "认 {0}｜重复 {1}", dup.Count, dup.Duplicates));

                // ---- 拒绝路径：失败必须"说得出原因"，不能静默返回空名单 ----
                TechnoWalkResult errZero = TechnoWalker.WalkArray(
                    io, scene.ArrayMain, 0, scene.ModuleBase, scene.ModuleSize);
                Check("对象遍历：数量为 0 时明确报错（不是静默当成「没有单位」）",
                    !errZero.Ok && errZero.Error.Length > 0, errZero.Error);

                TechnoWalkResult errModule = TechnoWalker.WalkArray(
                    io, scene.ArrayMain, 7, scene.ModuleBase, 0);
                Check("对象遍历：不知道模块区间时拒绝（没有判据就不能认对象）",
                    !errModule.Ok && errModule.Error.Length > 0, errModule.Error);

                TechnoWalkResult errAddr = TechnoWalker.WalkArray(
                    io, 0, 7, scene.ModuleBase, scene.ModuleSize);
                Check("对象遍历：名单地址无效时拒绝",
                    !errAddr.Ok && errAddr.Error.Length > 0, errAddr.Error);

                TechnoWalkResult huge = TechnoWalker.WalkArray(
                    io, scene.ArrayMain, 999999, scene.ModuleBase, scene.ModuleSize);
                Check("对象遍历：数量异常巨大时被截断（不会去读天文数字）",
                    huge.Scanned == TechnoWalker.MaxObjects,
                    string.Format(CultureInfo.InvariantCulture, "实际扫 {0}（上限 {1}）",
                        huge.Scanned, TechnoWalker.MaxObjects));
            }
            catch (Exception ex)
            {
                Check("对象遍历", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        /// <summary>
        /// 数组**尾部读不动**时，必须读出前面能读的部分，而不是整块失败。
        ///
        /// 这条来自一次真机失败（2026-09-29 用户采集）：遍历器一次性读 16384 字节，
        /// 整块读取失败 → "场上对象"全程为 0；而同一份配置在前一次会话明明读出了 4094 个对象。
        /// 根因是 `ReadProcessMemory` 要求整段可读，而数组的"容量"不等于"实际分配了那么长"。
        /// </summary>
        private static void CheckTechnoWalkerPartialRead()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("对象遍历（部分可读）", false, "OpenProcess 失败"); return; }

            IntPtr moduleBlock = IntPtr.Zero;
            IntPtr tailBlock = IntPtr.Zero;
            try
            {
                moduleBlock = AllocLowBlock(self.Handle);
                tailBlock = AllocLowBlock(self.Handle);
                if (moduleBlock == IntPtr.Zero || tailBlock == IntPtr.Zero)
                {
                    Check("对象遍历（部分可读）测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(moduleBlock) || !IsLowAddress(tailBlock))
                {
                    WarnItem("对象遍历（部分可读）", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint moduleBase = (uint)moduleBlock.ToInt64();
                uint moduleSize = 0x1000000;
                uint tail = (uint)tailBlock.ToInt64();
                MemoryIO io = new MemoryIO(self);

                uint blockEnd = tail + (uint)TestBlockSize;

                // **先确认这条断言的前提成立**：紧接块尾之后确实读不动。
                // 万一那块内存恰好可读，下面的断言就测不到想测的东西 ——
                // 那时如实降级为提示，而不是假装通过。
                if (io.ReadBytesQuiet(blockEnd, 4) != null)
                {
                    WarnItem("对象遍历（部分可读）", "块尾之后的内存恰好可读，这条断言测不了，跳过");
                    return;
                }

                // 数组紧贴块尾摆放：只有最后 0x100 字节落在块内，再往后就是未分配区域。
                // 这是"容量写的是 4096，实际只分配了几百字节"那种情形的缩小版。
                uint arrayAddress = blockEnd - 0x100;

                uint vtable = moduleBase + 0x2000;   // 类型表：必须落在模块区间内
                for (int i = 0; i < 4; i++)
                {
                    uint objectAddress = tail + 0x100 + (uint)(i * 0x100);
                    io.WriteUInt32(objectAddress, vtable);          // 对象头 = 类型表
                    io.WriteUInt32(arrayAddress + (uint)(i * 4), objectAddress);
                }
                // 数组其余部分保持为 0（新提交的页本来就是 0），会被遍历器当作空槽跳过

                // 请求 256 项（1024 字节），实际只有 64 项落在块内
                TechnoWalkResult walk = TechnoWalker.WalkArray(
                    io, arrayAddress, 256, moduleBase, moduleSize);

                Check("对象遍历：数组尾部读不动时，仍能读出前面有效的部分（不再整块失败）",
                    walk.Ok && walk.Truncated && walk.Scanned == 64 && walk.Count == 4,
                    string.Format(CultureInfo.InvariantCulture,
                        "扫 {0}｜认 {1}｜可读 {2} 项｜截断 {3}{4}",
                        walk.Scanned, walk.Count, walk.ReadableEntries,
                        walk.Truncated ? "是" : "否",
                        walk.Ok ? "" : "｜" + walk.Error));
            }
            catch (Exception ex)
            {
                Check("对象遍历（部分可读）", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (moduleBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, moduleBlock, IntPtr.Zero, MEM_RELEASE); }
                if (tailBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, tailBlock, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
            }
        }

        private static void CheckTechnoArrayFinder()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("自动查找总名单", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("自动查找总名单测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;

                // 摆三张名单，看查找器挑哪张：
                //   选中名单（线索来源，必须被排除）、含选中单位的总名单（应排第一）、
                //   不含选中单位的名单（应排在后面）
                uint slotSelected = scene.Arrays + 0x2000;
                uint arraySelected = scene.Arrays + 0x3000;
                uint slotTotal = scene.Arrays + 0x2100;
                uint arrayTotal = scene.Arrays + 0x4000;
                uint slotDecoy = scene.Arrays + 0x5100;
                uint arrayDecoy = scene.Arrays + 0x5000;

                io.WriteUInt32(slotSelected, arraySelected);
                io.WriteUInt32(slotTotal, arrayTotal);
                io.WriteUInt32(slotDecoy, arrayDecoy);

                scene.WriteArray(arraySelected, new uint[] { scene.Team[0] });
                scene.WriteArray(arrayTotal, new uint[]
                {
                    scene.Team[0], scene.Team[1], scene.Team[2],
                    scene.Team[3], scene.Team[4], scene.Team[5]
                });
                scene.WriteArray(arrayDecoy, new uint[] { scene.Team[1], scene.Team[2], scene.Team[3] });

                List<uint> hints = new List<uint>();
                hints.Add(scene.Team[0]);

                int rejectedFirst;
                List<TechnoArrayCandidate> found = TechnoArrayFinder.Find(
                    io, scene.ModuleBase, scene.ModuleSize, slotSelected, hints,
                    TechnoScene.OwnerOffset, out rejectedFirst);

                Check("自动查找总名单：含选中单位的那张排第一，有效对象数也对",
                    found.Count > 0 && found[0].ArrayAddress == arrayTotal
                        && found[0].HintHits == 1 && found[0].ValidObjects == 6,
                    found.Count > 0 ? found[0].Describe() : "一个候选都没有");

                bool excludesSelf = true;
                for (int i = 0; i < found.Count; i++)
                {
                    if (found[i].SlotAddress == slotSelected) { excludesSelf = false; }
                }
                Check("自动查找总名单：不会把「选中名单自己」当成总名单",
                    excludesSelf && found.Count >= 2,
                    string.Format(CultureInfo.InvariantCulture, "{0} 个候选", found.Count));

                // ---- 2026-10-08 新增：混进一张"阵营散成一片"的假名单 ----
                //
                // 真机教训：假容器不只会出现（"阵营"散成几千种），而且它的"有效对象数"
                // 可能比真名单**还多** —— 旧排序（只看对象数）会把它排到第一。
                // 这里造同款假名单：7 个对象各属一个不同的"阵营"（只有 Team[0] 是真线索）。
                // 断言：① 排序仍把真名单排第一；② 硬验收必须拦住它；③ 好消息不能被误伤。
                uint junkArray = scene.Arrays + 0x5800;
                uint slotJunk = scene.Arrays + 0x5400;
                uint[] junkObjects = new uint[6];
                for (int i = 0; i < junkObjects.Length; i++)
                {
                    junkObjects[i] = scene.Heap + 0x2000 + (uint)(i * 0x100);
                    io.WriteUInt32(junkObjects[i], scene.ModuleBase + 0x2000);   // 像真对象：共用类型表
                    io.WriteUInt32(junkObjects[i] + TechnoScene.OwnerOffset, 0x50000000u + (uint)i);
                    io.WriteUInt32(junkObjects[i] + TechnoScene.HealthOffset, 100);
                }
                scene.WriteArray(junkArray, new uint[]
                {
                    scene.Team[0], junkObjects[0], junkObjects[1],
                    junkObjects[2], junkObjects[3], junkObjects[4], junkObjects[5]
                });
                io.WriteUInt32(slotJunk, junkArray);

                int rejectedJunk;
                List<TechnoArrayCandidate> ranked = TechnoArrayFinder.Find(
                    io, scene.ModuleBase, scene.ModuleSize, slotSelected, hints,
                    TechnoScene.OwnerOffset, out rejectedJunk);

                Check("自动查找：混入「阵营散成一片」的假名单时，聚集成群的真名单仍排第一",
                    ranked.Count > 0 && ranked[0].ArrayAddress == arrayTotal,
                    ranked.Count > 0 ? ranked[0].Describe() : "一个候选都没有");

                TechnoArrayCandidate junk = null;
                for (int i = 0; i < ranked.Count; i++)
                {
                    if (ranked[i].ArrayAddress == junkArray) { junk = ranked[i]; }
                }
                string junkReason = "";
                Check("自动查找：硬验收拦住「阵营散成一片」的候选",
                    junk != null && !TechnoArrayFinder.MeetsAcceptance(junk, 1, out junkReason)
                        && junkReason.Length > 0,
                    junk == null ? "假候选没进前 8 名" : junkReason);

                string goodReason;
                Check("自动查找：正常名单通过硬验收（没把好数据也挡了）",
                    TechnoArrayFinder.MeetsAcceptance(ranked[0], 1, out goodReason),
                    goodReason.Length == 0 ? "通过" : goodReason);

                string missingReason;
                Check("自动查找：验收要求「选中的单位必须全部找到」（只找到一半也不行）",
                    !TechnoArrayFinder.MeetsAcceptance(ranked[0], 3, out missingReason)
                        && missingReason.Length > 0,
                    missingReason);

                // ---- 2026-10-08 晚新增：命中数**只许数数组自己的元素** ----
                //
                // 真机 16:46 的教训：旧实现把 4096 项的读取窗口**整个**数了一遍，
                // 而数组**后面**的内存里往往引用着玩家选中的单位（单位彼此引用很常见）——
                // 于是每张候选名单都显示"命中 6/6"，真假再也分不出来，
                // 最终选中了一张只有 60 个对象、玩家的 6 个单位**一个都不在**里面的假名单
                //（随后「扫描」的交叉验证诚实报了 0/6）。
                // 这里造同款：数组只有 3 项（第 4 项是 0，数组到此为止），紧跟着的内存里放着线索单位。
                uint arrayLeak = scene.Arrays + 0x9000;
                uint slotLeak = scene.Arrays + 0x9400;
                scene.WriteArray(arrayLeak, new uint[] { scene.Team[2], scene.Team[3], scene.Team[4] });
                io.WriteUInt32(arrayLeak + 0x0C, 0);                // 数组到此为止
                io.WriteUInt32(arrayLeak + 0x10, scene.Team[0]);    // 数组之后：引用着线索单位
                io.WriteUInt32(slotLeak, arrayLeak);

                int rejectedLeak;
                List<TechnoArrayCandidate> leakFound = TechnoArrayFinder.Find(
                    io, scene.ModuleBase, scene.ModuleSize, slotSelected, hints,
                    TechnoScene.OwnerOffset, out rejectedLeak);

                TechnoArrayCandidate leakCandidate = null;
                for (int i = 0; i < leakFound.Count; i++)
                {
                    if (leakFound[i].ArrayAddress == arrayLeak) { leakCandidate = leakFound[i]; }
                }
                string leakReason = "";
                Check("自动查找：命中数只数数组自己的元素（数组之后的内存里引用着线索也不算）",
                    leakCandidate != null && leakCandidate.HintHits == 0 && leakCandidate.ValidObjects == 3
                        && !TechnoArrayFinder.MeetsAcceptance(leakCandidate, 1, out leakReason),
                    leakCandidate == null
                        ? "候选没进前 8 名"
                        : leakCandidate.Describe() + "｜" + leakReason);

                // ---- 2026-10-07 加固：可执行页里的「格子」必须被排除 ----
                //
                // 真机两次实测（09-27、10-07）：代码段里某些 4 字节窗口的值**恰好等于当场的
                // 数组地址**，能通过全部旧校验、还因对象数最多排到第一 —— 存下来的"定位"其实
                // 指向代码字节：当天能用、换一局失效。这里把同款的"假格子"造出来（一张正常
                // 数组 + 一个写在可执行页里的指针格），断言它不再进入候选。
                // 改坏验证：删掉位置检查，本断言立刻转红。
                uint slotCode = scene.Arrays + 0x7000;
                uint arrayCode = scene.Arrays + 0x6000;
                io.WriteUInt32(slotCode, arrayCode);
                scene.WriteArray(arrayCode, new uint[] { scene.Team[0], scene.Team[1], scene.Team[2] });

                uint oldProtect;
                bool flipped = Win32Api.VirtualProtectEx(self.Handle,
                    (IntPtr)(long)slotCode, (IntPtr)0x1000, Win32Api.PAGE_EXECUTE_READWRITE, out oldProtect);
                Check("自动查找总名单（加固用例）：可执行页已就位",
                    flipped, flipped
                        ? string.Format(CultureInfo.InvariantCulture, "0x{0:X8} 一页已改为可执行", slotCode)
                        : Win32Api.LastErrorMessage());

                int rejectedCode;
                List<TechnoArrayCandidate> hardened = TechnoArrayFinder.Find(
                    io, scene.ModuleBase, scene.ModuleSize, slotSelected, hints,
                    TechnoScene.OwnerOffset, out rejectedCode);

                bool codeExcluded = true;
                for (int i = 0; i < hardened.Count; i++)
                {
                    if (hardened[i].SlotAddress == slotCode) { codeExcluded = false; }
                }
                Check("自动查找总名单：可执行页里的「格子」被排除（2026-10-07 加固）",
                    codeExcluded && rejectedCode >= 1
                        && hardened.Count > 0 && hardened[0].ArrayAddress == arrayTotal,
                    string.Format(CultureInfo.InvariantCulture,
                        "候选 {0} 个｜按位置规则排除 {1} 个｜可执行页格子混入={2}",
                        hardened.Count, rejectedCode, codeExcluded ? "否" : "是"));

                uint restoreOld;
                if (!Win32Api.VirtualProtectEx(self.Handle,
                    (IntPtr)(long)slotCode, (IntPtr)0x1000, oldProtect, out restoreOld))
                {
                    Logger.Warn("SelfTest", "加固用例的可执行页还原失败：" + Win32Api.LastErrorMessage());
                }
            }
            catch (Exception ex)
            {
                Check("自动查找总名单", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        /// <summary>
        /// 「高级功能」页的排版回归（2026-10-08 晚新增）。
        ///
        /// 为什么必须用代码量、不能靠截图看：用户显示器**开了 DPI 缩放**，按钮随字号变宽，
        /// 而本机是 96 DPI —— 这类"按钮挤成一堆、压住提示文字"的问题在本机根本复现不出来
        ///（`CLAUDE.md` 7.2 记的就是这类坑，真机上花了 6 轮才定位）。
        /// 做法：把页面真造出来，在几档宽度下重排，然后**逐个控件量矩形**：
        /// ① 任意两个控件不得相交（提示文字必须各占一行、按钮换行后不得压回去）；
        /// ② 任何控件不得超出页面宽度（超了就会出现横向滚动条、右侧被切掉）；
        /// ③ 输出框必须仍在页面内且有可用高度。
        /// </summary>
        private static void CheckAdvancedPageLayout()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("高级功能页排版", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            AdvancedPage page = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("高级功能页排版测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;
                List<MemoryRegion> heap = new List<MemoryRegion>();
                heap.Add(MakeRegion(scene.Heap, TestBlockSize));
                List<MemoryRegion> pointer = new List<MemoryRegion>();
                pointer.Add(MakeRegion(scene.ModuleBase, TestBlockSize));
                pointer.Add(MakeRegion(scene.Heap, TestBlockSize));
                SyntheticTarget target = new SyntheticTarget(
                    io, scene.ModuleBase, TechnoScene.FakeModuleSize, heap, pointer);

                page = new AdvancedPage(target, new CalibrationProfile(), new ProfileStore(null));

                // 四档宽度：560（很窄，第一排按钮必须换行）/ 820（用户常规窗口的内容区）/
                // 1400（中宽）/ 2380（用户最大化时的内容区，真机日志里就是这个数）
                int[] widths = new int[] { 560, 820, 1400, 2380 };
                int totalOverlaps = 0;
                int totalOverflow = 0;
                int worstWidth = 0;
                string worstPair = "";
                int minBoxHeight = int.MaxValue;
                bool boxOutside = false;

                for (int w = 0; w < widths.Length; w++)
                {
                    page.Size = new Size(widths[w], 484);
                    page.PerformLayout();

                    List<Control> items = new List<Control>();
                    for (int i = 0; i < page.Controls.Count; i++)
                    {
                        if (page.Controls[i].Visible) { items.Add(page.Controls[i]); }
                    }

                    for (int i = 0; i < items.Count; i++)
                    {
                        Rectangle a = items[i].Bounds;
                        if (a.Left < 0 || a.Right > page.ClientSize.Width)
                        {
                            totalOverflow++;
                            worstWidth = widths[w];
                        }
                        if (items[i] is TextBox)
                        {
                            if (items[i].Height < minBoxHeight) { minBoxHeight = items[i].Height; }
                            if (a.Bottom > page.ClientSize.Height) { boxOutside = true; }
                        }

                        for (int k = i + 1; k < items.Count; k++)
                        {
                            Rectangle hit = Rectangle.Intersect(a, items[k].Bounds);
                            if (hit.Width > 2 && hit.Height > 2)
                            {
                                totalOverlaps++;
                                worstWidth = widths[w];
                                worstPair = string.Format(CultureInfo.InvariantCulture,
                                    "「{0}」与「{1}」在宽 {2} 时相交 {3}x{4}",
                                    Short(items[i].Text), Short(items[k].Text),
                                    widths[w], hit.Width, hit.Height);
                            }
                        }
                    }
                }

                Check("高级功能页排版：560/820/1400/2380 四档宽度下，控件互不重叠（按钮放不下就换行）",
                    totalOverlaps == 0,
                    totalOverlaps == 0 ? "0 处重叠" : string.Format(CultureInfo.InvariantCulture,
                        "{0} 处重叠，例如 {1}", totalOverlaps, worstPair));

                Check("高级功能页排版：任何宽度下控件都不超出页面（否则右侧会被切掉/出横向滚动条）",
                    totalOverflow == 0,
                    totalOverflow == 0 ? "0 处超出" : string.Format(CultureInfo.InvariantCulture,
                        "{0} 处超出（宽 {1}）", totalOverflow, worstWidth));

                // 门槛取 100：这一条的意思是"输出框别被挤没"，不是"必须多高"。
                // 最窄的 560 宽度下按钮会多换一行、输出框相应变矮（实测 111），属正常；
                // 用户实际使用的 820 宽度下它在 150 以上（真机冒烟的日志可查）。
                Check("高级功能页排版：输出框仍在页面内且有可用高度（不会被挤没）",
                    !boxOutside && minBoxHeight >= 100,
                    string.Format(CultureInfo.InvariantCulture,
                        "最小高度 {0}｜越界={1}", minBoxHeight, boxOutside));
            }
            catch (Exception ex)
            {
                Check("高级功能页排版", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (page != null) { try { page.Dispose(); } catch (Exception) { } }
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        /// <summary>控件文字太长时截断（只用于断言说明，不影响界面）。</summary>
        private static string Short(string text)
        {
            if (text == null) { return ""; }
            string one = text.Replace("\n", " ");
            return one.Length > 14 ? one.Substring(0, 14) + "…" : one;
        }

        /// <summary>
        /// 环境（游戏版本）检查的回归（2026-10-08 晚新增）。
        ///
        /// 为什么必须有：这是"防止程序在**另一份引擎**上照着旧地址乱写"的唯一一道闸。
        /// 它一旦失效，表面完全看不出来 —— 功能照常"工作"，写进去的却是别人的内存。
        /// 所以这里从**算法**（CRC32 用标准校验值钉死）、**取文件**、**三种结论**
        /// （首次记录 / 一致 / 不一致）到**引擎是否真的停写**，逐层验一遍。
        /// </summary>
        private static void CheckEnvironmentCheck()
        {
            try
            {
                // ① CRC32 算法：用标准校验值钉死（这几个值是 CRC32 的公开测试向量）
                byte[] check = System.Text.Encoding.ASCII.GetBytes("123456789");
                uint crc = EnvironmentCheck.Crc32(check, 0, check.Length);
                Check("版本检查：CRC32 算法与标准测试向量一致（\"123456789\" = 0xCBF43926）",
                    crc == 0xCBF43926u,
                    string.Format(CultureInfo.InvariantCulture, "算出 0x{0:X8}", crc));

                byte[] empty = new byte[0];
                uint emptyCrc = EnvironmentCheck.Crc32(empty, 0, 0);
                Check("版本检查：空数据的 CRC32 为 0（边界值）",
                    emptyCrc == 0,
                    string.Format(CultureInfo.InvariantCulture, "算出 0x{0:X8}", emptyCrc));

                // ② 文件版：写一个临时文件，CRC 必须与"对同样字节的内存版"一致
                string temp = Path.Combine(Path.GetTempPath(),
                    "mo_trainer_crc_" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture) + ".bin");
                byte[] payload = new byte[70000];      // 跨过读缓冲（64KB）的边界，覆盖分块拼接
                for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i * 31 + 7); }
                File.WriteAllBytes(temp, payload);

                uint fileCrc;
                string fileError;
                bool fileOk = EnvironmentCheck.TryComputeFileCrc32(temp, out fileCrc, out fileError);
                uint memoryCrc = EnvironmentCheck.Crc32(payload, 0, payload.Length);
                Check("版本检查：文件 CRC 与内存 CRC 一致（分块读取不拼接错）",
                    fileOk && fileCrc == memoryCrc,
                    fileOk
                        ? string.Format(CultureInfo.InvariantCulture, "文件 0x{0:X8}｜内存 0x{1:X8}", fileCrc, memoryCrc)
                        : fileError);

                string missingError;
                uint missingCrc;
                bool missingOk = EnvironmentCheck.TryComputeFileCrc32(temp + ".notexist", out missingCrc, out missingError);
                Check("版本检查：文件不存在时返回失败而不是抛异常（并说明原因）",
                    !missingOk && missingError.Length > 0, missingError);

                try { File.Delete(temp); } catch (Exception) { }

                // ③ 三种结论：用**自检程序自己的进程**当被测对象（它的主程序文件就是 _selftest.exe）
                int pid = Process.GetCurrentProcess().Id;
                CalibrationProfile profile = new CalibrationProfile();

                EnvironmentCheck.ResetCache();
                EnvironmentCheck.Result first = EnvironmentCheck.Verify(profile, pid);
                Check("版本检查：校准文件里没记过 → 判定为「首次记录」，并给出当前指纹",
                    first.Available && first.FirstRecord && first.Match && first.GameCrc.Length > 0,
                    first.Detail);

                if (first.Available)
                {
                    profile.ExeCrc32 = first.GameCrc;
                    EnvironmentCheck.ResetCache();
                    EnvironmentCheck.Result same = EnvironmentCheck.Verify(profile, pid);
                    Check("版本检查：指纹一致 → 放行（不打扰用户）",
                        same.Available && same.Match && !same.FirstRecord,
                        same.Detail);

                    profile.ExeCrc32 = "0xDEADBEEF";
                    EnvironmentCheck.ResetCache();
                    EnvironmentCheck.Result diff = EnvironmentCheck.Verify(profile, pid);
                    Check("版本检查：指纹不一致 → 判为不一致并给出可执行的说明（该拒绝就拒绝）",
                        diff.Available && !diff.Match && !diff.FirstRecord && diff.Detail.Length > 0,
                        diff.Detail);

                    profile.ExeCrc32 = "";
                }

                // ④ 真机文档一致性（本机装了游戏时才有意义）：文档记的是 0x1B499086
                const string gamePath = @"D:\Mental Omega\gamemd.exe";
                if (File.Exists(gamePath))
                {
                    uint gameCrc;
                    string gameError;
                    bool ok = EnvironmentCheck.TryComputeFileCrc32(gamePath, out gameCrc, out gameError);
                    Check("版本检查：本机 gamemd.exe 的指纹与文档记载一致（0x1B499086）",
                        ok && gameCrc == 0x1B499086u,
                        ok ? string.Format(CultureInfo.InvariantCulture, "算出 0x{0:X8}", gameCrc) : gameError);
                }
                else
                {
                    WarnItem("版本检查：本机未找到游戏主程序，跳过「与文档核对指纹」一项", gamePath);
                }
            }
            catch (Exception ex)
            {
                Check("版本检查", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 「电力试探」的回归（2026-10-09 新增，`[S4-3]` B 方案）。
        ///
        /// 为什么必须测：这是**全程序唯一会往游戏里写"实验值"**的地方。它一旦出问题，
        /// 后果是"在用户自己的建筑上留下一个改过的值"。所以本节的中心不是"能不能判对"，
        /// 而是**"无论如何都要还原"**与**"不该写的时候一个字节都不写"**。
        /// </summary>
        private static void CheckPowerProbe()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("电力试探", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("电力试探测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;
                uint module = scene.ModuleBase;
                // 注：真机上"界面电力产出"在 House + 0x53A4；合成场景的堆块只有 64KB，照搬会落到块外
                // 读不出来（踩过一次），所以下面把模拟字段放在堆块内另起一处。本用例考的是**机制**。

                // 场景：我方房子（有钱，供锚定）+ 我方的一座"电厂"（对象 +0xD0 放候选字段）
                io.WriteUInt32(scene.MyOwner + TechnoOwnership.HouseMoneyOffset, 5000);
                uint plant = scene.Team[0];               // Team[0] 归属我方
                io.WriteUInt32(plant + PowerProbe.CandidateOffset, 100);   // 原值 = 100（真机实测的候选值）

                // "界面电力产出"模拟字段：放在堆块内某处（真机在 House+0x53A4，合成场景照搬会落到块外）。
                // 定位链：模块内静态根（0x104）→ 一块可写区（Heap+0xE000）→ +0x800 = Heap+0xE800。
                uint outputAddress = scene.Heap + 0xE800;
                io.WriteUInt32(module + 0x104, scene.Heap + 0xE000);
                io.WriteUInt32(outputAddress, 60);                          // 界面电力产出 = 60

                CalibrationProfile profile = new CalibrationProfile();
                profile.Money.SetOffsets(new uint[] { 0x100, TechnoOwnership.HouseMoneyOffset });
                profile.PowerOutput.SetOffsets(new uint[] { 0x104, 0x800 });
                io.WriteUInt32(module + 0x100, scene.MyOwner);

                // ---- ① 判定规则（纯函数）----
                Check("电力试探：判定规则 —— 读数跟着试探值涨、还原后回落 =「是来源」",
                    PowerProbe.Judge(60, 5060, 60, 4900),
                    "before=60 peak=5060 after=60 delta=4900");
                Check("电力试探：判定规则 —— 读数压根没动 =「不是来源」",
                    !PowerProbe.Judge(60, 60, 60, 4900), "before=60 peak=60 after=60");
                Check("电力试探：判定规则 —— 涨上去但还原后没回落 = 不算数（防误判）",
                    !PowerProbe.Judge(60, 5060, 3000, 4900), "before=60 peak=5060 after=3000");

                // ---- ② 机制：短时长的完整一遍（模拟"游戏会把字段汇总成产出"的循环）----
                // 起一个后台线程，每 20ms 把"电厂字段"抄进"界面产出" —— 这正是真机上游戏自己在做的事。
                bool running = true;
                System.Threading.Thread mimic = new System.Threading.Thread(delegate()
                {
                    while (running)
                    {
                        uint field;
                        if (io.ReadUInt32(plant + PowerProbe.CandidateOffset, out field))
                        {
                            io.WriteUInt32(outputAddress, field);
                        }
                        System.Threading.Thread.Sleep(20);
                    }
                });
                mimic.IsBackground = true;
                mimic.Start();

                PowerProbe.Result yes = PowerProbe.Run(
                    io, profile, module, plant, scene.MyOwner, TechnoScene.OwnerOffset, 240, 40);
                running = false;

                uint afterValue;
                io.ReadUInt32(plant + PowerProbe.CandidateOffset, out afterValue);
                Check("电力试探：试探值**无条件还原**（跑完之后字段必须是原值）",
                    afterValue == 100 && yes.Ok && yes.Restored,
                    string.Format(CultureInfo.InvariantCulture,
                        "字段现值 {0}（原值 100）｜还原标记 {1}", afterValue, yes.Restored));

                Check("电力试探：能判出「这个字段就是产出来源」（读数跟着涨、还原后回落）",
                    yes.LooksLikeSource,
                    string.Format(CultureInfo.InvariantCulture,
                        "写前 {0} → 写中 {1} → 还原后 {2}", yes.OutputBefore, yes.OutputPeak, yes.OutputAfter));
                Check("电力试探：界面上那三档读数都拿到了（日志与界面要能对上）",
                    yes.OutputBefore > 0 && yes.OutputPeak >= 5000
                        && yes.OutputAfter == yes.OutputBefore,
                    string.Format(CultureInfo.InvariantCulture,
                        "写前 {0}｜写中峰值 {1}｜还原后 {2}", yes.OutputBefore, yes.OutputPeak, yes.OutputAfter));

                // ---- ③ 不是我的东西 → 拒绝，且一个字节都不写 ----
                uint enemy = scene.Team[2];               // Team[2] 属敌方
                io.WriteUInt32(enemy + PowerProbe.CandidateOffset, 77);
                PowerProbe.Result refused = PowerProbe.Run(
                    io, profile, module, enemy, scene.MyOwner, TechnoScene.OwnerOffset, 120, 40);

                uint enemyAfter;
                io.ReadUInt32(enemy + PowerProbe.CandidateOffset, out enemyAfter);
                Check("电力试探：目标不属于我方时拒绝，且**一个字节都不写**",
                    !refused.Ok && enemyAfter == 77 && refused.Detail.Length > 0, refused.Detail);

                // ---- ④ 没校准电力产出 → 拒绝 ----
                CalibrationProfile bare = new CalibrationProfile();
                PowerProbe.Result noLocator = PowerProbe.Run(
                    io, bare, module, plant, scene.MyOwner, TechnoScene.OwnerOffset, 120, 40);
                Check("电力试探：配置里没有「电力产出」定位时拒绝并说明（不许悄悄什么都不做）",
                    !noLocator.Ok && noLocator.Detail.IndexOf("电力") >= 0, noLocator.Detail);
            }
            catch (Exception ex)
            {
                Check("电力试探", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        /// <summary>
        /// 「电力侦查」的回归（2026-10-09 新增，`[S4-3]` B 方案第三步）。
        ///
        /// 它是**只读**工具，所以本节的中心是两件事：
        ///   ① 真的能认出"类型对象"（顺着指针找到里面的建筑 ID 字符串）—— 这是下一步定位电力来源的线索；
        ///   ② **一个字节都不许写**（对照扫描前后的原始字节）。
        /// </summary>
        private static void CheckPowerScout()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("电力侦查", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("电力侦查测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;
                uint target = scene.Team[1];

                // 造一个"像建筑类型对象"的东西：它 +0x08 处指向一个可打印字符串（真机里就是建筑 ID）
                uint typeObject = scene.Heap + 0x5000;
                uint idString = scene.Heap + 0x5200;
                io.WriteUInt32(typeObject + 0x08, idString);
                byte[] id = System.Text.Encoding.ASCII.GetBytes("GAPOWR\0");
                io.WriteRaw((IntPtr)(long)idString, id, 0, id.Length);

                // 目标对象：+0x40 放"指向类型对象"的指针；+0x100 放一个"像电力读数"的小整数
                io.WriteUInt32(target + 0x40, typeObject);
                io.WriteUInt32(target + 0x100, 200);

                byte[] before = io.ReadBytesQuiet(target, PowerScout.ScanBytes);

                CalibrationProfile profile = new CalibrationProfile();
                string report = PowerScout.Run(io, profile, scene.ModuleBase, target);

                byte[] after = io.ReadBytesQuiet(target, PowerScout.ScanBytes);
                bool identical = before != null && after != null && before.Length == after.Length;
                if (identical)
                {
                    for (int i = 0; i < before.Length; i++)
                    {
                        if (before[i] != after[i]) { identical = false; break; }
                    }
                }

                Check("电力侦查：能认出「类型对象」（顺着指针找到里面的建筑 ID 字符串）",
                    report.IndexOf("疑似类型对象") >= 0 && report.IndexOf("GAPOWR") >= 0,
                    FirstLineWith(report, "疑似类型对象"));

                Check("电力侦查：会列出「数值像电力读数」的字段（供下一步试探挑）",
                    report.IndexOf("+0x100 = 200") >= 0,
                    FirstLineWith(report, "+0x100"));

                Check("电力侦查：**一个字节都不写**（扫描前后对象原始字节完全一致）",
                    identical,
                    identical ? "扫描前后逐字节一致（共 " + (before == null ? 0 : before.Length) + " 字节）"
                              : "**字节被改动了！**");
            }
            catch (Exception ex)
            {
                Check("电力侦查", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        /// <summary>取报告里第一条含关键字的行（断言说明用；取不到就返回空串）。</summary>
        private static string FirstLineWith(string text, string key)
        {
            if (text == null) { return ""; }
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(key) >= 0) { return lines[i].Trim(); }
            }
            return "";
        }

        private static void CheckTechnoOps()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("批量操作", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("批量操作测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;
                TechnoWalkResult walk = TechnoWalker.WalkArray(
                    io, scene.ArrayMain, 7, scene.ModuleBase, scene.ModuleSize);
                uint healthAll = scene.SumHealth(scene.Team);   // 6 个对象 × 100

                // ================= 秒杀敌人 =================
                //
                // 这一组是整个 S6 最重要的断言：**该拒绝时必须拒绝，而且一个字节都不能写**。
                // 秒杀写的是血量，判据没校准就执行 = 把自己的部队一起清空。

                TechnoOpResult noOwner = TechnoOps.KillEnemies(
                    io, walk.Objects, 0, TechnoScene.HealthOffset, scene.MyOwner);
                Check("秒杀：未校准「阵营」偏移时拒绝执行，且一个字节都没写",
                    !noOwner.Ok && noOwner.Blocked.Length > 0
                        && scene.SumHealth(scene.Team) == healthAll,
                    noOwner.Summary);

                TechnoOpResult noHealth = TechnoOps.KillEnemies(
                    io, walk.Objects, TechnoScene.OwnerOffset, 0, scene.MyOwner);
                Check("秒杀：未校准「血量」偏移时拒绝执行，且一个字节都没写",
                    !noHealth.Ok && scene.SumHealth(scene.Team) == healthAll,
                    noHealth.Summary);

                TechnoOpResult noMine = TechnoOps.KillEnemies(
                    io, walk.Objects, TechnoScene.OwnerOffset, TechnoScene.HealthOffset, 0);
                Check("秒杀：认不出自己的阵营时拒绝执行，且一个字节都没写",
                    !noMine.Ok && scene.SumHealth(scene.Team) == healthAll,
                    noMine.Summary);

                TechnoOpResult kill = TechnoOps.KillEnemies(
                    io, walk.Objects, TechnoScene.OwnerOffset, TechnoScene.HealthOffset, scene.MyOwner);

                uint mine = scene.ReadField(scene.Team[0], TechnoScene.HealthOffset)
                          + scene.ReadField(scene.Team[1], TechnoScene.HealthOffset);
                uint enemy = scene.ReadField(scene.Team[2], TechnoScene.HealthOffset)
                           + scene.ReadField(scene.Team[3], TechnoScene.HealthOffset)
                           + scene.ReadField(scene.Team[4], TechnoScene.HealthOffset);
                uint unknown = scene.ReadField(scene.Team[5], TechnoScene.HealthOffset);

                Check("秒杀：只清敌方，我方血量一点没动",
                    kill.Written == 3 && enemy == 0 && mine == 200,
                    string.Format(CultureInfo.InvariantCulture,
                        "写入 {0}｜跳过 {1}｜我方剩 {2}｜敌方剩 {3}",
                        kill.Written, kill.Skipped, mine, enemy));

                Check("秒杀：阵营读不出来的对象按「归属不明」跳过，绝不冒险",
                    unknown == 100,
                    string.Format(CultureInfo.InvariantCulture, "剩余血量 {0}", unknown));

                // 已经倒下的（血量 0）不再重复写。不跳过的后果不只是浪费：
                // 单拍写入有上限，前排"尸体"吃光额度会让后面的敌人永远轮不到（"杀着杀着不动了"）。
                TechnoOpResult dead = TechnoOps.KillEnemies(
                    io, walk.Objects, TechnoScene.OwnerOffset, TechnoScene.HealthOffset, scene.MyOwner);
                Check("秒杀：血量已经是 0 的目标不重复写（旧写法这里会再写 3 个）",
                    dead.Written == 0 && dead.Skipped >= 3,
                    string.Format(CultureInfo.InvariantCulture,
                        "第二次写入 {0}｜跳过 {1}", dead.Written, dead.Skipped));

                // ================= 瞬间建造 =================

                io.WriteUInt32(scene.Team[0] + TechnoScene.ProgressOffset, 500);
                io.WriteUInt32(scene.Team[1] + TechnoScene.ProgressOffset, 600);
                io.WriteUInt32(scene.Team[2] + TechnoScene.ProgressOffset, 700);

                TechnoOpResult noProg = TechnoOps.InstantBuild(
                    io, walk.Objects, TechnoScene.OwnerOffset, 0, scene.MyOwner);
                Check("瞬间建造：未校准「生产进度」偏移时拒绝执行",
                    !noProg.Ok && scene.ReadField(scene.Team[0], TechnoScene.ProgressOffset) == 500,
                    noProg.Summary);

                TechnoOpResult build = TechnoOps.InstantBuild(
                    io, walk.Objects, TechnoScene.OwnerOffset, TechnoScene.ProgressOffset, scene.MyOwner);
                Check("瞬间建造：只清自己的进度，敌方进度一点没动",
                    build.Written == 2
                        && scene.ReadField(scene.Team[0], TechnoScene.ProgressOffset) == 0
                        && scene.ReadField(scene.Team[1], TechnoScene.ProgressOffset) == 0
                        && scene.ReadField(scene.Team[2], TechnoScene.ProgressOffset) == 700,
                    string.Format(CultureInfo.InvariantCulture,
                        "写入 {0}｜跳过 {1}｜敌方进度 {2}",
                        build.Written, build.Skipped,
                        scene.ReadField(scene.Team[2], TechnoScene.ProgressOffset)));

                TechnoOpResult again = TechnoOps.InstantBuild(
                    io, walk.Objects, TechnoScene.OwnerOffset, TechnoScene.ProgressOffset, scene.MyOwner);
                Check("瞬间建造：进度本来就是 0 的对象不重复写",
                    again.Written == 0,
                    string.Format(CultureInfo.InvariantCulture, "第二次写入 {0}", again.Written));

                // ================= 全图视野 =================

                uint shroud = scene.Arrays + 0x1000;
                uint shroudSize = 0x4000;
                uint chunk = 0x1000;
                byte[] fill = new byte[shroudSize];
                for (int i = 0; i < fill.Length; i++) { fill[i] = 0xFF; }
                io.Write(shroud, fill, 0, fill.Length);

                uint cursor = 0;
                TechnoOpResult noShroud = TechnoOps.RevealMap(io, 0, shroudSize, chunk, ref cursor);
                Check("全图视野：未校准位图地址时拒绝执行，且一个字节都没写",
                    !noShroud.Ok && scene.ReadField(shroud, 0) == 0xFFFFFFFF, noShroud.Blocked);

                TechnoOpResult noShroudSize = TechnoOps.RevealMap(io, shroud, 0, chunk, ref cursor);
                Check("全图视野：未校准位图大小时拒绝执行，且一个字节都没写",
                    !noShroudSize.Ok && scene.ReadField(shroud, 0) == 0xFFFFFFFF, noShroudSize.Blocked);

                cursor = 0;
                TechnoOps.RevealMap(io, shroud, shroudSize, chunk, ref cursor);
                Check("全图视野：一次只写一片（不会一口气写几 MB 把游戏卡住）",
                    cursor == chunk
                        && scene.ReadField(shroud, 0x800) == 0
                        && scene.ReadField(shroud, 0x1800) == 0xFFFFFFFF,
                    string.Format(CultureInfo.InvariantCulture, "写完一片后游标 0x{0:X}", cursor));

                TechnoOps.RevealMap(io, shroud, shroudSize, chunk, ref cursor);
                TechnoOps.RevealMap(io, shroud, shroudSize, chunk, ref cursor);
                TechnoOps.RevealMap(io, shroud, shroudSize, chunk, ref cursor);
                Check("全图视野：铺满一轮后游标归零（下一拍从头再铺）",
                    cursor == 0,
                    string.Format(CultureInfo.InvariantCulture, "游标 0x{0:X}", cursor));

                bool allZero = true;
                for (uint offset = 0; offset < shroudSize; offset += 4)
                {
                    if (scene.ReadField(shroud, offset) != 0) { allZero = false; break; }
                }
                Check("全图视野：整块位图确实被清成 0", allZero,
                    string.Format(CultureInfo.InvariantCulture, "{0} 字节", shroudSize));

                // ================= 可信度判定（所有写操作的最后一道闸）=================

                string reasonOk;
                Check("可信度判定：正常名单放行",
                    TechnoOps.IsTrustworthy(walk, out reasonOk), "6 个对象");

                uint arrayTiny = scene.Arrays + 0x6000;
                scene.WriteArray(arrayTiny, new uint[] { scene.Team[0], scene.Team[1], scene.Team[2] });
                TechnoWalkResult tiny = TechnoWalker.WalkArray(
                    io, arrayTiny, 3, scene.ModuleBase, scene.ModuleSize);
                string reasonTiny;
                Check("可信度判定：只数出 3 个对象时判定为不可信，并说明原因",
                    !TechnoOps.IsTrustworthy(tiny, out reasonTiny) && reasonTiny.Length > 0, reasonTiny);

                string reasonBroken;
                TechnoWalkResult broken = TechnoWalker.WalkArray(
                    io, scene.ArrayMain, 0, scene.ModuleBase, scene.ModuleSize);
                Check("可信度判定：遍历本身报错时不可信，并原样带出错误原因",
                    !TechnoOps.IsTrustworthy(broken, out reasonBroken) && reasonBroken.Length > 0,
                    reasonBroken);

                // ================= 单拍写入上限 =================

                scene.PlantCrowd();
                TechnoWalkResult crowd = TechnoWalker.WalkArray(
                    io, scene.CrowdArray, TechnoScene.CrowdCount, scene.ModuleBase, scene.ModuleSize);
                TechnoOpResult capped = TechnoOps.KillEnemies(
                    io, crowd.Objects, TechnoScene.CrowdOwnerOffset, TechnoScene.CrowdHealthOffset,
                    scene.MyOwner);
                Check("秒杀：单拍写入数量有上限（不会一口气写穿，把游戏卡住）",
                    crowd.Count == TechnoScene.CrowdCount
                        && capped.Written == TechnoOps.MaxWritesPerTick,
                    string.Format(CultureInfo.InvariantCulture,
                        "名单 {0} 个｜本拍写入 {1}（上限 {2}）",
                        crowd.Count, capped.Written, TechnoOps.MaxWritesPerTick));
            }
            catch (Exception ex)
            {
                Check("批量操作", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        // ============================================================ 选中读取（共享件，2026-10-08）

        /// <summary>
        /// 「你选中的是哪些单位」的可靠读法（`SelectionResolver`）——
        /// 2026-10-08 真机教训的回归：**配置里的槽位指向的可能是垃圾数组**，
        /// 必须靠"金钱锚定"自己认出正确的那张；认不出时按档位拒绝（fail-closed）。
        /// </summary>
        private static void CheckSelectionResolver()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("选中读取（金钱锚定）", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("选中读取测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;
                uint module = scene.ModuleBase;
                const uint Money = 5000;

                // 我方房子：+0x30C 装着金钱（归属判据）
                io.WriteUInt32(scene.MyOwner + TechnoOwnership.HouseMoneyOffset, Money);

                // 两张数组：真的（装我方两个对象）、假的（装"看着像对象"的 Team[2]/Team[3]）
                uint realArray = scene.Arrays + 0x2000;
                uint junkArray = scene.Arrays + 0x2400;
                io.WriteUInt32(realArray, scene.Team[0]);
                io.WriteUInt32(realArray + 4, scene.Team[1]);
                io.WriteUInt32(junkArray, scene.Team[2]);
                io.WriteUInt32(junkArray + 4, scene.Team[3]);

                // 选中：数量 = 2；**配置槽位故意指向假数组**；真槽位放在数量字段 +0x24 处
                uint countAddress = module + 0x300;
                uint wrongSlot = module + 0x304;
                uint rightSlot = module + 0x324;
                io.WriteUInt32(countAddress, 2);
                io.WriteUInt32(wrongSlot, junkArray);
                io.WriteUInt32(rightSlot, realArray);

                CalibrationProfile profile = new CalibrationProfile();
                profile.SelectedCount.SetStaticAddress(0x300);
                profile.SelectedList.SetStaticAddress(0x304);   // ← 故意是错的
                profile.Money.SetOffsets(new uint[] { 0x100, TechnoOwnership.HouseMoneyOffset });
                io.WriteUInt32(module + 0x100, scene.MyOwner);

                List<MemoryRegion> heap = new List<MemoryRegion>();
                heap.Add(MakeRegion(scene.Heap, TestBlockSize));
                List<MemoryRegion> pointer = new List<MemoryRegion>();
                pointer.Add(MakeRegion(module, TestBlockSize));
                pointer.Add(MakeRegion(scene.Heap, TestBlockSize));
                SyntheticTarget target = new SyntheticTarget(
                    io, module, TechnoScene.FakeModuleSize, heap, pointer);

                // ① 自动更正：配置槽位是垃圾，靠金钱锚定认出真数组
                SelectionResolver resolver = new SelectionResolver(target);
                List<uint> objects;
                string note;
                bool ok = resolver.TryReadSelectedObjects(profile, 8, true, out objects, out note);

                bool sameSet = ok && objects.Count == 2;
                if (sameSet)
                {
                    for (int i = 0; i < objects.Count; i++)
                    {
                        if (objects[i] != scene.Team[0] && objects[i] != scene.Team[1]) { sameSet = false; }
                    }
                }
                Check("选中读取：配置槽位指向垃圾数组时，自动更正到真正含本方对象的那张",
                    sameSet && note.IndexOf("自动更正") >= 0,
                    ok ? string.Format(CultureInfo.InvariantCulture,
                        "读到 {0} 个｜{1}", objects.Count, note) : note);

                // ② 解析结果必须指向真数组，而不是配置槽位里那张
                SelectionResolver fresh = new SelectionResolver(target);
                uint resolved;
                string resolveNote;
                bool resolvedOk = fresh.ResolveArray(
                    profile, countAddress, wrongSlot, 2, out resolved, out resolveNote);
                Check("选中读取：解析结果指向真数组（不是配置槽位里那张垃圾）",
                    resolvedOk && resolved == realArray,
                    string.Format(CultureInfo.InvariantCulture, "解析到 0x{0:X8}（期望 0x{1:X8}）｜{2}",
                        resolved, realArray, resolveNote));

                // ③ fail-closed：金钱读不到时，"只返回自己人"这一档必须拒绝
                //（宁可不给线索，也不拿没核对过的东西去当线索）
                CalibrationProfile noMoney = new CalibrationProfile();
                noMoney.SelectedCount.SetStaticAddress(0x300);
                noMoney.SelectedList.SetStaticAddress(0x324);
                SelectionResolver resolver2 = new SelectionResolver(target);
                List<uint> objects2;
                string note2;
                bool ok2 = resolver2.TryReadSelectedObjects(noMoney, 8, true, out objects2, out note2);
                Check("选中读取：读不到金钱值时拒绝返回「自己人」（fail-closed）",
                    !ok2 && note2.Length > 0,
                    note2);

                // ④ 数量为 0：明确拒绝并说明，而不是静默返回空
                io.WriteUInt32(countAddress, 0);
                SelectionResolver resolver3 = new SelectionResolver(target);
                bool ok3 = resolver3.TryReadSelectedObjects(profile, 8, true, out objects2, out note2);
                Check("选中读取：没有选中任何对象时明确拒绝（不是静默返回空）",
                    !ok3 && note2.IndexOf("没有选中") >= 0,
                    note2);
                io.WriteUInt32(countAddress, 2);

                // ⑤ 宽松读取（2026-10-08 晚新增）：**选中的东西里没有本方单位**时必须读得出来。
                //    真机上的典型情形：玩家单独点了一座中立建筑 ——
                //    心灵终结里中立单位不能和友军一起选中，所以"选中立建筑看一眼"必然走这条路。
                //    布置：数量 1；"不属于我方"的对象放在 数量字段 −0x0C 的槽位里；
                //    配置槽位故意指向一张**内容不像对象**的垃圾数组（宽松版不许盲信它）。
                uint neutralArray = scene.Arrays + 0x2800;
                uint garbageArray = scene.Arrays + 0x2C00;
                uint layoutSlot = countAddress - 0x0C;
                io.WriteUInt32(neutralArray, scene.Team[2]);          // 只装一个"非我方"对象
                io.WriteUInt32(garbageArray, 0x00000010);             // 内容不像对象
                io.WriteUInt32(countAddress, 1);
                io.WriteUInt32(layoutSlot, neutralArray);
                io.WriteUInt32(wrongSlot, garbageArray);
                // 让"我方那张数组"也不再含我方对象 —— 否则严格版会从别的槽位兜到它，测不出差异
                io.WriteUInt32(realArray, scene.Team[2]);
                io.WriteUInt32(realArray + 4, scene.Team[3]);
                io.WriteUInt32(rightSlot, 0);

                SelectionResolver resolver4 = new SelectionResolver(target);
                List<uint> strict;
                string strictNote;
                bool strictFailed = !resolver4.TryReadSelectedObjects(profile, 8, true, out strict, out strictNote);

                SelectionResolver resolver5 = new SelectionResolver(target);
                List<uint> loose;
                string looseNote;
                bool looseOk = resolver5.TryReadSelectedObjectsLoose(profile, 8, out loose, out looseNote);
                Check("选中读取：只选中「不属于我方」的东西时，宽松版读得出、严格版拒绝",
                    strictFailed && looseOk && loose.Count == 1 && loose[0] == scene.Team[2],
                    string.Format(CultureInfo.InvariantCulture,
                        "严格版{0}｜宽松版读到 {1} 个（{2}）",
                        strictFailed ? "拒绝" : "居然成功了", loose.Count, looseNote));

                // ⑥ 宽松读取不是"能读就返回"：候选槽位全指向不像对象的东西 → 明确拒绝
                io.WriteUInt32(layoutSlot, garbageArray);
                SelectionResolver resolver6 = new SelectionResolver(target);
                List<uint> loose2;
                string looseNote2;
                bool looseRejected = !resolver6.TryReadSelectedObjectsLoose(profile, 8, out loose2, out looseNote2);
                Check("选中读取：宽松版读到的东西不像对象时明确拒绝（宁可不判定）",
                    looseRejected && looseNote2.Length > 0,
                    looseNote2);

                // ⑦ 沿用旧锚点的前提：它必须仍指着**金钱链此刻指向的那个房子**（换局即作废）
                uint moneyAddress = scene.MyOwner + TechnoOwnership.HouseMoneyOffset;
                string anchorNote;
                bool sameHouse = TechnoOwnership.TryVerifyCachedAnchor(
                    io, 0x14C, scene.MyOwner, moneyAddress, out anchorNote);
                bool otherHouse = TechnoOwnership.TryVerifyCachedAnchor(
                    io, 0x14C, scene.EnemyOwner, moneyAddress, out anchorNote);
                bool noAnchor = TechnoOwnership.TryVerifyCachedAnchor(
                    io, 0, 0, moneyAddress, out anchorNote);
                Check("沿用旧锚点：只有「还指着同一个房子」才放行（换了局 / 没建过一律拒绝）",
                    sameHouse && !otherHouse && !noAnchor && anchorNote.Length > 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "同一个房子={0}｜换了房子={1}｜没建过={2}｜最后一条说明：{3}",
                        sameHouse, otherHouse, noAnchor, anchorNote));
            }
            catch (Exception ex)
            {
                Check("选中读取（金钱锚定）", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        // ============================================================ 秒杀接线（S6-1）

        /// <summary>
        /// 「秒杀敌人」从引擎到内存的**端到端**回归（2026-10-07 接线时新增）。
        ///
        /// 为什么必须在引擎层再测一遍：`TechnoOps.KillEnemies` 单测只证明"给对参数就会写对"，
        /// 而接线的风险全在**参数从哪来** —— 名单怎么解析、我方是谁、什么时候该拒绝。
        /// 这是全项目唯一会一次性重写上千个对象状态的路径，出错的形态是"把自己的部队清空"，
        /// 所以这一组断言的重点和 CheckTechnoOps 一样：**该拒绝的时候一定拒绝、一个字节都不写**。
        ///
        /// 用到的关键设计：**故意把配置里的「阵营偏移」设成错的**（+0x2A8，那些位置全是 0）。
        /// 如果引擎真的用了它，全场都会判成"归属不明"，最终因"我方 0 个"而拒绝执行、
        /// 一个敌人都杀不掉 —— 所以"敌人确实被清掉"这件事本身就证明：
        /// 引擎用的是**金钱锚定**出来的偏移。
        /// </summary>
        private static void CheckKillWiring()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("秒杀接线", false, "OpenProcess 失败"); return; }

            TechnoScene scene = null;
            CheatEngine engine = null;
            try
            {
                scene = TechnoScene.Create(self);
                if (scene == null) { Check("秒杀接线测试内存块", false, Win32Api.LastErrorMessage()); return; }

                MemoryIO io = scene.Io;
                uint module = scene.ModuleBase;

                const uint Money = 5000;
                const uint EnemyMoney = 7000;
                const uint OwnerOffset = TechnoScene.OwnerOffset;     // 场景里对象的阵营字段（=真机的 +0xF0）
                const uint HealthOffset = TechnoScene.HealthOffset;   // =真机的 +0x6C

                // 房子：我方 / 敌方，各自 +0x30C 处放着金钱 —— 金钱锚定的判据就是它
                io.WriteUInt32(scene.MyOwner + TechnoOwnership.HouseMoneyOffset, Money);
                io.WriteUInt32(scene.EnemyOwner + TechnoOwnership.HouseMoneyOffset, EnemyMoney);

                // 2026-10-08 新增的三类"特殊对象"（挂进扩展名单）：
                //   · 疑似中立：归属房子的金钱读数为 0（中立/民用建筑通常如此）→ 绝不杀；
                //   · 归属存疑：归属值指向代码区（真机上抓到过 0x0087E294 这种）→ 绝不杀；
                //   · 邻居几何：一个敌方对象，其后方 0x600 处放一个我方对象 ——
                //     真机上"抽检扫 0x1000"正是把这样的邻居当成了它自己
                //     （被判为敌方的 0x2856C008，与线索单位 0x2856CE18 相距仅 0xE10）。
                uint neutralHouse = scene.Heap + 0xFC00;
                io.WriteUInt32(neutralHouse + TechnoOwnership.HouseMoneyOffset, 0);   // 金钱 0 = 疑似中立

                // 2026-10-08 晚新增：**有钱的友军阵营**（AI 队友/人类队友就是这样）——
                // 它和"疑似中立"的关键区别就是**有钱**，所以"金钱 0 不杀"那条挡不住它。
                // 用户拍板用「标记友军」兜底：没标记 → 会被当敌人清掉；标记过 → 一个字节都不碰。
                // 房子要放在堆块内：它的"金钱字段"在 +0x30C，放得太靠边会落到块外读不出来
                // （踩过一次：0xFE00 + 0x30C 已经越过 64KB 的堆块 → 阵营被当成"读不出"）。
                uint allyHouse = scene.Heap + 0xE800;
                io.WriteUInt32(allyHouse + TechnoOwnership.HouseMoneyOffset, 8888);
                uint ally1 = scene.Heap + 0x3300;
                uint ally2 = scene.Heap + 0x3400;

                // 2026-10-08 晚新增：**"孤零零只有一个对象的阵营"** —— 真机实测那几个可疑目标
                // （金钱读数 3.5 亿 / 8.6 亿 / 97 这种）每个都只对应 1 个对象，大概率根本不是单位
                // （子弹 / 动画之类），拿它们写血量等于往非单位对象上写。判据：本批里只有 1 个对象的
                // 阵营一律不杀（宁可少杀，绝不误杀）。
                uint lonelyHouse = scene.Heap + 0xE600;
                io.WriteUInt32(lonelyHouse + TechnoOwnership.HouseMoneyOffset, 12345);
                uint lonelyObject = scene.Heap + 0x3600;

                uint neutral1 = scene.Heap + 0x3000;
                uint neutral2 = scene.Heap + 0x3100;
                uint junkOwner = scene.Heap + 0x3200;      // 归属值 = 代码区地址 → 存疑
                uint myExtra = scene.Heap + 0x4300;        // 我方对象（邻居几何用）
                uint neighborEnemy = scene.Heap + 0x3D00;  // 敌方对象，其旧 0x1000 窗口含上面的自己人

                uint[] extra = new uint[] { neutral1, neutral2, junkOwner, myExtra, neighborEnemy, ally1, ally2, lonelyObject };
                for (int i = 0; i < extra.Length; i++)
                {
                    io.WriteUInt32(extra[i], scene.ModuleBase + 0x2000);   // 共用类型表（像真对象）
                    io.WriteUInt32(extra[i] + HealthOffset, 100);
                }
                io.WriteUInt32(neutral1 + OwnerOffset, neutralHouse);
                io.WriteUInt32(neutral2 + OwnerOffset, neutralHouse);
                io.WriteUInt32(junkOwner + OwnerOffset, scene.ModuleBase + 0x800);   // 代码区里的值
                // 让"代码区归属"指向的位置也放一个非 0 的"假金钱" —— 否则它会被
                // "金钱 0 = 疑似中立"这条顺手保护，显不出"归属存疑"这条判据本身
                //（改坏验证时要能把两条判据分开）。
                io.WriteUInt32(scene.ModuleBase + 0x800 + TechnoOwnership.HouseMoneyOffset, 12345);
                io.WriteUInt32(myExtra + OwnerOffset, scene.MyOwner);
                io.WriteUInt32(neighborEnemy + OwnerOffset, scene.EnemyOwner);
                io.WriteUInt32(ally1 + OwnerOffset, allyHouse);
                io.WriteUInt32(ally2 + OwnerOffset, allyHouse);
                io.WriteUInt32(lonelyObject + OwnerOffset, lonelyHouse);

                // 全对象名单：模块内的"格子"（静态地址）→ 扩展数组（原 7 项 + 这 8 个）
                uint rosterSlot = module + 0x200;
                uint rosterCount = module + 0x204;
                uint rosterArray = scene.Arrays + 0x2000;
                scene.WriteArray(rosterArray, new uint[]
                {
                    scene.Team[0], scene.Team[1], scene.Team[2],
                    scene.Team[3], scene.Team[4], scene.Team[5], scene.RareObject,
                    neutral1, neutral2, junkOwner, myExtra, neighborEnemy, ally1, ally2, lonelyObject
                });
                io.WriteUInt32(rosterSlot, rosterArray);
                io.WriteUInt32(rosterCount, 15);

                // 选中名单：数量 + 指针槽（槽里放一张只含我方两个对象的数组）
                uint selCount = module + 0x300;
                uint selSlot = module + 0x304;
                uint selArray = scene.Arrays + 0x1800;
                io.WriteUInt32(selCount, 2);
                io.WriteUInt32(selSlot, selArray);
                io.WriteUInt32(selArray, scene.Team[0]);
                io.WriteUInt32(selArray + 4, scene.Team[1]);

                // 金钱链：静态根（模块内）→ 我方房子 → +0x30C
                io.WriteUInt32(module + 0x100, scene.MyOwner);

                CalibrationProfile profile = new CalibrationProfile();
                profile.Money.SetOffsets(new uint[] { 0x100, TechnoOwnership.HouseMoneyOffset });
                profile.TechnoArray.SetStaticAddress(0x200);
                profile.TechnoArrayCount.SetStaticAddress(0x204);
                profile.SelectedCount.SetStaticAddress(0x300);
                profile.SelectedList.SetStaticAddress(0x304);
                profile.OffsetTechnoHealth = HealthOffset;
                profile.OffsetTechnoOwner = 0x2A8;   // **故意是错的** —— 见方法注释

                List<MemoryRegion> heap = new List<MemoryRegion>();
                heap.Add(MakeRegion(scene.Heap, TestBlockSize));
                List<MemoryRegion> pointer = new List<MemoryRegion>();
                pointer.Add(MakeRegion(scene.ModuleBase, TestBlockSize));
                pointer.Add(MakeRegion(scene.Heap, TestBlockSize));
                SyntheticTarget target = new SyntheticTarget(
                    io, scene.ModuleBase, TechnoScene.FakeModuleSize, heap, pointer);

                // ================= ① 正常路径 =================
                engine = new CheatEngine(target, new TrainerSettings());
                CheatFeature kill = engine.Add(new CheatFeature(
                    CheatId.KillEnemies, "秒杀", "测试用", SynchRisk.High, false));
                engine.BindProfile(profile);
                Check("秒杀接线：名单齐备时功能被视为已校准",
                    kill.IsCalibrated, "IsCalibrated=" + kill.IsCalibrated);

                // ---- 「孤例阵营」判据（2026-10-08 晚新增）----
                // 真机依据：预演"将击杀"里那几个可疑单例，每个都只对应 1 个对象（而真敌方阵营有 7~8 个）。
                // 这里造同款：一个归属**看着像真阵营**（有钱、读得出）但全场只有它自己一个对象的对象。
                // 断言两件事：① 它被判为存疑而不是可击杀；② 引擎跑完之后它的血量一点没动（见上面 ①）。
                TechnoWalkResult lonlyRoster = TechnoWalker.WalkArray(
                    io, rosterArray, 15, scene.ModuleBase, scene.ModuleSize);
                TechnoOwnership.OwnerClassification lonelyCls = TechnoOwnership.ClassifyOwners(
                    io, lonlyRoster.Objects, OwnerOffset, scene.MyOwner, scene.ModuleBase, scene.ModuleSize);
                bool lonelyKillable = false;
                for (int i = 0; i < lonelyCls.Killable.Count; i++)
                {
                    if (lonelyCls.Killable[i].Address == lonelyObject) { lonelyKillable = true; }
                }
                Check("分类：「全场只有一个对象的阵营」判为存疑、不进可击杀名单（真机那类都是垃圾归属）",
                    !lonelyKillable && lonelyCls.SingletonSkipped == 1,
                    string.Format(CultureInfo.InvariantCulture,
                        "孤例被判可击杀={0}｜孤例跳过 {1} 个｜存疑合计 {2}｜可击杀 {3}",
                        lonelyKillable, lonelyCls.SingletonSkipped, lonelyCls.Unknown, lonelyCls.KillableCount));

                kill.Enabled = true;
                engine.Start();

                bool killed = WaitUntil(delegate
                {
                    return scene.ReadField(scene.Team[2], HealthOffset) == 0
                        && scene.ReadField(scene.Team[3], HealthOffset) == 0
                        && scene.ReadField(scene.Team[4], HealthOffset) == 0;
                }, 3000);

                Check("秒杀接线：只清敌方；我方与归属不明的对象一点没动（配置里的阵营偏移是错的也照样分清）",
                    killed
                        && scene.ReadField(scene.Team[0], HealthOffset) == 100
                        && scene.ReadField(scene.Team[1], HealthOffset) == 100
                        && scene.ReadField(scene.Team[5], HealthOffset) == 100
                        && scene.ReadField(lonelyObject, HealthOffset) == 100,   // 孤例阵营也不许碰
                    string.Format(CultureInfo.InvariantCulture,
                        "我方 {0}/{1}｜敌方 {2}/{3}/{4}｜归属不明 {5}｜写入 {6} 次｜{7}",
                        scene.ReadField(scene.Team[0], HealthOffset),
                        scene.ReadField(scene.Team[1], HealthOffset),
                        scene.ReadField(scene.Team[2], HealthOffset),
                        scene.ReadField(scene.Team[3], HealthOffset),
                        scene.ReadField(scene.Team[4], HealthOffset),
                        scene.ReadField(scene.Team[5], HealthOffset),
                        kill.WriteCount,
                        kill.LastError.Length == 0 ? "无错误" : kill.LastError));

                Check("秒杀接线：疑似中立（归属房子金钱读数为 0）的对象绝不杀",
                    scene.ReadField(neutral1, HealthOffset) == 100
                        && scene.ReadField(neutral2, HealthOffset) == 100,
                    string.Format(CultureInfo.InvariantCulture,
                        "疑似中立血量 {0}/{1}",
                        scene.ReadField(neutral1, HealthOffset), scene.ReadField(neutral2, HealthOffset)));

                Check("秒杀接线：归属存疑（归属值指向代码区）的对象绝不杀",
                    scene.ReadField(junkOwner, HealthOffset) == 100,
                    string.Format(CultureInfo.InvariantCulture,
                        "存疑对象血量 {0}", scene.ReadField(junkOwner, HealthOffset)));

                Check("秒杀接线：紧邻自己人的敌方对象照杀不误（抽检窗口不再把邻居算成它）",
                    scene.ReadField(neighborEnemy, HealthOffset) == 0
                        && scene.ReadField(myExtra, HealthOffset) == 100,
                    string.Format(CultureInfo.InvariantCulture,
                        "邻居敌方血量 {0}｜旁边的自己人 {1}",
                        scene.ReadField(neighborEnemy, HealthOffset),
                        scene.ReadField(myExtra, HealthOffset)));

                engine.Stop();
                engine = null;

                // 后面每条用例都从"敌人满血"开始
                for (int i = 2; i <= 4; i++) { io.WriteUInt32(scene.Team[i] + HealthOffset, 100); }
                io.WriteUInt32(neighborEnemy + HealthOffset, 100);

                // ================= ② 建立不了我方锚点 → 拒绝 =================
                io.WriteUInt32(selCount, 0);   // 游戏里"什么都没选中"

                engine = new CheatEngine(target, new TrainerSettings());
                CheatFeature noAnchor = engine.Add(new CheatFeature(
                    CheatId.KillEnemies, "秒杀", "测试用", SynchRisk.High, false));
                engine.BindProfile(profile);
                noAnchor.Enabled = true;
                engine.Start();
                Thread.Sleep(900);

                Check("秒杀接线：没有可用的「我方锚点」时拒绝执行，一个字节都没写",
                    scene.ReadField(scene.Team[2], HealthOffset) == 100
                        && scene.ReadField(scene.Team[3], HealthOffset) == 100
                        && scene.ReadField(scene.Team[4], HealthOffset) == 100
                        && noAnchor.LastError.Length > 0,
                    noAnchor.LastError.Length == 0 ? "居然没有报错" : noAnchor.LastError);

                engine.Stop();
                engine = null;

                // ================= ③ 抽检发现问题 → 拒绝 =================
                io.WriteUInt32(selCount, 2);   // 恢复"选中了我方两个单位"
                // 每条用例都从"敌人满血"开始：任一条用例失败时，后面的布尔判据才仍然独立可读
                for (int i = 2; i <= 4; i++) { io.WriteUInt32(scene.Team[i] + HealthOffset, 100); }

                // 在一个"判为敌方"的对象窗口里塞入我方 House 地址 ——
                // 模拟真实世界里"分类偏移找错"的形态：被判成敌人的其实连着自己家。
                io.WriteUInt32(scene.Team[2] + 0x150, scene.MyOwner);

                engine = new CheatEngine(target, new TrainerSettings());
                CheatFeature suspect = engine.Add(new CheatFeature(
                    CheatId.KillEnemies, "秒杀", "测试用", SynchRisk.High, false));
                engine.BindProfile(profile);
                suspect.Enabled = true;
                engine.Start();
                Thread.Sleep(900);

                Check("秒杀接线：安全抽检发现「判为敌方」的其实连着我方 House → 拒绝执行，一个字节都没写",
                    scene.ReadField(scene.Team[2], HealthOffset) == 100
                        && scene.ReadField(scene.Team[3], HealthOffset) == 100
                        && scene.ReadField(scene.Team[4], HealthOffset) == 100
                        && suspect.LastError.IndexOf("抽检") >= 0,
                    suspect.LastError.Length == 0 ? "居然没有报错" : suspect.LastError);

                engine.Stop();
                engine = null;
                io.WriteUInt32(scene.Team[2] + 0x150, 0);   // 清掉污染

                // ================= ④ 名单指针失效 → 拒绝 =================
                for (int i = 2; i <= 4; i++) { io.WriteUInt32(scene.Team[i] + HealthOffset, 100); }
                io.WriteUInt32(rosterSlot, module + 0x7F00000);   // 指向一片没有映射的地址

                engine = new CheatEngine(target, new TrainerSettings());
                CheatFeature badArray = engine.Add(new CheatFeature(
                    CheatId.KillEnemies, "秒杀", "测试用", SynchRisk.High, false));
                engine.BindProfile(profile);
                badArray.Enabled = true;
                engine.Start();
                Thread.Sleep(900);

                Check("秒杀接线：名单指针失效时拒绝执行，一个字节都没写",
                    scene.ReadField(scene.Team[2], HealthOffset) == 100
                        && scene.ReadField(scene.Team[3], HealthOffset) == 100
                        && scene.ReadField(scene.Team[4], HealthOffset) == 100
                        && badArray.LastError.Length > 0,
                    badArray.LastError.Length == 0 ? "居然没有报错" : badArray.LastError);

                engine.Stop();
                engine = null;
                io.WriteUInt32(rosterSlot, rosterArray);

                // ================= ⑤ 「标记友军」→ 绝不杀（2026-10-08 晚新增） =================
                //
                // 背景（用户提问）：秒杀的判据只有"不是你 + 阵营有钱"，它**不知道"同盟"** ——
                // AI 队友 / 局域网人类队友恰好符合这两条，会被一起清掉，而这类误伤**不可逆**。
                // 用户拍板用"手动标记友军"兜底，这一节把"标记前会被杀、标记后一个字节不碰"钉死。
                for (int i = 2; i <= 4; i++) { io.WriteUInt32(scene.Team[i] + HealthOffset, 100); }
                io.WriteUInt32(neighborEnemy + HealthOffset, 100);
                io.WriteUInt32(ally1 + HealthOffset, 100);
                io.WriteUInt32(ally2 + HealthOffset, 100);

                // ---- ⑤-a 标记之后的分类：友军单独归一类，绝不进入可击杀名单 ----
                AllyGuard.Reset();
                AllyGuard.Mark(allyHouse);

                TechnoWalkResult allyRoster = TechnoWalker.WalkArray(
                    io, rosterArray, 14, scene.ModuleBase, scene.ModuleSize);
                TechnoOwnership.OwnerClassification allyCls = TechnoOwnership.ClassifyOwners(
                    io, allyRoster.Objects, OwnerOffset, scene.MyOwner, scene.ModuleBase, scene.ModuleSize);
                bool allyNotKillable = true;
                for (int i = 0; i < allyCls.Killable.Count; i++)
                {
                    if (allyCls.Killable[i].Address == ally1 || allyCls.Killable[i].Address == ally2)
                    {
                        allyNotKillable = false;
                    }
                }
                Check("分类：标记过的友军阵营单独归为「友军」，绝不进入可击杀名单",
                    allyCls.Ally == 2 && allyNotKillable && allyCls.KillableCount >= 3,
                    string.Format(CultureInfo.InvariantCulture,
                        "友军 {0}｜可击杀 {1}｜我方 {2}｜疑似中立 {3}｜存疑 {4}",
                        allyCls.Ally, allyCls.KillableCount, allyCls.Mine, allyCls.NeutralCount, allyCls.Unknown));

                // ---- ⑤-b **没标记**时它会被杀：这条断言记录"这个洞是真实存在的"（也解释了为什么必须标记）----
                AllyGuard.Reset();
                engine = new CheatEngine(target, new TrainerSettings());
                CheatFeature unmarked = engine.Add(new CheatFeature(
                    CheatId.KillEnemies, "秒杀", "测试用", SynchRisk.High, false));
                engine.BindProfile(profile);
                unmarked.Enabled = true;
                engine.Start();

                bool unmarkedAllyDied = WaitUntil(delegate
                {
                    return scene.ReadField(ally1, HealthOffset) == 0
                        && scene.ReadField(ally2, HealthOffset) == 0;
                }, 3000);
                Check("秒杀：**没标记**的友军阵营会被当成敌人清掉（这就是「必须先标记」的原因）",
                    unmarkedAllyDied,
                    string.Format(CultureInfo.InvariantCulture,
                        "未标记友军血量 {0}/{1}", scene.ReadField(ally1, HealthOffset), scene.ReadField(ally2, HealthOffset)));

                engine.Stop();
                engine = null;

                // ---- ⑤-c 标记之后：一个字节都不碰，敌人照杀 ----
                for (int i = 2; i <= 4; i++) { io.WriteUInt32(scene.Team[i] + HealthOffset, 100); }
                io.WriteUInt32(neighborEnemy + HealthOffset, 100);

                engine = new CheatEngine(target, new TrainerSettings());
                CheatFeature marked = engine.Add(new CheatFeature(
                    CheatId.KillEnemies, "秒杀", "测试用", SynchRisk.High, false));
                engine.BindProfile(profile);
                marked.Enabled = true;
                engine.Start();

                // 顺序要和真机一致：**先连上游戏、引擎跑起来，再标记友军**。
                // （引擎在"游戏刚连上"那一刻会清空会话级缓存 —— 那是防"换局拿旧地址认人"的，
                //   标记放在这之后才不会被那一拍清掉；真机上用户也是连上游戏之后才点标记。）
                Thread.Sleep(300);
                AllyGuard.Mark(allyHouse);
                io.WriteUInt32(ally1 + HealthOffset, 100);   // 标记前若挨过一拍，这里复原
                io.WriteUInt32(ally2 + HealthOffset, 100);

                bool enemiesStillDie = WaitUntil(delegate
                {
                    return scene.ReadField(scene.Team[2], HealthOffset) == 0
                        && scene.ReadField(scene.Team[3], HealthOffset) == 0
                        && scene.ReadField(scene.Team[4], HealthOffset) == 0;
                }, 3000);

                // 再让引擎多跑几拍：判据每一拍都重新算，若友军被算进"可击杀"，它 0.5 秒内就会被打回 0
                Thread.Sleep(1200);

                Check("秒杀：标记过的友军阵营一个字节都不写（队友免于误伤），敌人照杀不误",
                    enemiesStillDie
                        && scene.ReadField(ally1, HealthOffset) == 100
                        && scene.ReadField(ally2, HealthOffset) == 100,
                    string.Format(CultureInfo.InvariantCulture,
                        "友军血量 {0}/{1}｜敌方 {2}/{3}/{4}｜写入 {5} 次",
                        scene.ReadField(ally1, HealthOffset), scene.ReadField(ally2, HealthOffset),
                        scene.ReadField(scene.Team[2], HealthOffset),
                        scene.ReadField(scene.Team[3], HealthOffset),
                        scene.ReadField(scene.Team[4], HealthOffset),
                        marked.WriteCount));

                engine.Stop();
                engine = null;

                // 静态登记处是进程内共享的 —— 用完必须清掉，否则会污染后面的用例
                AllyGuard.Reset();

                // ================= ⑦ 「阵营分布」分析用的偏移必须由金钱锚定推导 =================
                //
                // 真机 10-08 两次查找证明：配置里那个人工试出的偏移（`fieldOffsets.technoOwner`）
                // 读出来的**不是阵营** —— 于是"阵营聚集成群"这条判据是在噪声上算的，
                // 16:46 那次因此选中了一张只有 60 个对象、玩家单位一个都不在里面的假名单。
                List<uint> lonelyHints = new List<uint>();
                lonelyHints.Add(scene.Team[0]);
                lonelyHints.Add(scene.Team[1]);

                string offsetNote;
                uint analysisOffset = TechnoOwnership.ResolveOwnerOffsetForAnalysis(
                    io, lonelyHints, Money, out offsetNote);
                Check("自动查找：「阵营分布」用的偏移由金钱锚定推导，不是配置里那个人工值",
                    analysisOffset == OwnerOffset && offsetNote.IndexOf("锚定") >= 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "推导出 +0x{0:X}（期望 +0x{1:X}；配置里的人工值是 +0x{2:X}）｜{3}",
                        analysisOffset, OwnerOffset, profile.OffsetTechnoOwner, offsetNote));

                string offsetNote2;
                uint analysisOffset2 = TechnoOwnership.ResolveOwnerOffsetForAnalysis(
                    io, lonelyHints, 0, out offsetNote2);
                Check("自动查找：推导不出阵营字段时返回 0（跳过该判据），绝不拿人工值充数",
                    analysisOffset2 == 0 && offsetNote2.Length > 0,
                    offsetNote2);

                // ================= ⑥ 环境（游戏版本）检查不过 → 一个字节都不写 =================
                //
                // 这是"防止在另一份引擎上照旧乱写"的最后一道闸（见 `EnvironmentCheck`）。
                // 它失效时表面完全看不出来：功能照常"工作"，写进去的却是别人的内存。
                for (int i = 2; i <= 4; i++) { io.WriteUInt32(scene.Team[i] + HealthOffset, 100); }
                io.WriteUInt32(neighborEnemy + HealthOffset, 100);

                engine = new CheatEngine(target, new TrainerSettings());
                CheatFeature versionBlocked = engine.Add(new CheatFeature(
                    CheatId.KillEnemies, "秒杀", "测试用", SynchRisk.High, false));
                engine.BindProfile(profile);
                engine.EnvironmentBlock = "游戏版本与校准记录不一致（测试用）——已停用全部写入功能";
                versionBlocked.Enabled = true;
                engine.Start();
                Thread.Sleep(900);

                Check("秒杀接线：环境（游戏版本）检查不过时拒绝写入，一个字节都没写，并把原因写在卡片上",
                    scene.ReadField(scene.Team[2], HealthOffset) == 100
                        && scene.ReadField(scene.Team[3], HealthOffset) == 100
                        && versionBlocked.LastError.IndexOf("版本") >= 0,
                    versionBlocked.LastError.Length == 0 ? "居然没有报错" : versionBlocked.LastError);

                engine.Stop();
                engine = null;
            }
            catch (Exception ex)
            {
                Check("秒杀接线", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                AllyGuard.Reset();   // 静态状态：任何一个用例抛异常都不能把"友军标记"留给后面
                if (engine != null) { try { engine.Dispose(); } catch (Exception) { } }
                if (scene != null) { scene.Dispose(); }
                self.Dispose();
            }
        }

        // ============================================================ 采集（S6/S4 校准辅助）

        /// <summary>
        /// 「一键采集」的端到端自检：在合成内存上完整跑一遍
        /// **启动 → 拒绝路径 → 采样 → 改字段 → 发现变化 → 出报告**。
        ///
        /// 为什么要端到端而不是只测单个函数：采集的价值全在"用户只操作一次"，
        /// 中间的定时、线程、报告落盘任何一环断了，用户的这一次操作就白费。
        /// 只测"某个方法返回对不对"是发现不了这种断裂的。
        /// </summary>
        private static void CheckHarvestSession()
        {
            SelfHandle self = SelfHandle.Create();
            if (self == null || !self.IsReady) { Check("一键采集", false, "OpenProcess 失败"); return; }

            IntPtr moduleBlock = IntPtr.Zero;
            IntPtr heapBlock = IntPtr.Zero;
            string reportPath = "";
            HarvestSession session = null;
            uint savedBase = SelectionProbe.ModuleBaseHint;
            uint savedSize = SelectionProbe.ModuleSizeHint;

            try
            {
                moduleBlock = AllocLowBlock(self.Handle);
                heapBlock = AllocLowBlock(self.Handle);
                if (moduleBlock == IntPtr.Zero || heapBlock == IntPtr.Zero)
                {
                    Check("一键采集测试内存块", false, Win32Api.LastErrorMessage());
                    return;
                }
                if (!IsLowAddress(moduleBlock) || !IsLowAddress(heapBlock))
                {
                    WarnItem("一键采集", "测试块不在 4GB 以下，跳过");
                    return;
                }

                uint moduleBase = (uint)moduleBlock.ToInt64();
                uint moduleSize = 0x1000000;
                uint heap = (uint)heapBlock.ToInt64();
                MemoryIO io = new MemoryIO(self);

                const uint OwnerOffset = 0xF0;
                const uint HealthOffset = 0x6C;
                uint vtable = moduleBase + 0x2000;
                uint owner = heap + 0xF000;

                // 三个同类型对象（同类型表 —— 遍历器的"孤例类型剔除"要求它们至少两个共用一张表）
                uint[] objects = new uint[3];
                for (int i = 0; i < 3; i++)
                {
                    objects[i] = heap + 0x2000 + (uint)(i * 0x100);
                    io.WriteUInt32(objects[i], vtable);
                    io.WriteUInt32(objects[i] + OwnerOffset, owner);
                    io.WriteUInt32(objects[i] + HealthOffset, 100);
                }

                // 全对象名单：格子 → 数组
                uint rosterSlot = heap + 0x5000;
                uint rosterArray = heap + 0x4000;
                io.WriteUInt32(rosterSlot, rosterArray);
                for (int i = 0; i < 3; i++) { io.WriteUInt32(rosterArray + (uint)(i * 4), objects[i]); }

                // 选中名单：数量字段 + 紧邻的数组指针槽（真机的 DynamicVectorClass 就是这个样子）
                uint countAddress = heap + 0x1040;
                uint selectedArray = heap + 0x1080;
                io.WriteUInt32(countAddress, 3);
                io.WriteUInt32(countAddress + 8, selectedArray);
                for (int i = 0; i < 3; i++) { io.WriteUInt32(selectedArray + (uint)(i * 4), objects[i]); }

                // 让选中探测走**真机路径**（带"对象头必须是模块内类型表"那条硬校验）
                SelectionProbe.ModuleBaseHint = moduleBase;
                SelectionProbe.ModuleSizeHint = moduleSize;

                CalibrationProfile profile = new CalibrationProfile();
                profile.ModuleBase = moduleBase;
                profile.ModuleSize = moduleSize;
                profile.TechnoArray.SetStaticAddress(rosterSlot - moduleBase);
                profile.SelectedCount.SetStaticAddress(countAddress - moduleBase);
                // 2026-10-08 起，选中读取走共享件（SelectionResolver），需要配置里的"槽位"作为起点；
                // 本用例刻意**不校准金钱**，覆盖"金钱不可用 → 直接用配置槽位"这条兜底分支。
                profile.SelectedList.SetStaticAddress(countAddress + 8 - moduleBase);
                profile.OffsetTechnoOwner = OwnerOffset;
                profile.OffsetTechnoHealth = HealthOffset;

                List<MemoryRegion> regions = new List<MemoryRegion>();
                regions.Add(MakeRegion(moduleBase, TestBlockSize));
                regions.Add(MakeRegion(heap, TestBlockSize));
                SyntheticTarget target = new SyntheticTarget(io, moduleBase, moduleSize, regions, regions);

                // 报告落到 dist\reports\（与非自检运行时同一处），跑完删掉
                ProfileStore store = new ProfileStore(AppDomain.CurrentDomain.BaseDirectory);

                // ---- 拒绝路径：缺什么必须说什么，且不能有线程被启动 ----
                HarvestSession noTarget = new HarvestSession(null, profile, store);
                string refusalTarget = noTarget.Start(10, 500);
                Check("一键采集：游戏未连接时拒绝启动（不会静默失败）",
                    refusalTarget.Length > 0 && !noTarget.IsRunning, refusalTarget);
                noTarget.Dispose();

                CalibrationProfile bare = new CalibrationProfile();
                HarvestSession noCalib = new HarvestSession(target, bare, store);
                string refusalCalib = noCalib.Start(10, 500);
                Check("一键采集：缺「全对象名单」校准时拒绝启动，并说明缺哪一项",
                    refusalCalib.Length > 0
                        && refusalCalib.IndexOf("全对象名单", StringComparison.Ordinal) >= 0
                        && !noCalib.IsRunning,
                    refusalCalib.Length > 0
                        ? refusalCalib
                        : "启动没有被拒绝（本该返回一句说明，而且不该把线程拉起来）");
                noCalib.Dispose();

                // ---- 正常路径：端到端跑一遍 ----
                session = new HarvestSession(target, profile, store);
                string started = session.Start(60, 200);
                Check("一键采集：校准齐全时能启动", started.Length == 0,
                    started.Length > 0 ? started : "已启动");

                int n0 = session.SampleCount;
                WaitUntil(delegate { return session.SampleCount > n0; }, 3000);

                // 让被跟踪对象的血量变一次 —— 采集的核心价值就是抓到这种变化
                io.WriteUInt32(objects[0] + HealthOffset, 87);
                int n1 = session.SampleCount;
                WaitUntil(delegate { return session.SampleCount > n1; }, 3000);

                io.WriteUInt32(objects[0] + HealthOffset, 100);
                int n2 = session.SampleCount;
                WaitUntil(delegate { return session.SampleCount > n2; }, 3000);

                int taken = session.SampleCount;
                int tracked = session.TrackedCount;

                // ---- 新出现的对象自动跟踪（瞬间建造用）----
                // 往名单里加一个"采集期间才造出来的"对象：用户**没有选中它**，
                // 程序应当自己发现它、等它活过观察期、然后开始跟踪它的字段变化。
                // 这正是"造东西时不必先选中它"那条自动化。
                uint newborn = heap + 0x8000;
                io.WriteUInt32(newborn, vtable);
                io.WriteUInt32(newborn + OwnerOffset, owner);
                io.WriteUInt32(newborn + HealthOffset, 100);
                io.WriteUInt32(rosterArray + 12, newborn);      // 名单第 4 项

                WaitUntil(delegate { return session.NewTrackedCount > 0; }, 6000);

                Check("一键采集：采集期间新造出来的对象会被自动跟踪（不必先选中它）",
                    session.NewTrackedCount == 1,
                    "新跟踪 " + session.NewTrackedCount + " 个｜已采 " + session.SampleCount + " 次");
                Check("一键采集：它确实进了跟踪集合（不只是记了个数）",
                    session.TrackedCount == tracked + 1,
                    "跟踪数 " + tracked + " → " + session.TrackedCount);

                // ---- 负面用例：短命对象不许占跟踪名额 ----
                // 战场上每秒都有大量子弹/碎片/动画冒出来、转眼就没了。
                // 若"一出现就跟踪"，跟踪名额（MaxTracked）会被它们瞬间占满，
                // 真正要看的"正在造的那座建筑"一个都进不来 —— 那样这功能就白做了。
                uint mayfly = heap + 0x8800;
                io.WriteUInt32(mayfly, vtable);
                io.WriteUInt32(mayfly + OwnerOffset, owner);
                io.WriteUInt32(mayfly + HealthOffset, 100);
                io.WriteUInt32(rosterArray + 16, mayfly);       // 名单第 5 项，冒个头

                int beforeMayfly = session.SampleCount;
                WaitUntil(delegate { return session.SampleCount > beforeMayfly; }, 3000);   // 让它被看见一次
                io.WriteUInt32(rosterArray + 16, 0);            // 下一拍就"死"了

                int afterMayfly = session.SampleCount;
                WaitUntil(delegate { return session.SampleCount > afterMayfly + 3; }, 6000);

                Check("一键采集：只冒一下就没的对象不会占跟踪名额（子弹/碎片要挡掉）",
                    session.NewTrackedCount == 1,
                    "新跟踪 " + session.NewTrackedCount + " 个（应为 1 —— 只有那个新造的建筑）");

                reportPath = session.Stop();

                Check("一键采集：采样按时钟持续推进", taken >= 3, taken + " 次");
                Check("一键采集：把选中的对象收进了跟踪集合", tracked == 3, tracked + " 个");
                Check("一键采集：报告落盘且非空",
                    reportPath.Length > 0 && File.Exists(reportPath)
                        && new FileInfo(reportPath).Length > 0,
                    reportPath);

                string text = (reportPath.Length > 0 && File.Exists(reportPath))
                    ? File.ReadAllText(reportPath, Encoding.UTF8) : "";

                Check("一键采集：报告含时间线、跟踪段、全对象名单三段",
                    text.IndexOf("一、时间线", StringComparison.Ordinal) >= 0
                        && text.IndexOf("二、被跟踪对象", StringComparison.Ordinal) >= 0
                        && text.IndexOf("三、全对象名单", StringComparison.Ordinal) >= 0,
                    text.Length + " 字符");

                Check("一键采集：报告里标出了「采集期间新造出来的」那个对象",
                    text.IndexOf("采集期间新造出来的", StringComparison.Ordinal) >= 0,
                    "已标注");

                // 核心断言：血量 100 → 87 这次改动必须被记下来。
                // 用完整的一对值来匹配，避免"恰好命中文档里的偏移对照表"这种假通过。
                Check("一键采集：抓到了血量字段的变化（100 → 87）",
                    text.IndexOf("0x00000064 → 0x00000057", StringComparison.Ordinal) >= 0,
                    text.IndexOf("0x00000064 → 0x00000057", StringComparison.Ordinal) >= 0
                        ? "已记录" : "报告里找不到这次变化");

                Check("一键采集：报告里能看出那 3 个对象属于同一个阵营",
                    text.IndexOf("0x" + owner.ToString("X8"), StringComparison.Ordinal) >= 0,
                    "阵营 0x" + owner.ToString("X8"));
            }
            catch (Exception ex)
            {
                Check("一键采集", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (session != null) { try { session.Dispose(); } catch { } }
                SelectionProbe.ModuleBaseHint = savedBase;
                SelectionProbe.ModuleSizeHint = savedSize;
                if (reportPath.Length > 0 && File.Exists(reportPath))
                {
                    try { File.Delete(reportPath); } catch (Exception) { }
                }
                if (moduleBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, moduleBlock, IntPtr.Zero, MEM_RELEASE); }
                if (heapBlock != IntPtr.Zero) { Win32Api.VirtualFreeEx(self.Handle, heapBlock, IntPtr.Zero, MEM_RELEASE); }
                self.Dispose();
            }
        }

        // ============================================================ 辅助

        /// <summary>自检专用的"自身进程"句柄实现。</summary>
        private sealed class SelfHandle : IProcessHandle
        {
            private IntPtr _handle = IntPtr.Zero;
            private int _pid;
            private bool _disposed;

            public static SelfHandle Create()
            {
                SelfHandle handle = new SelfHandle();
                Process current = Process.GetCurrentProcess();
                handle._pid = current.Id;
                handle._handle = Win32Api.OpenProcess(
                    Win32Api.PROCESS_QUERY_INFORMATION | Win32Api.PROCESS_VM_OPERATION
                        | Win32Api.PROCESS_VM_READ | Win32Api.PROCESS_VM_WRITE,
                    false, handle._pid);
                if (handle._handle == IntPtr.Zero)
                {
                    Logger.Error("SelfTest", "打开自身进程失败：" + Win32Api.LastErrorMessage());
                }
                return handle;
            }

            public IntPtr Handle { get { return _handle; } }
            public bool IsReady { get { return !_disposed && _handle != IntPtr.Zero; } }
            public int ProcessId { get { return _pid; } }

            // 自检进程位宽与游戏不同，模块基址无参考意义，故返回 0
            public uint ModuleBase { get { return 0; } }
            public uint ModuleSize { get { return 0; } }

            public string Describe()
            {
                return "self pid=" + _pid;
            }

            public void Dispose()
            {
                if (_disposed) { return; }
                _disposed = true;
                if (_handle != IntPtr.Zero)
                {
                    Win32Api.CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
    }
}
