using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TerrariaModder.Core.IO
{
    /// <summary>Bounded JSON object codec for mod-owned sidecars. Invalid data never becomes an empty object.</summary>
    public static class SidecarJson
    {
        public const int MaxCharacters = 4 * 1024 * 1024;
        private const int MaxDepth = 64;
        public static string ReadText(string path)
        {
            using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true))
            {
                var text = new StringBuilder(); var buffer = new char[4096]; int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + count > MaxCharacters) throw new InvalidDataException("Sidecar exceeds the size limit");
                    text.Append(buffer, 0, count);
                }
                return text.ToString();
            }
        }
        public static Dictionary<string, object> Deserialize(string text)
        {
            if (text == null || text.Length > MaxCharacters) throw new InvalidDataException("Sidecar JSON is missing or exceeds the size limit");
            var parser = new Parser(text);
            object value = parser.Value(0);
            parser.White();
            if (parser.Position != text.Length || !(value is Dictionary<string, object> result))
                throw new InvalidDataException("Sidecar must contain exactly one JSON object");
            return result;
        }
        public static string Serialize(object value)
        {
            var output = new StringBuilder(); Write(output, value, 0);
            if (output.Length > MaxCharacters) throw new InvalidDataException("Sidecar exceeds the size limit");
            return output.ToString();
        }
        private static void Write(StringBuilder output, object value, int depth)
        {
            if (depth > MaxDepth || output.Length > MaxCharacters) throw new InvalidDataException("Sidecar exceeds structural limits");
            if (value == null) { output.Append("null"); return; }
            if (value is string text) { Quote(output, text); return; }
            if (value is bool flag) { output.Append(flag ? "true" : "false"); return; }
            if (value is int || value is long) { output.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number))
            { output.Append(number.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is Dictionary<string, object> map)
            {
                output.Append('{'); bool first = true;
                foreach (var entry in map)
                {
                    if (!first) output.Append(','); first = false;
                    Quote(output, entry.Key); output.Append(':'); Write(output, entry.Value, depth + 1);
                }
                output.Append('}'); return;
            }
            if (value is IList list)
            {
                output.Append('[');
                for (int i = 0; i < list.Count; i++) { if (i > 0) output.Append(','); Write(output, list[i], depth + 1); }
                output.Append(']'); return;
            }
            throw new InvalidDataException("Unsupported JSON value type: " + value.GetType().FullName);
        }
        private static void Quote(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') { output.Append('\\'); output.Append(c); }
                else if (c < 32 || char.IsSurrogate(c)) { output.Append("\\u"); output.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
                else output.Append(c);
            }
            output.Append('"');
        }
        private sealed class Parser
        {
            private readonly string _text;
            internal int Position;
            internal Parser(string text) { _text = text; }
            internal void White() { while (Position < _text.Length && (_text[Position] == ' ' || _text[Position] == '\t' || _text[Position] == '\r' || _text[Position] == '\n')) Position++; }
            private InvalidDataException Error() => new InvalidDataException("Invalid sidecar JSON at character " + Position);
            private bool Take(char c) { if (Position >= _text.Length || _text[Position] != c) return false; Position++; return true; }
            private void Need(char c) { White(); if (!Take(c)) throw Error(); }
            internal object Value(int depth)
            {
                if (depth > MaxDepth) throw Error();
                White(); if (Position >= _text.Length) throw Error();
                char c = _text[Position];
                if (c == '"') return Text();
                if (Take('{'))
                {
                    var map = new Dictionary<string, object>(StringComparer.Ordinal); White();
                    if (Take('}')) return map;
                    do
                    {
                        White(); string key = Text(); Need(':');
                        if (map.ContainsKey(key)) throw Error();
                        map.Add(key, Value(depth + 1)); White();
                        if (Take('}')) return map;
                        Need(',');
                    } while (true);
                }
                if (Take('['))
                {
                    var list = new List<object>(); White(); if (Take(']')) return list;
                    do { list.Add(Value(depth + 1)); White(); if (Take(']')) return list; Need(','); } while (true);
                }
                if (c == 't') { Literal("true"); return true; }
                if (c == 'f') { Literal("false"); return false; }
                if (c == 'n') { Literal("null"); return null; }
                return Number();
            }
            private void Literal(string value)
            {
                foreach (char c in value) if (!Take(c)) throw Error();
            }
            private string Text()
            {
                if (!Take('"')) throw Error(); var output = new StringBuilder();
                while (Position < _text.Length)
                {
                    char c = _text[Position++];
                    if (c == '"') return output.ToString();
                    if (c < 32) throw Error();
                    if (c != '\\') { output.Append(c); continue; }
                    if (Position >= _text.Length) throw Error(); c = _text[Position++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': output.Append(c); break;
                        case 'b': output.Append('\b'); break; case 'f': output.Append('\f'); break;
                        case 'n': output.Append('\n'); break; case 'r': output.Append('\r'); break; case 't': output.Append('\t'); break;
                        case 'u':
                            if (Position + 4 > _text.Length || !ushort.TryParse(_text.Substring(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code)) throw Error();
                            output.Append((char)code); Position += 4; break;
                        default: throw Error();
                    }
                }
                throw Error();
            }
            private bool Digit() => Position < _text.Length && _text[Position] >= '0' && _text[Position] <= '9';
            private void Digits() { if (!Digit()) throw Error(); while (Digit()) Position++; }
            private object Number()
            {
                int start = Position; Take('-');
                if (!Take('0')) Digits();
                bool real = false;
                if (Take('.')) { real = true; Digits(); }
                if (Take('e') || Take('E')) { real = true; if (!Take('+')) Take('-'); Digits(); }
                string token = _text.Substring(start, Position - start);
                if (!real)
                {
                    if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int small)) return small;
                    if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long large)) return large;
                }
                else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && !double.IsNaN(number) && !double.IsInfinity(number)) return number;
                throw Error();
            }
        }
    }
}
