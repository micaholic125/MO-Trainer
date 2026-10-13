using System;
using System.Collections.Generic;
using System.Globalization;
using MoTrainer.Calibration;
using MoTrainer.Core;

namespace MoTrainer.Cheats
{
    /// <summary>「瞬间建造」一拍的结果。</summary>
    public sealed class InstantBuildResult
    {
        /// <summary>非空 = 这一拍**一个字节都没写**，原因（或状态）在这里。</summary>
        public string Note = "";

        /// <summary>此刻正在造的那个东西（房子 + 0x53BC 指向它）；0 = 现在没东西在造。</summary>
        public uint ProducingAddress;

        public uint TypeTable;

        /// <summary>它的"已经造了多少拍"：写之前 / 写之后。</summary>
        public uint ElapsedBefore;
        public uint ElapsedAfter;

        /// <summary>还差多少钱没付完（只读，用来判断"是不是已经在收尾了"）。</summary>
        public uint Remaining;
        public bool ReadRemaining;

        public bool Wrote;

        public bool Ok { get { return Note.Length == 0; } }
    }

    /// <summary>
    /// 「瞬间建造」—— 把**当前正在造的那个东西**的"已经造了多少拍"往前推，让它在下一拍就造好。
    ///
    /// ## 这条路是怎么来的（三轮真机试探，全部记在 `开发日志/2026-10-10.md`）
    ///
    /// 游戏的建造是一条**按拍走的流水线**（每一拍：扣一笔钱、进度 +1 拍）：
    ///
    ///   · `房子 + 0x53BC` = **正在造的那个东西**（开造时挂上、造完摘掉）；
    ///   · 那个东西的 `+0x24` = **已经造了多少拍**（真机实测：矿场造到第 54 拍时造好）；
    ///   · 它的 `+0x60` = **还差多少钱没付完**（每拍减少的钱 = 这一拍扣掉的钱）；
    ///   · 每拍扣多少 = `还差的钱 ÷ 还剩几拍`（所以最后一拍正好把余款结清、落到 0）。
    ///
    /// 真机实测（2026-10-10 18:53、2026-10-11 四次）：把 `+0x24` 往前推到"只剩 1 拍"，
    /// **游戏会自己把尾款一次付清、东西立刻造好**（用户看到侧边栏显示「就绪」）。
    ///
    /// ## ⚠ 2026-10-13 的重大修正：房子里的流水线**不止一条**
    ///
    /// 上面那条只认 `+0x53BC` **一个**槽位。用户 2026-10-13 报「防御 / 步兵 / 载具不能瞬间建造」，
    /// 当天用只读探针（`tools\HousePeek.cs`，`slots` 模式）在真机对局里连续采样，
    /// 拍到房子 `+0x5390` 起那一小段里**同时挂着三、四条互不相干的流水线**：
    ///
    /// | 槽位 | 那一局看到的 |
    /// | --- | --- |
    /// | `+0x53B4` | 已过 31→43 拍、还差 2086→950（慢，像建筑/防御） |
    /// | `+0x53BC` | 已过 14→32 拍、还差 2216→1197 ← **就是原来唯一在用的那条** |
    /// | `+0x53CC` | 已过 1→9 拍、还差 197→164（快 30 倍，像步兵/载具） |
    ///
    /// 三条的**对象类别完全相同**（头部都是 `0x007E88D0`）、`+0x24` / `+0x60` 语义也相同 ——
    /// 也就是说：**不是"步兵载具走另一套"，而是"原来只推了其中一条流水线"**。
    /// 现在改成扫 `+0x5390`~`+0x53F8` 这一小段，**凡是头部等于验过的那个类别的都推**。
    ///
    /// ## 本实现与"试探"的两处关键区别
    ///
    /// 1. **不还原**。试探会写回原值，而真机实测过：造好之后写回旧值，游戏会把**建好的东西退回未完成**。
    ///    功能就是功能 —— 加速了就是加速了，关掉开关时**不把进度退回去**（见下面"关掉时怎么办"）。
    /// 2. **只往前推，绝不往回拨**。每次读当前值再写"当前值 + <see cref="AdvanceTicks"/>"，
    ///    所以对"总拍数比 53 大的东西"（车厂、超武）也只会加速、不会拖慢 —— 而写死一个数就会。
    ///
    /// ## 安全设计（与秒杀 / 无敌 / 电力同一套标准）
    ///
    ///   · 只在**房子自己那一小段流水线槽位**里找（房子由金钱链锚定，是"我方"的），别处一律不碰；
    ///   · 目标**类别必须是真机验过的那一类**（<see cref="VerifiedTypeTable"/>），没验过的不写；
    ///   · `+0x60`（还差多少钱）**只读不写**，且**已经是 0 就不动它**（那说明尾款结清了、正在收尾，
    ///     这时候去动它正是 2026-10-10 把成果撤掉的那个动作）；
    ///   · 数值不合理（大得像地址、拍数大得离谱）一律拒绝；
    ///   · 写前查**可写页**、写后**回读校验**；
    ///   · **关掉开关时不还原**：已经在造的东西保持当前进度继续造（把它退回去才是破坏）。
    ///
    /// ## ⚠ 联机代价（卡片上必须常驻写明）
    ///
    /// 建造进度是**联网时会互相核对的状态**（每个客户端各自模拟同一条流水线）。
    /// 你这边提前造好、别人那边还在造 ⇒ 对不上 ⇒ 大概率 out of sync 掉线。
    /// 与「选中单位无敌」「秒杀敌人」同一档：**默认关闭、开启需二次确认、只适合单人 / 打 AI**。
    /// </summary>
    public static class InstantBuild
    {
        /// <summary>「正在造的那个东西」的"已经造了多少拍"。</summary>
        public const uint ElapsedOffset = 0x24;

