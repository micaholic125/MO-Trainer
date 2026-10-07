using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using MoTrainer.Calibration;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Cheats
{
    /// <summary>
    /// 实时锁定引擎。
    ///
    /// 为什么必须"持续回写"：游戏逻辑每秒重算这些数值十几次，
    /// 改一次立刻就会被下一帧覆盖。所以要开一个后台线程反复写，也就是"锁定"。
    /// 游戏逻辑约 15 FPS，本引擎跑 30Hz 足以稳压（每帧至少覆盖一次）。
    ///
    /// 省流设计：每轮**先读再比**，只有当前值和目标值不同才写。
    /// 这样绝大多数轮次只有读操作（ReadProcessMemory），写入只在游戏真的改动过数值时发生，
    /// 对游戏和系统的打扰都最小 —— 游戏里"金钱还在正常增减"也能正常显示。
    /// </summary>
    public sealed class CheatEngine : IDisposable
    {
        /// <summary>轮询间隔。33ms ≈ 30Hz。</summary>
        private const int TickIntervalMs = 33;

        /// <summary>每隔多少轮重新解析一次指针链（约 1 秒一次），用于对抗对象重建。</summary>
        private const int RelocateEveryTicks = 30;

        private readonly ICalibrationTarget _target;
        private readonly TrainerSettings _settings;
        private readonly List<CheatFeature> _features = new List<CheatFeature>();

        /// <summary>当前绑定的校准配置。无敌功能额外需要它里面的选中列表与血量偏移。</summary>
        private CalibrationProfile _profile;

        private Thread _thread;
        private ManualResetEvent _stop;
        private volatile bool _running;
        private long _ticks;

        /// <summary>字段解析成功/失败时触发，供界面刷新（可能来自后台线程）。</summary>
        public event Action Changed;

        public CheatEngine(ICalibrationTarget target, TrainerSettings settings)
        {
            if (target == null) { throw new ArgumentNullException("target"); }
            if (settings == null) { throw new ArgumentNullException("settings"); }

            _target = target;
            _settings = settings;
        }

        public IList<CheatFeature> Features { get { return _features; } }

        public bool IsRunning { get { return _running; } }

        public long Ticks { get { return Interlocked.Read(ref _ticks); } }

        /// <summary>当前是否有任一功能真的在生效。</summary>
        public bool HasActiveFeature
        {
            get
            {
                for (int i = 0; i < _features.Count; i++)
                {
                    if (_features[i].IsActive) { return true; }
                }
                return false;
            }
        }

        // ============================================================ 功能登记

        public CheatFeature Add(CheatFeature feature)
        {
            if (feature == null) { return null; }
            _features.Add(feature);
            return feature;
        }

        public CheatFeature Find(CheatId id)
        {
            for (int i = 0; i < _features.Count; i++)
            {
                if (_features[i].Id == id) { return _features[i]; }
            }
            return null;
        }

        /// <summary>把校准配置里的定位信息接到功能项上。每次校准完成后调用。</summary>
        public void BindProfile(CalibrationProfile profile)
        {
            if (profile == null) { return; }
            _profile = profile;

            for (int i = 0; i < _features.Count; i++)
            {
                CheatFeature feature = _features[i];
                feature.Locator = LocatorFor(profile, feature.Id);

                // 无敌功能要三件套齐全才算"可用"：选中数量 + 选中列表 + 血量偏移。
                // 缺任何一件都把它当作未校准 —— 否则开关能打开却什么都不做，
                // 玩家只会以为工具坏了（这正是 FeatureCard 注释里说的最恼人的失败模式）。
                if (feature.Id == CheatId.SelectedInvincible)
                {
                    if (feature.Locator != null && feature.Locator.IsCalibrated
                        && !HasInvinciblePrerequisites(profile))
                    {
                        Logger.Info("Engine", "选中单位无敌缺少必要条件（选中数量/选中列表/血量偏移），暂不可用");
                        feature.Locator = null;
                    }

                    // 注意：**不要**把校准测得的满血值当成默认锁定值。
                    // 那是"测某一个单位时测出来的"数字（比如动员兵 125），
                    // 而血量偏移是所有单位通用的、满血值却各不相同 ——
                    // 拿 125 去锁坦克，代码会判定"当前值高于目标，认错字段"而全部跳过，
                    // 表现就是"开了无敌但完全不生效"。
                    // 默认留 0 = 自动：按每个单位自己见过的最高血量锁（见 ApplySelectedInvincible）。
                }

                // 定位信息变了，之前解析出的地址立即作废，下一轮重新解析
                feature.Address = 0;
                feature.TicksSinceResolve = RelocateEveryTicks;

                if (!feature.IsCalibrated && feature.Enabled)
                {
                    feature.Enabled = false;
                    Logger.Warn("Engine", feature.Name + " 尚未校准，已自动关闭");
                }
            }
        }

        private static bool HasInvinciblePrerequisites(CalibrationProfile profile)
        {
            // 不再要求 TechnoMaxHealth：那个值现在只作为参考，真正的锁定值由运行时自己记。
            return profile.SelectedCount.IsCalibrated
                && profile.SelectedList.IsCalibrated
                && profile.OffsetTechnoHealth != 0;
        }

        private static FieldLocator LocatorFor(CalibrationProfile profile, CheatId id)
        {
            switch (id)
            {
                case CheatId.Money: return profile.Money;
                case CheatId.PowerOutput: return profile.PowerOutput;
                case CheatId.SelectedInvincible: return profile.SelectedList;
                case CheatId.KillEnemies: return profile.TechnoArray;
                case CheatId.InstantBuild: return null;
                case CheatId.RevealMap: return profile.MapShroud;
                default: return null;
            }
        }

        // ============================================================ 生命周期

        public void Start()
        {
            if (_running) { return; }

            _stop = new ManualResetEvent(false);
            _running = true;

            _thread = new Thread(new ThreadStart(Loop));
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.BelowNormal;   // 不与游戏抢 CPU
            _thread.Name = "MoTrainer-CheatEngine";
            _thread.Start();

            Logger.Info("Engine", "锁定线程已启动（30Hz）");
        }

        public void Stop()
        {
            if (!_running) { return; }

            _running = false;
            if (_stop != null) { _stop.Set(); }

            if (_thread != null)
            {
                try { _thread.Join(2000); }
                catch (Exception) { }
                _thread = null;
            }
            if (_stop != null)
            {
                _stop.Close();
                _stop = null;
            }

            Logger.Info("Engine", "锁定线程已停止，累计 " + Ticks + " 轮");
        }

        public void Dispose()
        {
            Stop();
        }

        // ============================================================ 主循环

        private void Loop()
        {
            while (!_stop.WaitOne(TickIntervalMs))
            {
                try
                {
                    Interlocked.Increment(ref _ticks);
                    ApplyAll();
                }
                catch (Exception ex)
                {
                    // 单轮异常绝不能让线程死掉，否则玩家会以为"功能在开但没效果"
                    Logger.Error("Engine", "锁定线程单轮异常", ex);
                }
            }
        }

        private void ApplyAll()
        {
            bool ready = _target != null && _target.IsReady;
            bool peekTick = (Interlocked.Read(ref _ticks) % RelocateEveryTicks) == 0;

            if (ready && !_readyPrev) { OnTargetReadyAgain(); }
            _readyPrev = ready;

            for (int i = 0; i < _features.Count; i++)
            {
                CheatFeature feature = _features[i];

                if (!feature.IsCalibrated)
                {
                    feature.Address = 0;
                    continue;
                }

                if (!ready)
                {
                    feature.Address = 0;
                    feature.LastError = "未连接游戏";
                    continue;
                }

                if (!feature.Enabled)
                {
                    // 关闭状态下也每秒探一次值。除了给界面显示"当前游戏里是多少"，
                    // 更重要的是让玩家在**开启之前**就能看到地址解析是否正常 ——
                    // 免得开了才发现没效果，还得猜是工具坏了还是地址错了。
                    if (peekTick)
                    {
                        if (feature.Id == CheatId.SelectedInvincible) { PeekSelection(feature); }
                        else if (feature.Id == CheatId.KillEnemies) { PeekTechnoArray(feature); }
                        else { PeekValue(feature); }
                    }

                    // 秒杀一旦关闭就把"已抽检"标记作废：下次开启会重新做一次安全抽检。
                    if (feature.Id == CheatId.KillEnemies && _killArmed)
                    {
                        _killArmed = false;
                        Logger.Info("Engine", "秒杀敌人已关闭：下次开启时会重新做一次安全抽检");
                    }
                    continue;
                }

                // 无敌不是"锁一个地址"，而是"遍历选中的每个对象逐一写血量"；
                // 秒杀敌人同理（整场对象遍历 + 批量写），两者都单独走专用路径，
                // 绝不复用单地址的读-比-写 —— 否则会拿"名单基地址"当单地址去写值。
                if (feature.Id == CheatId.SelectedInvincible) { ApplySelectedInvincible(feature); }
                else if (feature.Id == CheatId.KillEnemies) { ApplyKillEnemies(feature); }
                else { ApplyFeature(feature); }
            }
        }

        // ============================================================ 选中单位无敌

        /// <summary>解析"选中数量"与"选中列表数组指针存放位置"两个地址。</summary>
        private bool ResolveSelectionAddresses(out uint countAddress, out uint slotAddress)
        {
            countAddress = 0;
            slotAddress = 0;
            if (_profile == null) { return false; }

            string error;
            if (!_profile.SelectedCount.TryResolve(
                _target.Memory, _target.ModuleBase, out countAddress, out error))
            {
                return false;
            }
            if (!_profile.SelectedList.TryResolve(
                _target.Memory, _target.ModuleBase, out slotAddress, out error))
            {
                countAddress = 0;
                return false;
            }
            return true;
        }

        /// <summary>关闭状态下的只读探测：把"当前选中了几个"显示到界面上。</summary>
        private void PeekSelection(CheatFeature feature)
        {
            uint countAddress;
            uint slotAddress;
            if (!ResolveSelectionAddresses(out countAddress, out slotAddress))
            {
                feature.Address = 0;
                feature.LastError = "地址解析失败";
                return;
            }

            uint count;
            if (!_target.Memory.ReadUInt32(countAddress, out count))
            {
                feature.Address = 0;
                feature.LastError = "读取失败";
                return;
            }

            feature.Address = slotAddress;
            feature.LastSeenValue = count;
            if (IsResolutionError(feature.LastError)) { feature.LastError = ""; }
        }

        /// <summary>
        /// 遍历当前选中的每个对象，把血量写回满血值。
        ///
        /// 只在"当前血量低于满血"时才写：高于满血说明我们找错了字段或对象类型不对，
        /// 那时候贸然写下去可能把人家某个大数值字段压小，反而搞坏游戏状态。
        /// </summary>
        private void ApplySelectedInvincible(CheatFeature feature)
        {
            if (_profile == null)
            {
                feature.LastError = "缺少校准配置";
                return;
            }

            uint countAddress;
            uint slotAddress;
            if (!ResolveSelectionAddresses(out countAddress, out slotAddress))
            {
                feature.Address = 0;
                feature.LastError = "地址解析失败";
                return;
            }
            feature.Address = slotAddress;

            uint count;
            if (!_target.Memory.ReadUInt32(countAddress, out count))
            {
                feature.LastError = "读取失败";
                return;
            }
            feature.LastSeenValue = count;

            // **不要**在这里清空血量基准。
            //
            // 曾经想过"取消选中 = 一轮结束，清掉基准"，真机直接打脸：
            // 玩家要让单位挨打**必须先取消选中**，于是数量读到 0、基准被清；
            // 打完再选回来时，程序重新学到的"基准"是**已经被打掉的血量**（实测 125 → 17），
            // 于是锁在 17，单位再也回不去满血，继续挨打就死。
            // 这与早先"只在被选中时采样"是同一类错误：把清理动作加在了玩家不得不走的路径上。
            //
            // 保留基准反而是想要的效果：单位离队期间受的伤，选回来时会自动补满。
            if (count == 0) { feature.LastError = ""; return; }
            if (count > SelectionProbe.MaxCount)
            {
                feature.LastError = string.Format(CultureInfo.InvariantCulture,
                    "选中数量异常（读到 {0}），已跳过以免误写", count);
                feature.Address = 0;
                return;
            }

            // 选中列表数组：不再无条件相信落盘的槽位，而是"认对象"自己挑。
            // 见 ResolveSelectionArray 的注释 —— 配置里的槽位已被真机数据证明是错的。
            uint arrayAddress;
            string slotNote;
            if (!ResolveSelectionArray(countAddress, slotAddress, count, out arrayAddress, out slotNote))
            {
                feature.Address = 0;
                feature.LastError = "选中列表定位有误 —— " + slotNote;
                LogInvincibleNote(feature.LastError);
                return;
            }
            if (slotNote.Length > 0) { LogInvincibleNote(slotNote); }

            uint desiredOverride = feature.TargetValue;

            // 保护名单 = 当前选中的单位 ∪ **曾经选中过的**单位。
            //
            // 为什么必须并上后者：真机现象是"不选中时单位正常掉血，选中后回满血"——
            // 因为选中列表里只有"当前选中"的对象，而游戏机制又要求
            // **先取消选中才能让它挨打**（玩家多轮反馈反复确认）。
            // 于是单位恰好在我们最需要保护它的时候被排除在遍历之外。
            //
            // 这与 S5k（只在被选中时采样）、S5u（取消选中就清血量基准）是**同一类错误**：
            // 把关键动作挂在了"玩家不得不取消选中"这条必经路径上。第三次了，这次改成只增不减。
            // 语义（用户确认过的）：**开启那一刻选中的本方单位进入保护名单**。
            // 开启后才选中/新造的单位不自动无敌 —— 用户明确说"只选中 A1 进无敌，A2 死亡正是我想要的结果"。
            //
            // 名单只在"开启那一刻"建立：引擎开启期间每 33ms 调一次，
            // 两拍间隔超过 1 秒即视为刚开启（没有额外的开关回调可用）。
            bool freshStart = (DateTime.UtcNow - _lastInvincibleTick).TotalSeconds > 1.0;
            _lastInvincibleTick = DateTime.UtcNow;

            if (freshStart)
            {
                for (int i = 0; i < (int)count; i++)
                {
                    uint selectedObject;
                    if (!_target.Memory.ReadUInt32(arrayAddress + (uint)(i * 4), out selectedObject)) { break; }
                    if (selectedObject < 0x10000) { continue; }
                    if (!IsOwnedObject(selectedObject)) { continue; }

                    uint healthAddress = selectedObject + _profile.OffsetTechnoHealth;
                    uint healthNow;
                    if (!_target.Memory.ReadUInt32(healthAddress, out healthNow) || healthNow == 0) { continue; }

                    uint existing;
                    if (_healthPeak.TryGetValue(selectedObject, out existing) && existing >= healthNow) { continue; }

                    _healthPeak[selectedObject] = healthNow;
                    Logger.Info("Engine", string.Format(CultureInfo.InvariantCulture,
                        "选中单位无敌：保护 0x{0:X8}，基准血量 {1}", selectedObject, healthNow));
                }
            }

            List<uint> targets = new List<uint>();
            foreach (uint protectedAddress in _healthPeak.Keys)
            {
                if (!ContainsAddress(targets, protectedAddress)) { targets.Add(protectedAddress); }
            }



            int written = 0;
            int skipped = 0;
            int notOwned = 0;
            StringBuilder detail = new StringBuilder();   // 逐单位明细，供每 5 秒一条的日志用
            for (int i = 0; i < targets.Count; i++)
            {
                uint objectAddress = targets[i];

                // 归属校验：只对本方单位动手。
                //
                // 为什么必须有这一条：实测「选中数量」字段附近存在**多个**候选数组槽位，
                // 其中只有一部分指向本方单位（归属校验实测 1/2）。而落盘的槽位是按
                // DynamicVectorClass 布局推出来的"数量字段 − 8"，**这一推断已被真机数据证伪**。
                // 若数组找错，就会往完全无关的对象 +0x6C 处写值 —— 那是纯粹的破坏。
                // 判据：对象里有没有指针指向"持有你金钱的那个对象"（全局只有 House 持有金钱），
                // 不依赖任何硬编码偏移。
                if (!IsOwnedObject(objectAddress))
                {
                    // 归属校验不通过 → 从保护名单里剔除。
                    // 必须剔除而不是"跳过这一拍"：否则一个误判进来的敌方单位会一直留在名单里，
                    // 只要校验结果抖动一次就会重新被保护（用户实测：敌方单位突然满血并进入无敌）。
                    notOwned++;
                    _healthPeak.Remove(objectAddress);
                    continue;
                }

                uint fieldAddress = objectAddress + _profile.OffsetTechnoHealth;

                // 安全检查：血量字段必须落在可写页上。
                // 对象可能刚好在这一帧被销毁，指针就变成了野指针，直接写会破坏别的对象。
                MemoryRegion region = _target.Memory.Describe(fieldAddress);
                if (region == null || !region.Writable) { skipped++; continue; }

                uint current;
                if (!_target.Memory.ReadUInt32(fieldAddress, out current)) { skipped++; continue; }

                // 血量为 0 = 单位已死。绝不回写。
                // 真机日志里出现过 `0x…(血量0/锁119 写入)` —— 对已销毁对象写值，
                // 而那块内存很可能已经被别的对象复用，等于在改无关数据。
                if (current == 0)
                {
                    skipped++;
                    _healthPeak.Remove(objectAddress);   // 已死的对象不必再留
                    continue;
                }

                // 锁定值 = 玩家在界面上指定的值；没指定就用**这个单位自己见过的最高血量**。
                //
                // 为什么不用配置里那个满血值：那是测某个单位时测出来的（比如动员兵 125），
                // 而血量偏移通用、满血值却因单位而异。拿 125 去锁一辆 400 血的坦克，
                // 会被下面"绝不往下压"的安全判断直接跳过 —— 表现为"开了无敌完全没用"。
                //
                // 改成记录峰值后：只要在满血时开过无敌，峰值就是它的真实满血值，
                // 之后它再也掉不下去。任何单位类型都自动适用，不需要逐类型校准。
                // 基准取"见过的最高血量，只升不降"。
                // 只升不降是为了防住一个实测过的坑：单位带伤时被重复取快照，
                // 会把原本记对的满血值（125）覆盖成当时的伤血值（49），
                // 于是锁在 49、照样被打死。取最大值就不会出现这种"越记越低"。
                uint peak;
                if (!_healthPeak.TryGetValue(objectAddress, out peak) || peak == 0) { skipped++; continue; }
                if (current > peak)
                {
                    peak = current;
                    _healthPeak[objectAddress] = peak;
                }

                uint desired = desiredOverride > 0 ? desiredOverride : peak;
                if (desired == 0) { desired = 1; }

                if (detail.Length < 300)
                {
                    if (detail.Length > 0) { detail.Append("　"); }
                    detail.Append(string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8}(血量{1}/锁{2}{3})", objectAddress, current, desired,
                        current == desired ? " 无需写" : (current > desired ? " 跳过(高于锁定值)" : " 写入")));
                }

                if (current == desired) { continue; }
                if (current > desired) { skipped++; continue; }   // 高于锁定值 → 认错字段了，绝不往下压

                if (_target.Memory.WriteUInt32(fieldAddress, desired)) { written++; }

                // 顺带锁相邻的 +0x70。
                // 实测它与 +0x6C **同步变化**（候选列表里 125 → 0 一起走），
                // 很可能是"当前血量 / 估算血量"这类成对存在的字段。
                // 只锁一个的话，若游戏读的是另一个，就会出现"写进去了、单位却照旧掉血"。
                // 加 `mirrorCurrent > 0` 是为了不复活已死单位（镜像字段归零通常意味着对象已销毁）。
                if (_profile.OffsetTechnoHealth == 0x6C)
                {
                    uint mirrorAddress = fieldAddress + 4;
                    uint mirrorCurrent;
                    if (_target.Memory.ReadUInt32(mirrorAddress, out mirrorCurrent)
                        && mirrorCurrent > 0 && mirrorCurrent < desired)
                    {
                        _target.Memory.WriteUInt32(mirrorAddress, desired);
                    }
                }
            }

            if (written > 0) { feature.WriteCount += written; }

            // 全部被归属校验挡下 —— 这是"选中列表找错了"最直接的证据，必须报出来
            if (notOwned > 0 && written == 0 && skipped == 0)
            {
                feature.LastError = string.Format(CultureInfo.InvariantCulture,
                    "选中的 {0} 个都不是本方单位（选中列表槽位找错了），已全部跳过", notOwned);
            }
            else if (skipped > 0 && written == 0)
            {
                feature.LastError = string.Format(CultureInfo.InvariantCulture,
                    "有 {0} 个对象无法写入（可能血量偏移不对）", skipped);
            }

            // 结果写进日志：这个功能每 33ms 跑一次，不能每帧都记。
            // 用"状态变化记一行 + 每 5 秒一条心跳"两档 ——
            // 心跳是必需的：否则"到底有没有在写"这件事在日志里完全没有痕迹
            // （上一版就是这样，导出的日志里看不出是否写入过）。
            // 每 5 秒一条明细：把"选中几个、数组在哪、每个单位当前血量/锁定值/做了什么"
            // 全部摊开。前几轮反复卡在"看不到写入路径到底有没有跑"，这一条能一次问清。
            if ((DateTime.UtcNow - _lastInvincibleWriteLog).TotalSeconds >= 5.0)
            {
                _lastInvincibleWriteLog = DateTime.UtcNow;
                uint nowMoney;
                bool ownershipOk = TryReadMoney(out nowMoney);

                Logger.Info("Engine", string.Format(CultureInfo.InvariantCulture,
                    "选中单位无敌：选中 {0}｜数组 0x{1:X8}｜本次写入 {2} 跳过 {3} 非本方 {4}｜累计写入 {5}"
                    + "｜归属判据 {6}｜明细 {7}",
                    count, arrayAddress, written, skipped, notOwned, feature.WriteCount,
                    ownershipOk
                        ? string.Format(CultureInfo.InvariantCulture, "可用（金钱 {0}，比对偏移 +0x{1:X}）", nowMoney, HouseMoneyOffset)
                        : "**不可用**（未过滤，随时可能误伤）",
                    detail.Length == 0 ? "(空)" : detail.ToString()));
            }

            if (written > 0)
            {
                feature.LastError = "";
                LogInvincibleNote("");
            }
            else if (feature.LastError.Length > 0)
            {
                LogInvincibleNote(feature.LastError);
            }
            else if (feature.LastError.Length != 0 && IsResolutionError(feature.LastError))
            {
                feature.LastError = "";
            }
        }

        // ============================================================ 秒杀敌人（S6-1）

        /// <summary>
        /// 秒杀敌人：整场对象遍历 → 把"不属于我方"的对象血量清零。
        ///
        /// 与无敌的关键区别：无敌写的是"玩家选中的那几个对象"（数量个位数），
        /// 秒杀写的是**场上所有敌方对象**（真机规模上千个）——所以每一步都必须先假设
        /// "判据可能是错的"，任何一关不过就**一个字节都不写**（fail-closed）：
        ///   ① 名单先过信任关（`IsTrustworthy`）；
        ///   ② "我方是谁"必须**金钱锚定**（`TechnoOwnership`），不接受人工试出的偏移；
        ///   ③ 开启时做一次安全抽检（用独立判据复核分类结果），通过才允许写；
        ///   ④ 每拍先自己数一遍：我方对象一个都没数到 = 锚点已失效，拒绝执行。
        /// </summary>
        private void ApplyKillEnemies(CheatFeature feature)
        {
            // 节流：整场遍历 + 几千次读取，按 30Hz 跑会把游戏帧率拖垮。
            // 0.5 秒一拍：按真机规模（敌方一千上下）约 2 秒清完一轮，符合"3 秒内归零"。
            if ((DateTime.UtcNow - _lastKillRun).TotalSeconds < KillIntervalSeconds) { return; }
            _lastKillRun = DateTime.UtcNow;

            if (_profile == null) { feature.LastError = "缺少校准配置"; return; }
            if (_profile.OffsetTechnoHealth == 0)
            {
                feature.LastError = "未校准「对象血量」偏移，无法执行";
                return;
            }

            // ---- ① 名单 ----
            uint slotAddress;
            uint arrayAddress;
            uint count;
            string reason;
            if (!TryResolveTechnoArray(out slotAddress, out arrayAddress, out count, out reason))
            {
                feature.Address = 0;
                feature.LastError = reason;
                LogKillNote(reason);
                return;
            }
            feature.Address = slotAddress;

            TechnoWalkResult walk = TechnoWalker.WalkArray(
                _target.Memory, arrayAddress, count, _target.ModuleBase, _target.ModuleSize);
            if (!TechnoOps.IsTrustworthy(walk, out reason))
            {
                feature.Address = 0;
                feature.LastError = "对象名单不可信（" + reason + "），已拒绝执行";
                LogKillNote(feature.LastError);
                return;
            }

            // ---- ② 我方锚点（金钱锚定）----
            if (!EnsureOwnerAnchor(out reason))
            {
                feature.LastError = reason;
                LogKillNote(reason);
                return;
            }

            // ---- 每拍自查：把全场先数一遍 ----
            int mine = 0;
            int enemy = 0;
            int unknown = 0;
            for (int i = 0; i < walk.Objects.Count; i++)
            {
                uint owner;
                if (!_target.Memory.ReadUInt32(walk.Objects[i].Address + _myOwnerOffset, out owner))
                {
                    unknown++;
                    continue;
                }
                if (owner == 0) { unknown++; }
                else if (owner == _myOwner) { mine++; }
                else { enemy++; }
            }

            // 我方一个都没有 = 危险信号：锚点若是失效（比如换了一局），
            // 全场都会判成敌方 —— 包括自己的部队。必须拒绝，而不是照写。
            if (mine == 0)
            {
                _killArmed = false;
                feature.Address = 0;
                feature.LastError = "名单里没有任何一个对象属于「我方」——敌我锚点可能已失效，"
                    + "已拒绝执行（请重新选中自己的单位，或关掉再开一次此功能）";
                LogKillNote(feature.LastError);
                return;
            }

            // ---- ③ 安全抽检（开启时 / 锚点重建后各做一次；失败则退避重试）----
            if (!_killArmed && (DateTime.UtcNow - _lastKillVerify).TotalSeconds >= KillVerifyRetrySeconds)
            {
                _lastKillVerify = DateTime.UtcNow;

                int enemySeen;
                int checkedCount;
                uint suspect;
                string why;
                if (!TechnoOwnership.VerifyEnemySample(
                        _target.Memory, walk.Objects, _myOwnerOffset, _myOwner,
                        KillVerifySamples, out enemySeen, out checkedCount, out suspect, out why))
                {
                    feature.LastError = "安全抽检未通过，已拒绝执行：" + why;
                    LogKillNote(feature.LastError);
                    return;
                }

                _killArmed = true;
                Logger.Info("Engine", string.Format(CultureInfo.InvariantCulture,
                    "秒杀敌人：安全抽检通过（复核 {0}/{1} 个判为敌方的对象，均未连着我方 House）"
                    + "｜阵营字段 +0x{2:X} = 0x{3:X8}（金钱锚定，样本 {4} 个）",
                    checkedCount, enemySeen, _myOwnerOffset, _myOwner, _ownerAnchorSamples));
            }
            else if (!_killArmed)
            {
                return;   // 抽检未通过、还在退避期：不写
            }

            // ---- ④ 执行 ----
            TechnoOpResult result = TechnoOps.KillEnemies(
                _target.Memory, walk.Objects, _myOwnerOffset, _profile.OffsetTechnoHealth, _myOwner);

            if (result.Written > 0) { feature.WriteCount += result.Written; }
            feature.LastSeenValue = (uint)enemy;

            if (result.Blocked.Length > 0)
            {
                feature.Address = 0;
                feature.LastError = result.Blocked;
                LogKillNote(result.Blocked);
                return;
            }

            feature.LastError = result.Failed > 0
                ? string.Format(CultureInfo.InvariantCulture, "有 {0} 个目标写入失败（已跳过）", result.Failed)
                : "";
            LogKillNote(feature.LastError);

            // 心跳：每 5 秒一条。"到底有没有在写"在日志里必须看得见（与无敌同一套路数）。
            if ((DateTime.UtcNow - _lastKillLog).TotalSeconds >= 5.0)
            {
                _lastKillLog = DateTime.UtcNow;
                Logger.Info("Engine", string.Format(CultureInfo.InvariantCulture,
                    "秒杀敌人：名单 {0} 个｜我方 {1}｜敌方 {2}｜归属不明 {3}｜本拍写入 {4} 跳过 {5}"
                    + "｜累计写入 {6}｜阵营字段 +0x{7:X}=0x{8:X8}（金钱锚定）",
                    walk.Count, mine, enemy, unknown, result.Written, result.Skipped,
                    feature.WriteCount, _myOwnerOffset, _myOwner));
            }
        }

        /// <summary>
        /// 解析「全对象名单」：格子（静态地址）→ 读出数组指针 → 数量。
        /// 失败时 reason 是**给玩家看的话**（说明缺什么、去点哪个按钮）。
        /// </summary>
        private bool TryResolveTechnoArray(
            out uint slotAddress, out uint arrayAddress, out uint count, out string reason)
        {
            slotAddress = 0;
            arrayAddress = 0;
            count = 0;
            reason = "";

            if (_profile == null || !_profile.TechnoArray.IsCalibrated)
            {
                reason = "还没有校准「全对象名单」——请先到「高级功能」页点一次「自动查找总名单」";
                return false;
            }

            string error;
            if (!_profile.TechnoArray.TryResolve(_target.Memory, _target.ModuleBase, out slotAddress, out error))
            {
                reason = "名单位置解析失败：" + error;
                return false;
            }

            uint pointer;
            if (!_target.Memory.ReadUInt32(slotAddress, out pointer)
                || pointer < 0x10000 || pointer > 0x7FFFFFFF)
            {
                reason = "名单里读不到有效指针（跨会话失效？请重新点一次「自动查找总名单」）";
                return false;
            }
            arrayAddress = pointer;

            // 数量：优先用配置里的数量字段；读不到或明显不合理时按上限探测
            //（真机教训：这类容器的"数量"字段有时是容量；实际多少个由遍历器自己数）。
            count = 4096;
            if (_profile.TechnoArrayCount.IsCalibrated)
            {
                uint countAddress;
                if (_profile.TechnoArrayCount.TryResolve(_target.Memory, _target.ModuleBase, out countAddress, out error))
                {
                    uint value;
                    if (_target.Memory.ReadUInt32(countAddress, out value) && value >= 64)
                    {
                        count = value > TechnoWalker.MaxObjects ? TechnoWalker.MaxObjects : value;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 读出"当前选中对象"的地址列表 —— 建立我方锚点的样本来源。
        /// 复用无敌那套"多布局逐一尝试 + 归属自动更正"的解析，只取前几个。
        /// </summary>
        private List<uint> ReadSelectedObjectList()
        {
            List<uint> result = new List<uint>();

            uint countAddress;
            uint slotAddress;
            if (!ResolveSelectionAddresses(out countAddress, out slotAddress)) { return result; }

            uint count;
            if (!_target.Memory.ReadUInt32(countAddress, out count) || count == 0) { return result; }
            if (count > SelectionProbe.MaxCount) { return result; }

            uint arrayAddress;
            string note;
            if (!ResolveSelectionArray(countAddress, slotAddress, count, out arrayAddress, out note))
            {
                return result;
            }

            int limit = count < (uint)TechnoOwnership.MaxAnchorSamples
                ? (int)count : TechnoOwnership.MaxAnchorSamples;
            for (int i = 0; i < limit; i++)
            {
                uint objectAddress;
                if (!_target.Memory.ReadUInt32(arrayAddress + (uint)(i * 4), out objectAddress)) { break; }
                if (objectAddress >= 0x10000) { result.Add(objectAddress); }
            }
            return result;
        }

        /// <summary>
        /// 确保"我方锚点"（阵营字段偏移 + 我方 House 地址，金钱锚定）可用。
        ///
        /// 建立一次缓存 30 秒；到点且游戏里有选中单位时重建 —— 重建会把"已抽检"作废，
        /// 下一拍重新抽检（这样锚点在整局里被持续复核，而不是只在开启那一刻看一次）。
        /// 读不出金钱或建不出锚点时**拒绝执行**，绝不退回"人工试出的偏移"。
        /// </summary>
        private bool EnsureOwnerAnchor(out string reason)
        {
            reason = "";
            bool haveAnchor = _myOwner != 0 && _myOwnerOffset != 0;
            bool stale = (DateTime.UtcNow - _myOwnerStamp).TotalSeconds > OwnerAnchorRefreshSeconds;
            if (haveAnchor && !stale) { return true; }

            uint money;
            if (!TryReadMoney(out money))
            {
                if (haveAnchor)
                {
                    // 锚点还在，金钱只是暂时读不到（不推翻已有结论）
                    _myOwnerStamp = DateTime.UtcNow;
                    return true;
                }
                reason = "读不到金钱值（敌我锚点靠它判定）——请确认金钱仍处于已校准状态";
                return false;
            }

            List<uint> selected = ReadSelectedObjectList();
            if (selected.Count > 0)
            {
                TechnoOwnership.OwnerAnchor anchor;
                string why;
                if (TechnoOwnership.TryBuildAnchor(_target.Memory, selected, money, out anchor, out why))
                {
                    bool changed = _myOwner != anchor.MyHouse || _myOwnerOffset != anchor.OwnerOffset;
                    _myOwner = anchor.MyHouse;
                    _myOwnerOffset = anchor.OwnerOffset;
                    _ownerAnchorSamples = anchor.Samples;
                    _myOwnerStamp = DateTime.UtcNow;

                    // 锚点（可能）更新过 → 上一轮的抽检结论作废，下一拍重新抽检
                    _killArmed = false;
                    _lastKillVerify = DateTime.MinValue;

                    if (changed)
                    {
                        Logger.Info("Engine", string.Format(CultureInfo.InvariantCulture,
                            "秒杀敌人：我方锚点已建立 —— 阵营字段 +0x{0:X} = 0x{1:X8}"
                            + "（金钱 0x{2:X8} 锚定，样本 {3} 个{4}）",
                            _myOwnerOffset, _myOwner, money, anchor.Samples,
                            anchor.OtherOffsets.Count > 0
                                ? string.Format(CultureInfo.InvariantCulture,
                                    "；另有 {0} 个同样命中的偏移，已取最小", anchor.OtherOffsets.Count)
                                : ""));
                    }
                    return true;
                }

                if (haveAnchor)
                {
                    // 这次建不出来（比如选中的不是自己单位），但旧锚点还没过期 —— 沿用旧的
                    _myOwnerStamp = DateTime.UtcNow;
                    return true;
                }
                reason = "无法建立我方锚点：" + why;
                return false;
            }

            if (haveAnchor)
            {
                _myOwnerStamp = DateTime.UtcNow;   // 没有选中对象 → 沿用现有锚点
                return true;
            }

            reason = "请先在游戏里选中一个自己的单位（程序要靠它确认「我方」是谁），然后再开启此功能";
            return false;
        }

        /// <summary>
        /// 关闭状态下的只读探测：轻量检查名单是否还能读（格子 → 指针 → 首项是不是真对象），
        /// 并把"名单里登记了多少个对象"显示到卡片上。
        ///
        /// 刻意**不做整场遍历**：这一拍每秒一次，遍历要几千次读取；完整的名单健康检查
        /// 由「扫描全对象数组」按钮和开启后的实际执行来做。
        /// </summary>
        private void PeekTechnoArray(CheatFeature feature)
        {
            uint slotAddress;
            uint arrayAddress;
            uint count;
            string reason;
            if (!TryResolveTechnoArray(out slotAddress, out arrayAddress, out count, out reason))
            {
                feature.Address = 0;
                feature.LastError = reason;
                return;
            }

            // 轻量健康检查：数组第一项的头 4 字节必须是模块区间内的类型标记
            uint first;
            uint vtable;
            if (!_target.Memory.ReadUInt32(arrayAddress, out first) || first < 0x10000
                || !_target.Memory.ReadUInt32(first, out vtable)
                || vtable < _target.ModuleBase || vtable >= _target.ModuleBase + _target.ModuleSize)
            {
                feature.Address = 0;
                feature.LastError = "名单内容异常（首项不是游戏对象）——可能需要重新查找总名单";
                return;
            }

            feature.Address = slotAddress;
            feature.LastSeenValue = count;
            if (feature.LastError.Length > 0) { feature.LastError = ""; }
        }

        /// <summary>
        /// 把秒杀的结论写进日志，但只在**结论变化**时记一行
        /// （0.5 秒一拍，不这么做会把日志刷爆；完全不记又会让"没生效"变成悬案）。
        /// </summary>
        private void LogKillNote(string note)
        {
            if (note == null) { note = ""; }
            if (note == _lastKillNote) { return; }

            bool first = _lastKillNote == "(未开始)";
            bool wasFailing = _lastKillNote.Length > 0;
            _lastKillNote = note;

            if (note.Length == 0)
            {
                Logger.Info("Engine", (wasFailing && !first) ? "秒杀敌人：已恢复正常执行" : "秒杀敌人：已开始执行");
            }
            else
            {
                Logger.Warn("Engine", "秒杀敌人：" + note);
            }
        }

        // ============================================================ 归属校验

        private readonly Dictionary<uint, bool> _ownedCache = new Dictionary<uint, bool>();
        private DateTime _ownedCacheStamp = DateTime.MinValue;

        /// <summary>
        /// 单位对象 → 见过的最高血量（也就是它的满血值）。
        /// 无敌的锁定值取这个，而不是配置里那个"某个单位的满血值" —— 见 ApplySelectedInvincible。
        /// </summary>
        private readonly Dictionary<uint, uint> _healthPeak = new Dictionary<uint, uint>();

        private uint _resolvedSlot;
        private uint _lastCorrectedSlot;
        private DateTime _resolvedSlotStamp = DateTime.MinValue;
        /// <summary>
        /// 上一次记下的无敌结论。初值故意不是空串 ——
        /// 否则"第一次成功"会被当成"没变化"而不记日志（上一版就吃了这个亏：
        /// 导出的日志里看不出到底有没有写入过）。
        /// </summary>
        private string _lastInvincibleNote = "(未开始)";

        private DateTime _lastInvincibleWriteLog = DateTime.MinValue;
        private bool _ownershipWarned;
        private DateTime _lastInvincibleTick = DateTime.MinValue;

        // ---------------- 秒杀敌人（S6-1）的运行时状态 ----------------

        /// <summary>上次执行秒杀的时刻。整场遍历 + 上千次读取太重，不能按 30Hz 跑。</summary>
        private DateTime _lastKillRun = DateTime.MinValue;

        /// <summary>上次秒杀心跳日志的时刻。</summary>
        private DateTime _lastKillLog = DateTime.MinValue;

        /// <summary>上一条已记录的秒杀结论（去重，避免每拍刷日志）。初值故意非空，让第一条也能记上。</summary>
        private string _lastKillNote = "(未开始)";

        /// <summary>本次开启是否已通过"安全抽检"。关闭 / 换局 / 锚点重建都会作废。</summary>
        private bool _killArmed;

        /// <summary>上次安全抽检的时刻（抽检失败时按退避间隔重试，不至于每拍都重扫）。</summary>
        private DateTime _lastKillVerify = DateTime.MinValue;

        /// <summary>我方锚点：阵营字段偏移 + 我方 House 地址（金钱锚定，见 TechnoOwnership）。</summary>
        private uint _myOwner;
        private uint _myOwnerOffset;
        private int _ownerAnchorSamples;
        private DateTime _myOwnerStamp = DateTime.MinValue;

        /// <summary>上一拍的"游戏是否已连接"，用于识别（重新）连接并作废会话级缓存。</summary>
        private bool _readyPrev;

        /// <summary>秒杀的执行节拍：0.5 秒一拍（按真机规模约 2 秒清完一轮，符合"3 秒内归零"）。</summary>
        private const double KillIntervalSeconds = 0.5;

        /// <summary>开启时安全抽检复核多少个"判为敌方"的对象。</summary>
        private const int KillVerifySamples = 128;

        /// <summary>抽检失败后的重试间隔（秒）。</summary>
        private const double KillVerifyRetrySeconds = 10.0;

        /// <summary>我方锚点的缓存时长（秒）；到点且游戏里有选中单位时重新锚定并重新抽检。</summary>
        private const double OwnerAnchorRefreshSeconds = 30.0;

        /// <summary>
        /// 游戏（重新）连接上时清空所有**会话级**缓存。
        ///
        /// 为什么必须清：这些缓存里存的是"上一局内存里的地址"（保护名单的血量基准、
        /// 我方 House 地址……）。游戏关掉再开，堆的布局全变了，旧地址会落到**别的对象**上 ——
        /// 对无敌来说是"给无关对象写血量"，对秒杀来说是"把所有人（含自己）都判成敌人"。
        /// 这是跨会话最危险的一类 bug，宁可清掉重学，也不能赌地址没被复用。
        /// </summary>
        private void OnTargetReadyAgain()
        {
            bool hadState = _myOwner != 0 || _healthPeak.Count > 0 || _ownedCache.Count > 0;
            _myOwner = 0;
            _myOwnerOffset = 0;
            _ownerAnchorSamples = 0;
            _myOwnerStamp = DateTime.MinValue;
            _killArmed = false;
            _lastKillVerify = DateTime.MinValue;
            _lastKillRun = DateTime.MinValue;
            _healthPeak.Clear();
            _ownedCache.Clear();
            _resolvedSlot = 0;

            if (hadState)
            {
                Logger.Info("Engine", "游戏（重新）连接：已作废上一局的会话级缓存（保护名单 / 我方锚点），避免拿旧地址误写");
            }
        }

        /// <summary>自动更正过的槽位缓存多久（避免每帧重扫附近所有槽位）。</summary>
        private const double SlotCacheSeconds = 5.0;

        /// <summary>
        /// 金钱字段在 HouseClass 内的偏移。真机三次会话一致（见 docs/05-校准与测试手册）。
        /// 归属校验靠它把"金钱正好在这个偏移上"作为判据 —— 比"块里哪里有这个数"硬得多。
        /// 定义在 `TechnoOwnership` 里（秒杀的敌我锚定用的是同一条判据），这里只是别名。
        /// </summary>
        private const uint HouseMoneyOffset = TechnoOwnership.HouseMoneyOffset;

        /// <summary>
        /// 找出"真正指向本单位"的选中数组。
        ///
        /// 为什么不能直接用落盘的槽位：真机数据已经两次打脸 ——
        /// 配置里的槽位（数量字段 − 8）**根本不在**探测器找到的合格槽位里
        /// （实测合格的是 `数量字段 − 0x0C`，符合 `VectorClass{Items, Capacity, IsInitialized, Count}`）。
        /// 与其第三次押注某个字节位置，不如让它自己认：
        /// **谁指向的对象里有本方单位，谁才是选中列表。**
        ///
        /// 顺序：配置槽位 → 上次自动更正过的槽位（缓存 5 秒）→ 数量字段 ±0x40 内逐个试。
        /// </summary>
        private bool ResolveSelectionArray(
            uint countAddress, uint slotAddress, uint count, out uint arrayAddress, out string note)
        {
            arrayAddress = 0;
            note = "";

            uint configured;
            bool configuredValid = _target.Memory.ReadUInt32(slotAddress, out configured) && configured >= 0x10000;

            // 归属判据不可用时（金钱没校准）不做自动更正，避免误判成"全都不是本单位"
            if (!OwnershipCheckAvailable())
            {
                if (!configuredValid) { return false; }
                arrayAddress = configured;
                return true;
            }

            int ignored;
            if (configuredValid && ArrayHasOwnedObject(configured, count, out ignored))
            {
                arrayAddress = configured;
                _resolvedSlot = slotAddress;
                _resolvedSlotStamp = DateTime.UtcNow;
                return true;
            }

            if (_resolvedSlot != 0
                && (DateTime.UtcNow - _resolvedSlotStamp).TotalSeconds < SlotCacheSeconds)
            {
                uint cached;
                if (_target.Memory.ReadUInt32(_resolvedSlot, out cached) && cached >= 0x10000
                    && ArrayHasOwnedObject(cached, count, out ignored))
                {
                    arrayAddress = cached;
                    return true;
                }
            }

            uint start = countAddress > 0x40 ? countAddress - 0x40 : 0;
            uint end = countAddress + 0x40;
            int triedSlots = 0;
            int examined = 0;
            for (uint slot = start; slot <= end; slot += 4)
            {
                if (slot == slotAddress) { continue; }

                uint candidate;
                if (!_target.Memory.ReadUInt32(slot, out candidate)) { continue; }
                if (candidate < 0x10000) { continue; }
                triedSlots++;

                int count2;
                if (!ArrayHasOwnedObject(candidate, count, out count2)) { examined += count2; continue; }
                examined += count2;

                arrayAddress = candidate;
                _resolvedSlot = slot;
                _resolvedSlotStamp = DateTime.UtcNow;

                // 只在**槽位真的变了**时报一次。
                // 否则会和"写入成功"那条日志来回改结论，导致每 5 秒重复刷一遍同样的话。
                if (slot != _lastCorrectedSlot)
                {
                    _lastCorrectedSlot = slot;
                    note = string.Format(CultureInfo.InvariantCulture,
                        "选中列表槽位自动更正：0x{0:X8} → 0x{1:X8}（配置里的槽位指向的不是本方单位，已按归属自动改用正确的那个）",
                        slotAddress, slot);
                }
                return true;
            }

            // 失败时必须说清"试了多少、都没有" —— 否则日志里只剩一句"没找到"，照样得靠猜。
            note = string.Format(CultureInfo.InvariantCulture,
                "配置槽位 0x{0:X8}{1}；选中数量读到 {2}；数量字段 0x{3:X8} ±0x40 内试了 {4} 个候选数组、共检查 {5} 个对象，没有一个归属本方",
                slotAddress, configuredValid ? "" : "（指针无效）", count, countAddress, triedSlots, examined);
            return false;
        }

        /// <summary>
        /// 数组里有没有本方单位。
        ///
        /// **只查第一个元素**：选中列表的第一项必然是本方单位。
        /// 每个"对象"的归属校验最多要试几百条指针，而自动更正要在 ±0x40 内试三十几个槽位 ——
        /// 挨个查 8 个元素会让首次探测卡住近一秒。查第一个既准又快。
        /// examined 回报实际检查了几个对象，用于失败时给出可追查的数字。
        /// </summary>
        private static bool ContainsAddress(List<uint> list, uint value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value) { return true; }
            }
            return false;
        }

        private bool ArrayHasOwnedObject(uint arrayAddress, uint count, out int examined)
        {
            examined = 0;
            if (count == 0) { return false; }

            // 查前几个而不是只看第一个：真机日志里同一次会话内这条判定**两次结果不同**
            // （自动更正被触发了两次），说明数组的第 0 项并不可靠
            // （可能混着"当前操作目标"这类非单位引用）。
            int limit = count < 4 ? (int)count : 4;
            for (int i = 0; i < limit; i++)
            {
                // 安静读取：候选数组地址可能是野指针，普通读取会往日志里刷失败记录
                byte[] raw = _target.Memory.ReadBytesQuiet(arrayAddress + (uint)(i * 4), 4);
                if (raw == null) { return false; }
                uint objectAddress = HexUtil.ReadUInt32(raw, 0);
                if (objectAddress < 0x10000) { continue; }

                examined++;
                if (IsOwnedObject(objectAddress)) { return true; }
            }
            return false;
        }

        /// <summary>归属判据是否可用（金钱已校准且能读到）。</summary>
        private bool OwnershipCheckAvailable()
        {
            uint money;
            return TryReadMoney(out money);
        }

        /// <summary>读当前金钱值（归属校验的唯一外部依据）。读不到就说明判据不可用。</summary>
        private bool TryReadMoney(out uint money)
        {
            money = 0;
            if (_profile == null || !_profile.Money.IsCalibrated) { return false; }

            uint moneyAddress;
            string error;
            if (!_profile.Money.TryResolve(
                _target.Memory, _target.ModuleBase, out moneyAddress, out error)) { return false; }

            return _target.Memory.ReadUInt32(moneyAddress, out money) && money != 0;
        }

        /// <summary>
        /// 归属判据不可用时记一行（去重）。
        /// 必须留下痕迹：否则"无敌不生效"会变成又一场悬案 ——
        /// 上一轮 B1 被误保护，根因就是这个兜底悄悄放行了所有单位，而日志里毫无线索。
        /// </summary>
        private void LogOwnershipUnavailable()
        {
            if (_ownershipWarned) { return; }
            _ownershipWarned = true;
            Logger.Warn("Engine", "选中单位无敌：归属校验不可用（金钱字段读不到），"
                + "为避免误伤已暂停对本方单位的判定 —— 请确认金钱仍处于已校准状态");
        }

        /// <summary>
        /// 把无敌功能的结论写进日志，但只在**结论变化**时记一行。
        /// 否则 30Hz 会把日志刷爆；而完全不记又会让"没生效"变成一桩悬案
        /// —— 上一版就是这样：界面上有错误、日志里什么都没有，只能靠截图。
        /// </summary>
        private void LogInvincibleNote(string note)
        {
            if (note == null) { note = ""; }
            if (note == _lastInvincibleNote) { return; }

            bool wasFailing = _lastInvincibleNote.Length > 0;
            _lastInvincibleNote = note;

            if (note.Length == 0)
            {
                Logger.Info("Engine", wasFailing ? "选中单位无敌：已恢复正常锁定" : "选中单位无敌：已开始锁定血量");
            }
            else
            {
                Logger.Warn("Engine", "选中单位无敌：" + note);
            }
        }

        /// <summary>归属校验结果的缓存有效期：太短会卡帧，太长则新造出来的单位要等一会儿才生效。</summary>
        private const double OwnedCacheSeconds = 3.0;

        /// <summary>
        /// 对象是否归属本方。判据：对象里有没有指针指向"持有你金钱的那个对象"。
        ///
        /// 单位对象必然带一个 Owner 指针指向自己的 HouseClass，而全局只有 House 持有金钱 ——
        /// 所以"在对象里找到一条指针，它指向的内存里出现了当前金钱值"就等价于"这个对象属于你"。
        /// 全程不硬编码 OffsetTechnoOwner（那个值我们其实还没校准）。
        ///
        /// 结果带缓存：每个对象要读几十次小内存，30Hz 下不可能每帧重算。
        /// 拿不到判据时（金钱未校准等）返回 true，即"不拦" —— 不能因为判不了就不干活。
        /// </summary>
        private bool IsOwnedObject(uint objectAddress)
        {
            uint money;
            if (!TryReadMoney(out money))
            {
                // 判不了就**不认**（fail-closed）。
                //
                // 这里早期是 `return true`，理由是"不能因为判不了就不干活" ——
                // 真机证明这个默认值很危险：判据一旦不可用，**所有单位（含敌方）都会被认成本方**，
                // 于是敌方单位被记进保护名单并回血（用户实测 B1 也变成了无敌）。
                // 宁可"无敌暂时不生效但绝不误伤"，也不要"看起来能用却给敌人加血"。
                LogOwnershipUnavailable();
                return false;
            }

            if ((DateTime.UtcNow - _ownedCacheStamp).TotalSeconds > OwnedCacheSeconds)
            {
                _ownedCache.Clear();
                _ownedCacheStamp = DateTime.UtcNow;
            }

            bool owned;
            if (_ownedCache.TryGetValue(objectAddress, out owned)) { return owned; }

            owned = ObjectReferencesMoney(objectAddress, money);
            _ownedCache[objectAddress] = owned;
            return owned;
        }

        /// <summary>
        /// 在对象里找出所有像堆指针的字段，看它们指向的内存里有没有出现当前金钱值。
        ///
        /// 扫描参数都是被真机数据"教"出来的（详见 `TechnoOwnership.FindHouseFields`，
        /// 真正的扫描实现在那里 —— 秒杀的敌我锚定用的是同一条判据，不能有两份会走样的拷贝）：
        ///   · **扫描窗口 0x1000**：早先收窄到 0x100 就再也认不出本方单位了 ——
        ///     Owner 那种字段并不在对象开头 0x100 字节内（对照能正常工作的「单位校准」页，
        ///     它扫的是 0x1000）。
        ///   · **不做 Describe 预检**：试过"先问这块可读吗再读"，结果把好指针也滤掉了
        ///     （Describe 的粒度覆盖不住堆内的细碎分配），垃圾指针反倒放行。
        ///     改成直接读、失败即跳过。
        ///   · **安静读取**：试探性读取必然大量失败，走普通读取会把日志刷满失败记录，
        ///     把真正的错误原因埋掉。
        ///
        /// `maxMatches` 传 1：这里只关心"有没有"，命中第一个立刻收工（保持既有性能特征）。
        /// </summary>
        private bool ObjectReferencesMoney(uint objectAddress, uint money)
        {
            int probes;
            return TechnoOwnership.FindHouseFields(
                _target.Memory, objectAddress, money, 1, out probes).Count > 0;
        }

        /// <summary>只读探测：解析地址并读一次当前值，不写入。</summary>
        private void PeekValue(CheatFeature feature)
        {
            // 每秒都强制重新解析一次，保证"地址是否还有效"的显示是实时的
            feature.TicksSinceResolve = RelocateEveryTicks;
            EnsureAddress(feature);
            if (feature.Address == 0) { return; }

            uint current;
            if (!ReadCurrent(feature, out current))
            {
                feature.Address = 0;
                feature.TicksSinceResolve = RelocateEveryTicks;
                feature.LastError = "读取失败";
                return;
            }

            feature.LastSeenValue = current;
            if (IsResolutionError(feature.LastError)) { feature.LastError = ""; }
        }

        private static bool IsResolutionError(string error)
        {
            return error == "地址解析失败" || error == "地址不可写" || error == "读取失败";
        }

        private void ApplyFeature(CheatFeature feature)
        {
            EnsureAddress(feature);
            if (feature.Address == 0) { return; }

            uint current;
            if (!ReadCurrent(feature, out current))
            {
                // 读不到通常意味着对象已经重建，下一轮强制重新解析
                feature.Address = 0;
                feature.TicksSinceResolve = RelocateEveryTicks;
                feature.LastError = "读取失败";
                return;
            }

            feature.LastSeenValue = current;

            uint desired = Scale(feature.Locator, feature.TargetValue);
            if (current == desired) { return; }

            if (Write(feature, desired))
            {
                feature.WriteCount++;
                if (feature.LastError.Length != 0) { feature.LastError = ""; }
            }
            else
            {
                feature.LastError = "写入失败";
                feature.Address = 0;
                feature.TicksSinceResolve = RelocateEveryTicks;
            }
        }

        /// <summary>解析（或定期重新解析）字段地址。</summary>
        private void EnsureAddress(CheatFeature feature)
        {
            if (feature.Address != 0 && feature.TicksSinceResolve < RelocateEveryTicks)
            {
                feature.TicksSinceResolve++;
                return;
            }

            uint resolved;
            string error;
            if (!feature.Locator.TryResolve(_target.Memory, _target.ModuleBase, out resolved, out error))
            {
                if (feature.LastError.Length == 0)
                {
                    Logger.Warn("Engine", feature.Name + " 地址解析失败：" + error);
                    NotifyChanged();
                }
                feature.Address = 0;
                feature.LastError = "地址解析失败";
                feature.TicksSinceResolve = RelocateEveryTicks;
                return;
            }

            // 安全检查：解析出的地址必须落在可写页上。
            // 指针链一旦失效可能指向任意垃圾地址，直接写会破坏其它对象 —— 必须先确认可写。
            MemoryRegion region = _target.Memory.Describe(resolved);
            if (region == null || !region.Writable)
            {
                if (feature.LastError.Length == 0)
                {
                    Logger.Warn("Engine", feature.Name + " 解析到不可写地址，已跳过：0x"
                        + resolved.ToString("X8", CultureInfo.InvariantCulture));
                    NotifyChanged();
                }
                feature.Address = 0;
                feature.LastError = "地址不可写";
                feature.TicksSinceResolve = RelocateEveryTicks;
                return;
            }

            if (resolved != feature.Address)
            {
                Logger.Info("Engine", string.Format(CultureInfo.InvariantCulture,
                    "{0} 地址解析为 0x{1:X8}", feature.Name, resolved));
                NotifyChanged();
            }

            feature.Address = resolved;
            feature.TicksSinceResolve = 0;
            feature.LastError = "";
        }

        private bool ReadCurrent(CheatFeature feature, out uint value)
        {
            if (feature.Locator.ValueWidth == 2)
            {
                ushort narrow;
                if (!_target.Memory.ReadUInt16(feature.Address, out narrow)) { value = 0; return false; }
                value = narrow;
                return true;
            }
            return _target.Memory.ReadUInt32(feature.Address, out value);
        }

        private bool Write(CheatFeature feature, uint desired)
        {
            if (feature.Locator.ValueWidth == 2)
            {
                return _target.Memory.WriteUInt16(feature.Address, (ushort)desired);
            }
            return _target.Memory.WriteUInt32(feature.Address, desired);
        }

        /// <summary>把界面显示值换算成内存里应该写的值（含倍数与宽度上限）。</summary>
        private static uint Scale(FieldLocator locator, uint displayed)
        {
            int scale = locator.ValueScale <= 0 ? 1 : locator.ValueScale;
            ulong value = (ulong)displayed * (ulong)scale;
            ulong cap = locator.ValueWidth == 2 ? 0xFFFFUL : (ulong)uint.MaxValue;
            return value > cap ? (uint)cap : (uint)value;
        }

        private void NotifyChanged()
        {
            Action handler = Changed;
            if (handler == null) { return; }
            try { handler(); }
            catch (Exception) { /* 界面已销毁，忽略 */ }
        }
    }
}
