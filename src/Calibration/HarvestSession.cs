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

        /// <summary>本拍**新出现**的对象（采集期间才被造出来的）—— 会自动进入跟踪集合。</summary>
        public readonly List<uint> NewObjects = new List<uint>();

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

        /// <summary>
        /// 这个对象身上**每一个变过的字段**各自的取值时间线（偏移 → 时间线）。
        ///
        /// 2026-10-09 深夜改掉了原来的做法（一个 List&lt;string&gt; 装变化文字、上限 400 条）：
        /// 真机报告证明那会把**最该看的数据毁掉** —— 新建造的对象出生那一瞬有上百个字段
        /// 同时初始化（"初始化洪流"），400 条名额当场吃光，之后真正的**建造过程**一条都留不下。
        /// 按偏移分开记账之后，洪流里每个偏移只占 1~2 条，而变化几十次的"进度字段"照样完整。
        /// </summary>
        public readonly Dictionary<int, OffsetSeries> Series = new Dictionary<int, OffsetSeries>();

        /// <summary>变化事件总数（含因上限而未逐条记录的那些），用来判断记录是否完整。</summary>
        public int ChangeEvents;

        /// <summary>变化事件总数触顶 —— 后面的变化**没有记全**（如实标注，绝不假装完整）。</summary>
        public bool Truncated;

        /// <summary>
        /// 是不是"采集期间新造出来的"（而不是开局就在场上的，也不是用户选中的）。
        ///
        /// 这是"瞬间建造"要用的一类数据：用户造东西时**不必先选中它**，
        /// 程序自己盯着新出现的对象看它每一秒的字节变化 —— 建造进度就该在里面。
        /// 见 `TrackNewObjects`。
        /// </summary>
        public bool NewlyAppeared;

        /// <summary>是不是"我方房子"（`HouseClass`）。生产队列/工厂大概率住在它里面，见 `TrackPlayerHouse`。</summary>
        public bool IsPlayerHouse;

        /// <summary>来源说明（"我方房子" / "我方房子 → 跟随 +0xNNN"），报告里直接印出来。</summary>
        public string Source = "";

        /// <summary>
        /// 「型号线索」：这个对象里能找到的、像"建造 ID"的字符串（如 GAPOWR / HARV / E1）。
        ///
        /// 为什么需要：报告的读者（人）看得见偏移和取值，却**看不出这个对象是什么东西**。
        /// 2026-10-10 就卡在这里：两个"倒数到 0"的字段，谁都说不清它们属于兵、车还是别的。
        /// 现在每个对象都带一串线索，对不上型号也能一眼看出"这是哪一类东西"。
        /// 采集收尾时统一扫一次（`ScanTypeIdHints`），扫不到就空着 —— 宁可空，不要瞎猜。
        /// </summary>
        public readonly List<string> TypeIdHints = new List<string>();

        /// <summary>本对象每拍 dump 多少字节（房子比普通对象大：金钱 0x30C、电力 0x53A4 都在它身上）。</summary>
        public int DumpBytes = HarvestSession.DumpBytes;

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

        /// <summary>
        /// "新出现的对象"要连续存活这么多拍才真正开始跟踪。
        ///
        /// 为什么必须有个观察期：战场上每秒都有大量**短命对象**冒出来（子弹、碎片、动画），
        /// 它们同样会出现在对象名单里。若"一出现就跟踪"，跟踪名额会被这些东西瞬间占满，
        /// 真正要看的"正在造的那座建筑"反而一个都进不来。多活几秒的才值得跟。
        /// </summary>
        private const int NewObjectMinAge = 3;

        /// <summary>新对象跟踪单独占用的名额上限（其余名额留给"被选中的对象"）。</summary>
        public const int MaxNewTracked = 24;

        /// <summary>
        /// 我方房子（`HouseClass`）dump 多少字节。
        ///
        /// 比普通对象大得多：已知的金钱在 +0x30C、电力产出/消耗在 +0x53A4/+0x53A8，
        /// 而"生产队列 / 工厂"这类东西按引擎惯例都挂在这个对象身上 —— 它在哪一段还没定论，
        /// 所以整个对象一起盯，别让它落在窗口外面。
        /// </summary>
        public const int HouseDumpBytes = 0x6000;

        /// <summary>从房子里找"可以跟随的指针"时，扫它这么多字节。</summary>
        public const int HousePointerScanBytes = 0x6000;

        /// <summary>
        /// 从我方房子**跟随**（再盯一层）的对象个数上限。
        ///
        /// 为什么要有这个设计：生产进度**未必**记在房子对象内部 —— 按引擎惯例，
        /// 它更可能记在房子里某个指针指向的"生产队列/工厂"对象里。只盯房子本体的话，
        /// 一旦它在外面，这一局用户就白玩了。跟随一层是最划算的兜底。
        ///
        /// 2026-10-10 真机教训：上一版把 8 个名额全给了**模块内的静态结构**
        /// （房子 +0x4/+0x8/+0xC… 指过去的都是程序自己的静态数据，整局一个字节都没动），
        /// 真正该跟的**堆对象**一个都没轮到 —— 于是"跟随"这一层等于白做。
        /// 现在只跟**不在模块区间内**的目标（真对象都在堆上），名额也放宽到 12。
        /// </summary>
        public const int MaxFollowedFromHouse = 12;

        /// <summary>指针要在同一个位置上连续出现这么多拍，才认为它是"稳定的成员指针"并跟随过去。</summary>
        public const int FollowStableSamples = 3;

        /// <summary>
        /// 单个对象最多记这么多"变化事件"（纯内存保护；撞上了就在报告里如实标注"没记全"）。
        /// 取这个数是因为"初始化洪流"一次就有上千条 —— 上限必须远离它，否则等于没改。
        /// </summary>
        public const int MaxChangeEventsPerObject = 8000;

        /// <summary>采样的最短/最长间隔与最长时长，防止界面传入离谱的值。</summary>
        public const int MinIntervalMs = 200;
        public const int MaxIntervalMs = 5000;
        public const int MaxSeconds = 1800;

        /// <summary>
        /// 「阵营」这一列在没有选中线索时，**至少要多少个名单对象现场核对通过**才敢用配置里那个值。
        /// 取 2 而不是 1：一个对象"恰好"命中的偶然性太高（真机名单里混着大量非单位对象），
        /// 两个各自独立地指向"金钱正好等于我此刻金钱的那座房子"就基本不可能是巧合。
        /// </summary>
        public const int OwnerCrossCheckMinSamples = 2;

        /// <summary>现场核对最多读多少个名单对象（核对要的是"够不够构成证据"，不是把几百个全读一遍）。</summary>
        public const int OwnerCrossCheckProbes = 96;

        private readonly ICalibrationTarget _target;
        private readonly CalibrationProfile _profile;
        private readonly ProfileStore _store;
        private readonly object _lock = new object();

        /// <summary>"你选中的是哪些单位"的可靠读法（金钱锚定 + 自动更正）——
        /// 与引擎 / 高级功能页共用同一套实现，见 `SelectionResolver` 的类注释。</summary>
        private readonly SelectionResolver _selection;

        private readonly List<HarvestSample> _samples = new List<HarvestSample>();
        private readonly List<HarvestTracked> _tracked = new List<HarvestTracked>();
        private readonly Dictionary<uint, HarvestTracked> _byAddress = new Dictionary<uint, HarvestTracked>();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);

        private List<HarvestObjectRow> _lastRoster = new List<HarvestObjectRow>();

        // ---- 「阵营」这一列该用哪个偏移（见 EnsureOwnerOffset）----
        /// <summary>
        /// 本次采集**现场认定可用**的阵营字段偏移；0 = 还没认定出来。
        /// **绝不等于** `_profile.OffsetTechnoOwner` —— 那个是人工试出来的，见 `EnsureOwnerOffset` 的注释。
        /// </summary>
        private uint _ownerOffset;

        /// <summary>认定成功时的说明（写进报告，让人知道这一列的出处）。</summary>
        private string _ownerOffsetNote = "";

        /// <summary>上一次"还没认定出来"的原因 —— 同一个原因只写一次日志，不按拍刷屏。</summary>
        private string _ownerOffsetWaitLogged = "";

        // ---- "新出现的对象"跟踪（瞬间建造用）----
        /// <summary>已经见过的对象地址（用来判断"新出现"）。只增不减，见 TrackNewObjects 的注释。</summary>
        private readonly HashSet<uint> _knownObjects = new HashSet<uint>();
        /// <summary>刚出现、还在观察期的对象：地址 → 已经活了几拍。</summary>
        private readonly Dictionary<uint, int> _newborn = new Dictionary<uint, int>();
        private readonly Dictionary<uint, uint> _newbornVtable = new Dictionary<uint, uint>();
        private int _newTracked;
        private bool _rosterEverRead;

        // ---- 我方房子 + "跟随一层"（[S6-2] 瞬间建造用）----
        // 生产进度未必记在建筑对象身上 —— 按引擎惯例，它更可能记在房子的生产队列/工厂里。
        // 所以除了盯新建造的对象，还要盯**我方房子**，并从房子里跟随一层指针。
        /// <summary>从房子里跟随的堆对象之外，再盯这么多"指向我方房子"的对象。</summary>
        public const int MaxMyObjects = 12;

        /// <summary>「正在生产的东西」最多同时盯几个（换一样就多一个，别把跟踪名额吃光）。</summary>
        public const int MaxProducingTracked = 8;

        /// <summary>
        /// 「我方房子 +0x53BC 指向的对象」—— 2026-10-10 从真机数据里认出来的**生产状态持有者**。
        ///
        /// 判据（两次真机采集一致，见 `开发日志/2026-10-10.md` 第七节）：
        /// 这个指针**每件东西只变一次**：`0 → 某个对象 → 0`；而且变动次数**恰好等于用户这一局造了几样东西**
        /// （第三次采集他造了 4 样 —— 兵营、电厂1、电厂2、动员兵 —— 该字段正好变 4 次）。
        /// 换句话说：游戏在"开始造一样东西"时把它挂在这里、造完就摘掉。这就是生产队列的把手。
        ///
        /// **注意**：这是**从数据里认出来的线索，不是"已确认的字段语义"**（本项目规矩：字段语义必须运行时自证）。
        /// 所以这里只用它**只读地盯住那个对象**（读、不写），真相靠"它旁边哪些字段随生产推进"来说话。
        /// </summary>
        public const uint ProducingPointerOffset = 0x53BC;

        /// <summary>房子 +0x53BC 上一次指向谁（用来发现"换了一个新对象"）。</summary>
        private uint _lastProducingTarget;
        private int _producingTracked;

        /// <summary>"指向我方房子"的扫描只做一次（它要读几百个对象的头部，不能每拍都做）。</summary>
        private bool _myObjectsScanned;
        private int _myObjectsTracked;

        private uint _houseAddress;
        private int _followedFromHouse;
        private int _followSkippedStatic;
        private readonly Dictionary<int, FollowSlot> _followSlots = new Dictionary<int, FollowSlot>();

        /// <summary>
        /// "我方房子认出没认出来"的一句话结论 —— 报告头部会原样印出来。
        ///
        /// 为什么必须有：认不出房子时，如果什么都不写，用户那一局就**白玩了而且查不出原因**
        /// （表现只是"报告里没有房子那一段"）。本项目最忌讳的失败模式就是"看起来做了、
        /// 实际没做、且无从查证"，所以这一句必须写清楚是"认出来了"还是"没认出来、卡在哪一步"。
        /// </summary>
        private string _houseNote = "";

        /// <summary>房子里某个偏移上"连续几拍都是同一个指针"的记账。</summary>
        private sealed class FollowSlot
        {
            public uint Target;
            public int Streak;
            public bool Promoted;
        }

        /// <summary>自动巡检算出来的报告段落（在采样线程里算好，报告落盘时直接接上去）。</summary>
        private string _inspectionText = "";
        private bool _finalizing;

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
            _selection = target == null ? null : new SelectionResolver(target);
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

        /// <summary>
        /// 采样已经结束、正在做收尾（自动巡检 + 写报告）。
        ///
        /// 界面靠它区分"还在采"和"在收尾"：收尾要跑一次全内存反查，可能要十几秒，
        /// 这段时间里 `IsRunning` 仍然是 true（线程还活着），不区分的话用户会以为卡住了。
        /// </summary>
        public bool IsFinalizing { get { return _finalizing; } }

        /// <summary>本次采集期间**新出现**（不是开局就有的）的对象个数 —— 界面与报告都要能说出来。</summary>
        public int NewTrackedCount { get { lock (_lock) { return _newTracked; } } }

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
                _knownObjects.Clear();
                _newborn.Clear();
                _newbornVtable.Clear();
                _newTracked = 0;
                _rosterEverRead = false;
                _houseAddress = 0;
                _followedFromHouse = 0;
                _followSkippedStatic = 0;
                _followSlots.Clear();
                _houseNote = "";
                _myObjectsScanned = false;
                _myObjectsTracked = 0;
                _lastProducingTarget = 0;
                _producingTracked = 0;
                _inspectionText = "";
                _finalizing = false;
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

        /// <summary>停止采集并返回报告路径；**报告还没写完时返回空串**（见下方注释）。</summary>
        public string Stop()
        {
            if (!IsRunning && SampleCount == 0) { return ""; }

            bool wasRunning = IsRunning;
            _stop.Set();

            Thread t = _thread;
            if (t != null) { t.Join(8000); }   // 采样循环每轮只睡一个间隔，会立刻退出来

            // 报告现在**由采样线程自己写**（见 Loop 的注释）。
            // 为什么不在这里等：收尾要先跑「自动巡检」，其中主链反查要全内存扫十几秒，
            // 在这里 Join 住就把界面冻住了。所以等不到就返回空串，
            // 由界面按 `IsRunning` 继续轮询 —— 线程一结束，报告就一定已经落盘了。
            if (wasRunning)
            {
                Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                    "采集结束：{0} 次采样，跟踪 {1} 个对象（其中新出现 {2} 个），报告 {3}",
                    SampleCount, TrackedCount, NewTrackedCount,
                    _reportPath.Length > 0 ? _reportPath : "（正在收尾，稍后自动落盘）"));
            }
            return _reportPath;
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

            // ---- 收尾：自动巡检 + 写报告 ----
            //
            // 这两件事都放在采样线程里做，而不是让 Stop() 去干：
            // 自动巡检里有一次**全内存反查指针链**（可能要十几秒），放到界面上等就是界面假死。
            // 放在这里，界面该怎么转还怎么转，只是按钮上多显示一句"正在收尾"。
            _finalizing = true;

            // 先落一版**不含巡检**的报告。
            // 为什么值得写两遍：收尾里那次全内存反查要十几秒，用户完全可能在这期间
            // 把修改器关掉 —— 只写一次的话，这一局的采集数据就整个没了。
            // 先落一版，最坏情况也只是少最后两节。
            ScanTypeIdHints();
            WriteReport(false);

            try
            {
                RunInspection();
            }
            catch (Exception ex)
            {
                Logger.Error("Harvest", "自动巡检出错（报告照常出，只是少一段）", ex);
                _inspectionText = "（自动巡检出错：" + ex.Message + " —— 本次报告缺这一段）\n\n";
            }
            finally
            {
                _finalizing = false;
            }

            // 收尾完成，把带巡检的那一版覆盖上去（写进日志的只有这一次，免得日志翻倍）
            WriteReport(true);
        }

        /// <summary>
        /// 自动巡检：把"本来要用户再开一局、再点几次"的几项一次算完（见 `AutoInspection`）。
        /// 纯只读，不写游戏内存。
        /// </summary>
        private void RunInspection()
        {
            List<HarvestObjectRow> roster;
            uint myMoney;
            lock (_lock) { roster = new List<HarvestObjectRow>(_lastRoster); }

            myMoney = ResolveAndRead(_target.Memory, _target.ModuleBase, _profile.Money);

            Logger.Info("Harvest", "自动巡检开始：找类型对象 + 主链反查（只读）");
            DateTime started = DateTime.UtcNow;

            StringBuilder text = new StringBuilder();
            TypePointerAnalysis analysis = AutoInspection.FindTypePointers(
                _target.Memory, _target.ModuleBase, _target.ModuleSize,
                roster, MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset, myMoney);
            text.Append(AutoInspection.FormatTypePointers(analysis, roster.Count > 0));

            // 主链反查是全内存扫描，给个上限（真机内存画像约 347 MB）
            text.Append(AutoInspection.RefitMainChain(_target, _profile, 30));

            // 「六、施工进度候选字段」（[S6-2] 瞬间建造用）——
            // 把"哪个字段像建造进度"当场排好序写进报告，用户下一次开局的采集就能直接给答案。
            List<HarvestTracked> trackedSnapshot;
            List<HarvestSample> sampleSnapshot;
            lock (_lock)
            {
                trackedSnapshot = new List<HarvestTracked>(_tracked);
                sampleSnapshot = new List<HarvestSample>(_samples);
            }
            text.Append(ProgressHunter.Format(trackedSnapshot, sampleSnapshot,
                _target.ModuleBase, _target.ModuleSize));

            _inspectionText = text.ToString();
            Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                "自动巡检结束，耗时 {0:0.0} 秒", (DateTime.UtcNow - started).TotalSeconds));
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

            // 「阵营」这一列该读哪个偏移 —— **现场认定**，绝不用配置里那个人工试出的 +0xF0。
            // 放在名单读完之后：名单（地址）本身不依赖偏移，先拿到手，认定时才有样本可核对；
            // 认定成功会把**本拍**这几行的阵营顺手补上，不必等到下一拍。
            EnsureOwnerOffset(memory, roster);

            // 「新出现的对象」自动跟踪（瞬间建造用）：用户不必先选中它，程序自己盯
            TrackNewObjects(roster, memory, sample);

            // 我方房子（HouseClass）——生产队列/工厂大概率住在它里面（[S6-2] 用）。
            // 放在"刷新被跟踪对象"之前，这样它这一拍就能拿到第一份快照。
            TrackPlayerHouse(memory, moduleBase, moduleSize, sample);

            // 从房子里跟随一层指针（生产队列也可能是房子里的指针指向的另一个对象）
            FollowHousePointers(memory, moduleBase, moduleSize, sample);

            // 「谁指向我方房子」→ 盯上那些房子名下的对象（生产队列大概率在这一类里，见方法注释）
            TrackMyObjects(memory, moduleBase, moduleSize, sample);

            // 房子 +0x53BC 指向的对象 = **正在生产的东西**（2026-10-10 从真机数据认出来的把手）
            TrackProducingObject(memory, sample);

            // 阵营分组（用来判断阵营偏移是否有效：有效的表现是"少数几个阵营、各自聚集大量单位"）
            // **门槛用的是现场认定出来的偏移，不是配置里那个** —— 见 EnsureOwnerOffset。
            if (_ownerOffset != 0 && roster.Count > 0)
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
                UpdateTracked(_tracked[i], memory, sample.Index);
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

        /// <summary>
        /// 现场认定「阵营」这一列该读哪个偏移 —— **绝不拿配置里那个人工试出的值充数**。
        ///
        /// ## 为什么要有这个方法（2026-10-10 记下的待办，2026-10-12 修）
        ///
        /// 报告里「阵营」这一列此前一直读 `_profile.OffsetTechnoOwner`，而配置里存的是
        /// **`+0xF0` —— 2026-09-27 人工试出来的**。真机早已证明**那里读出来的不是阵营**
        /// （动员兵的阵营指针在 `+0x14C`）。后果不是"少一列数据"，而是：
        /// 用它算出来的「阵营分组」是**在噪声上做统计**，而报告里却写得像结论 ——
        /// 用户照着它核对，得到的是一个早已知错的答案（2026-10-12 核对时发现，
        /// 原计划的"顺手修"一直没做，所以第 1 批那件"核对阵营列"当时**验了也白验**）。
        ///
        /// ## 认定的两条路（都要求**现场证据**，没有任何"配置里有就用"的兜底）
        ///
        /// ① **有你选中的单位** → 走 `TechnoOwnership.ResolveOwnerOffsetForAnalysis`：
        ///    与「自动查找」「预演」**同一份实现**（金钱锚定反推），真机验证过。
        ///    这条路上**有线索却推不出来时直接放弃**，不回头去翻配置 —— 那正是要杜绝的做法。
        /// ② **没有线索**（采集的用法是"你正常玩"，可能一直没选中任何东西）→
        ///    拿**名单本身**现场核对配置里那个值：在若干对象上读该偏移，
        ///    看它是不是"指向一个 +0x30C 正好等于我此刻金钱的房子"（`TryReadHouseMoney`）。
        ///    核对通过才采用（报告里写明是"沿用配置值但已现场核对"），通不过就**不采用**。
        ///
        /// 认定不出来时：这一列**留空并在报告里写明为什么**（§4.2：缺数据必须拒绝执行并说明缺什么），
        /// 「阵营分组」一节也不出 —— 宁可没有，也不给噪声。
        ///
        /// 认定成功一次就缓存（同一局里偏移不会变）。所以用户中途才选中单位也没关系：
        /// 每拍都会重试，一选中就自动补上。
        /// </summary>
        private void EnsureOwnerOffset(MemoryIO memory, List<HarvestObjectRow> roster)
        {
            if (_ownerOffset != 0) { return; }

            uint moduleBase = _target.ModuleBase;
            uint moduleSize = _target.ModuleSize;

            // 我方房子的"身份证"：此刻的金钱读数。偏移对不对，就看它指的房子钱对不对得上。
            uint moneyAddress;
            uint money;
            string error;
            if (_profile.Money == null || !_profile.Money.IsCalibrated
                || !_profile.Money.TryResolve(memory, moduleBase, out moneyAddress, out error)
                || !memory.ReadUInt32(moneyAddress, out money) || money == 0)
            {
                WaitOwnerOffset("读不到金钱值（金钱未校准，或本拍解析不出来）—— 没有\"身份证\"就没法核对阵营字段");
                return;
            }

            // ---- 路 ①：有你选中的单位 → 金钱锚定反推（与「自动查找」同一份实现）----
            List<uint> hints;
            string hintsNote;
            if (_selection != null
                && _selection.TryReadSelectedObjects(_profile, SelectionProbe.MaxCount, false, out hints, out hintsNote)
                && hints != null && hints.Count > 0)
            {
                string note;
                uint offset = MoTrainer.Cheats.TechnoOwnership.ResolveOwnerOffsetForAnalysis(
                    memory, hints, money, out note);
                if (offset != 0)
                {
                    AdoptOwnerOffset(offset, roster,
                        string.Format(CultureInfo.InvariantCulture,
                            "+0x{0:X}（由 {1} 个选中单位的金钱锚定现场推出，**非**配置里的人工值）",
                            offset, hints.Count));
                    return;
                }
                WaitOwnerOffset(note);
                return;
            }

            // ---- 路 ②：没有线索 → 拿名单现场核对配置里那个值 ----
            uint stored = _profile.OffsetTechnoOwner;
            int agree = CountRosterAgreeingWithMoney(memory, roster, stored, money, moduleBase, moduleSize);
            if (agree >= OwnerCrossCheckMinSamples)
            {
                AdoptOwnerOffset(stored, roster,
                    string.Format(CultureInfo.InvariantCulture,
                        "+0x{0:X}（配置里存的值，**已在本次名单上现场核对**：{1} 个对象的该偏移指向你的房子）",
                        stored, agree));
                return;
            }

            WaitOwnerOffset(string.Format(CultureInfo.InvariantCulture,
                "还没有选中任何自己的单位（反推需要一个线索）；配置里那个 +0x{0:X} 现场核对**没通过**"
                + "（名单里只有 {1} 个对象在该偏移上指向你的房子）—— 所以这一列留空，不给噪声",
                stored, agree));
        }

        /// <summary>在名单上核对某个偏移：有多少个对象的该偏移**指向我此刻金钱所在的那座房子**。</summary>
        private static int CountRosterAgreeingWithMoney(
            MemoryIO memory, List<HarvestObjectRow> roster, uint offset, uint money,
            uint moduleBase, uint moduleSize)
        {
            if (offset == 0 || roster == null || roster.Count == 0) { return 0; }

            int agree = 0;
            // 只抽前若干个：核对只需要"够不够构成证据"，不需要把几百个对象全读一遍。
            int limit = roster.Count < OwnerCrossCheckProbes ? roster.Count : OwnerCrossCheckProbes;
            for (int i = 0; i < limit; i++)
            {
                uint value;
                if (!memory.ReadUInt32(roster[i].Address + offset, out value)) { continue; }
                uint houseMoney;
                if (!MoTrainer.Cheats.TechnoOwnership.TryReadHouseMoney(
                        memory, value, moduleBase, moduleSize, out houseMoney)) { continue; }
                if (houseMoney == money) { agree++; }
            }
            return agree;
        }

        /// <summary>认定成功：缓存 + 把**本拍已经读出来的名单**顺手补上阵营（否则要等到下一拍才有值）。</summary>
        private void AdoptOwnerOffset(uint offset, List<HarvestObjectRow> roster, string note)
        {
            _ownerOffset = offset;
            _ownerOffsetNote = note;
            _ownerOffsetWaitLogged = "";

            if (roster != null)
            {
                for (int i = 0; i < roster.Count; i++)
                {
                    roster[i].Owner = TechnoWalker.ReadOwner(_target.Memory, roster[i].Address, offset);
                }
            }

            Logger.Info("Harvest", "阵营字段现场认定：" + note);
        }

        /// <summary>还没认定出来 —— 同一个原因只写一次日志（认定前的每一拍都会走到这里）。</summary>
        private void WaitOwnerOffset(string reason)
        {
            if (reason == _ownerOffsetWaitLogged) { return; }
            _ownerOffsetWaitLogged = reason;
            Logger.Info("Harvest", "阵营字段暂不可用：" + reason);
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
                // 阵营用**现场认定**的偏移（还没认定出来时读 0 = 空）。
                // 这里刻意不读 `_profile.OffsetTechnoOwner`：那个是人工试出来的值，见 EnsureOwnerOffset。
                row.Owner = _ownerOffset != 0
                    ? TechnoWalker.ReadOwner(memory, row.Address, _ownerOffset) : 0;
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

        /// <summary>
        /// 把"采集期间**新出现的**对象"自动收进跟踪集合 —— 这就是「瞬间建造」要的数据。
        ///
        /// 原来想看某个东西的字段变化，玩家必须**先在游戏里选中它**；造东西的时候
        /// 根本没空去选中它（而且建造中的建筑常常选不中）。这一条把它变成全自动：
        /// 只要你造了，它就自己进跟踪名单，每秒记一次字节变化。
        ///
        /// 两个刻意的设计：
        ///   · **第一轮名单不算"新出现"**：开局本来就有几百个对象，全记下来等于没筛。
        ///     开局已有的只登记进 `_knownObjects`，之后才冒出来的才算新。
        ///   · **观察期**：新地址要先连续活过 <see cref="NewObjectMinAge"/> 拍才真正开始跟踪。
        ///     战场上每秒都有大量短命对象（子弹、碎片、动画）冒出来，不筛的话
        ///     跟踪名额会被它们瞬间占满，"正在造的那座建筑"一个都进不来。
        /// </summary>
        private void TrackNewObjects(List<HarvestObjectRow> roster, MemoryIO memory, HarvestSample sample)
        {
            if (roster.Count == 0) { return; }

            // 第一轮：只登记，不跟踪
            if (!_rosterEverRead)
            {
                for (int i = 0; i < roster.Count; i++) { _knownObjects.Add(roster[i].Address); }
                _rosterEverRead = true;
                return;
            }

            HashSet<uint> current = new HashSet<uint>();
            for (int i = 0; i < roster.Count; i++) { current.Add(roster[i].Address); }

            // 第一遍：本次新冒出来的，进入观察期
            for (int i = 0; i < roster.Count; i++)
            {
                uint address = roster[i].Address;
                if (_knownObjects.Contains(address)) { continue; }
                _knownObjects.Add(address);
                _newborn[address] = 1;
                _newbornVtable[address] = roster[i].VTable;
            }

            if (_newborn.Count == 0) { return; }

            // 第二遍：给观察期里的对象计数。先取一份键的快照再改字典（遍历中不能改）。
            List<uint> keys = new List<uint>(_newborn.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                uint address = keys[i];

                if (!current.Contains(address))
                {
                    // 观察期内就没了 —— 多半是子弹/碎片这类短命对象，丢掉
                    _newborn.Remove(address);
                    _newbornVtable.Remove(address);
                    continue;
                }

                int age = _newborn[address] + 1;
                if (age < NewObjectMinAge) { _newborn[address] = age; continue; }

                uint vtable = 0;
                _newbornVtable.TryGetValue(address, out vtable);
                _newborn.Remove(address);
                _newbornVtable.Remove(address);

                if (_byAddress.ContainsKey(address)) { continue; }
                if (_tracked.Count >= MaxTracked || _newTracked >= MaxNewTracked) { continue; }

                HarvestTracked tracked = new HarvestTracked();
                tracked.Address = address;
                tracked.FirstSeen = sample.Index;
                tracked.FirstVTable = vtable;
                tracked.NewlyAppeared = true;
                uint head;
                if (memory.ReadUInt32(address, out head) && head != 0) { tracked.FirstVTable = head; }

                _byAddress[address] = tracked;
                _tracked.Add(tracked);
                _newTracked++;
                sample.NewObjects.Add(address);

                Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                    "发现新建造的对象 0x{0:X8}（类型表 0x{1:X8}），已开始跟踪它的字段变化",
                    address, tracked.FirstVTable));
            }
        }

        /// <summary>
        /// 开始跟踪**我方房子**（`HouseClass`）—— `[S6-2]` 瞬间建造用。
        ///
        /// 为什么必须盯它：2026-10-09 的真机报告里，建造中的建筑身上每秒在变的只有
        /// +0x0C4/+0x0C8 那两个"永不停歇"的计数器，看不到进度字段的影子。而按引擎惯例，
        /// 生产队列（在造什么、还差多久）是记在**房子**身上的 —— 金钱与电力都住在那里，
        /// 生产队列没有理由例外。所以这一项把房子整个盯起来。
        ///
        /// 怎么认出房子：金钱地址减去 0x30C（与「主链反查」同一口径），
        /// 再验一条硬条件 —— 头 4 字节必须是**模块区间内的类型表指针**（C++ 对象的样子）。
        /// 验不过就不跟踪：宁可少盯一个对象，也不要盯上一个"看起来像"的地址。
        /// </summary>
        private void TrackPlayerHouse(MemoryIO memory, uint moduleBase, uint moduleSize, HarvestSample sample)
        {
            if (_profile.Money == null || !_profile.Money.IsCalibrated)
            {
                _houseNote = "金钱未校准 —— 认不出我方房子（生产队列那一段会缺数据）";
                return;
            }

            uint moneyAddress;
            string error;
            if (!_profile.Money.TryResolve(memory, moduleBase, out moneyAddress, out error))
            {
                _houseNote = "金钱地址当前解析不出来（" + error + "）—— 本拍没盯上房子";
                return;
            }
            if (moneyAddress < MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset + 0x10000) { return; }

            uint house = moneyAddress - MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset;
            if (_byAddress.ContainsKey(house)) { return; }

            uint vtable;
            if (!memory.ReadUInt32(house, out vtable))
            {
                _houseNote = string.Format(CultureInfo.InvariantCulture,
                    "金钱地址 0x{0:X8} − 0x{1:X} = 0x{2:X8}，但那里读不动 —— 本拍没盯上房子",
                    moneyAddress, MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset, house);
                return;
            }
            if (vtable < moduleBase || vtable >= moduleBase + moduleSize)
            {
                // 这条**如果出现在真机报告里，就是一条要发回来的线索**：说明"房子"的认法要改。
                _houseNote = string.Format(CultureInfo.InvariantCulture,
                    "金钱地址 0x{0:X8} − 0x{1:X} = 0x{2:X8}，但那里头 4 字节是 0x{3:X8}，"
                    + "不像类型表指针（模块区间 0x{4:X8}~0x{5:X8}）—— 没有按房子跟踪。"
                    + "**请把这一行发回来**，说明房子的认法要改。",
                    moneyAddress, MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset, house, vtable,
                    moduleBase, moduleBase + moduleSize);
                return;
            }

            HarvestTracked tracked = new HarvestTracked();
            tracked.Address = house;
            tracked.FirstSeen = sample.Index;
            tracked.FirstVTable = vtable;
            tracked.IsPlayerHouse = true;
            tracked.Source = "我方房子";
            tracked.DumpBytes = HouseDumpBytes;
            _byAddress[house] = tracked;
            _tracked.Add(tracked);
            _houseAddress = house;
            _houseNote = string.Format(CultureInfo.InvariantCulture,
                "已跟踪 0x{0:X8}（金钱地址 0x{1:X8} − 0x{2:X}），每拍盯 {3} 字节",
                house, moneyAddress, MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset, HouseDumpBytes);

            Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                "开始跟踪「我方房子」0x{0:X8}（金钱地址 0x{1:X8} 减去 0x{2:X}）—— 生产队列大概率住在这里",
                house, moneyAddress, MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset));
        }

        /// <summary>
        /// 从**我方房子**里"跟随一层"：把房子身上那些**稳定的指针成员**指向的对象也盯起来。
        ///
        /// 为什么要有这一层：生产队列/工厂按引擎惯例很可能是**独立的对象**，
        /// 房子身上只存一个指向它的指针。只盯房子本体的话，进度字段会落在视野之外，
        /// 而这一局用户就白玩了 —— 用户的时间是本项目最稀缺的东西。
        ///
        /// 三道硬门，宁可漏也不许跟错：
        ///   · 同一个偏移上连续 <see cref="FollowStableSamples"/> 拍都是同一个值（排除临时值）；
        ///   · 值 ≥ 0x100000（排除"金钱"这类小数字被当成地址 —— 金钱 50000 正好落在指针范围里）；
        ///   · **目标对象的头 4 字节必须是模块区间内的类型表指针**（确认它真的是个 C++ 对象）。
        /// 名额上限 <see cref="MaxFollowedFromHouse"/> 个，报告里逐个标明是"从哪个偏移跟过来的"。
        /// </summary>
        private void FollowHousePointers(MemoryIO memory, uint moduleBase, uint moduleSize, HarvestSample sample)
        {
            if (_houseAddress == 0) { return; }

            byte[] block = memory.ReadBytesQuiet(_houseAddress, HousePointerScanBytes);
            if (block == null || block.Length < 8) { return; }

            for (int offset = 4; offset + 4 <= block.Length; offset += 4)
            {
                FollowSlot slot;
                bool known = _followSlots.TryGetValue(offset, out slot);
                if (known && slot.Promoted) { continue; }

                uint value = ReadUInt32(block, offset);

                if (value < 0x100000 || value > 0x7FFFFFFF)
                {
                    if (known) { _followSlots.Remove(offset); }   // 这一格不是指针
                    continue;
                }

                if (!known)
                {
                    FollowSlot fresh = new FollowSlot();
                    fresh.Target = value;
                    fresh.Streak = 1;
                    _followSlots[offset] = fresh;
                    continue;
                }

                if (slot.Target != value)
                {
                    // 值变了 —— 不是"稳定的成员指针"，重新计数
                    slot.Target = value;
                    slot.Streak = 1;
                    continue;
                }

                slot.Streak++;
                if (slot.Streak < FollowStableSamples) { continue; }

                slot.Promoted = true;                 // 这一格从此不再处理（跟过或明确放弃）
                if (_followedFromHouse >= MaxFollowedFromHouse) { continue; }
                if (_byAddress.ContainsKey(value) || value == _houseAddress) { continue; }

                // **只跟堆对象**（2026-10-10 真机教训）：房子低偏移处的指针指向的是
                // 程序自己的静态结构（在模块区间内，整局一动不动）—— 上一版把名额全给了它们，
                // 真正的"生产队列/工厂"（堆对象）一个都没跟上。真对象一律在堆上。
                bool insideModule = value >= moduleBase && value < moduleBase + moduleSize;
                if (insideModule)
                {
                    _followSkippedStatic++;
                    continue;
                }

                uint targetVtable;
                if (!memory.ReadUInt32(value, out targetVtable)) { continue; }
                if (targetVtable < moduleBase || targetVtable >= moduleBase + moduleSize) { continue; }

                HarvestTracked tracked = new HarvestTracked();
                tracked.Address = value;
                tracked.FirstSeen = sample.Index;
                tracked.FirstVTable = targetVtable;
                tracked.Source = string.Format(CultureInfo.InvariantCulture, "我方房子 → 跟随 +0x{0:X}", offset);
                _byAddress[value] = tracked;
                _tracked.Add(tracked);
                _followedFromHouse++;

                Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                    "从「我方房子」跟随到对象 0x{0:X8}（房子 +0x{1:X} 指向它，类型表 0x{2:X8}）",
                    value, offset, targetVtable));
            }
        }

        /// <summary>
        /// 给每个被跟踪对象扫一遍「型号线索」（像 GAPOWR / HARV / E1 这样的建造 ID 字符串）。
        ///
        /// 放在收尾里做，不在采样循环里做：它是几十次字符串读取，采样循环每拍都跑，
        /// 不该为它增加每一拍的负担。**只读**，且读不到就空着（不猜）。
        /// </summary>
        private void ScanTypeIdHints()
        {
            MemoryIO memory = _target != null ? _target.Memory : null;
            if (memory == null) { return; }

            List<HarvestTracked> tracked;
            lock (_lock) { tracked = new List<HarvestTracked>(_tracked); }

            DateTime started = DateTime.UtcNow;
            int found = 0;
            for (int i = 0; i < tracked.Count; i++)
            {
                tracked[i].TypeIdHints.Clear();
                List<string> ids = ProgressHunter.ScanTypeIds(memory, tracked[i].Address, 0x800);
                for (int k = 0; k < ids.Count; k++) { tracked[i].TypeIdHints.Add(ids[k]); }
                if (ids.Count > 0) { found++; }
            }
            Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                "型号线索扫描完毕：{0}/{1} 个对象扫到线索，耗时 {2:0.0} 秒",
                found, tracked.Count, (DateTime.UtcNow - started).TotalSeconds));
        }

        /// <summary>
        /// 「属于我的对象」自动跟踪：找出那些**指向我方房子**的对象，按"**类别越稀有越可疑**"
        /// 排序后盯上前若干个。
        ///
        /// 为什么需要（2026-10-10 跑完第三次采集才想通）：建造/生产进度
        /// **不在建筑身上、不在单位身上、也不在房子身上** ——
        /// 房子的 80 个变化字段逐个核过，只有"金钱"和"累计花费"跟生产同步，其余都不是。
        /// 那它只可能在**房子名下的另一个对象**里，而那种对象**开局就存在**：
        /// 既不是"采集期间新出现的"（采集器只跟新对象），也未必被房子直接指着
        /// （"跟随一层"在真机上找到的是界面对象，整局一字未动）。
        ///
        /// 所以换成"从**谁指向我方房子**入手"，并且优先挑**稀有的类别**：
        /// 生产队列/工厂这类东西一个房子只有几份，而建筑、单位、界面对象动辄几十份。
        ///
        /// 只做一次（要读几百个对象的头部，不能每拍都跑），失败也不影响采集继续进行。
        /// </summary>
        private void TrackMyObjects(MemoryIO memory, uint moduleBase, uint moduleSize, HarvestSample sample)
        {
            if (_houseAddress == 0 || _myObjectsScanned) { return; }

            List<HarvestObjectRow> roster;
            lock (_lock) { roster = new List<HarvestObjectRow>(_lastRoster); }
            if (roster.Count < 4) { return; }        // 名单还没读到，下一拍再试
            _myObjectsScanned = true;

            // 各"类别"在名单里出现了几次（用来判断稀不稀有）
            Dictionary<uint, int> classCount = new Dictionary<uint, int>();
            for (int i = 0; i < roster.Count; i++)
            {
                uint vtable = roster[i].VTable;
                int existing;
                classCount[vtable] = classCount.TryGetValue(vtable, out existing) ? existing + 1 : 1;
            }

            List<uint> mine = new List<uint>();
            for (int i = 0; i < roster.Count && mine.Count < 256; i++)
            {
                uint address = roster[i].Address;
                if (address < 0x10000) { continue; }

                byte[] block = memory.ReadBytesQuiet(address, 0x800);
                if (block == null) { continue; }

                for (int offset = 0; offset + 4 <= block.Length; offset += 4)
                {
                    if (ReadUInt32(block, offset) != _houseAddress) { continue; }
                    mine.Add(address);
                    break;
                }
            }

            if (mine.Count == 0)
            {
                Logger.Info("Harvest", "「指向我方房子」的对象：一个都没找到（这条线索这次用不上）");
                return;
            }

            // 稀有的类别排前面（同类别内按地址顺序，保证可复现）
            mine.Sort(delegate(uint a, uint b)
            {
                int ca, cb;
                classCount.TryGetValue(ClassOf(roster, a), out ca);
                classCount.TryGetValue(ClassOf(roster, b), out cb);
                if (ca != cb) { return ca.CompareTo(cb); }
                return a.CompareTo(b);
            });

            for (int i = 0; i < mine.Count && _myObjectsTracked < MaxMyObjects; i++)
            {
                uint address = mine[i];
                if (_byAddress.ContainsKey(address)) { continue; }
                if (_tracked.Count >= MaxTracked) { break; }

                uint vtable = ClassOf(roster, address);
                int count;
                classCount.TryGetValue(vtable, out count);

                HarvestTracked tracked = new HarvestTracked();
                tracked.Address = address;
                tracked.FirstSeen = sample.Index;
                tracked.FirstVTable = vtable;
                tracked.Source = string.Format(CultureInfo.InvariantCulture,
                    "指向我方房子（类别 0x{0:X8}，全场 {1} 个）", vtable, count);
                _byAddress[address] = tracked;
                _tracked.Add(tracked);
                _myObjectsTracked++;
            }

            Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                "「指向我方房子」的对象：共有 {0} 个，按稀有类别挑了 {1} 个盯起来",
                mine.Count, _myObjectsTracked));
        }

        private static uint ClassOf(List<HarvestObjectRow> roster, uint address)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].Address == address) { return roster[i].VTable; }
            }
            return 0;
        }

        /// <summary>
        /// 盯上「房子 +0x53BC 指向的那个对象」—— 游戏挂"当前正在造的东西"的把手（判据见常量注释）。
        /// **只读**：读指针、盯住目标；不写、也不据此下结论（真相靠它旁边的字段随时间的表现）。
        /// </summary>
        private void TrackProducingObject(MemoryIO memory, HarvestSample sample)
        {
            if (_houseAddress == 0) { return; }

            uint target;
            if (!memory.ReadUInt32(_houseAddress + ProducingPointerOffset, out target)) { return; }
            if (target == _lastProducingTarget) { return; }

            _lastProducingTarget = target;
            if (target == 0)
            {
                Logger.Info("Harvest", "房子 +0x53BC 归零（这一件造完了）");
                return;
            }
            if (target < 0x10000 || _byAddress.ContainsKey(target)) { return; }
            if (_producingTracked >= MaxProducingTracked || _tracked.Count >= MaxTracked) { return; }

            HarvestTracked tracked = new HarvestTracked();
            tracked.Address = target;
            tracked.FirstSeen = sample.Index;
            tracked.Source = "我方房子 +0x53BC 指向的（**正在生产的东西**）";
            tracked.DumpBytes = 0x1000;
            uint head;
            if (memory.ReadUInt32(target, out head)) { tracked.FirstVTable = head; }
            _byAddress[target] = tracked;
            _tracked.Add(tracked);
            _producingTracked++;

            Logger.Info("Harvest", string.Format(CultureInfo.InvariantCulture,
                "房子 +0x53BC 指向新对象 0x{0:X8}（类型表 0x{1:X8}）—— 这就是**正在生产的东西**，盯住它",
                target, tracked.FirstVTable));
        }

        private void ReadSelection(MemoryIO memory, uint moduleBase, HarvestSample sample)        {
            // **与引擎 / 高级功能页共用同一套"金钱锚定 + 自动更正"的读法**（2026-10-08 起）。
            //
            // 之前这里用的是结构探测（SelectionProbe）：它只验证"看起来像对象数组"，
            // 不验证"是不是你的单位"。真机 10-08 证明那样读出来的"选中对象"根本不是玩家选中的
            //（历次采集里它们一个字节都没变过、字段里能读出英文单词碎片），
            // 把整份采集报告的可信度都拖垮了 —— 本次修复的起因就是它。
            if (_selection == null) { return; }

            List<uint> objects;
            string note;
            if (!_selection.TryReadSelectedObjects(_profile, SelectionProbe.MaxCount, false, out objects, out note))
            {
                return;
            }

            sample.SelectedCount = objects.Count;
            for (int i = 0; i < objects.Count; i++)
            {
                uint address = objects[i];
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

        private static void UpdateTracked(HarvestTracked tracked, MemoryIO memory, int sampleIndex)
        {
            int dumpBytes = tracked.DumpBytes;
            byte[] block = memory.ReadBytesQuiet(tracked.Address, dumpBytes);

            if (block == null || block.Length < dumpBytes)
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
                tracked.Last = new uint[dumpBytes / 4];
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
                if (tracked.ChangeEvents >= MaxChangeEventsPerObject)
                {
                    // 记不下了：**如实标注"没记全"**，而不是悄悄丢掉后面的变化。
                    // （旧版就是悄悄丢的：400 条名额被出生瞬间的"初始化洪流"吃光，
                    //   之后真正的建造过程一条都没留下 —— 报告看起来却是完整的。）
                    tracked.Truncated = true;
                }
                else
                {
                    OffsetSeries series;
                    if (!tracked.Series.TryGetValue(offset, out series))
                    {
                        // 第一次看到它变化：把"变化之前"的那个值当作序列起点
                        series = new OffsetSeries(offset, sampleIndex, tracked.Last[i]);
                        tracked.Series[offset] = series;
                    }
                    series.Record(sampleIndex, value);
                    tracked.ChangeEvents++;
                }
                tracked.Last[i] = value;
            }

            tracked.LastSeen = sampleIndex;
            tracked.SeenCount++;
        }

        // ============================================================ 报告

        /// <summary>
        /// 把报告写到磁盘。**同一个采集会话只对应一个文件**（第二次调用覆盖第一次）——
        /// 收尾时先写一版不含巡检的，巡检完再覆盖成完整的，两次必须落在同一个文件名上。
        ///
        /// 写法是"先写临时文件再改名"：直接覆盖的话，万一写到一半进程被关掉，
        /// 原地留下的就是一个**被截断的报告**——比少两节更糟，因为它看起来是完整的。
        /// </summary>
        private string WriteReport(bool dumpToLog)
        {
            try
            {
                // **生成报告也要在 try 里**（2026-10-10 自检抓到）：这一段跑在收尾线程上，
                // 一旦异常逃到线程外，整个进程会被带走 —— 用户那一局的采集数据就**全丢了**，
                // 而且因为报告没落盘，事后什么都查不到。宁可少一段，也不能没有报告。
                string text = BuildReport();

                if (_reportPath.Length == 0)
                {
                    string folder = _store != null ? _store.ReportsFolder : "reports";
                    Directory.CreateDirectory(folder);
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    _reportPath = Path.Combine(folder, "harvest-" + stamp + ".txt");
                }

                string temp = _reportPath + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(true));
                if (File.Exists(_reportPath)) { File.Delete(_reportPath); }
                File.Move(temp, _reportPath);

                if (dumpToLog)
                {
                    // 同时也逐行写进日志：「导出日志」能一并带出来，方便用户只发一个文件
                    string[] lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        Logger.Info("Harvest", lines[i].TrimEnd('\r'));
                    }
                }
                return _reportPath;
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
            // 「阵营」这一项**故意不写配置里那个值**：它是人工试出来的，真机证明读出来的不是阵营。
            // 写"现场认定出来的偏移 + 出处"，认定不出来就写清为什么 —— 见 EnsureOwnerOffset。
            uint ownerOffset;
            string ownerNote;
            string ownerWait;
            lock (_lock)
            {
                ownerOffset = _ownerOffset;
                ownerNote = _ownerOffsetNote;
                ownerWait = _ownerOffsetWaitLogged;
            }
            string ownerText = ownerOffset != 0
                ? ownerNote
                : string.Format(CultureInfo.InvariantCulture,
                    "**本次未能认定**（配置里存的 +0x{0:X} 未采用）—— {1}",
                    _profile.OffsetTechnoOwner,
                    ownerWait.Length > 0 ? ownerWait : "还没有现场证据");
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "字段偏移：阵营 {0}｜血量 +0x{1:X}｜最大血量 +0x{2:X}｜铁幕 +0x{3:X}｜生产进度 +0x{4:X}",
                ownerText, _profile.OffsetTechnoHealth, _profile.TechnoMaxHealth,
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

            // 「我方房子」认出来了没有 —— 必须写出来。
            // 认不出来时若什么都不写，用户那一局就白玩了、而且事后查不出原因。
            text.AppendLine("我方房子：" + (_houseNote.Length > 0 ? _houseNote : "（采集期间还没轮到认房子）"));
            if (_followedFromHouse > 0 || _followSkippedStatic > 0)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  从房子里跟随到 {0} 个**堆对象**（上限 {1}）—— 生产队列很可能是其中之一，见「二」里的「来源：我方房子 → 跟随」"
                    + "；另有 {2} 个指向静态结构（在程序自己的数据段里、整局不动）的指针被跳过（2026-10-10 起：只跟堆对象）",
                    _followedFromHouse, MaxFollowedFromHouse, _followSkippedStatic));
            }

            // ---- 一、时间线 ----
            text.AppendLine();
            text.AppendLine("=== 一、时间线 ===");
            text.AppendLine("（房子数值 + 场上对象总数 + 阵营分组。阵营分组如果出现「少数几个阵营、各自聚集大量对象」，说明阵营偏移是对的。）");
            if (ownerOffset == 0)
            {
                text.AppendLine("　⚠ **本次「阵营分组」整列为空**：阵营字段未能现场认定（原因见上方「字段偏移」那一行）。");
                text.AppendLine("　　这是刻意留空 —— 以前这里填的是配置里一个人工试出来的偏移，真机证明读出来的不是阵营，");
                text.AppendLine("　　那样统计出来的是噪声、却看起来像结论。想让这一列有值：**在游戏里选中一个自己的单位**即可（下一拍自动补上）。");
            }
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
            text.AppendLine("=== 二、被跟踪对象 ===");
            text.AppendLine("（来源有三种：**你选中过的**、**本次采集期间新造出来的**（后者自动收进来、不必先选中）、");
            text.AppendLine("  以及**我方房子与它跟随出来的对象**（生产队列大概率在那里）。都会一直跟到采集结束。）");
            if (tracked.Count == 0)
            {
                text.AppendLine("本次没有跟踪到任何对象 —— 采集期间你既没有选中过单位，也没有造过东西。");
                text.AppendLine("想观察某个单位的字段变化：采集过程中用鼠标选中它（哪怕只选一下），或者直接造一个。");
            }
            for (int i = 0; i < tracked.Count; i++)
            {
                HarvestTracked t = tracked[i];
                try
                {
                text.AppendLine();
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "对象 #{0}  地址 0x{1:X8}  类型表 0x{2:X8}{3}{4}",
                    i + 1, t.Address, t.FirstVTable,
                    t.NewlyAppeared ? "  ← **采集期间新造出来的**" : "",
                    t.Source.Length > 0 ? "  ｜ 来源：" + t.Source : ""));
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
                if (t.TypeIdHints.Count > 0)
                {
                    // 「型号线索」= 这个对象内部的指针指向的、像建造 ID 的字符串。
                    // 有了它，报告里每个对象是什么东西就不再只能靠猜（2026-10-10 加的）。
                    text.AppendLine("  型号线索：" + string.Join(", ", t.TypeIdHints.ToArray())
                        + "（它内部指针指向的、像「建造 ID」的字符串）");
                }

                // 变化**按偏移分组**列（每个偏移一行 + 它的取值序列）。
                // 旧版是一条变化一行、且总条数封顶 400 —— 出生瞬间的"初始化洪流"会把名额吃光，
                // 真正要看的建造过程一条都留不下（2026-10-09 报告实测到）。
                List<OffsetSeries> top = ProgressHunter.TopOffsets(t, 12);
                if (top.Count == 0)
                {
                    text.AppendLine("  字段变化：无（整个采集期间这个对象的字节一个都没动）");
                }
                else
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  变过的字段共 {0} 个，变化最多的 {1} 个（完整序列见「六」）：",
                        t.Series.Count, top.Count));
                    for (int k = 0; k < top.Count; k++)
                    {
                        OffsetSeries s = top[k];
                        // 三样都给：第一次变化的"从哪到哪"（十六进制，便于和日志/其它报告对上）、
                        // 末值、以及完整取值序列（十进制，判断"是不是一路推进"就看它）。
                        string firstStep = s.Values.Count > 0
                            ? string.Format(CultureInfo.InvariantCulture,
                                "首次 0x{0:X8} → 0x{1:X8}", s.FirstValue, s.Values[0])
                            : "（无变化点）";
                        text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "    +0x{0:X3}  变了 {1} 次  ｜ {2} ｜ 末值 0x{3:X8} ｜ 序列：{4}",
                            s.Offset, s.Changes, firstStep, s.LastValue,
                            ProgressHunter.FormatSeries(s, 16)));
                    }
                }
                if (t.Truncated)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  ⚠ 本对象变化太多，只记下了前 {0} 条 —— **后面的变化没有记全**（如实标注，别当成完整的）",
                        MaxChangeEventsPerObject));
                }
                text.AppendLine("  常见字段偏移对照：+0x06C 血量｜+0x0F0 阵营｜+0x07D 最大血量");
                }
                catch (Exception ex)
                {
                    // 一个对象的数据再怪，也不许把**整份报告**毁掉：如实记一行，继续下一个。
                    // （2026-10-10 序列打印越界那次就是这条思路救的场：错了也要留下"哪里错了"。）
                    Logger.Error("Harvest", "报告：对象 #" + (i + 1) + " 这段生成失败", ex);
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  ！（本对象这段生成失败：{0} —— 其余对象不受影响）", ex.GetType().Name));
                }
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
                if (ownerOffset != 0)
                {
                    text.AppendLine("  地址        类型表      阵营        血量");
                }
                else
                {
                    // 阵营那一列**没有认定出来就不印数**。印一排 0x00000000 看起来像数据、
                    // 实际是"什么都没读到"，比留空更误导（2026-10-12 修：这一列以前印的是
                    // 配置里那个人工试出来的偏移读出来的噪声）。
                    text.AppendLine("  地址        类型表      血量　（**阵营**那一列本次没能认定，故不印 —— 见上方「字段偏移」）");
                }
                for (int i = 0; i < roster.Count; i++)
                {
                    HarvestObjectRow row = roster[i];
                    text.AppendLine(ownerOffset != 0
                        ? string.Format(CultureInfo.InvariantCulture,
                            "  0x{0:X8}  0x{1:X8}  0x{2:X8}  {3}",
                            row.Address, row.VTable, row.Owner, row.Health)
                        : string.Format(CultureInfo.InvariantCulture,
                            "  0x{0:X8}  0x{1:X8}  {2}",
                            row.Address, row.VTable, row.Health));
                }
            }

            // ---- 四、五、自动巡检（找类型对象 / 主链反查）----
            // 这两段是"本来要用户再开一局、再点几次"的活，改由程序自己算（见 AutoInspection）。
            // 内容在采样线程里就算好了（全内存反查太慢，不能在写报告时才算）。
            if (_inspectionText.Length > 0)
            {
                text.AppendLine();
                text.Append(_inspectionText);
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
