using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MoTrainer.Util
{
    public enum JsonKind
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    /// <summary>
    /// 轻量 JSON 读写，不依赖任何第三方库。
    /// 用于校准配置（profile.json）、用户设置（settings.json）与校准报告。
    /// 对象保持插入顺序，便于人工阅读与 diff。
    /// </summary>
    public sealed class JsonValue
    {
        private readonly JsonKind _kind;
        private bool _bool;
        private double _number;
        private string _text;
        private List<JsonValue> _array;
        private List<string> _keys;
        private List<JsonValue> _values;

        private JsonValue(JsonKind kind)
        {
            _kind = kind;
        }

        // ============================================================ 构造

        public static JsonValue Null()
        {
            return new JsonValue(JsonKind.Null);
        }

        public static JsonValue NewObject()
        {
            JsonValue value = new JsonValue(JsonKind.Object);
            value._keys = new List<string>();
            value._values = new List<JsonValue>();
            return value;
        }

        public static JsonValue NewArray()
        {
            JsonValue value = new JsonValue(JsonKind.Array);
            value._array = new List<JsonValue>();
            return value;
        }

        public static JsonValue FromString(string text)
        {
            JsonValue value = new JsonValue(JsonKind.String);
            value._text = text == null ? "" : text;
            return value;
        }

        public static JsonValue FromNumber(double number)
        {
            JsonValue value = new JsonValue(JsonKind.Number);
            value._number = number;
            return value;
        }

        public static JsonValue FromUInt(uint number)
        {
            return FromNumber(number);
        }

        public static JsonValue FromBool(bool flag)
        {
            JsonValue value = new JsonValue(JsonKind.Bool);
            value._bool = flag;
            return value;
        }

        // ============================================================ 属性

        public JsonKind Kind { get { return _kind; } }

        public int Count
        {
            get
            {
                if (_kind == JsonKind.Object) { return _keys.Count; }
                if (_kind == JsonKind.Array) { return _array.Count; }
                return 0;
            }
        }

        public bool IsNull { get { return _kind == JsonKind.Null; } }

        public JsonValue Get(string key)
        {
            if (_kind != JsonKind.Object || key == null) { return null; }
            int index = _keys.IndexOf(key);
            return index < 0 ? null : _values[index];
        }

        public JsonValue GetAt(int index)
        {
            if (_kind != JsonKind.Array) { return null; }
            if (index < 0 || index >= _array.Count) { return null; }
            return _array[index];
        }

        public string GetKeyAt(int index)
        {
            if (_kind != JsonKind.Object) { return null; }
            if (index < 0 || index >= _keys.Count) { return null; }
            return _keys[index];
        }

        public string AsString(string fallback)
        {
            if (_kind == JsonKind.String) { return _text; }
            if (_kind == JsonKind.Number) { return FormatNumber(_number); }
            if (_kind == JsonKind.Bool) { return _bool ? "true" : "false"; }
            return fallback;
        }

        public double AsDouble(double fallback)
        {
            if (_kind == JsonKind.Number) { return _number; }
            if (_kind == JsonKind.String)
            {
                double parsed;
                if (double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }

        public int AsInt(int fallback)
        {
            double value = AsDouble(double.NaN);
            if (double.IsNaN(value)) { return fallback; }
            return (int)value;
        }

        public uint AsUInt(uint fallback)
        {
            double value = AsDouble(double.NaN);
            if (double.IsNaN(value) || value < 0) { return fallback; }
            if (value > uint.MaxValue) { return fallback; }
            return (uint)value;
        }

        public bool AsBool(bool fallback)
        {
            if (_kind == JsonKind.Bool) { return _bool; }
            if (_kind == JsonKind.Number) { return _number != 0; }
            return fallback;
        }

        // ============================================================ 写入

        public JsonValue Set(string key, JsonValue value)
        {
            if (_kind != JsonKind.Object) { throw new JsonException("Set 只能用在 Object 上"); }
            if (key == null) { throw new ArgumentNullException("key"); }
            if (value == null) { value = Null(); }

            int index = _keys.IndexOf(key);
            if (index >= 0) { _values[index] = value; }
            else { _keys.Add(key); _values.Add(value); }
            return this;
        }

        public JsonValue Set(string key, string value) { return Set(key, FromString(value)); }
        public JsonValue Set(string key, int value) { return Set(key, FromNumber(value)); }
        public JsonValue Set(string key, uint value) { return Set(key, FromNumber(value)); }
        public JsonValue Set(string key, bool value) { return Set(key, FromBool(value)); }

        /// <summary>以十六进制字符串形式写入（如 "0x00400000"），便于人工核对。</summary>
        public JsonValue SetHex(string key, uint value)
        {
            return Set(key, FromString("0x" + value.ToString("X8", CultureInfo.InvariantCulture)));
        }

        public JsonValue Add(JsonValue value)
        {
            if (_kind != JsonKind.Array) { throw new JsonException("Add 只能用在 Array 上"); }
            _array.Add(value == null ? Null() : value);
            return this;
        }

        public JsonValue Add(string value) { return Add(FromString(value)); }
        public JsonValue Add(uint value) { return Add(FromNumber(value)); }

        // ============================================================ 序列化

        public string ToJson(bool pretty)
        {
            StringBuilder builder = new StringBuilder(512);
            Write(builder, pretty, 0);
            return builder.ToString();
        }

        public override string ToString()
        {
            return ToJson(false);
        }

        private void Write(StringBuilder builder, bool pretty, int depth)
        {
            switch (_kind)
            {
                case JsonKind.Null:
                    builder.Append("null");
                    break;
                case JsonKind.Bool:
                    builder.Append(_bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    builder.Append(FormatNumber(_number));
                    break;
                case JsonKind.String:
                    WriteString(builder, _text);
                    break;
                case JsonKind.Array:
                    WriteArray(builder, pretty, depth);
                    break;
                case JsonKind.Object:
                    WriteObject(builder, pretty, depth);
                    break;
            }
        }

        private void WriteArray(StringBuilder builder, bool pretty, int depth)
        {
            if (_array.Count == 0) { builder.Append("[]"); return; }

            builder.Append('[');
            for (int i = 0; i < _array.Count; i++)
            {
                if (i > 0) { builder.Append(','); }
                if (pretty) { NewLine(builder, depth + 1); }
                _array[i].Write(builder, pretty, depth + 1);
            }
            if (pretty) { NewLine(builder, depth); }
            builder.Append(']');
        }

        private void WriteObject(StringBuilder builder, bool pretty, int depth)
        {
            if (_keys.Count == 0) { builder.Append("{}"); return; }

            builder.Append('{');
            for (int i = 0; i < _keys.Count; i++)
            {
                if (i > 0) { builder.Append(','); }
                if (pretty) { NewLine(builder, depth + 1); }
                WriteString(builder, _keys[i]);
                builder.Append(pretty ? ": " : ":");
                _values[i].Write(builder, pretty, depth + 1);
            }
            if (pretty) { NewLine(builder, depth); }
            builder.Append('}');
        }

        private static void NewLine(StringBuilder builder, int depth)
        {
            builder.Append('\n');
            for (int i = 0; i < depth; i++) { builder.Append("  "); }
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) { return "0"; }
            double rounded = Math.Round(value);
            if (Math.Abs(value - rounded) < 0.0000001 && Math.Abs(value) < 9.0e15)
            {
                return ((long)rounded).ToString(CultureInfo.InvariantCulture);
            }
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // ============================================================ 解析

        public static JsonValue Parse(string text)
        {
            if (text == null) { throw new JsonException("JSON 文本为 null"); }
            int index = 0;
            JsonValue value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index < text.Length)
            {
                throw new JsonException("JSON 解析结束位置仍有残留内容，位置 " + index);
            }
            return value;
        }

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            value = null;
            error = null;
            try
            {
                value = Parse(text);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static JsonValue ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) { throw new JsonException("JSON 意外结束"); }

            char c = text[index];
            switch (c)
            {
                case '{': return ParseObject(text, ref index);
                case '[': return ParseArray(text, ref index);
                case '"': return FromString(ParseString(text, ref index));
                case 't':
                    Expect(text, ref index, "true");
                    return FromBool(true);
                case 'f':
                    Expect(text, ref index, "false");
                    return FromBool(false);
                case 'n':
                    Expect(text, ref index, "null");
                    return Null();
                default:
                    return ParseNumber(text, ref index);
            }
        }

        private static JsonValue ParseObject(string text, ref int index)
        {
            JsonValue result = NewObject();
            index++; // '{'
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == '}') { index++; return result; }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                {
                    throw new JsonException("对象键必须是字符串，位置 " + index);
                }
                string key = ParseString(text, ref index);

                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                {
                    throw new JsonException("对象键后缺少冒号，位置 " + index);
                }
                index++;

                JsonValue value = ParseValue(text, ref index);
                result.Set(key, value);

                SkipWhitespace(text, ref index);
                if (index >= text.Length) { throw new JsonException("对象未闭合"); }
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return result; }
                throw new JsonException("对象中出现意外字符，位置 " + index);
            }
        }

        private static JsonValue ParseArray(string text, ref int index)
        {
            JsonValue result = NewArray();
            index++; // '['
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == ']') { index++; return result; }

            while (true)
            {
                JsonValue value = ParseValue(text, ref index);
                result.Add(value);

                SkipWhitespace(text, ref index);
                if (index >= text.Length) { throw new JsonException("数组未闭合"); }
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return result; }
                throw new JsonException("数组中出现意外字符，位置 " + index);
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++; // 开引号
            StringBuilder builder = new StringBuilder();

            while (true)
            {
                if (index >= text.Length) { throw new JsonException("字符串未闭合"); }
                char c = text[index++];

                if (c == '"') { return builder.ToString(); }
                if (c != '\\') { builder.Append(c); continue; }

                if (index >= text.Length) { throw new JsonException("转义序列不完整"); }
                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) { throw new JsonException("\\u 转义不完整"); }
                        string hex = text.Substring(index, 4);
                        int code;
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            throw new JsonException("\\u 转义不是合法十六进制：" + hex);
                        }
                        builder.Append((char)code);
                        index += 4;
                        break;
                    default:
                        throw new JsonException("不支持的转义字符：\\" + escape);
                }
            }
        }

        private static JsonValue ParseNumber(string text, ref int index)
        {
            int start = index;
            while (index < text.Length)
            {
                char c = text[index];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                {
                    index++;
                    continue;
                }
                break;
            }

            if (index == start) { throw new JsonException("无法识别的值，位置 " + start); }

            string token = text.Substring(start, index - start);
            double value;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new JsonException("非法数字：" + token);
            }
            return FromNumber(value);
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length) { throw new JsonException("期望 " + literal); }
            if (string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new JsonException("期望 " + literal + "，位置 " + index);
            }
            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { index++; continue; }
                break;
            }
        }

        // ============================================================ 文件

        public static JsonValue LoadFile(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            return Parse(text);
        }

        public static void SaveFile(string path, JsonValue value, bool pretty)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
            File.WriteAllText(path, value.ToJson(pretty), new UTF8Encoding(true));
        }
    }
}
