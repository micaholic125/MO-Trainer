using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 「电力侦查」（2026-10-09 新增；`[S4-3]` B 方案第三步）—— **纯只读**，一个字节都不写。
    ///
    /// 为什么需要它：`+0xD0` 那个候选已被真机试探**否掉**（写 5000、界面电力读数纹丝不动）。
    /// 电力真正的来源最可能在**建筑类型对象**里（尤里复仇的电力 `Power=` 是"类型"上的属性，
    /// 同类建筑共用一份）—— 而类型对象**整局都不变化**，所以采集报告（只记"变化过的字段"）
    /// 里永远看不到它。只能把选中那座建筑的对象**摊开看一遍**：
    ///
    ///   ① 逐 4 字节扫描对象头部：哪些位置放着**指向别处的指针**；
    ///   ② 顺着指针看一眼：目标里有没有**指向可打印 ASCII 字符串**的指针
    ///      —— 建筑类型对象里存着建筑 ID（"GAPOWR" 这种），这是最像"类型对象"的特征；
    ///   ③ 顺手列出头部里**数值像电力读数**的字段（0~5000 的小整数），供下一步试探挑选；
    ///   ④ 把家庭电力相关字段（`House + 0x53A4` / `+0x53A8`）的当前值一并记下做对照。
    ///
    /// 输出全部落进日志（用户点一下、说一声，开发者自己读），**不改动游戏**。
    /// </summary>
    public static class PowerScout
    {
        /// <summary>扫描对象头部多少字节（真机上这些对象的字段都挤在前 0x800 里）。</summary>
        public const int ScanBytes = 0x600;

        /// <summary>顺着指针看目标的前多少个双字（找"指向字符串的指针"）。</summary>
        private const int PeekDwords = 24;

        public sealed class Finding
        {
            public uint ObjectOffset;
            public uint Value;
            public string Kind = "";        // "疑似类型对象" / "字符串" / "小整数候选"
            public string Note = "";
        }

        /// <summary>跑一次只读侦查，返回可直接写进日志的文本。</summary>
        public static string Run(MemoryIO memory, CalibrationProfile profile, uint moduleBase, uint objectAddress)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 电力侦查（只读，不写任何内存）===");

            if (memory == null) { return text.AppendLine("内存接口不可用").ToString(); }
            if (objectAddress < 0x10000) { return text.AppendLine("没有读到选中的对象").ToString(); }

            uint vtable;
            memory.ReadUInt32(objectAddress, out vtable);
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "对象 0x{0:X8}｜头 4 字节（类型表）0x{1:X8}", objectAddress, vtable));

            // 家庭电力相关字段：做对照用（真机实测：这两个是"界面显示值"，写它没用）
            if (profile != null && profile.PowerOutput.IsCalibrated)
            {
                uint outputAddress;
                string error;
                if (profile.PowerOutput.TryResolve(memory, moduleBase, out outputAddress, out error))
                {
                    uint outputValue;
                    memory.ReadUInt32(outputAddress, out outputValue);
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "家庭电力产出字段 0x{0:X8} = {1}", outputAddress, outputValue));
                }
            }

            byte[] head = memory.ReadBytesQuiet(objectAddress, ScanBytes);
            if (head == null)
            {
                text.AppendLine("读不动这个对象的头部 —— 请确认选中的是一座你自己的建筑");
                return text.ToString();
            }

            List<Finding> pointers = new List<Finding>();
            List<Finding> numbers = new List<Finding>();

            for (int offset = 0; offset + 4 <= head.Length; offset += 4)
            {
                uint value = ReadUInt32(head, offset);

                // ① 像指针的值：跟着看一眼
                if (value >= 0x10000 && value <= 0x7FFFFFFF && memory.Describe(value) != null)
                {
                    string note = PeekTarget(memory, value);
                    if (note.Length > 0)
                    {
                        Finding f = new Finding();
                        f.ObjectOffset = (uint)offset;
                        f.Value = value;
                        f.Kind = note.IndexOf("字符串") >= 0 ? "疑似类型对象" : "指针";
                        f.Note = note;
                        pointers.Add(f);
                    }
                    continue;
                }

                // ② 像"电力读数"的小整数（尤里复仇里电厂产出一般是 50~500 这个量级）
                if (value >= 10 && value <= 5000)
                {
                    Finding f = new Finding();
                    f.ObjectOffset = (uint)offset;
                    f.Value = value;
                    f.Kind = "小整数候选";
                    numbers.Add(f);
                }
            }

            text.AppendLine();
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "—— 指向别处的字段（共 {0} 个；带「疑似类型对象」的最值得看）——", pointers.Count));
            for (int i = 0; i < pointers.Count && i < 40; i++)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  +0x{0:X3} → 0x{1:X8}｜{2}｜{3}",
                    pointers[i].ObjectOffset, pointers[i].Value, pointers[i].Kind, pointers[i].Note));
            }

            text.AppendLine();
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "—— 数值看起来像「电力读数」的字段（共 {0} 个）——", numbers.Count));
            for (int i = 0; i < numbers.Count && i < 60; i++)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  +0x{0:X3} = {1}", numbers[i].ObjectOffset, numbers[i].Value));
            }

            // ③ **深挖**：把上面那些"疑似类型对象"的结构本身也摊开看一遍（只读）。
            // 为什么：电力在尤里复仇里是"建筑类型"上的属性（同类建筑共用一份），
            // 而类型对象整局不变、采集报告里永远看不到 —— 只能现场看。
            text.AppendLine();
            text.AppendLine("—— 深挖：指针目标结构内部（每个最多列 40 个字段）——");
            for (int i = 0; i < pointers.Count && i < 6; i++)
            {
                AppendTargetDump(text, memory, pointers[i].ObjectOffset, pointers[i].Value);
            }

            text.AppendLine();
            text.AppendLine("本次**没有写入任何内存**。把这段（或整个日志）发回给开发者即可。");
            return text.ToString();
        }

        /// <summary>
        /// 把一个"指针目标结构"摊开看（**只读**）：字符串指针（带原文）+ 小整数，
        /// 两类都按"像不像建筑类型的属性"来列（电力值一般就是 50~500 这种小整数）。
        /// </summary>
        private static void AppendTargetDump(StringBuilder text, MemoryIO memory, uint fromOffset, uint target)
        {
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  · 目标 0x{0:X8}（来自对象 +0x{1:X3}）：", target, fromOffset));

            byte[] block = memory.ReadBytesQuiet(target, 0x400);
            if (block == null) { text.AppendLine("      读不动"); return; }

            // 字符串指针（大概率是名字/ID/EVA 事件名）
            int strings = 0;
            for (int i = 0; i + 4 <= block.Length && strings < 6; i += 4)
            {
                uint inner = ReadUInt32(block, i);
                if (inner < 0x10000 || inner > 0x7FFFFFFF) { continue; }
                string ascii = ReadAsciiString(memory, inner, 40);
                if (ascii != null)
                {
                    strings++;
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "      +0x{0:X3} → 字符串「{1}」", i, ascii));
                }
            }

            // 小整数（电力值就在这一堆里）
            StringBuilder numbers = new StringBuilder();
            int count = 0;
            for (int i = 0; i + 4 <= block.Length; i += 4)
            {
                uint value = ReadUInt32(block, i);
                if (value < 10 || value > 100000) { continue; }
                if (count > 0) { numbers.Append("、"); }
                numbers.Append(string.Format(CultureInfo.InvariantCulture, "+0x{0:X3}={1}", i, value));
                count++;
                if (count >= 40) { break; }
            }
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "      小整数 {0} 个：{1}", count, numbers.ToString()));
        }

        /// <summary>
        /// 顺着一个指针看一眼它指向的东西：里面有没有**指向可打印字符串的指针**？
        /// 有的话基本就是"类型对象"（建筑 ID 是字符串）。返回一句人话说明。
        /// </summary>
        private static string PeekTarget(MemoryIO memory, uint target)
        {
            byte[] block = memory.ReadBytesQuiet(target, PeekDwords * 4);
            if (block == null) { return ""; }

            for (int i = 0; i + 4 <= block.Length; i += 4)
            {
                uint inner = ReadUInt32(block, i);
                if (inner < 0x10000 || inner > 0x7FFFFFFF) { continue; }

                string ascii = ReadAsciiString(memory, inner, 16);
                if (ascii != null && ascii.Length >= 3)
                {
                    return string.Format(CultureInfo.InvariantCulture,
                        "目标 +0x{0:X2} 指向字符串「{1}」", i, ascii);
                }
            }
            return "";
        }

        /// <summary>读一段可打印 ASCII（读不出或不长这样返回 null）。</summary>
        private static string ReadAsciiString(MemoryIO memory, uint address, int maxLength)
        {
            byte[] raw = memory.ReadBytesQuiet(address, maxLength);
            if (raw == null) { return null; }

            StringBuilder text = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                byte b = raw[i];
                if (b == 0) { break; }
                if (b < 0x20 || b > 0x7E) { return null; }   // 出现不可打印字符 → 不是字符串
                text.Append((char)b);
            }
            // 至少 5 个字符、且只由字母/数字/下划线组成 —— 真机踩过：3 个字符的巧合（如 "0hO"）
            // 会把"阵营对象的虚表指针"误报成"类型对象里的 ID 字符串"。
            if (text.Length < 5) { return null; }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '_';
                if (!ok) { return null; }
            }
            return text.ToString();
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }
    }
}
