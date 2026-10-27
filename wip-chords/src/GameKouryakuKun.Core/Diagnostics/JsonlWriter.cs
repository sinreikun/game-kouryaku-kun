using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameKouryakuKun.Core.Diagnostics;

/// <summary>
/// 1行1レコードの JSON ログ（JSON Lines）。リフレクションを使わず Utf8JsonWriter で書く（トリミング安全）。
/// 値は string / bool / 数値 / null / IDictionary&lt;string, object?&gt; / IEnumerable を受け付ける。
/// </summary>
public sealed class JsonlWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _w;
    public string Path { get; }

    public JsonlWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _w = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(string type, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var line = Serialize(type, fields);
        lock (_gate) _w.WriteLine(line);
    }

    public void Write(string type, params (string Key, object? Value)[] fields) =>
        Write(type, fields.Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)));

    public static string Serialize(string type, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            j.WriteStartObject();
            j.WriteString("t", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            j.WriteString("type", type);
            foreach (var kv in fields) { j.WritePropertyName(kv.Key); WriteValue(j, kv.Value); }
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter j, object? v)
    {
        switch (v)
        {
            case null: j.WriteNullValue(); break;
            case string s: j.WriteStringValue(s); break;
            case bool b: j.WriteBooleanValue(b); break;
            case int i: j.WriteNumberValue(i); break;
            case long l: j.WriteNumberValue(l); break;
            case double d: if (double.IsFinite(d)) j.WriteNumberValue(Math.Round(d, 5)); else j.WriteNullValue(); break;
            case float f: j.WriteNumberValue(f); break;
            case Enum e: j.WriteStringValue(e.ToString()); break;
            case IEnumerable<KeyValuePair<string, object?>> dict:
                j.WriteStartObject(); foreach (var kv in dict) { j.WritePropertyName(kv.Key); WriteValue(j, kv.Value); } j.WriteEndObject(); break;
            case System.Collections.IEnumerable seq:
                j.WriteStartArray(); foreach (var x in seq) WriteValue(j, x); j.WriteEndArray(); break;
            default: j.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }

    public void Dispose() { lock (_gate) _w.Dispose(); }
}