        /// <summary>「正在造的那个东西」的"还差多少钱没付完"（**只读**，用来判断是不是在收尾）。</summary>
        public const uint RemainingOffset = 0x60;

        /// <summary>
        /// 真机验过的**唯一**一类「正在造的东西」（2026-10-10 / 10-11 造矿场、兵营、车厂等命中；
        /// 2026-10-13 又在步兵/载具/另一条线上复验，头部同样是这个值）。
        /// 别的类别一律不写：**宁可少加速一次、回头补验，也不往没验过的对象上写**。
        /// </summary>
        public const uint VerifiedTypeTable = 0x007E88D0;

        /// <summary>
        /// 房子的"生产流水线槽位"窗口。
        ///
        /// 2026-10-13 真机采样定的范围：这一小段里出现过 `+0x53AC` / `+0x53B0` / `+0x53B4` /
        /// `+0x53BC` / `+0x53CC` 五个会挂上"正在造的东西"的位置。窗口**刻意开得很窄** ——
        /// 开得越宽，越有可能把别的指针误当成流水线。窗口外一个字节都不看。
        /// </summary>
        public const uint ProductionWindowFirst = 0x5390;
        public const uint ProductionWindowLast = 0x53F8;

        /// <summary>一次最多同时推几条流水线（真机见过 3 条同时跑，留一倍余量）。</summary>
        public const int MaxCandidates = 8;

        /// <summary>
        /// 收尾前**留几拍不推**，交给游戏自己走完。
        ///
        /// 为什么必须留余量（2026-10-11 真机事故）：建造完成的**唯一**触发方式是
        /// "某一拍把尾款一次结清"（还剩 1 拍时那一拍正好把余款付掉）。若把进度推**过了**那个点，
        /// 游戏再也不会执行"结清"那一下 —— 表现就是**读条满了、不显示就绪、也放不下去**。
        /// 留 3 拍：就算"总拍数"估算差了 ±2 拍，也绝不会推过头。
        /// </summary>
        public const uint ReserveTicks = 3;

        /// <summary>拍数上限：超过这个数说明这不是一条正常的建造流水线，拒绝动它。</summary>
        public const uint MaxElapsed = 5000;

