using System.Collections;
using System.Globalization;
using System.Text;

namespace GameKouryakuKun.Phase0;

/// <summary>トリミング安全な最小 JSON 書き出し（リフレクション不使用）。</summary>
public static class Json
{
    public static string Write(object? v) { var sb = new StringBuilder(); W(sb, v, 0); return sb.ToString(); }

    private static void W(StringBuilder sb, object? v, int ind)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: Str(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case double d: sb.Append(double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null"); break;
            case float f: sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); break;
            case int or long or uint or ulong or short or ushort or byte: sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case DateTime dt: Str(sb, dt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")); break;
            case IDictionary<string, object?> dict: Obj(sb, dict.Select(kv => (kv.Key, kv.Value)), ind); break;
            case IEnumerable e:
            {
                var items = e.Cast<object?>().ToList();
                if (items.Count == 0) { sb.Append("[]"); break; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(' ', (ind + 1) * 2); W(sb, items[i], ind + 1);
                    sb.Append(i < items.Count - 1 ? ",\n" : "\n");
                }
                sb.Append(' ', ind * 2).Append(']');
                break;
            }
            default: Str(sb, v.ToString() ?? ""); break;
        }
    }

    private static void Obj(StringBuilder sb, IEnumerable<(string k, object? v)> props, int ind)
    {
        var list = props.ToList();
        if (list.Count == 0) { sb.Append("{}"); return; }
        sb.Append("{\n");
        for (int i = 0; i < list.Count; i++)
        {
            sb.Append(' ', (ind + 1) * 2); Str(sb, list[i].k); sb.Append(": "); W(sb, list[i].v, ind + 1);
            sb.Append(i < list.Count - 1 ? ",\n" : "\n");
        }
        sb.Append(' ', ind * 2).Append('}');
    }

    private static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: if (c < 0x20) sb.Append($"\\u{(int)c:X4}"); else sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}
