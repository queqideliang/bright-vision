using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BrightVision.Export
{
    internal static class Json
    {
        internal static string Encode(object value)
        {
            StringBuilder text = new StringBuilder();
            using (StringWriter writer = new StringWriter(text, CultureInfo.InvariantCulture)) Write(writer, value, 0);
            return text.ToString();
        }

        internal static void Write(TextWriter writer, object value, int depth)
        {
            if (depth > 40) throw new InvalidDataException("JSON nesting limit exceeded.");
            if (value == null) { writer.Write("null"); return; }
            string text = value as string;
            if (text != null) { String(writer, text); return; }
            if (value is bool) { writer.Write((bool)value ? "true" : "false"); return; }
            // Mesh arrays dominate export volume. Avoid boxing every coordinate
            // through non-generic IEnumerable; preserve the same round-trip text.
            double[] doubles = value as double[];
            if (doubles != null)
            {
                writer.Write('[');
                for (int i = 0; i < doubles.Length; i++)
                {
                    double number = doubles[i];
                    if (Double.IsNaN(number) || Double.IsInfinity(number)) throw new InvalidDataException("Non-finite JSON number.");
                    if (i > 0) writer.Write(',');
                    writer.Write(number.ToString("R", CultureInfo.InvariantCulture));
                }
                writer.Write(']'); return;
            }
            int[] integers = value as int[];
            if (integers != null)
            {
                writer.Write('[');
                for (int i = 0; i < integers.Length; i++)
                { if (i > 0) writer.Write(','); writer.Write(integers[i].ToString(CultureInfo.InvariantCulture)); }
                writer.Write(']'); return;
            }
            IDictionary<string, object> fields = value as IDictionary<string, object>;
            if (fields != null)
            {
                writer.Write('{'); bool first = true;
                foreach (KeyValuePair<string, object> field in fields)
                {
                    if (!first) writer.Write(','); first = false;
                    String(writer, field.Key); writer.Write(':'); Write(writer, field.Value, depth + 1);
                }
                writer.Write('}'); return;
            }
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                writer.Write('['); bool first = true;
                foreach (object item in sequence)
                {
                    if (!first) writer.Write(','); first = false; Write(writer, item, depth + 1);
                }
                writer.Write(']'); return;
            }
            if (value is double || value is float)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (Double.IsNaN(number) || Double.IsInfinity(number)) throw new InvalidDataException("Non-finite JSON number.");
                writer.Write(number.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            if (value is int || value is long || value is byte || value is short || value is uint || value is ulong || value is decimal)
            { writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            throw new InvalidDataException("Unsupported JSON value: " + value.GetType().FullName);
        }

        private static void String(TextWriter writer, string text)
        {
            writer.Write('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': writer.Write("\\\""); break;
                    case '\\': writer.Write("\\\\"); break;
                    case '\b': writer.Write("\\b"); break;
                    case '\f': writer.Write("\\f"); break;
                    case '\n': writer.Write("\\n"); break;
                    case '\r': writer.Write("\\r"); break;
                    case '\t': writer.Write("\\t"); break;
                    default:
                        if (c < 0x20 || Char.IsSurrogate(c)) writer.Write("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else writer.Write(c);
                        break;
                }
            }
            writer.Write('"');
        }
    }
}
