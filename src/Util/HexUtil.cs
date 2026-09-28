using System;
using System.Globalization;
using System.Text;

namespace MoTrainer.Util
{
    /// <summary>十六进制 / 字节序 / 特征码模板相关的纯函数工具。</summary>
    public static class HexUtil
    {
        /// <summary>解析十六进制字符串，允许带或不带 0x 前缀。</summary>
        public static bool TryParseHex(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) { return false; }
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(2); }
            if (s.Length == 0 || s.Length > 8) { return false; }
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>解析十进制或十六进制（0x 前缀）整数。</summary>
        public static bool TryParseAddressOrNumber(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) { return false; }
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseHex(s, out value);
            }
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static string ToHex(uint value)
        {
            return "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
        }

        public static string ToHexLong(ulong value)
        {
            return "0x" + value.ToString("X", CultureInfo.InvariantCulture);
        }

        public static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        public static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        public static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        public static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        /// <summary>
        /// 解析 AOB 模板，如 "8B 0D ?? ?? ?? ?? 89"。null 元素表示通配字节。
        /// </summary>
        public static byte?[] ParsePattern(string text)
        {
            if (string.IsNullOrEmpty(text)) { return null; }
            string[] parts = text.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { return null; }

            byte?[] pattern = new byte?[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == "?" || p == "??" || p == "*")
                {
                    pattern[i] = null;
                    continue;
                }
                string s = p;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(2); }
                if (s.Length != 2) { return null; }
                byte b;
                if (!byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) { return null; }
                pattern[i] = b;
            }
            return pattern;
        }

        /// <summary>在 buffer[offset] 处匹配模板，全部字节（含通配）需落在缓冲区内。</summary>
        public static bool MatchPattern(byte[] buffer, int offset, byte?[] pattern)
        {
            if (buffer == null || pattern == null) { return false; }
            if (offset < 0 || offset + pattern.Length > buffer.Length) { return false; }
            for (int i = 0; i < pattern.Length; i++)
            {
                byte? expected = pattern[i];
                if (!expected.HasValue) { continue; }
                if (buffer[offset + i] != expected.Value) { return false; }
            }
            return true;
        }

        public static string BytesToText(byte[] data, int offset, int count)
        {
            if (data == null) { return ""; }
            StringBuilder sb = new StringBuilder(count * 3);
            for (int i = 0; i < count && offset + i < data.Length; i++)
            {
                if (i > 0) { sb.Append(' '); }
                sb.Append(data[offset + i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
