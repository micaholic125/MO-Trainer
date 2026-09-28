using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MoTrainer.Util;

namespace MoTrainer.Core
{
    /// <summary>
    /// 多级指针链。约定与 Cheat Engine 一致：
    /// 偏移链 [A, B, C] 表示 `[[模块基址 + A] + B] + C`，
    /// 即前 n-1 个偏移之后各解引用一次，最后一个偏移直接相加得到目标地址。
    ///
    /// 单元素链 [A] 表示纯静态地址 `模块基址 + A`（不解引用）。
    /// 任何一级读取失败都返回失败，绝不返回"看起来能用"的脏地址。
    /// </summary>
    public sealed class PointerChain
    {
        private static readonly PointerChain EmptyChain = new PointerChain(null);

        private readonly uint[] _offsets;

        public PointerChain(uint[] offsets)
        {
            if (offsets == null || offsets.Length == 0)
            {
                _offsets = new uint[0];
            }
            else
            {
                _offsets = new uint[offsets.Length];
                Array.Copy(offsets, _offsets, offsets.Length);
            }
        }

        public static PointerChain Empty { get { return EmptyChain; } }

        public uint[] Offsets
        {
            get
            {
                uint[] copy = new uint[_offsets.Length];
                Array.Copy(_offsets, copy, _offsets.Length);
                return copy;
            }
        }

        public bool IsEmpty { get { return _offsets.Length == 0; } }

        /// <summary>解引用次数（0 表示纯静态地址）。</summary>
        public int Depth { get { return _offsets.Length <= 1 ? 0 : _offsets.Length - 1; } }

        public int LevelCount { get { return _offsets.Length; } }

        // ============================================================ 解析

        /// <summary>解析 "0xA8B230, 0x30, 0x18" 形式的偏移链文本；支持用 "->" 分隔。</summary>
        public static PointerChain Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) { return null; }

            string normalized = text.Replace("->", ",").Replace(">", ",");
            string[] parts = normalized.Split(
                new char[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);

            List<uint> values = new List<uint>();
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0) { continue; }

                uint value;
                if (!HexUtil.TryParseHex(token, out value)) { return null; }
                values.Add(value);
            }

            if (values.Count == 0) { return null; }
            return new PointerChain(values.ToArray());
        }

        /// <summary>序列化为配置用的短字符串。</summary>
        public string Serialize()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < _offsets.Length; i++)
            {
                if (i > 0) { builder.Append(','); }
                builder.Append("0x");
                builder.Append(_offsets[i].ToString("X", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        /// <summary>
        /// 人类可读形式，用于日志与校准报告。
        /// 采用与 Cheat Engine 一致的书写法，例如 [[0x400000+0xA8B230]+0x30]+0x18。
        /// </summary>
        public string Format(uint moduleBase)
        {
            if (_offsets.Length == 0) { return "(未校准)"; }

            if (_offsets.Length == 1)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8}", moduleBase + _offsets[0]);
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < _offsets.Length - 1; i++) { builder.Append('['); }
            builder.Append("0x");
            builder.Append(moduleBase.ToString("X", CultureInfo.InvariantCulture));

            for (int i = 0; i < _offsets.Length; i++)
            {
                builder.Append("+0x");
                builder.Append(_offsets[i].ToString("X", CultureInfo.InvariantCulture));
                if (i < _offsets.Length - 1) { builder.Append(']'); }
            }
            return builder.ToString();
        }

        public override string ToString()
        {
            return Serialize();
        }

        // ============================================================ 求值

        /// <summary>解析出最终的目标地址。失败时返回 false 并给出可读原因。</summary>
        public bool TryResolve(MemoryIO memory, uint moduleBase, out uint address, out string error)
        {
            address = 0;
            error = "";

            if (memory == null)
            {
                error = "内存内核不可用";
                return false;
            }
            if (_offsets.Length == 0)
            {
                error = "未校准：偏移链为空";
                return false;
            }

            uint cursor = moduleBase;
            for (int i = 0; i < _offsets.Length; i++)
            {
                cursor += _offsets[i];

                bool isLast = i == _offsets.Length - 1;
                if (isLast)
                {
                    address = cursor;
                    return true;
                }

                uint next;
                if (!memory.ReadUInt32(cursor, out next))
                {
                    error = string.Format(CultureInfo.InvariantCulture,
                        "第 {0} 级读取失败：0x{1:X8}", i + 1, cursor);
                    return false;
                }
                if (next < 0x00010000)
                {
                    error = string.Format(CultureInfo.InvariantCulture,
                        "第 {0} 级指针异常：0x{1:X8} → 0x{2:X8}", i + 1, cursor, next);
                    return false;
                }
                cursor = next;
            }

            error = "偏移链无效";
            return false;
        }

        /// <summary>解析失败时返回 0。</summary>
        public uint ResolveOrZero(MemoryIO memory, uint moduleBase)
        {
            uint address;
            string error;
            return TryResolve(memory, moduleBase, out address, out error) ? address : 0;
        }

        /// <summary>逐级轨迹，用于校准报告排查哪一级断掉。</summary>
        public List<string> Trace(MemoryIO memory, uint moduleBase)
        {
            List<string> lines = new List<string>();
            if (_offsets.Length == 0)
            {
                lines.Add("(未校准：偏移链为空)");
                return lines;
            }

            uint cursor = moduleBase;
            for (int i = 0; i < _offsets.Length; i++)
            {
                cursor += _offsets[i];
                bool isLast = i == _offsets.Length - 1;
                if (isLast)
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "L{0}: +0x{1:X} → 最终地址 0x{2:X8}", i, _offsets[i], cursor));
                    break;
                }

                uint next;
                if (!memory.ReadUInt32(cursor, out next))
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "L{0}: +0x{1:X} → 0x{2:X8} 读取失败", i, _offsets[i], cursor));
                    break;
                }
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "L{0}: +0x{1:X} → 0x{2:X8} → [0x{2:X8}] = 0x{3:X8}", i, _offsets[i], cursor, next));
                cursor = next;
            }
            return lines;
        }
    }
}