        /// <summary>与 `ProgressHunter`「取值像地址」同口径：比这大的数不可能是"还差多少钱"。</summary>
        public const uint MaxSaneRemaining = 0x10000000;

        /// <summary>一条流水线的观测状态（**只认同一件东西**，换东西就重新量）。</summary>
        private sealed class WatchState
        {
            public uint Elapsed;
            public uint Remaining;

            /// <summary>最后一次看到它是在第几趟（用来把早就消失的东西清出去，别让表无限长）。</summary>
            public int LastPass;
        }

        // 静态是因为 `Tick` 是无状态的工具方法，而这几格只服务于"量一拍"这一件事，
        // 不需要跟着功能开关走。**按对象分别记账** —— 2026-10-13 起同时可能推好几条流水线。
        private static readonly Dictionary<uint, WatchState> _watch = new Dictionary<uint, WatchState>();
        private static int _pass;
        private const int WatchEvictPasses = 200;

        /// <summary>
        /// 走一拍。**任何一步对不上就一个字节都不写**，原因写在返回值的 <see cref="InstantBuildResult.Note"/> 里。
        ///
        /// 返回的是"最值得一提"的那一条结果（写成功 > 有事发生 > 第一条），
        /// 供自检与单对象调用方使用；要拿到全部流水线的结果用 <see cref="TickAll"/>。
        /// </summary>
        public static InstantBuildResult Tick(
            MemoryIO memory, uint houseAddress, uint moduleBase, uint moduleSize)
        {
            return Tick(memory, houseAddress, moduleBase, moduleSize, VerifiedTypeTable);
        }

        /// <summary>
        /// 同上。`expectedTypeTable` 是给自动体检用的：合成场景里的"模块"是临时分配的内存块，
        /// 不可能正好落在真机那个 `0x007E88D0` 上；生产路径一律走上面那个重载（用真机验过的值）。
        /// </summary>
        public static InstantBuildResult Tick(
            MemoryIO memory, uint houseAddress, uint moduleBase, uint moduleSize, uint expectedTypeTable)
        {
            List<InstantBuildResult> all = TickAll(memory, houseAddress, moduleBase, moduleSize, expectedTypeTable);
            if (all.Count == 0)
            {
                InstantBuildResult empty = new InstantBuildResult();
                empty.Note = "现在没有东西在造";
                return empty;
            }
            for (int i = 0; i < all.Count; i++) { if (all[i].Wrote) { return all[i]; } }
            for (int i = 0; i < all.Count; i++) { if (all[i].Note.Length > 0) { return all[i]; } }
            return all[0];
        }

        /// <summary>房子里的每一条生产流水线各走一拍（生产路径用这个重载）。</summary>
        public static List<InstantBuildResult> TickAll(
            MemoryIO memory, uint houseAddress, uint moduleBase, uint moduleSize)
        {
            return TickAll(memory, houseAddress, moduleBase, moduleSize, VerifiedTypeTable);
        }

        /// <summary>
        /// 房子里的每一条生产流水线各走一拍。**任何一步对不上就那一条一个字节都不写**。
        /// </summary>
        public static List<InstantBuildResult> TickAll(
            MemoryIO memory, uint houseAddress, uint moduleBase, uint moduleSize, uint expectedTypeTable)
        {
            List<InstantBuildResult> results = new List<InstantBuildResult>();

            // ---- 前置条件：不满足就一条都不处理，如实说明缺什么 ----
            if (memory == null)
            {
                results.Add(Fail("内存接口不可用"));
                return results;
            }
            if (houseAddress == 0)
            {
                results.Add(Fail("认不出我方房子（金钱链没解析出来）"));
                return results;
            }
            if (moduleSize == 0)
            {
                results.Add(Fail("配置里没记「模块大小」，没法核对目标类别"));
                return results;
            }

            // ---- ① 在房子自己的那一小段流水线槽位里，把"此刻挂着的东西"都找出来 ----
            List<uint> targets = FindProductionTargets(memory, houseAddress, moduleBase, moduleSize, expectedTypeTable);
            if (targets.Count == 0)
            {
                results.Add(Fail("现在没有东西在造"));
                return results;
            }

            // ---- ② 每一条流水线各走一拍 ----
            _pass++;
            for (int i = 0; i < targets.Count; i++)
            {
                results.Add(TickOne(memory, targets[i], moduleBase, moduleSize, expectedTypeTable));
            }
            EvictStaleWatch();

            return results;
        }

