using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace GameKouryakuKun.Ocr;

/// <summary>
/// ONNX Runtime のネイティブライブラリを「セットアップマネージャが導入したフォルダ」から読み込む設定。
/// 自プロセスに推論ライブラリを読み込むだけで、ゲームのプロセスには一切関与しない。
/// （禁止API検査: NativeLibrary の使用はこのファイルだけに限定し、読込先はコンポーネントフォルダ内のファイルだけ）
/// </summary>
public static class OrtNative
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static bool _resolverSet;

    public static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";

    /// <summary>ネイティブDLLの場所を設定する（最初の推論より前に1回だけ有効）。戻り値: そのフォルダにDLLがあるか</summary>
    public static bool Configure(string componentDir)
    {
        lock (Gate)
        {
            var full = Path.GetFullPath(componentDir);
            if (!File.Exists(Path.Combine(full, LibraryFileName))) return false;
            _dir = full;
            if (!_resolverSet)
            {
                NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
                _resolverSet = true;
            }
            return true;
        }
    }

    public static string? Directory { get { lock (Gate) return _dir; } }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != "onnxruntime" && name != "libonnxruntime") return IntPtr.Zero;
        var dir = Directory;
        if (dir == null) return IntPtr.Zero;
        var file = Path.Combine(dir, LibraryFileName);
        return File.Exists(file) && NativeLibrary.TryLoad(file, out var h) ? h : IntPtr.Zero;
    }
}
