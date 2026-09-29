using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MoTrainer.Cheats;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Calibration
{
    /// <summary>全对象名单里的一项（只取关键字段，避免报告体积爆炸）。</summary>
    public sealed class HarvestObjectRow
    {
        public uint Address;
        public uint VTable;
        public uint Owner;
        public uint Health;
    }

    /// <summary>一次采样的结果。</summary>
    public sealed class HarvestSample
    {
        public int Index;
        public double Seconds;
        public uint Money;
        public uint PowerOutput;
        public uint PowerDrain;
        public int SelectedCount;
        public readonly List<uint> Selected = new List<uint>();
        public int ObjectCount;
        public int WalkSkipped;
        public readonly List<string> OwnerGroups = new List<string>();
        public string Note = "";
    }

    /// <summary>一个被跟踪对象（曾经被选中过的）在整个采集期间的变化。</summary>
    public sealed class HarvestTracked
    {
        public uint Address;
        public uint FirstVTable;
        public int FirstSeen;
        public int LastSeen = -1;
        public int SeenCount;
        public int DiedAt = -1;
        public readonly List<int> SelectedAt = new List<int>();
        public readonly List<string> Changes = new List<string>();

        /// <summary>上一次采样的完整字节镜像（按 uint 存），用来求差。</summary>
        internal uint[] Last;
        internal bool HasLast;
    }

    /// <summary>
    /// 「一键采集」：在一局游戏里连续采样，把后面几轮要用的数据**一次收齐**。
    ///
    /// 为什么要有这个东西：本项目的瓶颈不是写代码，而是**每要一个数据就得让用户开一局游戏**。
    /// 传统做法是"要一个数据 → 用户开局 → 再要一个数据 → 用户再开局"，一趟一趟地磨。
    /// 这个类改成：用户只操作一次（开一局、点开始、随便玩几分钟、点停止），
    /// 期间程序自己按秒采样，把**时间线、房子数值、全对象名单、被选中对象的完整字节变化**
    /// 全部落到一个报告里。剩下的分析在离线做，不再占用用户的游戏时间。
    ///
    /// 关键设计：**跟踪集合只增不减**。
    /// 用户一旦选中过某个单位，之后即使取消选中，程序仍会每轮重新读它的字节。
    /// 这条是从真机失败里学来的（见 `docs/05` 的「只采样当前选中的对象采不到受害者」）：
    /// 想让一个单位挨打，按流程必须先取消选中换人强制攻击，一旦取消就再也采不到它了 ——
    /// 于是 58 次采样一个字节都没动。只增不减之后，受害者才会留在名单里。
    ///
    /// **本类只读，绝不写游戏内存。**
    /// </summary>
    public sealed class HarvestSession : IDisposable
    {
        /// <summary>
        /// 每个被跟踪对象 dump 多少字节。0x1000 覆盖已知字段（血量 0x6C、阵营 0xF0）
        /// 并留出充足余量 —— 未定位的字段（生产进度等）可能落在更靠后的位置。
        /// </summary>
        public const int DumpBytes = 0x1000;

        /// <summary>跟踪对象数上限，防止用户狂点选择把报告撑爆。</summary>
        public const int MaxTracked = 64;

        /// <summary>对象数量字段未校准时，按这个上限探测（与高级页口径一致）。</summary>
        private const uint ProbeObjectCount = 4096;

        /// <summary>采样的最短/最长间隔与最长时长，防止界面传入离谱的值。</summary>
        public const int MinIntervalMs = 200;
        public const int MaxIntervalMs = 5000;
        public const int MaxSeconds = 1800;

        private readonly ICalibrationTarget _target;
        private readonly CalibrationProfile _profile;
        private readonly ProfileStore _store;
        private readonly object _lock = new object();

        private readonly List<HarvestSample> _samples = new List<HarvestSample>();
        private readonly List<HarvestTracked> _tracked = new List<HarvestTracked>();
        private readonly Dictionary<uint, HarvestTracked> _byAddress = new Dictionary<uint, HarvestTracked>();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);

        private List<HarvestObjectRow> _lastRoster = new List<HarvestObjectRow>();
        private uint _rosterSlot;
        private uint _rosterArray;
        private uint _rosterFirstArray;
        private uint _rosterLastArray;
        private int _rosterArrayMoves;
        private int _rosterReadable = -1;
        private bool _rosterTruncated;
        private Thread _thread;
        private DateTime _startedAt;
        private int _intervalMs = 1000;
        private int _plannedSeconds = 180;
        private string _reportPath = "";
        private bool _disposed;

        public HarvestSession(ICalibrationTarget target, CalibrationProfile profile, ProfileStore store)
        {
            _target = target;
            _profile = profile == null ? new CalibrationProfile() : profile;
            _store = store;
        }

        public bool IsRunning
        {
            get
            {
                Thread t = _thread;
                return t != null && t.IsAlive;
            }
        }

        public int SampleCount { get { lock (_lock) { return _samples.Count; } } }
        public int TrackedCount { get { lock (_lock) { return _tracked.Count; } } }
        public string ReportPath { get { return _reportPath; } }
        public int PlannedSeconds { get { return _plannedSeconds; } }

        public double ElapsedSeconds
        {
            get
            {
                if (_startedAt == DateTime.MinValue) { return 0; }
                return (DateTime.UtcNow - _startedAt).TotalSeconds;
            }
        }

        /// <summary>
        /// 开始采集。返回空串表示已开始；返回非空就是"为什么不能开始"（界面直接显示这句话）。
        ///
        /// 缺什么就说什么 —— 绝不允许"按钮能点、程序悄悄什么都不做"。
        /// </summary>
        public string Start(int seconds, int intervalMs)
        {
            if (IsRunning) { return "已经在采集中了。"; }
            if (_target == null || !_target.IsReady)
            {
                return "游戏未连接。请先启动游戏，确认状态条显示「已连接」，再开始采集。";
            }
            if (!_profile.TechnoArray.IsCalibrated)
            {
                return "缺校准：配置里还没有「全对象名单」地址。采集要靠它枚举游戏里的单位，"
                     + "请先在「高级功能」页点一次「自动查找总名单」。";
            }

            if (intervalMs < MinIntervalMs) { intervalMs = MinIntervalMs; }
            if (intervalMs > MaxIntervalMs) { intervalMs = MaxIntervalMs; }
            if (seconds < 10) { seconds = 10; }
            if (seconds > MaxSeconds) { seconds = MaxSeconds; }

            _intervalMs = intervalMs;
            _plannedSeconds = seconds;

            lock (_lock)
            {
                _samples.Clear();
                _tracked.Clear();
                _byAddress.Clear();
                _lastRoster = new List<HarvestObjectRow>();
                _rosterSlot = 0;
                _rosterArray = 0;
                _rosterFirstArray = 0;
                _rosterLastArray = 0;
                _rosterArrayMoves = 0;
                _rosterReadable = -1;
                _rosterTruncated = false;
                _reportPath = "";
            }

            _startedAt = DateTime.UtcNow;
            _stop.Reset();
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.BelowNormal;   // 别去抢游戏的帧
            _thread.Name = "MoTrainer.Harvest";
            _thread.Start();

            Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                "采集开始：计划 {0} 秒，间隔 {1} ms", seconds, intervalMs));
            return "";
        }

        /// <summary>停止采集并把报告落盘。返回报告路径（失败时返回空串，原因写进日志）。</summary>
        public string Stop()
        {
            if (!IsRunning && SampleCount == 0) { return ""; }

            _stop.Set();
            Thread t = _thread;
            if (t != null) { t.Join(5000); }

            string path = WriteReport();
            Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                "采集结束：{0} 次采样，跟踪 {1} 个对象，报告 {2}",
                SampleCount, TrackedCount, path.Length > 0 ? path : "（写入失败）"));
            return path;
        }

        // ============================================================ 采样

        private void Loop()
        {
            while (!_stop.WaitOne(_intervalMs))
            {
                try
                {
                    TakeSample();

                    if (ElapsedSeconds >= _plannedSeconds) { break; }
                }
                catch (Exception ex)
                {
                    // 单次采样失败绝不能让线程死掉 —— 否则用户以为在采，其实早停了
                    Logger.Error("Harvest", "单次采样异常", ex);
                }
            }
        }

        private void TakeSample()
        {
            MemoryIO memory = _target.Memory;
            uint moduleBase = _target.ModuleBase;
            uint moduleSize = _target.ModuleSize;

            HarvestSample sample = new HarvestSample();
            sample.Index = _samples.Count;
            sample.Seconds = ElapsedSeconds;

            sample.Money = ResolveAndRead(memory, moduleBase, _profile.Money);
            sample.PowerOutput = ResolveAndRead(memory, moduleBase, _profile.PowerOutput);
            sample.PowerDrain = ResolveAndRead(memory, moduleBase, _profile.PowerDrain);

            // 全对象名单：地址、类型表、阵营、血量（紧凑记录，只留关键字段）
            List<HarvestObjectRow> roster = ReadRoster(memory, moduleBase, moduleSize, sample);
            lock (_lock) { _lastRoster = roster; }

            // 阵营分组（用来判断阵营偏移是否有效：有效的表现是"少数几个阵营、各自聚集大量单位"）
            if (_profile.OffsetTechnoOwner != 0 && roster.Count > 0)
            {
                Dictionary<uint, int> counts = new Dictionary<uint, int>();
                for (int i = 0; i < roster.Count; i++)
                {
                    if (roster[i].Owner == 0) { continue; }
                    int existing;
                    counts[roster[i].Owner] = counts.TryGetValue(roster[i].Owner, out existing) ? existing + 1 : 1;
                }
                List<KeyValuePair<uint, int>> ordered = new List<KeyValuePair<uint, int>>();
                foreach (KeyValuePair<uint, int> pair in counts) { ordered.Add(pair); }
                ordered.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
                {
                    return b.Value.CompareTo(a.Value);
                });
                for (int i = 0; i < ordered.Count && i < 8; i++)
                {
                    sample.OwnerGroups.Add(string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8}:{1}", ordered[i].Key, ordered[i].Value));
                }
            }

            // 选中列表 → 扩充跟踪集合（只增不减）
            ReadSelection(memory, moduleBase, sample);

            // 刷新每个被跟踪对象的字节差别
            for (int i = 0; i < _tracked.Count; i++)
            {
                UpdateTracked(_tracked[i], memory, sample.Index, sample.Seconds);
            }

            lock (_lock) { _samples.Add(sample); }
        }

        private static uint ResolveAndRead(MemoryIO memory, uint moduleBase, FieldLocator locator)
        {
            if (locator == null || !locator.IsCalibrated) { return 0; }

            uint address;
            string error;
            if (!locator.TryResolve(memory, moduleBase, out address, out error)) { return 0; }

            uint value;
            if (!memory.ReadUInt32(address, out value)) { return 0; }
            return value;
        }

        private List<HarvestObjectRow> ReadRoster(
            MemoryIO memory, uint moduleBase, uint moduleSize, HarvestSample sample)
        {
            List<HarvestObjectRow> rows = new List<HarvestObjectRow>();

            uint slotAddress;
            string error;
            if (!_profile.TechnoArray.TryResolve(memory, moduleBase, out slotAddress, out error))
            {
                sample.Note = "对象名单解析失败：" + error;
                return rows;
            }

            // 配置里存的是"存放数组地址的那个格子"，必须再解一次引用才是数组本身。
            // 少这一步读到的就是格子里那串垃圾值（真机踩过：通过率 1%）。
            uint arrayAddress;
            if (!memory.ReadUInt32(slotAddress, out arrayAddress) || arrayAddress < 0x10000)
            {
                sample.Note = "对象名单格 0x" + slotAddress.ToString("X8", CultureInfo.InvariantCulture)
                            + " 里读不到有效指针";
                return rows;
            }

            TechnoWalkResult walk = TechnoWalker.WalkArray(
                memory, arrayAddress, ProbeObjectCount, moduleBase, moduleSize);
            if (!walk.Ok)
            {
                // 失败提示里必须带上**两个地址**：只有"遍历失败"四个字，
                // 事后完全分不清是"格子里的值不是数组"还是"数组读不动"。
                // （第一版报告就漏了这一点，导致只能靠推测排查。）
                sample.Note = string.Format(CultureInfo.InvariantCulture,
                    "遍历失败：{0}（格子 0x{1:X8} → 数组 0x{2:X8}）",
                    walk.Error, slotAddress, arrayAddress);
                return rows;
            }

            RecordRosterAddresses(slotAddress, arrayAddress, walk);

            sample.ObjectCount = walk.Count;
            sample.WalkSkipped = walk.Skipped;

            for (int i = 0; i < walk.Objects.Count; i++)
            {
                HarvestObjectRow row = new HarvestObjectRow();
                row.Address = walk.Objects[i].Address;
                row.VTable = walk.Objects[i].VTable;
                row.Owner = TechnoWalker.ReadOwner(memory, row.Address, _profile.OffsetTechnoOwner);
                row.Health = TechnoWalker.ReadHealth(memory, row.Address, _profile.OffsetTechnoHealth);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// 记录对象名单的解析结果。**这几个数字是排查"名单读不出来"的唯一线索**，
        /// 所以每轮都更新，并在报告头部如实列出来。
        /// </summary>
        private void RecordRosterAddresses(uint slot, uint array, TechnoWalkResult walk)
        {
            _rosterSlot = slot;
            _rosterArray = array;
            _rosterReadable = walk.ReadableEntries;
            _rosterTruncated = walk.Truncated;

            if (array != 0 && array != _rosterFirstArray)
            {
                if (_rosterFirstArray == 0) { _rosterFirstArray = array; }
                else { _rosterArrayMoves++; _rosterLastArray = array; }
            }
        }

        private void ReadSelection(MemoryIO memory, uint moduleBase, HarvestSample sample)
        {
            if (!_profile.SelectedCount.IsCalibrated) { return; }

            uint countAddress;
            string error;
            if (!_profile.SelectedCount.TryResolve(memory, moduleBase, out countAddress, out error)) { return; }

            uint count;
            if (!memory.ReadUInt32(countAddress, out count)) { return; }
            if (count == 0 || count > SelectionProbe.MaxCount) { return; }

            // 自带一份模块区间：不依赖界面有没有刷过。
            // 缺了它，探测里"对象头必须是模块内类型表"这条硬校验会被跳过，
            // 结果就是把模块里的代码地址当成选中对象收进跟踪集合（真机踩过）。
            SelectionProbe.SetModuleRange(moduleBase, _target.ModuleSize);

            SelectionProbeResult probe = SelectionProbe.ProbeWithEscalation(memory, countAddress, (int)count);
            if (probe == null || !probe.Found) { return; }

            sample.SelectedCount = probe.ObjectAddresses.Count;
            for (int i = 0; i < probe.ObjectAddresses.Count; i++)
            {
                uint address = probe.ObjectAddresses[i];
                sample.Selected.Add(address);

                HarvestTracked tracked;
                if (!_byAddress.TryGetValue(address, out tracked))
                {
                    if (_tracked.Count >= MaxTracked) { continue; }
                    tracked = new HarvestTracked();
                    tracked.Address = address;
                    tracked.FirstSeen = sample.Index;
                    uint head;
                    if (memory.ReadUInt32(address, out head)) { tracked.FirstVTable = head; }
                    _byAddress[address] = tracked;
                    _tracked.Add(tracked);
                }
                tracked.SelectedAt.Add(sample.Index);
            }
        }

        private static void UpdateTracked(HarvestTracked tracked, MemoryIO memory, int sampleIndex, double seconds)
        {
            byte[] block = memory.ReadBytesQuiet(tracked.Address, DumpBytes);

            if (block == null || block.Length < DumpBytes)
            {
                if (tracked.DiedAt < 0) { tracked.DiedAt = sampleIndex; }
                return;
            }

            uint head = ReadUInt32(block, 0);

            // 地址被复用：同一个地址上换了个对象。
            // 如果继续按同一个对象求差，会把"新对象的初始值"记成"旧对象的变化" ——
            // 那比丢掉这段数据更糟，因为它看起来像一条真实的变化记录。
            if (tracked.HasLast && head != tracked.FirstVTable)
            {
                if (tracked.DiedAt < 0) { tracked.DiedAt = sampleIndex; }
                return;
            }

            if (!tracked.HasLast)
            {
                tracked.Last = new uint[DumpBytes / 4];
                for (int i = 0; i < tracked.Last.Length; i++) { tracked.Last[i] = ReadUInt32(block, i * 4); }
                tracked.HasLast = true;
                tracked.FirstVTable = head;
                tracked.LastSeen = sampleIndex;
                tracked.SeenCount = 1;
                return;
            }

            for (int i = 0; i < tracked.Last.Length; i++)
            {
                uint value = ReadUInt32(block, i * 4);
                if (value == tracked.Last[i]) { continue; }

                int offset = i * 4;
                if (tracked.Changes.Count < 400)   // 单个对象的变化条数封顶，防止异常对象把报告撑爆
                {
                    tracked.Changes.Add(string.Format(CultureInfo.InvariantCulture,
                        "t={0:0.0}s  采样#{1}  +0x{2:X3}  0x{3:X8} → 0x{4:X8}  ({3} → {4})",
                        seconds, sampleIndex, offset, tracked.Last[i], value));
                }
                tracked.Last[i] = value;
            }

            tracked.LastSeen = sampleIndex;
            tracked.SeenCount++;
        }

        // ============================================================ 报告

        private string WriteReport()
        {
            string text = BuildReport();
            try
            {
                string folder = _store != null ? _store.ReportsFolder : "reports";
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(folder, "harvest-" + stamp + ".txt");
                File.WriteAllText(path, text, new UTF8Encoding(true));
                _reportPath = path;

                // 同时也逐行写进日志：「导出日志」能一并带出来，方便用户只发一个文件
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    Logger.Info("Harvest", lines[i].TrimEnd('\r'));
                }
                return path;
            }
            catch (Exception ex)
            {
                Logger.Error("Harvest", "写采集报告失败", ex);
                return "";
            }
        }

        /// <summary>生成报告正文（纯文本，人可读，也可直接回传分析）。</summary>
        public string BuildReport()
        {
            StringBuilder text = new StringBuilder();
            List<HarvestSample> samples;
            List<HarvestTracked> tracked;
            List<HarvestObjectRow> roster;
            lock (_lock)
            {
                samples = new List<HarvestSample>(_samples);
                tracked = new List<HarvestTracked>(_tracked);
                roster = new List<HarvestObjectRow>(_lastRoster);
            }

            text.AppendLine("=== MO-Trainer 采集报告 ===");
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "生成时间：{0:yyyy-MM-dd HH:mm:ss}", DateTime.Now));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "采样：{0} 次，间隔 {1} ms", samples.Count, _intervalMs));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "时长：{0:0.0} 秒（计划 {1} 秒）", ElapsedSeconds, _plannedSeconds));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "环境：{0}｜基址 0x{1:X8}｜模块大小 0x{2:X8}",
                _target != null ? _target.DescribeEnvironment() : "未知",
                _target != null ? _target.ModuleBase : 0,
                _target != null ? _target.ModuleSize : 0));

            text.AppendLine();
            text.AppendLine("定位使用情况（未校准的项在本次采集里没有数据）：");
            AppendLocator(text, "金钱", _profile.Money);
            AppendLocator(text, "电力产出", _profile.PowerOutput);
            AppendLocator(text, "电力消耗", _profile.PowerDrain);
            AppendLocator(text, "选中数量", _profile.SelectedCount);
            AppendLocator(text, "全对象名单", _profile.TechnoArray);
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "字段偏移：阵营 +0x{0:X}｜血量 +0x{1:X}｜最大血量 +0x{2:X}｜铁幕 +0x{3:X}｜生产进度 +0x{4:X}",
                _profile.OffsetTechnoOwner, _profile.OffsetTechnoHealth, _profile.TechnoMaxHealth,
                _profile.OffsetTechnoIronCurtain, _profile.OffsetProductionTimer));

            // 对象名单的解析结果。**这几个数字是排查"名单读不出来"的唯一线索** ——
            // 第一版报告没记，导致只能靠推测，所以这里固定列出来。
            if (_rosterArray != 0)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "对象名单：格子 0x{0:X8} → 数组 0x{1:X8}｜可读 {2} 项（请求 4096 项）{3}",
                    _rosterSlot, _rosterArray, _rosterReadable,
                    _rosterTruncated ? "  ← 数组尾部读不动，只读到这么多" : ""));
                if (_rosterArrayMoves > 0)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  ⚠ 采集期间数组地址变过 {0} 次（首 0x{1:X8} → 末 0x{2:X8}）：这是正常的，游戏会重新分配它",
                        _rosterArrayMoves, _rosterFirstArray, _rosterLastArray));
                }
            }
            else
            {
                text.AppendLine("对象名单：**整个采集期间一次都没解析成功**（详见下方时间线里的「！」行）");
            }

            // ---- 一、时间线 ----
            text.AppendLine();
            text.AppendLine("=== 一、时间线 ===");
            text.AppendLine("（房子数值 + 场上对象总数 + 阵营分组。阵营分组如果出现「少数几个阵营、各自聚集大量对象」，说明阵营偏移是对的。）");
            text.AppendLine();
            // 刻意**不列**"跳过数"：对象数量字段没校准，所以每轮都按 4096 位上限探测，
            // 跳过数恒为 4096 减去有效对象数，是个没有信息量的常数。
            // 真正要看的是"对象数"和"阵营分组"。
            text.AppendLine("  #     时刻     金钱        电力产出  电力消耗  选中  场上对象  阵营分组");
            for (int i = 0; i < samples.Count; i++)
            {
                HarvestSample s = samples[i];
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-5} {1,6:0.0}s  {2,-11} {3,-9} {4,-9} {5,-5} {6,-9} {7}",
                    s.Index, s.Seconds, s.Money, s.PowerOutput, s.PowerDrain,
                    s.SelectedCount, s.ObjectCount,
                    s.OwnerGroups.Count > 0 ? string.Join(", ", s.OwnerGroups.ToArray()) : "-"));
                if (s.Note.Length > 0)
                {
                    text.AppendLine("        ！" + s.Note);
                }
            }

            // ---- 二、被跟踪对象 ----
            text.AppendLine();
            text.AppendLine("=== 二、被跟踪对象（曾经被选中过的，之后即使取消选中也继续跟踪）===");
            if (tracked.Count == 0)
            {
                text.AppendLine("本次没有跟踪到任何对象 —— 采集期间你一次都没有选中过单位。");
                text.AppendLine("如果要观察某个单位的字段变化，请在采集过程中用鼠标选中它（哪怕只选一下）。");
            }
            for (int i = 0; i < tracked.Count; i++)
            {
                HarvestTracked t = tracked[i];
                text.AppendLine();
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "对象 #{0}  地址 0x{1:X8}  类型表 0x{2:X8}", i + 1, t.Address, t.FirstVTable));
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  首次出现：采样 #{0}｜共成功读到 {1} 次｜其中被选中 {2} 次",
                    t.FirstSeen, t.SeenCount, t.SelectedAt.Count));
                if (t.SelectedAt.Count > 0)
                {
                    text.AppendLine("  被选中的采样点：" + JoinInts(t.SelectedAt));
                }
                if (t.DiedAt >= 0)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  ⚠ 从采样 #{0} 起读不到了（对象已被摧毁，或该地址被其它对象复用）", t.DiedAt));
                }

                if (t.Changes.Count == 0)
                {
                    text.AppendLine("  字段变化：无（整个采集期间这个对象的字节一个都没动）");
                }
                else
                {
                    text.AppendLine("  字段变化：");
                    for (int k = 0; k < t.Changes.Count; k++)
                    {
                        text.AppendLine("    " + t.Changes[k]);
                    }
                }
                text.AppendLine("  常见字段偏移对照：+0x06C 血量｜+0x0F0 阵营｜+0x07D 最大血量");
            }

            // ---- 三、全对象名单 ----
            text.AppendLine();
            text.AppendLine("=== 三、全对象名单（最后一次采样）===");
            text.AppendLine("（同一类型表 = 同一种单位/建筑。用它可以统计每类对象有多少个、分别属于哪个阵营。）");
            text.AppendLine();
            if (roster.Count == 0)
            {
                text.AppendLine("（本次没有读到对象名单）");
            }
            else
            {
                Dictionary<uint, int> typeCount = new Dictionary<uint, int>();
                for (int i = 0; i < roster.Count; i++)
                {
                    int existing;
                    typeCount[roster[i].VTable] = typeCount.TryGetValue(roster[i].VTable, out existing) ? existing + 1 : 1;
                }
                List<KeyValuePair<uint, int>> types = new List<KeyValuePair<uint, int>>();
                foreach (KeyValuePair<uint, int> pair in typeCount) { types.Add(pair); }
                types.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
                {
                    return b.Value.CompareTo(a.Value);
                });

                text.AppendLine("按类型汇总（数量多的在前）：");
                for (int i = 0; i < types.Count; i++)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  类型表 0x{0:X8}  共 {1} 个", types[i].Key, types[i].Value));
                }

                text.AppendLine();
                text.AppendLine("明细：");
                text.AppendLine("  地址        类型表      阵营        血量");
                for (int i = 0; i < roster.Count; i++)
                {
                    HarvestObjectRow row = roster[i];
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}  0x{1:X8}  0x{2:X8}  {3}",
                        row.Address, row.VTable, row.Owner, row.Health));
                }
            }

            text.AppendLine();
            text.AppendLine("=== 报告结束 ===");
            return text.ToString();
        }

        private static void AppendLocator(StringBuilder text, string label, FieldLocator locator)
        {
            if (locator == null || !locator.IsCalibrated)
            {
                text.AppendLine("  " + label + "：未校准");
                return;
            }
            if (locator.HasStaticAddress)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0}：模块 +0x{1:X}（静态地址）", label, locator.StaticOffset));
                return;
            }
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0}：{1} 条候选链，主链 {2}", label, locator.ChainCount, DescribeChain(locator.Chain)));
        }

        /// <summary>把偏移链写成「静态根 0x683D4C → +0x30C」这种可读形式。</summary>
        private static string DescribeChain(PointerChain chain)
        {
            if (chain == null || chain.IsEmpty) { return "(空)"; }

            uint[] offsets = chain.Offsets;
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < offsets.Length; i++)
            {
                if (i > 0) { text.Append(" → "); }
                text.Append(i == 0
                    ? string.Format(CultureInfo.InvariantCulture, "静态根 0x{0:X}", offsets[i])
                    : string.Format(CultureInfo.InvariantCulture, "+0x{0:X}", offsets[i]));
            }
            return text.ToString();
        }

        private static string JoinInts(List<int> values)
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < values.Count && i < 40; i++)
            {
                if (i > 0) { text.Append(", "); }
                text.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            if (values.Count > 40) { text.Append(", …"); }
            return text.ToString();
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            try { _stop.Set(); } catch { }
            try { if (_thread != null) { _thread.Join(2000); } } catch { }
            try { _stop.Close(); } catch { }
        }
    }
}