        /// <summary>
        /// **只读**地看一眼：此刻房子上挂着哪几条流水线、各造到第几拍、还差多少钱。
        ///
        /// 与 <see cref="TickAll"/> 共用同一套"怎么找流水线"的判据，但**一个字节都不写** ——
        /// 开关关着的时候卡片上显示的就是它。之所以必须是单独一个方法而不是"给 TickAll 加个开关"：
        /// 关闭状态的探测路径绝不允许有任何写入分支，加开关就等于给"关着也会写"留了条缝。
        /// </summary>
        public static List<InstantBuildResult> Peek(
            MemoryIO memory, uint houseAddress, uint moduleBase, uint moduleSize)
        {
            return Peek(memory, houseAddress, moduleBase, moduleSize, VerifiedTypeTable);
        }

        /// <summary>
        /// 同上。`expectedTypeTable` 与 <see cref="TickAll"/> 那个重载同一用途：
        /// 合成场景里的"模块"是临时分配的内存块，不可能正好落在真机那个 `0x007E88D0` 上。
        /// </summary>
        public static List<InstantBuildResult> Peek(
            MemoryIO memory, uint houseAddress, uint moduleBase, uint moduleSize, uint expectedTypeTable)
        {
            List<InstantBuildResult> results = new List<InstantBuildResult>();
            if (memory == null || houseAddress == 0 || moduleSize == 0) { return results; }

            List<uint> targets = FindProductionTargets(memory, houseAddress, moduleBase, moduleSize, expectedTypeTable);
            for (int i = 0; i < targets.Count; i++)
            {
                InstantBuildResult result = new InstantBuildResult();
                result.ProducingAddress = targets[i];
                if (!memory.ReadUInt32(targets[i], out result.TypeTable)) { continue; }
                memory.ReadUInt32(targets[i] + ElapsedOffset, out result.ElapsedBefore);
                result.ReadRemaining = memory.ReadUInt32(targets[i] + RemainingOffset, out result.Remaining);
                results.Add(result);
            }
            return results;
        }

        /// <summary>
        /// 清空"正在量哪几件东西"的观测表。
        ///
        /// 只有自动体检用得上：自检会反复搭合成场景，而临时内存块的地址会被系统回收重用 ——
        /// 上一个场景留下的观测值会让下一个场景误以为"游戏已经走过一拍了"，结论就不确定了。
        /// 真机路径**永远不会**调用它（关掉开关不清、重新打开也不清，进度只进不退）。
        /// </summary>
        public static void ResetWatching()
        {
            _watch.Clear();
            _pass = 0;
        }

        private static InstantBuildResult Fail(string note)
        {
            InstantBuildResult result = new InstantBuildResult();
            result.Note = note;
            return result;
        }

