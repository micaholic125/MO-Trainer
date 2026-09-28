using System;
using System.Collections.Generic;
using MoTrainer.Core;

namespace MoTrainer.Cheats
{
    /// <summary>一次批量操作的执行结果。**每一项都要能回答"为什么没生效"**。</summary>
    public sealed class TechnoOpResult
    {
        /// <summary>实际写入成功的对象数。</summary>
        public int Written;

        /// <summary>因为不属于目标集合而被跳过的对象数（例如秒杀时跳过自己的单位）。</summary>
        public int Skipped;

        /// <summary>写入后回读校验失败的数量。</summary>
        public int Failed;

        /// <summary>非空表示这次**什么都没做**，内容就是原因（界面直接显示这句）。</summary>
        public string Blocked = "";

        public bool Ok { get { return Blocked.Length == 0; } }

        public string Summary
        {
            get
            {
                if (Blocked.Length > 0) { return Blocked; }
                return string.Format("写入 {0} 个，跳过 {1} 个，失败 {2} 个", Written, Skipped, Failed);
            }
        }
    }

    /// <summary>
    /// 建立在 `TechnoWalker` 之上的三项批量操作：秒杀敌人 / 瞬间建造 / 全图视野。
    ///
    /// 贯穿这整个类的两条设计原则，都是前面几轮真机失败换来的：
    ///
    /// **一、缺少必要校准数据时"拒绝执行"，而不是猜着写。**
    /// 尤其是秒杀敌人：如果 `OffsetTechnoOwner` 没校准，就**无法区分敌我** ——
    /// 此时写血量的后果是把自己的部队一起清空。宁可不动，也不能出错。
    /// 界面必须显示"缺什么"，而不是开关能点、程序却悄悄什么都不做。
    ///
    /// **二、写之前先验证对象是否真的可信**（虚表在模块内），写之后回读校验。
    /// 数组地址万一找错，`TechnoWalker` 的 `Skipped` 计数会爆掉，这里据此直接拒绝，
    /// 不会往一堆随机内存上乱写。
    /// </summary>
    public static class TechnoOps
    {
        /// <summary>找不到目标时的上限，防止把"重复写同一个对象"当成有很多目标。</summary>
        public const int MaxWritesPerTick = 256;

        /// <summary>
        /// 秒杀敌人：把**不属于自己阵营**的单位血量清零。
        ///
        /// 前提（缺一不可，否则直接拒绝）：
        /// - `ownerOffset` 已校准 —— 否则分不出敌我，绝不能写；
        /// - `healthOffset` 已校准 —— 否则不知道写哪。
        ///
        /// `myOwner` 由调用方提供：可以从"自己选中的单位"的阵营字段取，
        /// 也可以从持有金钱的阵营对象取（那边已经有校准结果可用）。
        /// </summary>
        public static TechnoOpResult KillEnemies(
            MemoryIO memory, IList<TechnoObject> objects,
            uint ownerOffset, uint healthOffset, uint myOwner)
        {
            TechnoOpResult result = new TechnoOpResult();

            if (memory == null) { result.Blocked = "内存接口不可用"; return result; }
            if (ownerOffset == 0) { result.Blocked = "未校准「对象阵营」偏移，无法区分敌我，已拒绝执行"; return result; }
            if (healthOffset == 0) { result.Blocked = "未校准「对象血量」偏移，无法执行"; return result; }
            if (myOwner == 0) { result.Blocked = "未识别出你的阵营，无法区分敌我，已拒绝执行"; return result; }
            if (objects == null || objects.Count == 0) { result.Blocked = "没有遍历到任何对象"; return result; }

            int guard = 0;
            for (int i = 0; i < objects.Count && guard < MaxWritesPerTick; i++)
            {
                uint address = objects[i].Address;
                uint owner = TechnoWalker.ReadOwner(memory, address, ownerOffset);

                // 阵营读不出来 → 归属不明，绝不冒险
                if (owner == 0 || owner == myOwner) { result.Skipped++; continue; }

                if (memory.WriteUInt32Verified(address + healthOffset, 0)) { result.Written++; guard++; }
                else { result.Failed++; }
            }

            return result;
        }

