using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Calibration
{
    public enum WizardStep
    {
        NotStarted = 0,
        Ready = 1,
        MoneyScan = 2,
        MoneyNarrow = 3,
        MoneyVerify = 4,

        /// <summary>
        /// 消费检验：用户真的花掉一笔钱后回读地址。
        /// 这是判断"这个地址是游戏真正在用的余额，还是只影响显示的影子字段"的决定性步骤——
        /// 影子字段在消费后会回落到"原值 - 消费额"，而真实字段是"测试值 - 消费额"。
        /// </summary>
        MoneySpendTest = 5,

        MoneyChain = 6,

        /// <summary>电力定位：输入游戏里读到的「负载」或「电力」数值做精确匹配。</summary>
        PowerScan = 7,
        PowerVerify = 8,
        Completed = 9,
        Failed = 10
    }

    /// <summary>
    /// 校准向导的核心逻辑（不含界面）。
    ///
    /// 流程：
    /// ① 定位金钱 —— 输入界面金额首轮扫描 → 反复输入变化后的金额收敛 → 写入测试值由用户肉眼确认；
    /// ② 回溯指针链 —— 把绝对地址反推成跨重启稳定的 `[[模块基址+静态偏移]+偏移]`；
    /// ③ 定位电力 —— 以金钱字段为中心的窗口做差分（用户每改变一次电力消耗筛选一轮），
    ///    **全程不写入游戏内存**，避免误写导致崩溃。
    ///
    /// 所有耗时操作都支持取消；进度通过 <see cref="ProgressText"/> 供界面轮询。
    /// </summary>
    public sealed class CalibrationWizard
    {
        public const int StepCount = 6;

        // 注：曾经用"金钱地址 ±0x2000 窗口"来找电力，实测在真机上一个候选都搜不到
        // （界面显示「电力 = 60」，窗口内搜 60 是 0 个候选）。
        // 现已改为全内存搜索（约 0.2 秒），不再依赖窗口假设。

        /// <summary>
        /// 测试值相对"当前金额"的增量。
        /// 旧版写死 777777 / 888888，结果 MO 的金钱显示是**平滑滚动**的，
        /// 目标值离当前值越远滚得越久，用户要干等半天（实际反馈）。
        /// 改成"当前金额 + 一个小增量"：既一眼能看出变化，又几乎瞬间滚到位。
        /// 用 12345 这种非整数尾数，是为了让它看起来不像正常收入攒出来的数。
        /// </summary>
        private const uint ProbeDelta = 12345;

        /// <summary>步骤指示器上的 6 个阶段名。</summary>
        public static readonly string[] StepNames = new string[]
        {
            "准备", "定位金钱", "验证金钱", "定位电力", "验证电力", "完成"
        };

        private readonly ICalibrationTarget _target;
        private readonly ProfileStore _store;
        private readonly CalibrationProfile _profile;
        private ValueScanner _valueScanner;

        private readonly List<string> _detailLines = new List<string>();
        private List<ScanCandidate> _candidates = new List<ScanCandidate>();
        private List<ScanCandidate> _powerCandidates = new List<ScanCandidate>();

        private CancellationTokenSource _cts;
        private int _selected = -1;
        private uint _moneyValue;
        private uint _moneyAddress;
        private uint _originalValue;
        private uint _probeValue;
        private uint _probeDelta;
        private string _reportPath = "";

        // ---- 电力定位状态 ----
        private bool _powerFieldIsDrain = true;
        private string _powerFieldName = "负载";
        private uint _powerValue;
        private bool _powerScanStarted;
        private int _powerEncodingIndex;
        private PowerEncoding _powerEncoding = PowerEncodings[0];

        public CalibrationWizard(ICalibrationTarget target, ProfileStore store, CalibrationProfile profile)
        {
            if (target == null) { throw new ArgumentNullException("target"); }
            if (store == null) { throw new ArgumentNullException("store"); }
            if (profile == null) { throw new ArgumentNullException("profile"); }

            _target = target;
            _store = store;
            _profile = profile;
            // 数值搜索器延迟创建：未连接游戏时 target.Memory 可能为空，
            // 此时向导只能走到"拒绝开始"，不应在构造阶段就抛异常。

            Step = WizardStep.NotStarted;
            Instruction = "点击『开始校准』。校准会读取游戏内存来定位金钱与电力字段，不会修改任何游戏文件。";
        }

        // ============================================================ 对外状态

        public WizardStep Step { get; private set; }
        public string StepTitle { get { return StepNames[StepIndicatorIndex]; } }
        public string Instruction { get; private set; }
        public string Status { get; private set; }
        public bool IsBusy { get; private set; }
        public string ProgressText { get; private set; }
        public string ReportPath { get { return _reportPath; } }
        public string ChainDescription { get; private set; }

        /// <summary>已经找到的候选（金钱）。</summary>
        public List<ScanCandidate> Candidates { get { return _candidates; } }

        /// <summary>已经找到的候选（电力）。</summary>
        public List<ScanCandidate> PowerCandidates { get { return _powerCandidates; } }

        public uint MoneyAddress { get { return _moneyAddress; } }
        public uint ProbeValue { get { return _probeValue; } }

        /// <summary>当前正在校准的电力字段名（「负载」或「电力」）。</summary>
        public string PowerFieldName { get { return _powerFieldName; } }

        /// <summary>电力读数是否已经完成首轮扫描（决定输入框按钮显示"开始搜索"还是"按值筛选"）。</summary>
        public bool PowerScanStarted { get { return _powerScanStarted; } }

        /// <summary>步骤指示器下标（0~5）。</summary>
        public int StepIndicatorIndex
        {
            get
            {
                switch (Step)
                {
                    case WizardStep.MoneyScan:
                    case WizardStep.MoneyNarrow:
                        return 1;
                    case WizardStep.MoneyVerify:
                    case WizardStep.MoneySpendTest:
                    case WizardStep.MoneyChain:
                        return 2;
                    case WizardStep.PowerScan:
                        return 3;
                    case WizardStep.PowerVerify:
                        return 4;
                    case WizardStep.Completed:
                        return 5;
                    default:
                        return 0;
                }
            }
        }

        /// <summary>状态变化回调。可能在后台线程触发，订阅方需自行切回 UI 线程。</summary>
        public event Action Changed;

        /// <summary>过程消息回调（同时写入日志）。</summary>
        public event Action<string> MessageReceived;

        /// <summary>数值搜索器（延迟创建）。</summary>
        private ValueScanner Scanner
        {
            get
            {
                if (_valueScanner == null) { _valueScanner = new ValueScanner(_target.Memory); }
                return _valueScanner;
            }
        }

        public void Cancel()
        {
            CancellationTokenSource cts = _cts;
            if (cts != null)
            {
                try { cts.Cancel(); }
                catch { }
            }
        }

        // ============================================================ 生命周期

        public bool Start()
        {
            if (!_target.IsReady)
            {
                Fail("尚未连接游戏进程。请先启动游戏，确认状态条显示『已连接』后再开始校准。");
                return false;
            }

            _candidates = new List<ScanCandidate>();
            _powerCandidates = new List<ScanCandidate>();
            _powerScanStarted = false;
            _selected = -1;
            _detailLines.Clear();
            _reportPath = "";
            ChainDescription = "";
            ClearLocatorState();

            _profile.ModuleBase = _target.ModuleBase;
            _profile.ModuleSize = _target.ModuleSize;

            Step = WizardStep.MoneyScan;
            Status = "";
            _detailLines.Add("环境：" + _target.DescribeEnvironment().Replace("\r\n", " | "));
            MessageLine("校准开始。" + _target.DescribeEnvironment().Replace("\r\n", " | "));

            Instruction = "请切到游戏看一眼当前金钱数额，把它填进下面输入框（纯数字），然后点『开始搜索』。";
            RaiseChanged();
            return true;
        }

        public void Reset()
        {
            Cancel();
            Step = WizardStep.NotStarted;
            _candidates = new List<ScanCandidate>();
            _powerCandidates = new List<ScanCandidate>();
            _powerScanStarted = false;
            _selected = -1;
            _detailLines.Clear();
            ChainDescription = "";
            ClearLocatorState();
            Status = "";
            ProgressText = "";
            Instruction = "已重置。点击『开始校准』重新开始。";
            RaiseChanged();
        }

        /// <summary>清空定位过程中累积的中间状态，避免上一次的残留值被误用。</summary>
        private void ClearLocatorState()
        {
            _moneyValue = 0;
            _moneyAddress = 0;
            _originalValue = 0;
            _probeValue = 0;
            _probeDelta = 0;
            _powerValue = 0;
            _powerFieldIsDrain = true;
            _powerFieldName = "负载";
            _powerEncodingIndex = 0;
            _powerEncoding = PowerEncodings[0];
        }

        private void Fail(string reason)
        {
            Step = WizardStep.Failed;
            Status = reason;
            Instruction = "校准中断。请按上面的提示处理后重新开始（或点『重置』）。";
            Logger.Warn("Wizard", "校准中断：" + reason);
            MessageLine("中断：" + reason);
            RaiseChanged();
        }

        // ============================================================ ① 金钱

        /// <summary>提交一个金额：首轮做全量扫描，之后按该值收敛。</summary>
        public bool SubmitMoneyValue(uint value)
        {
            if (IsBusy) { return false; }

            if (Step == WizardStep.MoneyScan)
            {
                return MoneyFirstScan(value);
            }
            if (Step == WizardStep.MoneyNarrow)
            {
                if (value == _moneyValue)
                {
                    Status = string.Format(CultureInfo.InvariantCulture,
                        "输入值与上次相同（{0}），筛选不会收敛。请在游戏里先花掉或赚到一些钱，再输入新的金额。", value);
                    RaiseChanged();
                    return false;
                }

                int kept = Scanner.Refilter(_candidates, ValueMatch.EqualTo, value);
                _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "按值筛选 {0} → 剩余 {1} 个候选", value, kept));
                MessageLine(string.Format(CultureInfo.InvariantCulture,
                    "按 {0} 筛选后剩余 {1} 个候选", value, kept));
                _moneyValue = value;

                if (kept == 0)
                {
                    Step = WizardStep.MoneyScan;
                    Status = string.Format(CultureInfo.InvariantCulture,
                        "筛选后一个候选都不剩，说明之前输入的金额和游戏里的实际数值对不上。已回到首轮，请重新输入当前金额。");
                    RaiseChanged();
                    return false;
                }
                if (kept == 1) { EnterMoneyVerify(0); }
                else { Status = string.Format(CultureInfo.InvariantCulture, "还剩 {0} 个候选，请再改变一次金钱并输入新数值。", kept); }
                RaiseChanged();
                return true;
            }

            Status = "当前步骤不接受金额输入。";
            RaiseChanged();
            return false;
        }

        private bool MoneyFirstScan(uint value)
        {
            ValueScanOptions options = new ValueScanOptions();
            MessageLine("首轮扫描目标值 " + value);
            BeginScan("正在扫描内存…");
            ValueScanReport report;
            try
            {
                report = Scanner.Scan(_target.HeapRegions, value, options, Token, OnScanProgress);
            }
            finally
            {
                EndScan();
            }

            if (!report.Ok) { Fail("扫描失败：" + report.Error); return false; }
            if (report.Cancelled) { Status = "扫描已取消。"; RaiseChanged(); return false; }

            _detailLines.Add("首轮扫描：" + report.Summary());
            MessageLine("首轮扫描完成：" + report.Summary());

            if (report.Candidates.Count == 0)
            {
                Status = string.Format(CultureInfo.InvariantCulture,
                    "没有找到数值 {0}。请核对游戏里显示的金额是否完全一致（不要带逗号或单位），然后重试。", value);
                RaiseChanged();
                return false;
            }

            _candidates = report.Candidates;
            _moneyValue = value;

            if (_candidates.Count == 1)
            {
                EnterMoneyVerify(0);
            }
            else
            {
                Step = WizardStep.MoneyNarrow;
                Status = string.Format(CultureInfo.InvariantCulture,
                    "找到 {0} 个候选。请在游戏里花掉或赚到一些钱，把新的金额填进来再点『筛选』。", _candidates.Count);
                Instruction = "改变游戏里的金钱（造个兵、卖个东西都行），输入变化后的新金额，点『按值筛选』。\n"
                    + "如果不想核对数字，也可以直接点『金钱变多了』/『金钱变少了』。";
            }
            RaiseChanged();
            return true;
        }

        /// <summary>不输入数值，直接按变化方向收敛（适合不爱核对数字的用户）。</summary>
        public bool NarrowByChange(ValueMatch match)
        {
            if (IsBusy) { return false; }
            if (Step != WizardStep.MoneyScan && Step != WizardStep.MoneyNarrow)
            {
                Status = "当前步骤不支持按变化筛选。";
                RaiseChanged();
                return false;
            }

            if (Step == WizardStep.MoneyScan)
            {
                Status = "还没有候选列表，请先输入一个金额做首轮搜索。";
                RaiseChanged();
                return false;
            }

            int kept = Scanner.Refilter(_candidates, match, 0);
            _detailLines.Add(string.Format("按变化筛选（{0}）→ 剩余 {1} 个候选", DescribeMatch(match), kept));
            MessageLine(string.Format("按变化筛选（{0}）后剩余 {1} 个候选", DescribeMatch(match), kept));

            if (kept == 0)
            {
                Status = "筛选后没有候选了，请回到首轮重新输入当前金额。";
                Step = WizardStep.MoneyScan;
            }
            else if (kept == 1)
            {
                EnterMoneyVerify(0);
            }
            else
            {
                Status = string.Format(CultureInfo.InvariantCulture,
                    "还剩 {0} 个候选，请再次改变金钱后重复筛选。", kept);
            }
            RaiseChanged();
            return true;
        }

        /// <summary>选定某个候选进入验证。</summary>
        public bool SelectCandidate(int index)
        {
            if (index < 0 || index >= _candidates.Count) { return false; }
            if (Step != WizardStep.MoneyNarrow && Step != WizardStep.MoneyVerify) { return false; }

            EnterMoneyVerify(index);
            RaiseChanged();
            return true;
        }

        private void EnterMoneyVerify(int index)
        {
            if (index < 0 || index >= _candidates.Count) { return; }

            _selected = index;
            _moneyAddress = _candidates[index].Address;
            _originalValue = _candidates[index].PreviousValue;

            // 测试值 = 当前金额 + 小增量（不写死大数，避免游戏里金钱数字滚半天）
            _probeDelta = ProbeDelta;
            _probeValue = _originalValue + _probeDelta;
            if (_probeValue < _originalValue)
            {
                // 理论上溢出了（金额接近 uint 上限），改为往下走
                _probeValue = _originalValue - _probeDelta;
            }

            Step = WizardStep.MoneyVerify;
            Instruction = string.Format(CultureInfo.InvariantCulture,
                "候选地址 0x{0:X8}（当时值 {1}）。\n"
                + "点『写入测试值 {2}』（= 当前金额 +{3}），然后切回游戏看金钱有没有变成 {2}。\n"
                + "不用等数字慢慢滚完，只要看到它在往 {2} 涨、或者已经停在 {2} 就可以回来确认。",
                _moneyAddress, _originalValue, _probeValue, _probeDelta);
            Status = "";
        }

        /// <summary>把当前候选写成测试值，等待用户肉眼确认。</summary>
        public bool WriteProbe()
        {
            if (Step != WizardStep.MoneyVerify) { return false; }
            if (!_target.Memory.WriteUInt32Verified(_moneyAddress, _probeValue))
            {
                Status = "写入失败。请确认修改器以管理员身份运行、游戏仍在运行。";
                RaiseChanged();
                return false;
            }

            _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                "写入测试值 {0} → 0x{1:X8} 成功", _probeValue, _moneyAddress));
            MessageLine(string.Format(CultureInfo.InvariantCulture,
                "已把 0x{0:X8} 写成 {1}，等待用户确认", _moneyAddress, _probeValue));

            Status = string.Format(CultureInfo.InvariantCulture,
                "已写入测试值 {0}。请切回游戏看金钱是否显示为 {0}，然后回来选择『确实变了』或『没变化』。", _probeValue);
            RaiseChanged();
            return true;
        }

        /// <summary>用户确认测试值在游戏里生效 → 进入消费检验。</summary>
        public bool ConfirmProbe()
        {
            if (Step != WizardStep.MoneyVerify) { return false; }

            // 顺手复核一次：如果游戏已经把测试值改回去了，说明这个地址不生效
            uint current;
            bool canRead = _target.Memory.ReadUInt32(_moneyAddress, out current);
            _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                "用户确认测试值生效；复核读回 0x{0:X8} = {1}（期望 {2}）",
                _moneyAddress, canRead ? current.ToString() : "读取失败", _probeValue));

            Step = WizardStep.MoneySpendTest;
            Status = "";
            Instruction =
                "最后一步验证：请在游戏里花掉一笔钱（随便造个便宜的兵或建筑都行），"
                + "等金钱数字确实减少了之后，回来点『已花钱，检验』。\n"
                + "这一步是为了区分两种地址：真正影响余额的字段会从测试值开始扣减；"
                + "只影响显示的影子字段则会突然跳回原来的金额。";
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// 消费检验：用户确实花过钱之后回读地址。
        /// 判据——读到的值若仍明显大于"原值"，说明它是权威余额；否则是影子字段，直接排除。
        /// 用"原值与测试值的中点"作为门槛，避免玩家有收入时误判。
        /// </summary>
        public bool VerifySpend()
        {
            if (Step != WizardStep.MoneySpendTest) { return false; }

            uint current;
            if (!_target.Memory.ReadUInt32(_moneyAddress, out current))
            {
                Status = "读取失败，请确认游戏仍在运行、修改器权限正常。";
                RaiseChanged();
                return false;
            }

            // 门槛取"原值 + 增量的 1/4"：
            //   权威余额 —— 消费是从测试值往下扣，读回值仍远高于原值；
            //   影子字段 —— 会跳回原值附近，正好落在门槛以下。
            // 用 1/4 而不是 1/2，是因为增量本身已经调到很小（见 ProbeDelta），
            // 得给"这笔花销可能比预想的大一点"留出余量，否则容易误判成影子字段。
            uint threshold = _originalValue + _probeDelta / 4;
            bool authoritative = current > threshold;

            _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                "消费检验：原值 {0}｜测试值 {1}｜消费后读回 {2}｜门槛 {3}｜判定 {4}",
                _originalValue, _probeValue, current, threshold,
                authoritative ? "权威字段（通过）" : "影子字段（排除）"));

            if (authoritative)
            {
                MessageLine(string.Format(CultureInfo.InvariantCulture,
                    "消费检验通过：写入的金额被游戏当作真实余额扣减（读回 {0}）", current));

                // 检验通过，这时才把账户还原成玩家原本的金额
                RestoreOriginalMoney();
                MessageLine(string.Format(CultureInfo.InvariantCulture,
                    "已将金钱还原为原值 {0}", _originalValue));
                return CommitMoneyChain();
            }

            MessageLine(string.Format(CultureInfo.InvariantCulture,
                "消费检验未通过：地址 0x{0:X8} 读了 {1}，已回落到原值附近，判定为影子字段",
                _moneyAddress, current));

            RestoreOriginalMoney();
            _candidates.RemoveAt(0);
            if (_candidates.Count == 0)
            {
                Step = WizardStep.MoneyScan;
                Status = "这个候选只影响显示、不影响余额，已排除。候选已经用完，请重新开始金钱定位。\n"
                    + "提示：换个时机再试——先在游戏里正常花一笔钱、等金钱数字稳定下来，再输入当前金额。";
                Instruction = "请输入游戏里当前的金钱数额，重新开始搜索。";
            }
            else
            {
                EnterMoneyVerify(0);
                Status = string.Format(CultureInfo.InvariantCulture,
                    "该候选是影子字段，已排除，换下一个（还剩 {0} 个）。", _candidates.Count);
            }
            RaiseChanged();
            return false;
        }

        private bool CommitMoneyChain()
        {
            Step = WizardStep.MoneyChain;
            Instruction = "正在回溯静态指针链，确定跨重启可用的地址，请稍候…";
            Status = "";
            RaiseChanged();

            PointerScanReport report;
            BeginScan("正在回溯指针链…");
            try
            {
                PointerScanner scanner = new PointerScanner(_target.Memory);
                PointerScanOptions options = new PointerScanOptions();
                report = scanner.Scan(_moneyAddress, _target.ModuleBase, _target.ModuleSize,
                    _target.PointerRegions, options, Token, OnTextProgress);
            }
            finally
            {
                EndScan();
            }

            for (int i = 0; i < report.Trace.Count; i++) { _detailLines.Add("  " + report.Trace[i]); }
            MessageLine("指针回溯：" + report.Summary());

            // 注意：测试值的还原已经在消费检验里做过了，这里不再重复还原
            if (report.Paths.Count == 0)
            {
                Fail("没有找到可持久化的静态指针链，无法生成跨重启有效的配置。"
                    + "可能是该字段由动态对象直接管理；请把校准报告发回分析。");
                return false;
            }

            PointerPath best = report.Paths[0];
            // 实测：指向 House 对象的静态全局不止一个，且不同会话里"能被找到的那几个"会变，
            // 所以把当时找到的多条链全部存下来，运行时依次尝试。
            _profile.Money.SetFromPaths(report.Paths);
            _profile.Money.Fingerprint = "测试值 " + _probeValue + " 已验证（消费检验通过）";
            _profile.Money.Note = string.Format(CultureInfo.InvariantCulture,
                "静态根 0x{0:X8}，{1} 层，共保留 {2} 条候选链（原找到 {3} 条）",
                best.StaticAddress, best.LevelCount, _profile.Money.ChainCount, report.Paths.Count);

            ChainDescription = best.Format(_target.ModuleBase);
            _detailLines.Add("选中指针链：" + best.Format(_target.ModuleBase)
                + "（共 " + report.Paths.Count + " 条候选路径）");
            for (int i = 0; i < report.Paths.Count && i < 8; i++)
            {
                _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "  候选路径 {0}：{1}", i + 1, report.Paths[i].Format(_target.ModuleBase)));
            }

            MessageLine("已锁定金钱指针链：" + ChainDescription);

            // 中间结果先落盘，避免后面出错白做
            string saveError;
            _store.Save(_profile, out saveError);

            EnterPowerScan();
            RaiseChanged();
            return true;
        }

        /// <summary>用户反馈测试值没生效 → 排除该候选，试下一个。</summary>
        public bool RejectProbe()
        {
            if (Step != WizardStep.MoneyVerify) { return false; }

            RestoreOriginalMoney();
            _candidates.RemoveAt(0);
            _detailLines.Add("用户反馈测试值未生效，排除候选 0x" + _moneyAddress.ToString("X8", CultureInfo.InvariantCulture));
            MessageLine("候选未生效，已排除");

            if (_candidates.Count == 0)
            {
                Step = WizardStep.MoneyScan;
                Status = "候选都排除了。请重新开始金钱定位。";
                Instruction = "请输入游戏里当前的金钱数额，重新开始搜索。";
            }
            else
            {
                EnterMoneyVerify(0);
                Status = string.Format(CultureInfo.InvariantCulture,
                    "已排除，换下一个候选（还剩 {0} 个）。", _candidates.Count);
            }
            RaiseChanged();
            return true;
        }

        private void RestoreOriginalMoney()
        {
            if (_moneyAddress == 0) { return; }
            if (_target.Memory.WriteUInt32(_moneyAddress, _originalValue))
            {
                _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "已把 0x{0:X8} 还原为原值 {1}", _moneyAddress, _originalValue));
            }
        }

        // ============================================================ ② 电力

        /// <summary>
        /// 进入电力定位。先定位「负载」，再定位「电力」。
        /// 采用和金钱一样的"精确数值收敛"策略：游戏里能直接读到这两个数字，
        /// 精确匹配比"观察变化"可靠得多，也几乎不可能找错字段。
        /// </summary>
        private void EnterPowerScan()
        {
            EnterPowerScan(!_profile.PowerDrain.IsCalibrated);
        }

        private void EnterPowerScan(bool drain)
        {
            _powerFieldIsDrain = drain;
            _powerFieldName = drain ? "负载" : "电力";
            _powerCandidates = new List<ScanCandidate>();
            _powerValue = 0;
            _powerScanStarted = false;
            _powerEncodingIndex = 0;
            _powerEncoding = PowerEncodings[0];

            Step = WizardStep.PowerScan;
            Status = "";
            Instruction = string.Format(CultureInfo.InvariantCulture,
                "接下来定位「{0}」。游戏界面里能看到『电力』和『负载』两个数字，"
                + "请读出当前的「{0}」数值填进输入框，然后点『开始搜索』。\n"
                + "会在整个游戏内存里精确匹配这个数字（约 0.2 秒）。一次搜不到会自动换一种数值存储格式再试，"
                + "所以不确定的话直接点搜索就行。\n"
                + "确认整个内存都搜不到时，说明这一项是游戏算出来的显示值而不是存储值，点『跳过这一项』即可。",
                _powerFieldName);
        }

        /// <summary>提交一个电力读数：首轮全内存精确匹配，之后按新读数收敛。</summary>
        public bool SubmitPowerValue(uint value)
        {
            if (IsBusy) { return false; }
            if (Step != WizardStep.PowerScan)
            {
                Status = "当前步骤不接受电力读数输入。";
                RaiseChanged();
                return false;
            }

            if (!_powerScanStarted) { return PowerFirstScan(value); }
            return PowerNarrow(value);
        }

        /// <summary>
        /// 首轮搜索：直接在**整个游戏内存**里精确匹配读数。
        /// 依次尝试各种数值编码；哪种能搜到就用哪种。
        /// </summary>
        private bool PowerFirstScan(uint value)
        {
            while (true)
            {
                PowerEncoding encoding = _powerEncoding;
                uint stored = encoding.ToStored(value);

                ValueScanOptions options = new ValueScanOptions();
                options.FourByteAligned = true;
                options.MatchUInt16 = encoding.UInt16;
                options.MaxResults = 200000;

                MessageLine(string.Format(CultureInfo.InvariantCulture,
                    "搜索「{0}」：界面值 {1} → 内存目标 {2}（{3}）",
                    _powerFieldName, value, stored, encoding.Describe()));

                BeginScan(string.Format(CultureInfo.InvariantCulture,
                    "正在搜索「{0}」（{1}）…", _powerFieldName, encoding.Describe()));
                ValueScanReport report;
                try
                {
                    report = Scanner.Scan(_target.HeapRegions, stored, options, Token, OnScanProgress);
                }
                finally
                {
                    EndScan();
                }

                if (!report.Ok) { Fail("扫描失败：" + report.Error); return false; }
                if (report.Cancelled) { Status = "扫描已取消。"; RaiseChanged(); return false; }

                _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "「{0}」搜索，编码 {1}，内存目标 {2}：{3}",
                    _powerFieldName, encoding.Describe(), stored, report.Summary()));

                if (report.Candidates.Count > 0)
                {
                    _powerCandidates = report.Candidates;
                    _powerValue = value;
                    _powerScanStarted = true;

                    if (_powerCandidates.Count == 1) { EnterPowerVerify(); }
                    else
                    {
                        Status = string.Format(CultureInfo.InvariantCulture,
                            "找到 {0} 个候选（编码：{1}）。请在游戏里改变「{2}」"
                            + "（造一个耗电建筑，或拆掉一个），把新的数值填进来再点『按值筛选』。",
                            _powerCandidates.Count, encoding.Describe(), _powerFieldName);
                    }
                    RaiseChanged();
                    return true;
                }

                // 这种编码搜不到，换下一种继续
                _powerEncodingIndex++;
                if (_powerEncodingIndex >= PowerEncodings.Length)
                {
                    MessageLine(string.Format(CultureInfo.InvariantCulture,
                        "「{0}」六种编码（4/2 字节 × 1/10/100）全都没命中", _powerFieldName));
                    Status = string.Format(CultureInfo.InvariantCulture,
                        "整个内存里用 6 种编码都找不到数值 {0}。\n"
                        + "请先核对「{1}」是否读对了（只要数字）。如果确实没读错，"
                        + "说明这一项是游戏算出来的显示值而不是存储值，直接点『跳过这一项』，不影响其它功能。",
                        value, _powerFieldName);
                    RaiseChanged();
                    return false;
                }

                _powerEncoding = PowerEncodings[_powerEncodingIndex];
                MessageLine(string.Format(CultureInfo.InvariantCulture,
                    "该编码下 0 个候选，换用「{0}」重试", _powerEncoding.Describe()));
            }
        }

        private bool PowerNarrow(uint value)
        {
            if (value == _powerValue)
            {
                Status = string.Format(CultureInfo.InvariantCulture,
                    "新数值和上次一样，筛选不会收敛。请先在游戏里让「{0}」真的变化。", _powerFieldName);
                RaiseChanged();
                return false;
            }

            uint stored = _powerEncoding.ToStored(value);
            int kept = Scanner.Refilter(_powerCandidates, ValueMatch.EqualTo, stored);
            _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                "「{0}」按值筛选（内存目标 {1}）→ 剩余 {2} 个候选", _powerFieldName, stored, kept));
            MessageLine(string.Format(CultureInfo.InvariantCulture,
                "「{0}」按值筛选后剩余 {1} 个候选", _powerFieldName, kept));

            if (kept > 0)
            {
                _powerValue = value;
                if (kept == 1) { EnterPowerVerify(); }
                else
                {
                    Status = string.Format(CultureInfo.InvariantCulture,
                        "还剩 {0} 个候选，请再改变一次「{1}」并输入新数值。", kept, _powerFieldName);
                }
                RaiseChanged();
                return true;
            }

            // 一个都没剩：很可能是编码猜错了。换下一种编码，用当前读数重新搜一遍。
            if (_powerEncodingIndex + 1 < PowerEncodings.Length)
            {
                _powerEncodingIndex++;
                _powerEncoding = PowerEncodings[_powerEncodingIndex];
                _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "筛选后 0 个候选，判定编码不符，改用「{0}」重新搜索", _powerEncoding.Describe()));
                MessageLine(string.Format(CultureInfo.InvariantCulture,
                    "上次的编码对不上，换用「{0}」重新搜索", _powerEncoding.Describe()));

                _powerCandidates = new List<ScanCandidate>();
                _powerScanStarted = false;
                _powerValue = 0;
                return PowerFirstScan(value);
            }

            _powerScanStarted = false;
            _powerCandidates = new List<ScanCandidate>();
            _powerValue = 0;
            Status = string.Format(CultureInfo.InvariantCulture,
                "筛选后一个候选都不剩，且六种编码都试过了。请重新输入当前的「{0}」再看一次。", _powerFieldName);
            RaiseChanged();
            return false;
        }

        private void EnterPowerVerify()
        {
            Step = WizardStep.PowerVerify;
            Instruction = string.Format(CultureInfo.InvariantCulture,
                "「{0}」已经收敛到 {1} 个候选（编码：{2}）。请在下面的候选列表里选中它，然后点『确认{0}字段』。\n"
                + "若候选不止一个，宁可再改变一次「{0}」的数值把它筛到只剩一个，不要凭感觉选。",
                _powerFieldName, _powerCandidates.Count, _powerEncoding.Describe());
            Status = "";
        }

        /// <summary>
        /// 电力（及负载）在内存里可能的编码方式。
        /// 实测教训：只按"4 字节 + 数值原样"搜索，会出现一个候选都搜不到的情况
        /// （界面明明显示「电力 = 60」，全内存搜 60 却是 0 个候选）。
        /// 可能是：① 字段是 short（2 字节），按 4 字节比时高 2 位是邻居数据；
        /// ② 模组为了做小数级微调，把电力按 ×10 / ×100 放大存储。
        /// 因此依次尝试多种编码，哪种能搜到就用哪种，并把编码记进配置供写回时使用。
        /// </summary>
        private sealed class PowerEncoding
        {
            public readonly int Scale;
            public readonly bool UInt16;

            public PowerEncoding(int scale, bool uint16)
            {
                Scale = scale;
                UInt16 = uint16;
            }

            public int Width { get { return UInt16 ? 2 : 4; } }

            public string Describe()
            {
                string text = UInt16 ? "2 字节" : "4 字节";
                if (Scale != 1) { text += "×" + Scale; }
                return text;
            }

            /// <summary>把游戏里显示的数值换算成内存里应该存的值。</summary>
            public uint ToStored(uint displayed)
            {
                ulong value = (ulong)displayed * (ulong)Scale;
                ulong cap = UInt16 ? 0xFFFFUL : (ulong)uint.MaxValue;
                return value > cap ? (uint)cap : (uint)value;
            }
        }

        private static readonly PowerEncoding[] PowerEncodings = new PowerEncoding[]
        {
            new PowerEncoding(1, false),
            new PowerEncoding(1, true),
            new PowerEncoding(10, false),
            new PowerEncoding(10, true),
            new PowerEncoding(100, false),
            new PowerEncoding(100, true)
        };

        /// <summary>
        /// 已废弃：旧的"观察变化"定位方式。
        /// 实测证明它会把"累计消费 / 建造计数"这类干扰字段混进来（曾选出数值高达 11361 的伪字段），
        /// 现已改为用户读数精确匹配，见 <see cref="SubmitPowerValue"/>。保留此方法仅用于兼容旧调用点。
        /// </summary>
        public bool RecordPowerChange()
        {
            Status = "该方法已废弃，请改用『输入电力读数』的方式定位。";
            RaiseChanged();
            return false;
        }
        /// <summary>确认当前电力字段（负载 或 电力）。</summary>
        public bool ConfirmPowerField(uint address, uint currentValue)
        {
            if (address == 0)
            {
                Status = "还没有可确认的候选。";
                RaiseChanged();
                return false;
            }

            // 电力字段与金钱同属 HouseClass，用同一套回溯方式
            BeginScan("正在回溯指针链…");
            PointerScanReport report;
            try
            {
                PointerScanner scanner = new PointerScanner(_target.Memory);
                PointerScanOptions options = new PointerScanOptions();
                report = scanner.Scan(address, _target.ModuleBase, _target.ModuleSize,
                    _target.PointerRegions, options, Token, OnTextProgress);
            }
            finally
            {
                EndScan();
            }

            for (int i = 0; i < report.Trace.Count; i++) { _detailLines.Add("  " + report.Trace[i]); }
            MessageLine(string.Format(CultureInfo.InvariantCulture,
                "「{0}」指针回溯：{1}", _powerFieldName, report.Summary()));

            FieldLocator locator = _powerFieldIsDrain ? _profile.PowerDrain : _profile.PowerOutput;
            if (report.Paths.Count > 0)
            {
                locator.SetFromPaths(report.Paths);
                locator.ValueWidth = _powerEncoding.Width;
                locator.ValueScale = _powerEncoding.Scale;
                locator.Fingerprint = string.Format(CultureInfo.InvariantCulture,
                    "读数核对值 {0}（{1}）", currentValue, _powerEncoding.Describe());
                locator.Note = string.Format(CultureInfo.InvariantCulture,
                    "全内存精确值匹配定位（{0}），静态根 0x{1:X8}，{2} 层，保留 {3} 条候选链",
                    _powerEncoding.Describe(), report.Paths[0].StaticAddress,
                    report.Paths[0].LevelCount, locator.ChainCount);

                _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "「{0}」锁定：{1}（共 {2} 条候选链，编码 {3}）",
                    _powerFieldName, report.Paths[0].Format(_target.ModuleBase),
                    locator.ChainCount, _powerEncoding.Describe()));
            }
            else if (TryDeriveFromMoney(locator, address, currentValue))
            {
                // 直接回溯失败，但字段与金钱同对象 —— 已用金钱链推导出可用链，明细见方法内
            }
            else
            {
                locator.Clear();
                locator.ValueWidth = 4;
                locator.ValueScale = 1;
                locator.Fingerprint = "";
                _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "「{0}」既回溯不到静态链，也无法从金钱链推导，本次不记录该项", _powerFieldName));
                MessageLine(string.Format(CultureInfo.InvariantCulture,
                    "「{0}」未能生成可持久化的指针链，本次不记录", _powerFieldName));
            }

            string saveError;
            _store.Save(_profile, out saveError);

            // 负载完成后再校准「电力」，两项都过一遍才收尾
            if (_powerFieldIsDrain && !_profile.PowerOutput.IsCalibrated)
            {
                EnterPowerScan(false);
                RaiseChanged();
                return true;
            }

            Finish();
            return true;
        }

        /// <summary>
        /// 直接指针回溯失败时，用"金钱链 + 相对偏移"推导同对象内字段的指针链。
        ///
        /// 真机实测：电力字段在 House 对象内偏移 0x53A4 处，金钱在 0x30C 处，
        /// 两者相距 0x5098 字节。而指针回溯的最大偏移只有 0x400，
        /// "指向该对象的那个全局指针"恰好落在搜索范围之外 → 回溯 0 命中。
        ///
        /// 既然两个字段确定在同一个对象里（相对偏移是固定的结构事实），
        /// 就把金钱链的最后一段偏移加上这个差值即可得到目标字段的链。
        /// 附带好处：直接继承金钱链的多条候选，跨会话的稳健性一并拿到。
        /// </summary>
        private bool TryDeriveFromMoney(FieldLocator target, uint fieldAddress, uint currentValue)
        {
            if (fieldAddress == 0 || _moneyAddress == 0) { return false; }
            if (!_profile.Money.IsCalibrated) { return false; }

            long delta = (long)fieldAddress - (long)_moneyAddress;
            _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                "  相对位置：0x{0:X8} - 0x{1:X8}（金钱） = +0x{2:X} 字节，与金钱同属一个对象",
                fieldAddress, _moneyAddress, delta));
            _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                "  回溯 0 命中是因为对象内偏移 0x{0:X} 超出回溯上限 0x400，改用金钱链推导",
                delta + 0x30C));

            List<PointerChain> moneyChains = _profile.Money.Chains;
            target.Clear();
            for (int i = 0; i < moneyChains.Count; i++)
            {
                uint[] offsets = moneyChains[i].Offsets;
                if (offsets == null || offsets.Length == 0) { continue; }

                long last = (long)offsets[offsets.Length - 1] + delta;
                if (last < 0 || last > uint.MaxValue) { continue; }

                uint[] derived = new uint[offsets.Length];
                Array.Copy(offsets, derived, offsets.Length);
                derived[offsets.Length - 1] = (uint)last;
                target.AddChain(new PointerChain(derived));
            }

            if (target.ChainCount == 0)
            {
                _detailLines.Add("  金钱链推导失败：没有可用的候选链");
                return false;
            }

            target.ValueWidth = _powerEncoding.Width;
            target.ValueScale = _powerEncoding.Scale;
            target.Fingerprint = string.Format(CultureInfo.InvariantCulture,
                "读数核对值 {0}（{1}）", currentValue, _powerEncoding.Describe());
            target.Note = string.Format(CultureInfo.InvariantCulture,
                "全内存精确值匹配定位（{0}）。与金钱同属一个对象、相距 0x{1:X} 字节；"
                + "直接回溯 0 命中（对象内偏移超出回溯上限 0x400），改由金钱链加该相对偏移推导，"
                + "共 {2} 条候选链",
                _powerEncoding.Describe(), delta, target.ChainCount);

            _detailLines.Add(string.Format(CultureInfo.InvariantCulture,
                "「{0}」锁定（由金钱链推导）：{1}（共 {2} 条候选链，编码 {3}）",
                _powerFieldName, target.Chain.Format(_target.ModuleBase),
                target.ChainCount, _powerEncoding.Describe()));
            MessageLine(string.Format(CultureInfo.InvariantCulture,
                "「{0}」改由金钱链推导出 {1} 条候选链", _powerFieldName, target.ChainCount));
            return true;
        }

        /// <summary>跳过当前电力字段：先跳「负载」，再跳「电力」，两次都跳就收尾。</summary>
        public bool SkipPowerField()
        {
            _detailLines.Add(string.Format("用户跳过「{0}」校准", _powerFieldName));
            MessageLine(string.Format("已跳过「{0}」", _powerFieldName));

            if (_powerFieldIsDrain && !_profile.PowerOutput.IsCalibrated)
            {
                EnterPowerScan(false);
                RaiseChanged();
                return true;
            }

            Finish();
            return true;
        }

        // ============================================================ ③ 收尾

        public bool Finish()
        {
            string error;
            if (!_store.Save(_profile, out error))
            {
                Status = "保存校准配置失败：" + error;
                MessageLine(Status);
                RaiseChanged();
                return false;
            }

            _reportPath = _store.WriteReport(
                _profile, _target.Memory, _target.DescribeEnvironment(), _detailLines);

            Step = WizardStep.Completed;
            Instruction = "校准完成。配置已保存，下次启动会自动加载，无需重新校准。";
            Status = _reportPath == null
                ? "校准完成，但报告生成失败（详见日志）。"
                : "校准完成。报告已生成：" + _reportPath;

            MessageLine("校准完成。" + _profile.Describe());
            if (_reportPath != null) { MessageLine("报告路径：" + _reportPath); }
            RaiseChanged();
            return true;
        }

        // ============================================================ 工具
        // 电力定位改为"读数精确匹配"后，原先的窗口快照与差分代码已移除

        private CancellationToken Token
        {
            get { return _cts == null ? CancellationToken.None : _cts.Token; }
        }

        private void BeginScan(string progressText)
        {
            _cts = new CancellationTokenSource();
            IsBusy = true;
            ProgressText = progressText;
        }

        private void EndScan()
        {
            IsBusy = false;
            ProgressText = "";
            CancellationTokenSource cts = _cts;
            _cts = null;
            if (cts != null)
            {
                try { cts.Dispose(); }
                catch { }
            }
        }

        private void OnScanProgress(ValueScanReport progress)
        {
            ProgressText = string.Format(CultureInfo.InvariantCulture,
                "已扫描 {0:N0} MB｜比对 {1:N0} 个位置｜候选 {2} 个",
                progress.BytesScanned / 1048576.0, progress.PositionsTested, progress.Candidates.Count);
        }

        private void OnTextProgress(string text)
        {
            ProgressText = text;
        }

        private void MessageLine(string text)
        {
            Logger.Info("Wizard", text);
            Action<string> handler = MessageReceived;
            if (handler != null)
            {
                try { handler(text); }
                catch { }
            }
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null)
            {
                try { handler(); }
                catch { }
            }
        }

        private static string DescribeMatch(ValueMatch match)
        {
            switch (match)
            {
                case ValueMatch.Increased: return "变大";
                case ValueMatch.Decreased: return "变小";
                case ValueMatch.Changed: return "有变化";
                case ValueMatch.Unchanged: return "没变化";
                case ValueMatch.EqualTo: return "等于指定值";
                case ValueMatch.GreaterThan: return "大于指定值";
                case ValueMatch.LessThan: return "小于指定值";
                default: return match.ToString();
            }
        }
    }
}
