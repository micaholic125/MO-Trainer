using System;
using System.Collections.Generic;

namespace MoTrainer.Cheats
{
    /// <summary>
    /// 「不允许杀掉」的阵营名单 —— **由用户手动标记**（2026-10-08 晚新增，回应用户提问）。
    ///
    /// 为什么需要它：秒杀的敌我判据只有两条 ——「不是你」+「阵营有钱」，
    /// 它**不知道"同盟"这回事**。于是 AI 队友（以及局域网里的人类队友）会被当成敌人一起清掉，
    /// 而这类误伤**不可逆**（队友的兵和建筑真的没了）。
    /// 用户 2026-10-08 拍板采用方案 A：**游戏里点选队友的任意一个单位 → 回修改器点一下「标记友军」**，
    /// 该阵营从此与"疑似中立"享受同一层保护（永不参与秒杀）。
    ///
    /// 为什么是进程内共享的静态状态（与 `FeatureCard` 的"知情确认"同一做法）：
    /// 标记动作发生在「高级功能」页，而**消费它的是引擎的锁定线程**（`CheatEngine.ApplyKillEnemies`），
    /// 两者手里没有彼此引用；共用一个登记处是最省事、也最不容易接错的做法。
    ///
    /// 三条硬规矩：
    ///   · **会话级**：阵营地址每局都变，所以换局（游戏重连）时必须 `Reset()`，
    ///     否则旧地址可能落到别的阵营头上（"保护错人"= 该杀的没杀），甚至会保护到真正的敌人；
    ///   · **只增不减地保护**：误标记一个敌人的后果只是"少杀"，方向安全（宁可少杀、绝不误杀）；
    ///   · **线程安全**：界面（按钮）写、锁定线程（每 0.5 秒一拍）读，必须加锁。
    /// </summary>
    public static class AllyGuard
    {
        private static readonly object _lock = new object();
        private static readonly List<uint> _houses = new List<uint>();

        /// <summary>标记过的阵营个数。</summary>
        public static int Count
        {
            get { lock (_lock) { return _houses.Count; } }
        }

        /// <summary>
        /// 换局 / 重新连接游戏时清空 —— 阵营地址是每局重新分配的，跨局沿用等于保护错人。
        /// </summary>
        public static void Reset()
        {
            lock (_lock) { _houses.Clear(); }
        }

        /// <summary>
        /// 把某个阵营记入"不许杀"名单。
        /// `house` 必须是看起来像真阵营的地址（调用方负责先用 `TechnoOwnership.TryReadHouseMoney` 验过）；
        /// 已经在名单里时返回 false（不是错误，只是重复标记）。
        /// </summary>
        public static bool Mark(uint house)
        {
            if (house < 0x10000) { return false; }
            lock (_lock)
            {
                if (_houses.Contains(house)) { return false; }
                _houses.Add(house);
                return true;
            }
        }

        /// <summary>这个阵营是否已被标记为友军（0 一律不算）。</summary>
        public static bool IsProtected(uint house)
        {
            if (house < 0x10000) { return false; }
            lock (_lock) { return _houses.Contains(house); }
        }

        /// <summary>取一份当前名单的快照（供日志/界面显示）。</summary>
        public static List<uint> Snapshot()
        {
            lock (_lock) { return new List<uint>(_houses); }
        }
    }
}
