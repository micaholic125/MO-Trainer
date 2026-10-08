using System;
using System.Collections.Generic;
using System.Globalization;
using MoTrainer.Calibration;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Cheats
{
    /// <summary>
    /// 「你选中的是哪些单位」的可靠读法 —— **金钱锚定 + 自动更正**（2026-10-08 提取为共享件）。
    ///
    /// 为什么必须只有一份实现：在此之前，"读选中对象"有三份 —— 引擎里一份（本类的来源，可靠）、
    /// 「高级功能」页一份（按配置槽位直读 + 很弱的"看着像对象"校验）、采集器再一份（结构探测）。
    /// 真机 10-08 的会话证明后两份读出来的**根本不是玩家选中的单位**：
    /// 历次采集里"被选中的对象"从头到尾一个字节都没变过，字段里还读出英文单词碎片 ——
    /// 于是「自动查找总名单」拿着垃圾线索去找名单，找到的自然也是垃圾（三次定位全败）。
    /// 教训：**同一件事只能有一套实现，而且必须是经过真实检验的那一套。**
    ///
    /// 判据链（与无敌功能的归属校验同源，2026-09-22 起真机验证过）：
    ///   ① 数量：读「选中数量」全局变量；
    ///   ② 数组：配置槽位指向的数组里必须**含有金钱锚定确认属于我方的对象**，
    ///      否则把数量字段 ±0x40 内的每个槽位都试一遍（"自动更正"，结果缓存 5 秒）；
    ///   ③ 归属：对象里存在指针 P 使 `*(P + 0x30C)` 正好是当前金钱值
    ///      （`TechnoOwnership.FindHouseFields`）—— 这是全项目最硬的一条归属判据。
    ///
    /// 拿不到金钱值时**不推翻**已有解析（读操作不写内存，风险可控），但会记一行日志说明判据不可用。
    /// </summary>
    public sealed class SelectionResolver
    {
        /// <summary>自动更正过的槽位缓存多久（避免每次重扫附近所有槽位）。</summary>
        private const double SlotCacheSeconds = 5.0;

        /// <summary>归属校验结果的缓存有效期：太短会卡帧，太长则新造出来的单位要等一会儿才生效。</summary>
        private const double OwnedCacheSeconds = 3.0;

        private readonly ICalibrationTarget _target;

        private readonly Dictionary<uint, bool> _ownedCache = new Dictionary<uint, bool>();
        private DateTime _ownedCacheStamp = DateTime.MinValue;
        private bool _ownershipWarned;

        private uint _resolvedSlot;
        private uint _lastCorrectedSlot;
        private DateTime _resolvedSlotStamp = DateTime.MinValue;

        public SelectionResolver(ICalibrationTarget target)
        {
            if (target == null) { throw new ArgumentNullException("target"); }
            _target = target;
        }

        /// <summary>
        /// 清空所有**会话级**缓存。游戏（重新）连接时必须调用 ——
        /// 旧会话的槽位/归属缓存落到新会话的地址上，读到的是别的对象。
        /// </summary>
        public void Reset()
        {
            _ownedCache.Clear();
            _ownedCacheStamp = DateTime.MinValue;
            _resolvedSlot = 0;
            _lastCorrectedSlot = 0;
            _resolvedSlotStamp = DateTime.MinValue;
        }

        // ============================================================ 地址解析

        /// <summary>解析「选中数量」与「选中列表槽位」两个地址。</summary>
        public bool TryResolveAddresses(CalibrationProfile profile, out uint countAddress, out uint slotAddress)
        {
            countAddress = 0;
            slotAddress = 0;
            if (profile == null) { return false; }

            string error;
            if (!profile.SelectedCount.TryResolve(_target.Memory, _target.ModuleBase, out countAddress, out error))
            {
                return false;
            }
            if (!profile.SelectedList.TryResolve(_target.Memory, _target.ModuleBase, out slotAddress, out error))
            {
                countAddress = 0;
                return false;
            }
            return true;
        }

        /// <summary>读当前金钱值（归属校验的唯一外部依据）。读不到就说明判据不可用。</summary>
        public bool TryReadMoney(CalibrationProfile profile, out uint money)
        {
            money = 0;
            if (profile == null || !profile.Money.IsCalibrated) { return false; }

            uint moneyAddress;
            string error;
            if (!profile.Money.TryResolve(_target.Memory, _target.ModuleBase, out moneyAddress, out error))
            {
                return false;
            }
            return _target.Memory.ReadUInt32(moneyAddress, out money) && money != 0;
        }

        // ============================================================ 归属校验

        /// <summary>
        /// 对象是否归属本方。判据：对象里有没有指针指向"持有你金钱的那个对象"。
        ///
        /// 结果带缓存：每个对象要读几十次小内存，高频调用时不可能每次重算。
        /// 拿不到判据时（金钱未校准等）返回 false，即**不认**（fail-closed）。
        /// </summary>
        public bool IsOwnedObject(CalibrationProfile profile, uint objectAddress)
        {
            uint money;
            if (!TryReadMoney(profile, out money))
            {
                // 判不了就**不认**（fail-closed）。
                //
                // 这里早期是 `return true`，理由是"不能因为判不了就不干活" ——
                // 真机证明这个默认值很危险：判据一旦不可用，**所有单位（含敌方）都会被认成本方**，
                // 于是敌方单位被记进保护名单并回血（用户实测 B1 也变成了无敌）。
                // 宁可"暂时不生效但绝不误伤"，也不要"看起来能用却给敌人加血"。
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

            int probes;
            owned = TechnoOwnership.FindHouseFields(
                _target.Memory, objectAddress, money, 1, out probes).Count > 0;
            _ownedCache[objectAddress] = owned;
            return owned;
        }

        /// <summary>
        /// 归属判据不可用时记一行（去重）。
        /// 必须留下痕迹：否则"不生效"会变成又一场悬案 ——
        /// 曾经有一轮敌方单位被误保护，根因就是这个兜底悄悄放行了所有单位，而日志里毫无线索。
        /// </summary>
        private void LogOwnershipUnavailable()
        {
            if (_ownershipWarned) { return; }
            _ownershipWarned = true;
            Logger.Warn("Selection", "归属校验不可用（金钱字段读不到），"
                + "为避免误判已暂停对本方单位的判定 —— 请确认金钱仍处于已校准状态");
        }

        /// <summary>
        /// 数组里有没有本方单位。
        ///
        /// **只查前几个元素**：选中列表的前几项必然是本方单位。
        /// 每个"对象"的归属校验最多要试几百条指针，而自动更正要在 ±0x40 内试三十几个槽位 ——
        /// 挨个查 8 个元素会让首次探测卡住近一秒。查前几个既准又快。
        /// examined 回报实际检查了几个对象，用于失败时给出可追查的数字。
        /// </summary>
        private bool ArrayHasOwnedObject(CalibrationProfile profile, uint arrayAddress, uint count, out int examined)
        {
            examined = 0;
            if (count == 0) { return false; }

            // 查前几个而不是只看第一个：真机日志里同一次会话内这条判定**两次结果不同**
            //（自动更正被触发了两次），说明数组的第 0 项并不可靠
            //（可能混着"当前操作目标"这类非单位引用）。
            int limit = count < 4 ? (int)count : 4;
            for (int i = 0; i < limit; i++)
            {
                // 安静读取：候选数组地址可能是野指针，普通读取会往日志里刷失败记录
                byte[] raw = _target.Memory.ReadBytesQuiet(arrayAddress + (uint)(i * 4), 4);
                if (raw == null) { return false; }
                uint objectAddress = HexUtil.ReadUInt32(raw, 0);
                if (objectAddress < 0x10000) { continue; }

                examined++;
                if (IsOwnedObject(profile, objectAddress)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// 找出"真正指向本方对象"的选中数组。
        ///
        /// 为什么不能直接用落盘的槽位：真机数据已经三次打脸 ——
        /// 配置里的槽位（数量字段 − 0x24）**根本不在**探测器找到的合格槽位里
        ///（实测合格的是 `数量字段 − 0x0C`，符合 `VectorClass{Items, Capacity, IsInitialized, Count}`）。
        /// 与其第四次押注某个字节位置，不如让它自己认：
        /// **谁指向的对象里有本方单位，谁才是选中列表。**
        ///
        /// 顺序：配置槽位 → 上次自动更正过的槽位（缓存 5 秒）→ 数量字段 ±0x40 内逐个试。
        /// </summary>
        public bool ResolveArray(
            CalibrationProfile profile, uint countAddress, uint slotAddress, uint count,
            out uint arrayAddress, out string note)
        {
            arrayAddress = 0;
            note = "";

            uint configured;
            bool configuredValid = _target.Memory.ReadUInt32(slotAddress, out configured) && configured >= 0x10000;

            // 归属判据不可用时（金钱没校准）不做自动更正，避免误判成"全都不是本单位"
            uint money;
            if (!TryReadMoney(profile, out money))
            {
                if (!configuredValid) { return false; }
                arrayAddress = configured;
                return true;
            }

            int ignored;
            if (configuredValid && ArrayHasOwnedObject(profile, configured, count, out ignored))
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
                    && ArrayHasOwnedObject(profile, cached, count, out ignored))
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
                if (!ArrayHasOwnedObject(profile, candidate, count, out count2)) { examined += count2; continue; }
                examined += count2;

                arrayAddress = candidate;
                _resolvedSlot = slot;
                _resolvedSlotStamp = DateTime.UtcNow;

                // 只在**槽位真的变了**时报一次。
                // 否则会和"读取成功"那条日志来回改结论，导致每 5 秒重复刷一遍同样的话。
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

        // ============================================================ 面向页面 / 采集器的整体读取

        /// <summary>
        /// 读出"当前选中的对象"。
        ///
        /// `verifiedOnly = true` 时只返回**金钱锚定确认属于我方**的那些（也就是"你选中的自己的单位"）——
        /// 「自动查找总名单」的线索、扫描的交叉验证都用这一档；此时若金钱不可用则**拒绝返回**
        ///（宁可不给线索，也不拿没核对过的东西去当线索）。
        /// `maxObjects` 限制返回数量（线索引只用前几个就够）。
        /// </summary>
        public bool TryReadSelectedObjects(
            CalibrationProfile profile, int maxObjects, bool verifiedOnly,
            out List<uint> objects, out string note)
        {
            objects = new List<uint>();
            note = "";

            uint countAddress;
            uint slotAddress;
            if (!TryResolveAddresses(profile, out countAddress, out slotAddress))
            {
                note = "选中列表未校准或地址解析失败";
                return false;
            }

            uint count;
            if (!_target.Memory.ReadUInt32(countAddress, out count) || count == 0)
            {
                note = "当前没有选中任何对象（先在游戏里选中一个自己的单位）";
                return false;
            }
            if (count > SelectionProbe.MaxCount)
            {
                note = string.Format(CultureInfo.InvariantCulture,
                    "选中数量异常（读到 {0}），已拒绝读取", count);
                return false;
            }

            uint arrayAddress;
            if (!ResolveArray(profile, countAddress, slotAddress, count, out arrayAddress, out note))
            {
                return false;
            }

            HashSet<uint> seen = new HashSet<uint>();
            uint money;
            bool haveMoney = TryReadMoney(profile, out money);

            if (verifiedOnly && !haveMoney)
            {
                note = "读不到金钱值，无法确认「哪些是你自己的单位」（请确认金钱仍处于已校准状态）";
                return false;
            }

            int limit = (int)count < maxObjects ? (int)count : maxObjects;
            for (int i = 0; i < limit; i++)
            {
                uint raw;
                if (!_target.Memory.ReadUInt32(arrayAddress + (uint)(i * 4), out raw)) { break; }
                if (raw < 0x10000) { continue; }

                uint address = UnwrapObject(raw);
                if (!seen.Add(address)) { continue; }
                if (verifiedOnly && !IsOwnedObject(profile, address)) { continue; }

                objects.Add(address);
            }

            if (objects.Count == 0)
            {
                note = verifiedOnly
                    ? "选中的对象里没有一个是「金钱锚定」确认属于我方的（选中的不是自己的单位？金钱未校准？）"
                    : "选中列表里没有可用的对象";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 数组里的一项可能是"包了一层的指针"（真机两种容器都见过）：
        /// 先看它自己的头 4 字节是不是模块内的类型标记；不是就再跳一层。
        /// </summary>
        private uint UnwrapObject(uint address)
        {
            uint first;
            if (!_target.Memory.ReadUInt32(address, out first)) { return address; }
            if (InModule(first)) { return address; }

            if (first >= 0x10000 && first <= 0x7FFFFFFF)
            {
                uint second;
                if (_target.Memory.ReadUInt32(first, out second) && InModule(second)) { return first; }
            }
            return address;
        }

        private bool InModule(uint value)
        {
            return value >= _target.ModuleBase && value < _target.ModuleBase + _target.ModuleSize;
        }
    }
}