        /// <summary>
        /// 瞬间建造：把**自己阵营**的建造/生产进度清零，使生产在下一帧立刻完成。
        ///
        /// 只动自己的对象 —— 给敌人也清零等于帮敌人加速，属于反向作弊。
        /// 同样要求阵营与进度字段都已校准。
        /// </summary>
        public static TechnoOpResult InstantBuild(
            MemoryIO memory, IList<TechnoObject> objects,
            uint ownerOffset, uint progressOffset, uint myOwner)
        {
            TechnoOpResult result = new TechnoOpResult();

            if (memory == null) { result.Blocked = "内存接口不可用"; return result; }
            if (progressOffset == 0) { result.Blocked = "未校准「生产进度」偏移，已拒绝执行"; return result; }
            if (ownerOffset == 0) { result.Blocked = "未校准「对象阵营」偏移，无法限定只对自己的单位生效"; return result; }
            if (myOwner == 0) { result.Blocked = "未识别出你的阵营，已拒绝执行"; return result; }
            if (objects == null || objects.Count == 0) { result.Blocked = "没有遍历到任何对象"; return result; }

            int guard = 0;
            for (int i = 0; i < objects.Count && guard < MaxWritesPerTick; i++)
            {
                uint address = objects[i].Address;
                uint owner = TechnoWalker.ReadOwner(memory, address, ownerOffset);
                if (owner == 0 || owner != myOwner) { result.Skipped++; continue; }

                uint current = 0;
                memory.ReadUInt32(address + progressOffset, out current);
                // 进度本来就是 0 的（没有在生产的）不用写，省下大量无意义的写操作
                if (current == 0) { result.Skipped++; continue; }

                if (memory.WriteUInt32Verified(address + progressOffset, 0)) { result.Written++; guard++; }
                else { result.Failed++; }
            }

            return result;
        }

        /// <summary>
        /// 全图视野：分块把战争迷雾 / 未探索黑幕清零。
        ///
        /// 与上面两项不同，这里是**一大片连续位图**（典型几百 KB 到几 MB），
        /// 一次性写完会把游戏卡住一帧以上。所以按 `chunkBytes` 分片，
        /// 由调用方在锁定线程里**每拍写一片**，并把 `cursor` 带回来继续 ——
        /// 这样既不留卡顿，又能在一两秒内铺满整张图。
        /// </summary>
        public static TechnoOpResult RevealMap(
            MemoryIO memory, uint shroudAddress, uint shroudSize, uint chunkBytes, ref uint cursor)
        {
            TechnoOpResult result = new TechnoOpResult();

            if (memory == null) { result.Blocked = "内存接口不可用"; return result; }
            if (shroudAddress < 0x10000) { result.Blocked = "未校准「迷雾位图」地址，已拒绝执行"; return result; }
            if (shroudSize == 0) { result.Blocked = "未校准「迷雾位图大小」，已拒绝执行"; return result; }
            if (chunkBytes == 0) { chunkBytes = 64 * 1024; }

            // cursor 只增不减：一轮铺完后停在末尾，下一轮由调用方决定是否重置。
            if (cursor >= shroudSize) { cursor = 0; }

            uint remaining = shroudSize - cursor;
            uint thisChunk = remaining < chunkBytes ? remaining : chunkBytes;
            uint address = shroudAddress + cursor;

            byte[] zeros = new byte[thisChunk];
            if (!memory.Write(address, zeros, 0, (int)thisChunk))
            {
                result.Blocked = string.Format("写入迷雾位图失败（0x{0:X8} 起 {1} 字节）", address, thisChunk);
                return result;
            }

            cursor += thisChunk;
            result.Written = (int)thisChunk;

            // 铺完一轮就把游标归零，下拍从头再来 ——
            // 迷雾会被游戏逐帧重新绘制，必须持续覆盖，不能只清一次。
            if (cursor >= shroudSize) { cursor = 0; }
            return result;
        }

        /// <summary>
        /// 判断遍历结果是否可信。**在执行任何写操作之前必须先过这一关。**
        ///
        /// 判据：读到的候选里有足够高比例通过了"虚表落在模块内"的校验。
        /// 数组地址找错时，读到的是随机内存，绝大多数都会落在模块区间之外 ——
        /// 这时候一条都不能信。
        /// </summary>
        public static bool IsTrustworthy(TechnoWalkResult walk, out string reason)
        {
            reason = "";
            if (walk == null) { reason = "遍历结果为空"; return false; }
            if (!walk.Ok) { reason = walk.Error; return false; }

            int total = walk.Scanned;
            if (total == 0) { reason = "对象数组里没有任何指针"; return false; }

            // 只要求"确实数出了一批对象"，**不再拿比例当门槛**。
            //
            // 真机实测：这类容器的容量很大而实际只用了开头一小段
            // （容量 4096、里面真实单位二十来个），按"通过率 ≥50%"判定
            // 会把**正确的名单**误判成"可疑"，挡住后面所有功能。
            //
            // 安全性由两道更硬的关卡保证，不靠这个比例：
            //   ① 名单里必须真的含有"你选中的那个单位"（查找阶段已强制校验，命中 0 就不保存）；
            //   ② 每个写入目标在写之前都会单独校验"头 4 字节是模块内的类型标记"。
            if (walk.Count < 4)
            {
                reason = string.Format("只数出 {0} 个对象，不足以确认这是单位名单", walk.Count);
                return false;
            }
            return true;
        }
    }
}
