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
            CheckTechnoArrayFinder();
            CheckTechnoOps();

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

            if (!elevated)
            {
                WarnItem("当前不是管理员权限",
                    "若游戏以管理员启动，本节第 6 部分的附加检查会因权限不足而跳过；请用管理员身份运行本自检");
            }

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

        private static void CheckPointerScanner()
        {
            SelfHandle self = SelfHandle.Create();
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

                uint[] objects = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    objects[i] = blockAddress + 0x2000 + (uint)(i * 0x100);
                    io.WriteUInt32(objects[i], (uint)(0x1111 + i));      // 让对象"可读"
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
            }
            catch (Exception ex)
            {
                Check("选中列表探测", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
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
                io.WriteUInt32(decoy, 0x00000001);
                uint arrayWrong = scene.Arrays + 0x0400;
                uint[] wrong = new uint[64];
                for (int i = 0; i < wrong.Length; i++) { wrong[i] = decoy; }
                scene.WriteArray(arrayWrong, wrong);

                TechnoWalkResult bad = TechnoWalker.WalkArray(
                    io, arrayWrong, 64, scene.ModuleBase, scene.ModuleSize);
                Check("对象遍历：名单地址找错时一个都不认（不会拿垃圾当单位）",
                    bad.Ok && bad.Count == 0 && bad.Skipped == 64,
                    string.Format(CultureInfo.InvariantCulture, "认 {0}｜跳过 {1}", bad.Count, bad.Skipped));

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
                    string.Format(CultureInfo.InvariantCulture, "认 {0}｜跳过 {1}",
                        filtered.Count, filtered.Skipped));

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

                List<TechnoArrayCandidate> found = TechnoArrayFinder.Find(
                    io, scene.ModuleBase, scene.ModuleSize, slotSelected, hints);

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
