using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoTrainer.Core;

namespace MoTrainer.Tools
{
    /// <summary>
    /// 「整机快照」工具 —— **一次性开发工具，不参与产品编译**（`build.ps1` 只编 `src\`）。
    ///
    /// ## 为什么要它
    ///
    /// 2026-10-12 前几轮的比对都是"**当场比、只留摘要**"（`ShroudShapeScan watch/diff` 只写"哪块变了"
    /// 加一个校验和），于是每次换个判据就得**重新提权、重新让用户跑一局**。
    /// 结果是判据改一次、用户陪一次 —— 这是本日最大的浪费。
    ///
    /// 这一版换个做法：**把可写内存整块倒到一个文件里**，之后所有分析（字节级比对、交叉求交、
    /// 找步长、铺二维图、与截图轮廓对形状）**全部在离线文件上做，想试多少种判据就试多少种**。
    ///
    /// 一次快照 ≈ 350 MB 提交内存，其中全零的 64 KB 块**不写盘**（多数页面大部分是零），
    /// 实际文件通常小得多；解压侧把"文件里没有的地址"当作零即可。
    ///
    /// ## 文件格式（小端）
    ///
    /// ```
    /// magic  "MOSNAP01" (8 字节)
    /// int32  blockSize        // 64 KB
    /// int32  stampLen + UTF-8 时间串
    /// 记录流（读到 kind==0 为止）：
    ///   kind=1 区域头：uint64 base, uint64 size, uint32 protect, uint32 type
    ///   kind=2 数据块：uint32 addr, int32 len, len 字节
    ///   kind=0 结束  ：int32 总块数
    /// ```
    ///
    /// **全程只读**（只用 `ReadBytesQuiet`），一个字节都不写。
    ///
    /// 用法：`MemSnap.exe snap <输出文件> [all]`　／　`MemSnap.exe info <快照文件>`
    /// </summary>
    public static class MemSnap
    {
        private const int BlockSize = 0x10000;          // 64 KB
        private const uint MinAddress = 0x10000;

        private static readonly byte[] Magic = new byte[]
        {
            (byte)'M', (byte)'O', (byte)'S', (byte)'N', (byte)'A', (byte)'P', (byte)'0', (byte)'1'
        };

