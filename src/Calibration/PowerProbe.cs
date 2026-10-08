using System;
using System.Globalization;
using System.Threading;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 「电力试探」（2026-10-09 新增；`[S4-3]` B 方案的第一步）——
    /// 判定"建筑对象上的某个字段"是不是**家庭电力产出的来源**。
    ///
    /// 为什么必须动手试：A 方案（写 `House + 0x53A4`）早被真机推翻（那只是**界面显示值**：
    /// 写进去界面显示 30000，游戏仍判"电力不足"）。离线分析（见 `开发日志/2026-10-09.md` 第三节）
    /// 把 4 份采集报告的"电力跳变点"与"同秒变化的字段"交叉比对：两次**独立**会话都指向建筑对象的
    /// `+0xD0`（电厂建成时 `1 → 100`）—— 但同一偏移在步兵对象上有不相干的值在跳，
    /// **证据不足以固化**。采集报告只记"变化过的字段"，再怎么翻也就到这里了，只能动手试。
    ///
    /// 试探法（与校准向导的"消费检验"同一套路）：
    ///   ① 目标**必须是用户自己选中、且属性确认属于我方**的一座建筑（别人测不了，也不能测）；
    ///   ② 读出候选字段原值 → 写入试探值 → 期间连续读"界面上的电力产出"（`House + 0x53A4`）；
    ///   ③ **无论如何都还原原值**（`finally` 里做），并回读确认还原成功。
    ///
    /// 判定（`Judge`，可单独测）：写中读数与写前相差 ≈ 试探增量、且还原后回落到写前 ⇒ 这个字段就是产出来源。
    /// </summary>
    public static class PowerProbe
    {
        /// <summary>候选字段在建筑对象内的偏移（离线分析锁定的首要候选，见类注释）。</summary>
        public const uint CandidateOffset = 0xD0;

        /// <summary>试探值：足够大，游戏界面上"电力"一眼能看出差别。</summary>
        public const uint TestValue = 5000;

        /// <summary>试探期间观察多久（毫秒）与采样间隔。合计约 4 秒 —— 越短，万一字段猜错，暴露时间越短。</summary>
        public const int ObserveMillis = 2400;
        public const int SampleIntervalMillis = 300;

        public sealed class Result
        {
            public bool Ok;
            public string Detail = "";

            public uint ObjectAddress;
            public uint FieldAddress;
            public uint OriginalValue;
            public uint WrittenValue;

            /// <summary>写前 / 写中峰值 / 还原后的"界面电力产出"读数。</summary>
            public uint OutputBefore;
            public uint OutputPeak;
            public uint OutputAfter;

            /// <summary>是否已把原值写回去并回读确认。</summary>
            public bool Restored;

            /// <summary>结论：这个字段是不是电力产出的来源。</summary>
            public bool LooksLikeSource;

            public string Verdict = "";
        }

        /// <summary>
        /// 判定规则（纯函数，便于自检）：写中读数与写前相差接近试探增量、且还原后回到写前 ⇒ 是产出来源。
        /// `delta` 是实际写入的增量（试探值 − 原值）。
        /// </summary>
        public static bool Judge(uint before, uint peak, uint after, uint delta)
        {
            if (delta == 0) { return false; }

            // 写中要与写前差出"大半个增量"（游戏自己的汇总可能有取整/上限，别卡太死）
            uint rise = peak > before ? peak - before : 0;
            if (rise < delta / 2) { return false; }

            // 还原后要回落到写前附近（允许小幅漂移：对局里电力本来就会小幅变动）
            uint drift = after > before ? after - before : before - after;
            return drift <= delta / 4;
        }

        /// <summary>
        /// 执行一次试探。**必须在后台线程调用**（内部会 Sleep 约 4 秒）。
        /// 任何一步不满足前置条件都**拒绝并说明**，绝不"悄悄什么都没做"。
        /// </summary>
        public static Result Run(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase,
            uint objectAddress, uint myHouse, uint ownerOffset)
        {
            return Run(memory, profile, moduleBase, objectAddress, myHouse, ownerOffset,
                ObserveMillis, SampleIntervalMillis);
        }

        /// <summary>同上，但可指定观察时长/采样间隔（自检用短时长，免得每次跑 4 秒）。</summary>
        public static Result Run(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase,
            uint objectAddress, uint myHouse, uint ownerOffset,
            int observeMillis, int sampleMillis)
        {
            if (sampleMillis < 1) { sampleMillis = 1; }
            Result result = new Result();
            result.ObjectAddress = objectAddress;

            if (memory == null) { result.Detail = "内存接口不可用"; return result; }
            if (profile == null || !profile.PowerOutput.IsCalibrated)
            {
                result.Detail = "配置里没有「电力产出」的定位（先跑一次校准向导的电力步骤）";
                return result;
            }
            if (objectAddress < 0x10000) { result.Detail = "没有读到选中的对象"; return result; }

            // ① 目标必须**确实是我方的**建筑：拿"对象里指向我方 House 的指针"核对（与秒杀同一判据）
            uint owner;
            if (ownerOffset == 0 || !memory.ReadUInt32(objectAddress + ownerOffset, out owner) || owner == 0)
            {
                result.Detail = "读不到选中对象的归属 —— 请先选中**你自己的**一座发电厂再试";
                return result;
            }
            if (myHouse == 0 || owner != myHouse)
            {
                result.Detail = string.Format(CultureInfo.InvariantCulture,
                    "选中的对象不属于你（归属 0x{0:X8} ≠ 我方 0x{1:X8}）—— 只在**你自己的**建筑上做试探",
                    owner, myHouse);
                return result;
            }

            // ② 电力产出字段（界面读数）必须解析得出来
            uint outputAddress;
            string error;
            if (!profile.PowerOutput.TryResolve(memory, moduleBase, out outputAddress, out error))
            {
                result.Detail = "解析「电力产出」地址失败：" + error;
                return result;
            }

            result.FieldAddress = objectAddress + CandidateOffset;

            // 写之前必须先确认目标页可写（项目硬性约束：不可写就跳过并告警，不许硬写）
            if (!memory.IsWritable(result.FieldAddress))
            {
                result.Detail = string.Format(CultureInfo.InvariantCulture,
                    "候选字段所在的页不可写（0x{0:X8}）—— 已跳过，没有改动任何东西", result.FieldAddress);
                return result;
            }

            uint original;
            if (!memory.ReadUInt32(result.FieldAddress, out original))
            {
                result.Detail = string.Format(CultureInfo.InvariantCulture,
                    "读不出候选字段（0x{0:X8}）的原值 —— 拒绝写入", result.FieldAddress);
                return result;
            }
            result.OriginalValue = original;

            uint before;
            if (!memory.ReadUInt32(outputAddress, out before))
            {
                result.Detail = "读不到当前的电力产出读数";
                return result;
            }
            result.OutputBefore = before;

            // ③ 写入试探值（内存层会先校验目标页可写），写后立刻回读
            uint wrote = TestValue;
            if (!memory.WriteUInt32(result.FieldAddress, wrote))
            {
                result.Detail = string.Format(CultureInfo.InvariantCulture,
                    "写入试探值失败（0x{0:X8} 所在页不可写或写不进去）—— 没有改动任何东西", result.FieldAddress);
                return result;
            }

            uint readBack;
            if (!memory.ReadUInt32(result.FieldAddress, out readBack) || readBack != wrote)
            {
                // 写进去了但读不回 —— 先把原值还回去再说
                memory.WriteUInt32(result.FieldAddress, original);
                result.Detail = "试写后回读不一致 —— 已还原，未采纳本次结果";
                return result;
            }
            result.WrittenValue = wrote;

            try
            {
                // ④ 写中：连续观察电力产出
                uint peak = before;
                int rounds = observeMillis / sampleMillis;
                if (rounds < 3) { rounds = 3; }
                for (int i = 0; i < rounds; i++)
                {
                    Thread.Sleep(sampleMillis);
                    uint now;
                    if (memory.ReadUInt32(outputAddress, out now) && now > peak) { peak = now; }
                }
                result.OutputPeak = peak;
            }
            finally
            {
                // ⑤ **无论如何都还原**（这是本功能的安全底线：试探值只允许存在这几秒）
                bool back = memory.WriteUInt32(result.FieldAddress, original);
                uint check;
                result.Restored = back && memory.ReadUInt32(result.FieldAddress, out check) && check == original;
            }

            // ⑥ 还原后再观察一小段，确认读数回落
            uint after = result.OutputPeak;
            for (int i = 0; i < 4; i++)
            {
                Thread.Sleep(sampleMillis);
                uint now;
                if (memory.ReadUInt32(outputAddress, out now)) { after = now; }
            }
            result.OutputAfter = after;

            uint delta = wrote > original ? wrote - original : 0;
            result.LooksLikeSource = Judge(before, result.OutputPeak, after, delta);
            result.Verdict = result.LooksLikeSource
                ? string.Format(CultureInfo.InvariantCulture,
                    "**这个字段就是电力产出的来源**：写前 {0} → 写中 {1}（试探值 {2}）→ 还原后 {3}",
                    before, result.OutputPeak, wrote, after)
                : string.Format(CultureInfo.InvariantCulture,
                    "**这个字段不是电力产出的来源**：写前 {0} → 写中 {1} → 还原后 {2}（界面读数没有跟着试探值走）",
                    before, result.OutputPeak, after);

            result.Ok = true;
            result.Detail = string.Format(CultureInfo.InvariantCulture,
                "对象 0x{0:X8}｜候选字段 +0x{1:X}（0x{2:X8}）｜原值 {3} → 试探值 {4} → 已还原={5}",
                objectAddress, CandidateOffset, result.FieldAddress, original, wrote,
                result.Restored ? "是" : "**否（请立即检查！）**");
            return result;
        }
    }
}
