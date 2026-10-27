using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Storage;

/// <summary>リフレクションを使わない小さな JSON 生成ヘルパ（JSON列用。SQLite の JSON1 で検索できる形）</summary>
public static class Json
{
    public static string Obj(params (string Key, object? Value)[] fields)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (k, v) in fields) { w.WritePropertyName(k); WriteValue(w, v); }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Arr(IEnumerable<object?> items)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) WriteValue(w, items.ToList());
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: if (double.IsFinite(d)) w.WriteNumberValue(Math.Round(d, 6)); else w.WriteNullValue(); break;
            case float f: WriteValue(w, (double)f); break;
            case IReadOnlyDictionary<string, object?> dict:
                w.WriteStartObject(); foreach (var kv in dict) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value); } w.WriteEndObject(); break;
            case (string, object?)[] pairs:
                w.WriteStartObject(); foreach (var (k, x) in pairs) { w.WritePropertyName(k); WriteValue(w, x); } w.WriteEndObject(); break;
            case System.Collections.IEnumerable e:
                w.WriteStartArray(); foreach (var x in e) WriteValue(w, x); w.WriteEndArray(); break;
            default: w.WriteStringValue(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }
}
