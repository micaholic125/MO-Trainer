using System;
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
    /// ## 本实现与"试探"的两处关键区别
    ///
    /// 1. **不还原**。试探会写回原值，而真机实测过：造好之后写回旧值，游戏会把**建好的东西退回未完成**。
    ///    功能就是功能 —— 加速了就是加速了，关掉开关时**不把进度退回去**（见下面"关掉时怎么办"）。
    /// 2. **只往前推，绝不往回拨**。每次读当前值再写"当前值 + <see cref="AdvanceTicks"/>"，
    ///    所以对"总拍数比 53 大的东西"（车厂、超武）也只会加速、不会拖慢 —— 而写死一个数就会。
    ///
    /// ## 安全设计（与秒杀 / 无敌 / 电力同一套标准）
    ///
    ///   · 只认**房子 + 0x53BC 指向的那个东西**（房子由金钱链锚定，是"我方"的），别处一律不碰；
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
        /// 真机验过的**唯一**一类「正在造的东西」（2026-10-10 / 10-11 造矿场、兵营、车厂等命中）。
        /// 别的类别一律不写：**宁可少加速一次、回头补验，也不往没验过的对象上写**。
        /// </summary>
        public const uint VerifiedTypeTable = 0x007E88D0;

        /// <summary>
        /// 每拍往前推几拍。锁定线程 33 毫秒一拍 ⇒ 约 150 拍/秒，
        /// 一座 54 拍的矿场大约 0.4 秒造好（"瞬间"够用了，又不至于一步跳过头太多）。
        /// </summary>
        public const uint AdvanceTicks = 5;

        /// <summary>拍数上限：超过这个数说明这不是一条正常的建造流水线，拒绝动它。</summary>
        public const uint MaxElapsed = 5000;

        /// <summary>与 `ProgressHunter`「取值像地址」同口径：比这大的数不可能是"还差多少钱"。</summary>
        public const uint MaxSaneRemaining = 0x10000000;

        /// <summary>
        /// 走一拍。**任何一步对不上就一个字节都不写**，原因写在返回值的 <see cref="InstantBuildResult.Note"/> 里。
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
            InstantBuildResult result = new InstantBuildResult();
            if (memory == null) { result.Note = "内存接口不可用"; return result; }
            if (houseAddress == 0) { result.Note = "认不出我方房子（金钱链没解析出来）"; return result; }
            if (moduleSize == 0) { result.Note = "配置里没记「模块大小」，没法核对目标类别"; return result; }

            // ① 房子 + 0x53BC = 此刻正在造的那个东西
            uint target;
            if (!memory.ReadUInt32(houseAddress + HarvestSession.ProducingPointerOffset, out target))
            {
                result.Note = "读不到「正在造的东西」那个指针";
                return result;
            }
            if (target == 0)
            {
                result.Note = "现在没有东西在造";
                return result;
            }
            result.ProducingAddress = target;

            // ② 类别校验：头部必须像游戏对象，且是我真机验过的那一类
            uint typeTable;
            if (!memory.ReadUInt32(target, out typeTable))
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "读不到 0x{0:X8} 的头部", target);
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

            // ③ "还差多少钱没付完"：只读。已经是 0 ⇒ 尾款结清了、正在收尾，**这一格绝不能碰**。
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

            // ④ 往前推"已经造了多少拍"
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

            uint next = result.ElapsedBefore + AdvanceTicks;
            result.ElapsedAfter = next;

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