        /// <summary>
        /// 在房子自己那一小段流水线槽位里，把"此刻挂着的东西"找出来（**只读**）。
        ///
        /// 判据分两层：
        ///   · 第一层看**数值**像不像一个指针（0x10000 ~ 0x7FFFFFFF）；
        ///   · 第二层看**指过去的东西头部**是不是真机验过的那一类。
        ///
        /// 为什么要有第二层、还要分开处理：这一小段里除了流水线，还躺着 1.0 这样的浮点常量
        /// （`0x3F800000`），它的数值同样"像一个地址"。若不加区分地把每一个都报出来，
        /// 卡片上显示的就会是"读不到 0x3F800000 的头部"，而真正在造什么反而看不见。
        ///
        /// 但只要**一个验过的都没有**，就把第一个像指针的东西照常交出去处理 ——
        /// 拒绝的理由必须说得出口，不能因为"它长得不对"就一声不吭（自检里有条目在守这一点）。
        /// </summary>
        private static List<uint> FindProductionTargets(
            MemoryIO memory, uint houseAddress, uint moduleBase, uint moduleSize, uint expectedTypeTable)
        {
            List<uint> plausible = new List<uint>();
            for (uint offset = ProductionWindowFirst; offset <= ProductionWindowLast; offset += 4)
            {
                uint pointer;
                if (!memory.ReadUInt32(houseAddress + offset, out pointer)) { continue; }
                if (pointer < 0x10000 || pointer >= 0x80000000) { continue; }
                if (plausible.Contains(pointer)) { continue; }       // 同一件东西被两个槽位指着时只算一次

                // 指过去的地方**要真的读得出来**才算数。
                //
                // 为什么必须加这一关（2026-10-13 真机抓到）：这一小段里躺着三个 `1.0` 浮点常量
                // （`0x3F800000`），它的数值"像一个指针"，但其实什么都没指着。少了这一关，
                // 玩家**什么都没造**的时候卡片上会蹦出"读不到 0x3F800000 的头部"——
                // 看着像工具坏了，其实只是没东西在造。留着这一关，那种情况会如实说"现在没有东西在造"。
                uint header;
                if (!memory.ReadUInt32(pointer, out header)) { continue; }
                plausible.Add(pointer);
            }

            List<uint> targets = new List<uint>();
            if (plausible.Count == 0) { return targets; }

            for (int i = 0; i < plausible.Count; i++)
            {
                uint typeTable;
                if (!memory.ReadUInt32(plausible[i], out typeTable)) { continue; }
                if (typeTable < moduleBase || typeTable >= moduleBase + moduleSize) { continue; }
                if (typeTable != expectedTypeTable) { continue; }
                targets.Add(plausible[i]);
            }

            if (targets.Count == 0) { targets.Add(plausible[0]); }

            if (targets.Count > MaxCandidates)
            {
                targets.RemoveRange(MaxCandidates, targets.Count - MaxCandidates);
            }
            return targets;
        }

        /// <summary>把早就消失的流水线从观测表里清出去 —— 一局打下来开关会开很久，表不能只涨不消。</summary>
        private static void EvictStaleWatch()
        {
            if (_watch.Count == 0) { return; }
            List<uint> dead = null;
            foreach (KeyValuePair<uint, WatchState> pair in _watch)
            {
                if (_pass - pair.Value.LastPass > WatchEvictPasses)
                {
                    if (dead == null) { dead = new List<uint>(); }
                    dead.Add(pair.Key);
                }
            }
            if (dead == null) { return; }
            for (int i = 0; i < dead.Count; i++) { _watch.Remove(dead[i]); }
        }