        public static int Main(string[] args)
        {
            if (args == null || args.Length < 2)
            {
                Console.WriteLine("usage: MemSnap snap <outfile> [all]");
                Console.WriteLine("       MemSnap info <snapfile>");
                return 1;
            }
            if (string.Equals(args[0], "info", StringComparison.OrdinalIgnoreCase))
            {
                return Info(args[1]);
            }
            if (!string.Equals(args[0], "snap", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("unknown mode: " + args[0]);
                return 1;
            }
            bool includeReadOnly = false;
            if (args.Length > 2 && string.Equals(args[2], "all", StringComparison.OrdinalIgnoreCase))
            {
                includeReadOnly = true;
            }
            return Snap(args[1], includeReadOnly);
        }

        private static int Snap(string outPath, bool includeReadOnly)
        {
            ProcessAttach attach = new ProcessAttach();
            if (!attach.TryAttach())
            {
                Console.WriteLine("attach failed: " + attach.Describe());
                Console.WriteLine("(if the game runs elevated, this tool must run elevated too)");
                return 2;
            }
            Console.WriteLine("attached: " + attach.Describe());

            MemoryIO io = new MemoryIO(attach);
            List<MemoryRegion> regions = io.EnumRegions(
                MinAddress, Win32Api.MaxUserAddress32, true, includeReadOnly ? false : true, 0);
            Console.WriteLine("regions to walk: " + regions.Count);

            FileStream fs = null;
            BinaryWriter bw = null;
            int totalBlocks = 0;
            long totalBytes = 0;
            int totalZeroBlocks = 0;
            DateTime started = DateTime.UtcNow;

            try
            {
                fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                bw = new BinaryWriter(fs);
                bw.Write(Magic);
                bw.Write(BlockSize);
                byte[] stamp = Encoding.UTF8.GetBytes(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                bw.Write(stamp.Length);
                bw.Write(stamp);

                for (int i = 0; i < regions.Count; i++)
                {
                    MemoryRegion region = regions[i];
                    if (region.Size < (ulong)BlockSize) { continue; }
                    if (!region.Readable) { continue; }
                    if (Win32Api.IsExecutableProtect(region.Protect)) { continue; }

                    ulong blocks = region.Size / (ulong)BlockSize;
                    if (blocks == 0) { continue; }

                    bw.Write((byte)1);
                    bw.Write((ulong)region.Base);
                    bw.Write((ulong)region.Size);
                    bw.Write((uint)region.Protect);
                    bw.Write((uint)region.Type);

                    for (ulong b = 0; b < blocks; b++)
                    {
                        ulong raw = region.Base + b * (ulong)BlockSize;
                        if (raw > 0xFFFFFFFFul) { break; }
                        byte[] data = io.ReadBytesQuiet((uint)raw, BlockSize);
                        if (data == null) { continue; }
                        if (IsAllZero(data)) { totalZeroBlocks++; continue; }

                        bw.Write((byte)2);
                        bw.Write((uint)raw);
                        bw.Write(data.Length);
                        bw.Write(data);
                        totalBlocks++;
                        totalBytes += data.Length;
                    }

                    if ((i % 100) == 0)
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  region {0}/{1}  blocks={2}  bytes={3:0.0} MB  elapsed={4:0.0}s",
                            i, regions.Count, totalBlocks, totalBytes / 1048576.0,
                            (DateTime.UtcNow - started).TotalSeconds));
                    }
                }

                bw.Write((byte)0);
                bw.Write(totalBlocks);
                bw.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine("snapshot failed: " + ex.Message);
                return 3;
            }
            finally
            {
                if (bw != null) { try { bw.Close(); } catch (Exception) { } }
                else if (fs != null) { try { fs.Close(); } catch (Exception) { } }
                attach.Dispose();
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "done: {0}  {1} blocks  {2:0.0} MB  (skipped {3} zero blocks)  {4:0.0}s",
                outPath, totalBlocks, totalBytes / 1048576.0, totalZeroBlocks,
                (DateTime.UtcNow - started).TotalSeconds));
            return 0;
        }

        private static bool IsAllZero(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0) { return false; }
            }
            return true;
        }

        private static int Info(string path)
        {
            if (!File.Exists(path)) { Console.WriteLine("no such file: " + path); return 4; }
            FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            BinaryReader br = new BinaryReader(fs);
            try
            {
                byte[] magic = br.ReadBytes(8);
                if (magic.Length != 8) { Console.WriteLine("file too short"); return 5; }
                for (int i = 0; i < 8; i++)
                {
                    if (magic[i] != Magic[i]) { Console.WriteLine("bad magic"); return 5; }
                }
                int blockSize = br.ReadInt32();
                int stampLen = br.ReadInt32();
                string stamp = Encoding.UTF8.GetString(br.ReadBytes(stampLen));
                Console.WriteLine("snapshot " + path);
                Console.WriteLine("  taken    : " + stamp);
                Console.WriteLine("  blockSize: " + blockSize);

                int regions = 0;
                int blocks = 0;
                long bytes = 0;
                List<uint> addrs = new List<uint>();
                while (true)
                {
                    int kind = fs.Position < fs.Length ? br.ReadByte() : 0;
                    if (kind == 0)
                    {
                        int declared = fs.Position + 4 <= fs.Length ? br.ReadInt32() : -1;
                        Console.WriteLine("  regions  : " + regions);
                        Console.WriteLine("  blocks   : " + blocks + " (declared " + declared + ")");
                        Console.WriteLine("  bytes    : " + (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
                        if (addrs.Count > 0)
                        {
                            addrs.Sort();
                            Console.WriteLine("  addr span: 0x" + addrs[0].ToString("X8") + " .. 0x"
                                + addrs[addrs.Count - 1].ToString("X8"));
                        }
                        return 0;
                    }
                    if (kind == 1)
                    {
                        br.ReadUInt64(); br.ReadUInt64(); br.ReadUInt32(); br.ReadUInt32();
                        regions++;
                    }
                    else if (kind == 2)
                    {
                        uint addr = br.ReadUInt32();
                        int len = br.ReadInt32();
                        br.ReadBytes(len);
                        blocks++;
                        bytes += len;
                        addrs.Add(addr);
                    }
                    else
                    {
                        Console.WriteLine("unknown record kind " + kind + " at offset " + fs.Position);
                        return 5;
                    }
                }
            }
            finally
            {
                br.Close();
            }
        }
    }
}