        /// <summary>一条流水线走一拍。</summary>
        private static InstantBuildResult TickOne(
            MemoryIO memory, uint target, uint moduleBase, uint moduleSize, uint expectedTypeTable)
        {
            InstantBuildResult result = new InstantBuildResult();
            result.ProducingAddress = target;

            // ① 类别校验：头部必须像游戏对象，且是我真机验过的那一类
            uint typeTable;
            if (!memory.ReadUInt32(target, out typeTable))
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "读不到 0x{0:X8} 的头部", target);
                result.ProducingAddress = 0;
                return result;
            }
            result.TypeTable = typeTable;
            if (typeTable < moduleBase || typeTable >= moduleBase + moduleSize)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8} 的头部 0x{1:X8} 不像游戏对象 —— 拒绝动它", target, typeTable);
                result.ProducingAddress = 0;
                return result;
            }
            if (typeTable != expectedTypeTable)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8} 的类别是 0x{1:X8}，**不是真机验过的那一类（0x{2:X8}）** —— 没验过的不写",
                    target, typeTable, expectedTypeTable);
                result.ProducingAddress = 0;
                return result;
            }

            // ② "还差多少钱没付完"：只读。已经是 0 ⇒ 尾款结清了、正在收尾，**这一格绝不能碰**。
            result.ReadRemaining = memory.ReadUInt32(target + RemainingOffset, out result.Remaining);
            if (!result.ReadRemaining)
            {
                result.Note = "读不到「还差多少钱没付完」那一格";
                return result;
            }
            if (result.Remaining == 0)
            {
                result.Note = "这个东西的尾款已经结清（正在收尾）—— 不动它";
                return result;
            }
            if (result.Remaining >= MaxSaneRemaining)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "「还差多少钱」读出来是 {0}，大得像地址 —— 拒绝动它", result.Remaining);
                result.ProducingAddress = 0;
                return result;
            }

            // ③ 往前推"已经造了多少拍"
            if (!memory.ReadUInt32(target + ElapsedOffset, out result.ElapsedBefore))
            {
                result.Note = "读不到「已经造了多少拍」那一格";
                return result;
            }
            if (result.ElapsedBefore >= MaxElapsed)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "「已经造了多少拍」是 {0}，大得不像正常建造 —— 拒绝动它", result.ElapsedBefore);
                result.ProducingAddress = 0;
                return result;
            }

            // ④ 先"看一拍"：等游戏自己走一拍（它自己会同时改 elapsed 和 remaining），
            //    从这一拍扣掉的钱反推"还差几拍"，再决定推到哪儿停 —— **绝不闷头往前推**。
            WatchState state;
            if (!_watch.TryGetValue(target, out state))
            {
                state = new WatchState();
                state.Elapsed = result.ElapsedBefore;
                state.Remaining = result.Remaining;
                state.LastPass = _pass;
                _watch[target] = state;
                result.Note = "正在量这东西一共要造多少拍（等游戏自己走一拍，约 0.4 秒）";
                return result;
            }
            if (result.ElapsedBefore == state.Elapsed && result.Remaining == state.Remaining)
            {
                state.LastPass = _pass;
                result.Note = "正在量…（游戏这一拍还没走完）";
                return result;
            }

            uint paidThisTick = state.Remaining > result.Remaining
                ? state.Remaining - result.Remaining : 0;
            state.Elapsed = result.ElapsedBefore;
            state.Remaining = result.Remaining;
            state.LastPass = _pass;

            if (paidThisTick == 0)
            {
                result.Note = "这一拍没有扣钱（可能在等钱或正在收尾）—— 不推";
                return result;
            }

            // 还差几拍 ≈ 还差的钱 ÷ 这一拍扣的钱；总拍数 ≈ 已经过的 + 还差的
            uint ticksLeft = result.Remaining / paidThisTick;
            if (ticksLeft <= ReserveTicks)
            {
                result.Note = "就剩最后几拍了，交给游戏自己走完";
                return result;
            }

            uint next = result.ElapsedBefore + ticksLeft - ReserveTicks;
            result.ElapsedAfter = next;
            if (next <= result.ElapsedBefore)
            {
                result.Note = "算出来的目标不比现在大，不推";
                return result;
            }

            if (!memory.IsWritable(target + ElapsedOffset))
            {
                result.Note = "「已经造了多少拍」那一页不可写（VirtualQueryEx 判定）—— 拒绝写入";
                result.ProducingAddress = 0;
                return result;
            }

            result.Wrote = memory.WriteUInt32(target + ElapsedOffset, next);

            uint readBack;
            if (result.Wrote && memory.ReadUInt32(target + ElapsedOffset, out readBack)
                && readBack != next)
            {
                // 写进去了、立刻又不是这个值 ⇒ 游戏自己接手了这一格（可能这一拍刚好在推进）
                result.ElapsedAfter = readBack;
            }

            if (!result.Wrote)
            {
                result.Note = "写入没有成功（WriteProcessMemory 返回失败）";
            }

            return result;
        }
    }
}
