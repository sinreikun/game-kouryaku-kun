// ゲーム攻略くん.exe — 小さなネイティブ起動ツール（.NET 不要）。安定版（本体の更新では置き換えない）。
// 0) 公開リポジトリの release/latest.json を見て、必要なら本体（app\）を GitHub Releases から取得・照合・原子的に入れ替え
//    （ネットワーク失敗時は既存の app\ があればそれで起動。data\ は触らない。ランチャー自身は自動更新しない）
// 1) {ホーム}\runtime\ に .NET Runtime / Desktop Runtime（win-x64）が正しく入っているか導入記録で確認
// 2) 無い・壊れている部品だけを公式配布元から zip でダウンロード（途中から再開可）→ SHA-512 と SHA-256 を照合 → 展開
//    （管理者権限不要。システムには何もインストールしない。レジストリも触らない）
// 3) DOTNET_ROOT 等を設定して {ホーム}\app\GameKouryakuKun.exe を起動
// 4) ランタイムの版を上げたときは、新しい版の導入がすべて済んでから、旧版だけにあったファイルを削除（使用中なら次回に再試行）
// 引数: --verify（CRC32 まで再照合）/ --repair（本体・ランタイムを強制再導入）/ --check（確認だけ）/
//       --cleanup（旧版ランタイムの片付けだけ）/ --offline（本体の更新確認をしない）/ それ以外は本体へ渡す
#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <winhttp.h>
#include <atomic>
#include <cstdio>
#include <ctime>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include "core.h"
#include "manifest_gen.h"

using namespace gkk;

static std::wstring W(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0'); MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n); return w;
}
static std::string U(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0'); WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), &s[0], n, nullptr, nullptr); return s;
}
static std::string Now() {
    SYSTEMTIME t; GetLocalTime(&t);
    TIME_ZONE_INFORMATION tz; DWORD r = GetTimeZoneInformation(&tz);
    long bias = -(tz.Bias + (r == TIME_ZONE_ID_DAYLIGHT ? tz.DaylightBias : 0));
    char b[64]; snprintf(b, sizeof b, "%04d-%02d-%02dT%02d:%02d:%02d%c%02ld:%02ld", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond,
                         bias >= 0 ? '+' : '-', labs(bias) / 60, labs(bias) % 60);
    return b;
}

// ---------------- ログ（{ホーム}\data\logs\launcher.log、UTF-8 追記） ----------------
static fs::path g_root;     // アプリ配下（起動ツールのフォルダ）
static fs::path g_writable; // 書き込み先（通常は g_root。Program Files などで書けなければ %LOCALAPPDATA%\GameKouryakuKun）
static fs::path g_dataRoot; // 本体と同じデータフォルダ（g_root\data または %LOCALAPPDATA%\GameKouryakuKun）
static std::mutex g_logMu;
static bool CanWrite(const fs::path& dir) {
    std::error_code ec; fs::create_directories(dir, ec);
    auto t = dir / (L".gkk-write-test-" + std::to_wstring(GetCurrentProcessId()));
    HANDLE h = CreateFileW(t.wstring().c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    CloseHandle(h); return true;
}
static void Log(const std::string& msg) {
    std::lock_guard<std::mutex> lk(g_logMu);
    std::error_code ec; auto dir = g_dataRoot / "logs"; fs::create_directories(dir, ec);
    FILE* f = _wfopen((dir / "launcher.log").wstring().c_str(), L"ab");
    if (!f) return;
    std::string line = Now() + " " + msg + "\r\n"; fwrite(line.data(), 1, line.size(), f); fclose(f);
}
static std::string WinErr(DWORD e) {
    wchar_t* buf = nullptr;
    HMODULE mod = (e >= 12000 && e <= 12200) ? GetModuleHandleW(L"winhttp.dll") : nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_IGNORE_INSERTS | FORMAT_MESSAGE_FROM_SYSTEM | (mod ? FORMAT_MESSAGE_FROM_HMODULE : 0),
                   mod, e, 0, (LPWSTR)&buf, 0, nullptr);
    std::string s = buf ? U(buf) : std::string(); if (buf) LocalFree(buf);
    while (!s.empty() && (s.back() == '\n' || s.back() == '\r' || s.back() == ' ')) s.pop_back();
    return "エラー " + std::to_string(e) + (s.empty() ? "" : "（" + s + "）");
}

// ---------------- 進捗ウィンドウ ----------------
struct Progress {
    std::mutex mu; std::wstring title, status; double frac = 0; bool done = false;
    std::atomic<bool> cancel{false};
    void Set(const std::string& t, const std::string& s, double f) { std::lock_guard<std::mutex> lk(mu); title = W(t); status = W(s); frac = f; }
    void SetStatus(const std::string& s, double f) { std::lock_guard<std::mutex> lk(mu); status = W(s); frac = f; }
};
static Progress g_prog;
static HWND g_wnd, g_title, g_status, g_bar, g_cancel;
static HFONT g_font;
enum { IDC_CANCEL = 100, TIMER_ID = 1, WM_WORKER_DONE = WM_APP + 1 };
static std::atomic<bool> g_workerDone{false};

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM wp, LPARAM lp) {
    switch (m) {
    case WM_TIMER: {
        std::lock_guard<std::mutex> lk(g_prog.mu);
        wchar_t cur[512];
        GetWindowTextW(g_title, cur, 512); if (g_prog.title != cur) SetWindowTextW(g_title, g_prog.title.c_str());
        GetWindowTextW(g_status, cur, 512); if (g_prog.status != cur) SetWindowTextW(g_status, g_prog.status.c_str());
        SendMessageW(g_bar, PBM_SETPOS, (WPARAM)(g_prog.frac * 1000), 0);
        return 0;
    }
    case WM_COMMAND:
        if (LOWORD(wp) == IDC_CANCEL) {
            if (MessageBoxW(h, L"準備を中止しますか？\n（ダウンロード済みの分は保存され、次回は続きから再開します）", L"ゲーム攻略くん",
                            MB_YESNO | MB_ICONQUESTION) == IDYES) { g_prog.cancel = true; EnableWindow(g_cancel, FALSE); }
        }
        return 0;
    case WM_CLOSE: SendMessageW(h, WM_COMMAND, IDC_CANCEL, 0); return 0;
    case WM_WORKER_DONE: g_workerDone = true; return 0;  // 確認ダイアログ中（入れ子のループ）でも取りこぼさない
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(h, m, wp, lp);
}

static void CreateProgressWindow(HINSTANCE inst) {
    INITCOMMONCONTROLSEX icc{sizeof icc, ICC_PROGRESS_CLASS}; InitCommonControlsEx(&icc);
    WNDCLASSW wc{}; wc.lpfnWndProc = WndProc; wc.hInstance = inst; wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1); wc.lpszClassName = L"GkkLauncher"; wc.hIcon = LoadIconW(inst, MAKEINTRESOURCEW(1));
    RegisterClassW(&wc);
    HDC dc = GetDC(nullptr); int dpi = GetDeviceCaps(dc, LOGPIXELSY); ReleaseDC(nullptr, dc);
    auto S = [dpi](int v) { return MulDiv(v, dpi, 96); };
    NONCLIENTMETRICSW ncm{sizeof ncm}; SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof ncm, &ncm, 0);
    g_font = CreateFontIndirectW(&ncm.lfMessageFont);
    RECT rc{0, 0, S(480), S(150)}; DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    AdjustWindowRect(&rc, style, FALSE);
    int w = rc.right - rc.left, hgt = rc.bottom - rc.top;
    g_wnd = CreateWindowW(L"GkkLauncher", L"ゲーム攻略くん - 準備・更新", style, (GetSystemMetrics(SM_CXSCREEN) - w) / 2,
                          (GetSystemMetrics(SM_CYSCREEN) - hgt) / 2, w, hgt, nullptr, nullptr, inst, nullptr);
    g_title = CreateWindowW(L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_LEFTNOWORDWRAP, S(16), S(14), S(448), S(20), g_wnd, nullptr, inst, nullptr);
    g_bar = CreateWindowW(PROGRESS_CLASSW, L"", WS_CHILD | WS_VISIBLE | PBS_SMOOTH, S(16), S(40), S(448), S(20), g_wnd, nullptr, inst, nullptr);
    g_status = CreateWindowW(L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_LEFTNOWORDWRAP | SS_ENDELLIPSIS, S(16), S(68), S(448), S(36), g_wnd, nullptr, inst, nullptr);
    g_cancel = CreateWindowW(L"BUTTON", L"中止", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, S(376), S(110), S(88), S(28), g_wnd, (HMENU)IDC_CANCEL, inst, nullptr);
    for (HWND c : {g_title, g_bar, g_status, g_cancel}) SendMessageW(c, WM_SETFONT, (WPARAM)g_font, TRUE);
    SendMessageW(g_bar, PBM_SETRANGE32, 0, 1000);
    SetTimer(g_wnd, TIMER_ID, 100, nullptr);
    ShowWindow(g_wnd, SW_SHOWNORMAL); UpdateWindow(g_wnd);
}

// ---------------- ダウンロード（WinHTTP、Range で再開） ----------------
static std::string Mb(uint64_t b) { char s[32]; snprintf(s, sizeof s, "%.1f MB", b / 1048576.0); return s; }

struct DlResult { bool ok = false; bool cancelled = false; bool fatal = false; std::string error; };

static DlResult DownloadOnce(const std::wstring& url, const fs::path& part, uint64_t expected, const std::string& label) {
    DlResult r;
    std::error_code ec;
    uint64_t have = fs::exists(part, ec) ? fs::file_size(part, ec) : 0;
    uint64_t from = ResumeFrom(have, expected);
    if (have >= expected && have > 0) { fs::remove(part, ec); have = 0; }  // 期待サイズ以上の途中ファイルは信用しない

    URL_COMPONENTS uc{sizeof uc}; wchar_t host[256], path[2048];
    uc.lpszHostName = host; uc.dwHostNameLength = 256; uc.lpszUrlPath = path; uc.dwUrlPathLength = 2048;
    if (!WinHttpCrackUrl(url.c_str(), 0, 0, &uc) || uc.nScheme != INTERNET_SCHEME_HTTPS) { r.fatal = true; r.error = "URL が不正（https のみ許可）"; return r; }

    HINTERNET s = WinHttpOpen(L"GameKouryakuKun-Launcher/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) s = WinHttpOpen(L"GameKouryakuKun-Launcher/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) { r.error = "通信の初期化に失敗: " + WinErr(GetLastError()); return r; }
    WinHttpSetTimeouts(s, 15000, 15000, 30000, 30000);
    DWORD protos = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | 0x00002000 /*TLS1_3*/;
    if (!WinHttpSetOption(s, WINHTTP_OPTION_SECURE_PROTOCOLS, &protos, sizeof protos)) {
        protos = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2; WinHttpSetOption(s, WINHTTP_OPTION_SECURE_PROTOCOLS, &protos, sizeof protos);
    }
    HINTERNET c = WinHttpConnect(s, host, uc.nPort, 0);
    HINTERNET q = c ? WinHttpOpenRequest(c, L"GET", path, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE) : nullptr;
    auto cleanup = [&] { if (q) WinHttpCloseHandle(q); if (c) WinHttpCloseHandle(c); WinHttpCloseHandle(s); };
    if (!q) { r.error = "接続に失敗: " + WinErr(GetLastError()); cleanup(); return r; }
    std::wstring hdr;
    if (from > 0) hdr = L"Range: bytes=" + std::to_wstring(from) + L"-\r\n";
    if (!WinHttpSendRequest(q, hdr.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : hdr.c_str(), (DWORD)-1L, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(q, nullptr)) { r.error = "通信に失敗: " + WinErr(GetLastError()); cleanup(); return r; }
    DWORD status = 0, sz = sizeof status;
    WinHttpQueryHeaders(q, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &sz, WINHTTP_NO_HEADER_INDEX);
    bool append = false;
    if (status == 206) {
        wchar_t cr[128]; DWORD crs = sizeof cr; uint64_t st = 0, tot = 0;
        if (!WinHttpQueryHeaders(q, WINHTTP_QUERY_CONTENT_RANGE, WINHTTP_HEADER_NAME_BY_INDEX, cr, &crs, WINHTTP_NO_HEADER_INDEX) ||
            !ParseContentRange(U(cr), st, tot) || st != from || tot != expected) {
            r.error = "再開位置の応答が不正（最初から取り直します）"; fs::remove(part, ec); cleanup(); return r;
        }
        append = true;
    } else if (status == 200) {
        from = 0;  // サーバーが範囲指定を無視 → 最初から
    } else if (status == 416) {
        fs::remove(part, ec); r.error = "再開位置が範囲外（最初から取り直します）"; cleanup(); return r;
    } else {
        r.error = "サーバーの応答 HTTP " + std::to_string(status); if (status == 404 || status == 403 || status == 410) r.fatal = true; cleanup(); return r;
    }
    FILE* f = _wfopen(part.wstring().c_str(), append ? L"ab" : L"wb");
    if (!f) { r.fatal = true; r.error = "保存先に書き込めません: " + U(part.wstring()); cleanup(); return r; }
    uint64_t got = from; std::vector<char> buf(256 * 1024);
    DWORD t0 = GetTickCount(); uint64_t got0 = got;
    for (;;) {
        if (g_prog.cancel) { r.cancelled = true; r.error = "中止しました"; break; }
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(q, &avail)) { r.error = "受信が途切れました: " + WinErr(GetLastError()); break; }
        if (avail == 0) break;
        DWORD rd = 0;
        if (!WinHttpReadData(q, buf.data(), (DWORD)std::min<size_t>(avail, buf.size()), &rd)) { r.error = "受信エラー: " + WinErr(GetLastError()); break; }
        if (rd == 0) break;
        if (fwrite(buf.data(), 1, rd, f) != rd) { r.fatal = true; r.error = "書き込みエラー（空き容量を確認してください）"; break; }
        got += rd;
        if (got > expected) { r.error = "想定より大きいデータを受信"; fclose(f); f = nullptr; fs::remove(part, ec); break; }
        DWORD el = GetTickCount() - t0;
        double speed = el > 500 ? (got - got0) / (el / 1000.0) : 0;
        char line[200]; snprintf(line, sizeof line, "ダウンロード中: %s / %s%s", Mb(got).c_str(), Mb(expected).c_str(),
                                 speed > 0 ? ("（" + Mb((uint64_t)speed) + "/秒）").c_str() : "");
        g_prog.SetStatus(label + "\n" + line, double(got) / expected * 0.7);
    }
    if (f) fclose(f);
    cleanup();
    if (r.error.empty()) {
        if (got != expected) r.error = "受信が途中で終わりました（" + Mb(got) + " / " + Mb(expected) + "）";
        else r.ok = true;
    }
    return r;
}

// ---------------- 本体（app\）の自動更新 ----------------
static const wchar_t* kLatestManifestUrl = L"https://raw.githubusercontent.com/sinreikun/game-kouryaku-kun/main/release/latest.json";
static const uint64_t kMaxManifestBytes = 16384;

// サイズ未知（上限あり）の小さなファイル用。EOF で成功。exactExpected>0 ならそのサイズ必須
static DlResult DownloadBounded(const std::wstring& url, const fs::path& dest, uint64_t maxBytes, uint64_t exactExpected, const std::string& label) {
    DlResult r;
    std::error_code ec;
    fs::remove(dest, ec);
    URL_COMPONENTS uc{sizeof uc}; wchar_t host[256], path[2048];
    uc.lpszHostName = host; uc.dwHostNameLength = 256; uc.lpszUrlPath = path; uc.dwUrlPathLength = 2048;
    if (!WinHttpCrackUrl(url.c_str(), 0, 0, &uc) || uc.nScheme != INTERNET_SCHEME_HTTPS) { r.fatal = true; r.error = "URL が不正（https のみ許可）"; return r; }
    HINTERNET s = WinHttpOpen(L"GameKouryakuKun-Launcher/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) s = WinHttpOpen(L"GameKouryakuKun-Launcher/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) { r.error = "通信の初期化に失敗: " + WinErr(GetLastError()); return r; }
    WinHttpSetTimeouts(s, 15000, 15000, 30000, 30000);
    DWORD protos = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | 0x00002000;
    if (!WinHttpSetOption(s, WINHTTP_OPTION_SECURE_PROTOCOLS, &protos, sizeof protos)) {
        protos = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2; WinHttpSetOption(s, WINHTTP_OPTION_SECURE_PROTOCOLS, &protos, sizeof protos);
    }
    HINTERNET c = WinHttpConnect(s, host, uc.nPort, 0);
    HINTERNET q = c ? WinHttpOpenRequest(c, L"GET", path, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE) : nullptr;
    auto cleanup = [&] { if (q) WinHttpCloseHandle(q); if (c) WinHttpCloseHandle(c); WinHttpCloseHandle(s); };
    if (!q) { r.error = "接続に失敗: " + WinErr(GetLastError()); cleanup(); return r; }
    if (!WinHttpSendRequest(q, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(q, nullptr)) { r.error = "通信に失敗: " + WinErr(GetLastError()); cleanup(); return r; }
    DWORD status = 0, sz = sizeof status;
    WinHttpQueryHeaders(q, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &sz, WINHTTP_NO_HEADER_INDEX);
    if (status != 200) { r.error = "サーバーの応答 HTTP " + std::to_string(status); if (status == 404 || status == 403 || status == 410) r.fatal = true; cleanup(); return r; }
    FILE* f = _wfopen(dest.wstring().c_str(), L"wb");
    if (!f) { r.fatal = true; r.error = "保存先に書き込めません"; cleanup(); return r; }
    uint64_t got = 0; std::vector<char> buf(64 * 1024);
    for (;;) {
        if (g_prog.cancel) { r.cancelled = true; r.error = "中止しました"; break; }
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(q, &avail)) { r.error = "受信が途切れました: " + WinErr(GetLastError()); break; }
        if (avail == 0) break;
        DWORD rd = 0;
        if (!WinHttpReadData(q, buf.data(), (DWORD)std::min<size_t>(avail, buf.size()), &rd) || rd == 0) { if (rd == 0) break; r.error = "受信エラー"; break; }
        if (fwrite(buf.data(), 1, rd, f) != rd) { r.fatal = true; r.error = "書き込みエラー"; break; }
        got += rd;
        if (got > maxBytes) { r.error = "想定より大きいデータを受信"; break; }
        if (!label.empty()) g_prog.SetStatus(label + "\n受信中: " + std::to_string(got) + " バイト", 0.05);
    }
    fclose(f); cleanup();
    if (!r.error.empty()) { fs::remove(dest, ec); return r; }
    if (exactExpected > 0 && got != exactExpected) { fs::remove(dest, ec); r.error = "サイズが一致しません"; return r; }
    if (got == 0) { fs::remove(dest, ec); r.error = "空の応答です"; return r; }
    r.ok = true; return r;
}

struct AppUpdateResult {
    bool ok = false;          // 起動してよい（更新成功 or 既存で可）
    bool cancelled = false;
    bool updated = false;
    bool offlineFallback = false;
    std::string version;      // 使う本体の版
    std::string error;        // 致命（本体なし）または警告
    std::string report;       // --check 用
};

static AppUpdateResult EnsureAppUpdated(bool repair, bool offline, bool apply) {
    AppUpdateResult res;
    std::error_code ec;
    fs::path appDir = g_root / "app";
    fs::path appExe = appDir / L"GameKouryakuKun.exe";
    std::string localVer, localSha;
    bool haveLock = ReadInstalledApp(appDir, localVer, localSha);
    bool haveExe = fs::exists(appExe, ec);
    if (haveLock) res.version = localVer;
    else if (haveExe) res.version = "(未記録)";
    else res.version = "(なし)";

    if (offline) {
        res.report = "本体: " + res.version + "（--offline: 更新確認なし）\n";
        if (haveExe) { res.ok = true; return res; }
        res.error = "本体がありません。ネット接続のある環境で起動するか、配布パッケージを入れ直してください。";
        return res;
    }

    g_prog.Set("本体の更新を確認しています", "最新版の情報を取得中…", 0.02);
    fs::path updDir = g_writable / "updates";
    fs::create_directories(updDir, ec);
    fs::path manPart = updDir / "latest.json.part";
    fs::path manPath = updDir / "latest.json";
    auto dl = DownloadBounded(kLatestManifestUrl, manPart, kMaxManifestBytes, 0, "更新情報");
    if (dl.cancelled) { res.cancelled = true; res.error = dl.error; return res; }
    if (!dl.ok) {
        Log("app-update manifest failed: " + dl.error);
        res.report = "本体: " + res.version + "（更新確認失敗: " + dl.error + "）\n";
        if (haveExe) {
            res.ok = true; res.offlineFallback = true; res.error = dl.error;
            Log("app-update: using existing app after manifest failure");
            return res;
        }
        res.error = "本体がなく、更新情報も取得できませんでした。\n" + dl.error + "\nネット接続を確認して再起動してください。";
        return res;
    }
    fs::remove(manPath, ec); fs::rename(manPart, manPath, ec);
    std::string manText = ReadAll(manPath);
    AppUpdateManifest man; std::string perr;
    if (!ParseAppUpdateManifest(manText, man, perr)) {
        Log("app-update bad manifest: " + perr);
        res.report = "本体: " + res.version + "（更新情報不正: " + perr + "）\n";
        if (haveExe) { res.ok = true; res.offlineFallback = true; res.error = perr; return res; }
        res.error = "更新情報が不正で、本体もありません: " + perr;
        return res;
    }
    Log("app-update remote version=" + man.version + " bytes=" + std::to_string(man.bytes));

    bool need = repair || !haveExe || !haveLock;
    if (!need) {
        int cmp = CompareVersions(localVer, man.version);
        if (cmp == -2) need = true;
        else if (cmp < 0) need = true;
        else if (ToLower(localSha) != man.sha256) need = true;  // 同じ版でもハッシュ不一致なら取り直す
    }
    res.report = "本体 導入: " + res.version + " / 公開: " + man.version + (need ? (apply ? " → 更新します" : " → 更新あり") : "（最新）") + "\n";
    if (!need) { res.ok = true; res.version = haveLock ? localVer : man.version; g_prog.SetStatus("本体は最新です（v" + man.version + "）", 0.1); return res; }
    if (!apply) {
        res.ok = haveExe;
        if (!haveExe) res.error = "本体がありません（更新が必要です）";
        return res;
    }

    g_prog.Set("本体を更新しています", "v" + man.version + " をダウンロード中…", 0.1);
    fs::path zip = updDir / ("app-update-" + man.version + ".zip");
    fs::path part = zip; part += ".part";
    // 既存 zip が一致すれば再利用
    bool haveZip = false;
    if (fs::exists(zip, ec) && fs::file_size(zip, ec) == man.bytes) {
        std::string s256, s512;
        if (HashFile(zip, s256, s512) && s256 == man.sha256) haveZip = true;
        else fs::remove(zip, ec);
    } else fs::remove(zip, ec);

    if (!haveZip) {
        std::string lastErr;
        for (int attempt = 1; attempt <= 4 && !haveZip; attempt++) {
            Log("app-update download attempt=" + std::to_string(attempt) + " url=" + man.url);
            auto d = DownloadOnce(W(man.url), part, man.bytes, "本体 v" + man.version);
            if (d.cancelled) { res.cancelled = true; res.error = d.error; return res; }
            if (d.ok) {
                fs::remove(zip, ec); fs::rename(part, zip, ec);
                if (ec) { lastErr = "ダウンロードしたファイルを移動できません"; break; }
                haveZip = true; break;
            }
            lastErr = d.error; Log("app-update download failed: " + d.error);
            if (d.fatal) break;
            for (int w = attempt * 2; w > 0 && !g_prog.cancel; w--) {
                g_prog.SetStatus("本体 v" + man.version + "\n" + lastErr + " — " + std::to_string(w) + " 秒後に再試行", 0.15);
                Sleep(1000);
            }
            if (g_prog.cancel) { res.cancelled = true; res.error = "中止しました"; return res; }
        }
        if (!haveZip) {
            if (haveExe) {
                res.ok = true; res.offlineFallback = true; res.error = lastErr;
                Log("app-update: keep existing after download failure");
                return res;
            }
            res.error = "本体をダウンロードできませんでした: " + lastErr;
            return res;
        }
    }

    g_prog.SetStatus("本体 v" + man.version + "\n改ざん・破損がないか確認中（SHA-256）", 0.75);
    std::string s256, s512;
    if (!HashFile(zip, s256, s512, [&](uint64_t d, uint64_t t) {
            g_prog.SetStatus("本体 v" + man.version + "\n改ざん・破損がないか確認中（SHA-256）", 0.75 + 0.05 * d / (t ? t : 1));
        }) || s256 != man.sha256) {
        fs::remove(zip, ec);
        if (haveExe) { res.ok = true; res.offlineFallback = true; res.error = "ハッシュ不一致"; return res; }
        res.error = "本体パッケージの照合に失敗しました（ハッシュ不一致）";
        return res;
    }

    fs::path stage = g_writable / "app.new";
    fs::path oldApp = g_writable / "app.old";
    fs::remove_all(stage, ec);
    fs::create_directories(stage, ec);
    g_prog.SetStatus("本体 v" + man.version + "\n展開中…", 0.82);
    auto ex = ExtractAppZip(zip, stage,
                            [&](uint64_t d, uint64_t t) { g_prog.SetStatus("本体 v" + man.version + "\n展開中: " + Mb(d) + " / " + Mb(t), 0.82 + 0.12 * d / (t ? t : 1)); },
                            [] { return g_prog.cancel.load(); });
    if (!ex.ok) {
        fs::remove_all(stage, ec);
        if (g_prog.cancel) { res.cancelled = true; res.error = "中止しました"; return res; }
        if (haveExe) { res.ok = true; res.offlineFallback = true; res.error = ex.error; return res; }
        res.error = "本体の展開に失敗: " + ex.error;
        return res;
    }
    if (!WriteInstalledApp(stage, man, ex.files, Now())) {
        fs::remove_all(stage, ec);
        res.error = "本体の導入記録を書けません";
        return res;
    }
    // アプリ配下へ原子的に入れ替え（g_root\app）。書き込み先が別ならコピーでは足りないので、
    // 常に g_root\app を差し替える（起動ツールと同じフォルダ構成を保つ）
    fs::path destApp = g_root / "app";
    fs::path destOld = g_root / "app.old";
    // Program Files などで g_root に書けない場合は失敗 → 既存があればフォールバック
    auto sw = SwapAppDir(stage, destApp, destOld);
    if (!sw.ok) {
        fs::remove_all(stage, ec);
        Log("app-update swap failed: " + sw.error);
        if (haveExe) { res.ok = true; res.offlineFallback = true; res.error = sw.error; return res; }
        res.error = "本体の入れ替えに失敗: " + sw.error;
        return res;
    }
    fs::remove(zip, ec);
    Log("app-update installed " + man.version + " files=" + std::to_string(ex.files.size()));
    res.ok = true; res.updated = true; res.version = man.version;
    g_prog.SetStatus("本体を v" + man.version + " に更新しました", 0.95);
    return res;
}

// ---------------- 部品の導入 ----------------
struct InstallResult { bool ok = false; bool cancelled = false; std::string error; };

static InstallResult InstallComponent(const Component& c, const fs::path& runtimeDir, size_t idx, size_t count) {
    InstallResult res;
    std::error_code ec;
    std::string head = "(" + std::to_string(idx + 1) + "/" + std::to_string(count) + ") " + c.display_name;
    g_prog.Set(".NET を準備しています（初回のみ・システムにはインストールしません）", head, 0);
    fs::path dlDir = g_writable / "runtime-download"; fs::create_directories(dlDir, ec);
    fs::path zip = dlDir / fs::u8path(c.file_name), part = zip; part += ".part";

    // 空き容量（zip + 展開後 約2.6倍 + 余裕）
    ULARGE_INTEGER freeB;
    if (GetDiskFreeSpaceExW(g_writable.wstring().c_str(), &freeB, nullptr, nullptr)) {
        uint64_t need = c.size_bytes * 4 + 64ull * 1048576;
        if (freeB.QuadPart < need) { res.error = "空き容量が足りません（必要 約" + Mb(need) + "、空き " + Mb(freeB.QuadPart) + "）"; return res; }
    }

    // 既に完全な zip があれば再ダウンロードしない
    bool haveZip = false;
    if (fs::exists(zip, ec) && fs::file_size(zip, ec) == c.size_bytes) haveZip = true;
    else fs::remove(zip, ec);

    if (!haveZip) {
        std::string lastErr;
        for (int attempt = 1; attempt <= 5 && !haveZip; attempt++) {
            for (auto& url : c.urls) {
                Log("download " + c.id + " attempt=" + std::to_string(attempt) + " url=" + url + " part=" +
                    std::to_string(fs::exists(part, ec) ? fs::file_size(part, ec) : 0));
                auto d = DownloadOnce(W(url), part, c.size_bytes, head);
                if (d.cancelled) { res.cancelled = true; res.error = d.error; Log("download cancelled " + c.id); return res; }
                if (d.ok) { haveZip = true; break; }
                lastErr = d.error; Log("download failed " + c.id + ": " + d.error);
                if (d.fatal) break;
            }
            if (!haveZip && attempt < 5) {
                for (int w = attempt * 3; w > 0 && !g_prog.cancel; w--) {
                    g_prog.SetStatus(head + "\n" + lastErr + " — " + std::to_string(w) + " 秒後に再試行（" + std::to_string(attempt + 1) + "/5 回目）", 0);
                    Sleep(1000);
                }
                if (g_prog.cancel) { res.cancelled = true; res.error = "中止しました"; return res; }
            }
        }
        if (!haveZip) { res.error = "ダウンロードできませんでした: " + lastErr; return res; }
        fs::remove(zip, ec); fs::rename(part, zip, ec);
        if (ec) { res.error = "ダウンロードしたファイルを移動できません"; return res; }
    }

    // 照合（SHA-512 と SHA-256 の両方）
    g_prog.SetStatus(head + "\n改ざん・破損がないか確認中（SHA-512 / SHA-256）", 0.7);
    std::string s256, s512;
    if (!HashFile(zip, s256, s512, [&](uint64_t d, uint64_t t) { g_prog.SetStatus(head + "\n改ざん・破損がないか確認中（SHA-512 / SHA-256）", 0.7 + 0.1 * d / (t ? t : 1)); })) {
        res.error = "ダウンロードしたファイルを読めません"; return res;
    }
    Log("verify " + c.id + " sha256=" + s256 + " sha512=" + s512.substr(0, 16) + "...");
    if (s256 != c.sha256 || s512 != c.sha512) {
        fs::remove(zip, ec);
        res.error = "ファイルの照合に失敗しました（ハッシュ不一致。破損の可能性があるため削除しました。もう一度試すと取り直します）";
        return res;
    }

    // 展開（古い導入記録は先に外す → 途中で止まっても「未導入」扱いになり次回やり直す）。
    // 別の版の記録は .old-<版>.lock に退避し、全部品の導入が済んだあとで旧版のファイルだけを片付ける
    if (RetireLock(runtimeDir, c.id, c.version)) Log("retired old lock " + c.id + " (new " + c.version + ")");
    auto ex = ExtractZip(zip, runtimeDir,
                         [&](uint64_t d, uint64_t t) { g_prog.SetStatus(head + "\n展開中: " + Mb(d) + " / " + Mb(t), 0.8 + 0.2 * d / (t ? t : 1)); },
                         [] { return g_prog.cancel.load(); });
    if (!ex.ok) {
        if (g_prog.cancel) { res.cancelled = true; res.error = "中止しました"; return res; }
        res.error = "展開に失敗: " + ex.error; return res;
    }
    LockInfo li{c.id, c.version, c.sha256, Now(), ex.files};
    if (!WriteAllAtomic(LockPath(runtimeDir, c.id), FormatLock(li))) { res.error = "導入記録を書けません"; return res; }
    fs::remove(zip, ec);
    Log("installed " + c.id + " " + c.version + " files=" + std::to_string(ex.files.size()) + " bytes=" + std::to_string(ex.bytes));
    res.ok = true;
    return res;
}

// ---------------- 起動 ----------------
static std::wstring Quote(const std::wstring& a) {
    if (!a.empty() && a.find_first_of(L" \t\"") == std::wstring::npos) return a;
    std::wstring o = L"\""; size_t bs = 0;
    for (wchar_t ch : a) {
        if (ch == L'\\') { bs++; continue; }
        if (ch == L'"') { o.append(bs * 2 + 1, L'\\'); o += L'"'; bs = 0; continue; }
        o.append(bs, L'\\'); bs = 0; o += ch;
    }
    o.append(bs * 2, L'\\'); o += L'"'; return o;
}

static void Fail(const std::string& msg) {
    Log("error: " + msg);
    MessageBoxW(nullptr, W(msg + "\n\n詳細: data\\logs\\launcher.log").c_str(), L"ゲーム攻略くん", MB_OK | MB_ICONERROR);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, LPWSTR, int) {
    wchar_t self[MAX_PATH * 4]; GetModuleFileNameW(nullptr, self, MAX_PATH * 4);
    g_root = fs::path(self).parent_path();
    g_writable = g_root;
    // 本体（AppEnv）と同じ規則: portable.flag があるか書き込めるならアプリ配下、そうでなければ %LOCALAPPDATA%\GameKouryakuKun
    if (!fs::exists(g_root / "portable.flag") && !CanWrite(g_root)) {
        wchar_t la[MAX_PATH * 2]; DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", la, MAX_PATH * 2);
        if (n > 0 && n < MAX_PATH * 2) g_writable = fs::path(la) / L"GameKouryakuKun";
    }
    g_dataRoot = g_writable == g_root ? g_root / "data" : g_writable;
    int argc = 0; LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    bool verify = false, repair = false, checkOnly = false, cleanupOnly = false, offline = false; std::vector<std::wstring> pass;
    for (int i = 1; i < argc; i++) {
        std::wstring a = argv[i];
        if (a == L"--verify") verify = true;
        else if (a == L"--repair") repair = true;
        else if (a == L"--check") checkOnly = true;
        else if (a == L"--cleanup") cleanupOnly = true;
        else if (a == L"--offline") offline = true;
        else pass.push_back(a);
    }
    Log("start root=" + U(g_root.wstring()) + (g_writable != g_root ? " writable=" + U(g_writable.wstring()) : std::string())
        + (verify ? " --verify" : "") + (repair ? " --repair" : "") + (checkOnly ? " --check" : "")
        + (cleanupOnly ? " --cleanup" : "") + (offline ? " --offline" : ""));

    std::error_code ec;
    fs::path runtimeDir = g_writable / fs::u8path(kRuntimeDir);
    auto comps = EmbeddedComponents();

    // 同時に2つのランチャーが導入・更新しないように
    HANDLE mtx = CreateMutexW(nullptr, FALSE, L"Local\\GameKouryakuKun.Launcher.Runtime");
    if (mtx) WaitForSingleObject(mtx, INFINITE);

    // ---- 本体の更新確認（ランタイムより先。ダウンロード自体に .NET は不要）----
    AppUpdateResult appUp;
    bool progressShown = false;
    auto ensureProgress = [&] {
        if (!progressShown) { CreateProgressWindow(inst); progressShown = true; }
    };
    if (!cleanupOnly) {
        // --check でもネットで版を確認する（--offline ならローカルだけ）
        if (!checkOnly || !offline) ensureProgress();
        if (checkOnly && offline) {
            std::string v, s; bool have = ReadInstalledApp(g_root / "app", v, s);
            appUp.ok = fs::exists(g_root / "app" / L"GameKouryakuKun.exe", ec);
            appUp.version = have ? v : (appUp.ok ? "(未記録)" : "(なし)");
            appUp.report = "本体: " + appUp.version + "（--offline）\n";
        } else {
            g_prog.cancel = false; g_workerDone = false;
            if (progressShown) EnableWindow(g_cancel, TRUE);
            std::thread worker([&] {
                appUp = EnsureAppUpdated(repair, offline, !checkOnly);
                if (progressShown) {
                    PostMessageW(g_wnd, WM_TIMER, TIMER_ID, 0);
                    PostMessageW(g_wnd, WM_WORKER_DONE, 0, 0);
                } else g_workerDone = true;
            });
            if (progressShown) {
                MSG msg;
                while (!g_workerDone && GetMessageW(&msg, nullptr, 0, 0) > 0) {
                    if (!IsDialogMessageW(g_wnd, &msg)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
                }
            }
            worker.join();
            g_workerDone = false;
        }
        if (appUp.cancelled) { Log("app-update cancelled"); if (mtx) ReleaseMutex(mtx); if (progressShown) DestroyWindow(g_wnd); return 3; }
        if (!appUp.ok) {
            if (mtx) ReleaseMutex(mtx);
            if (progressShown) DestroyWindow(g_wnd);
            Fail(appUp.error.empty() ? "本体を用意できませんでした。" : appUp.error);
            return 2;
        }
        if (appUp.offlineFallback && !appUp.error.empty())
            Log("app-update warning (using existing): " + appUp.error);
    }

    fs::path appExe = g_root / "app" / L"GameKouryakuKun.exe";
    if (!fs::exists(appExe, ec)) {
        if (mtx) ReleaseMutex(mtx);
        if (progressShown) DestroyWindow(g_wnd);
        Fail("本体が見つかりません: " + U(appExe.wstring()));
        return 2;
    }

    std::vector<size_t> need;
    std::string report = appUp.report;
    for (size_t i = 0; i < comps.size(); i++) {
        auto h = CheckComponent(runtimeDir, comps[i], verify);
        Log("check " + comps[i].id + " " + comps[i].version + ": " + h.detail);
        report += comps[i].display_name + ": " + h.detail + "\n";
        if (repair || h.health != Health::Ok) need.push_back(i);
    }
    auto retired = RetiredLocks(runtimeDir);
    if (!retired.empty()) report += "片付け待ちの旧版の記録: " + std::to_string(retired.size()) + " 件\n";
    if (cleanupOnly) {
        std::string msg;
        if (!need.empty()) msg = "新しい版の .NET がまだ揃っていないため、旧版は消しません（先に通常どおり起動してください）。\n\n" + report;
        else { auto cr = CleanupRetired(runtimeDir); Log("cleanup: " + cr.detail); msg = cr.detail; }
        if (mtx) ReleaseMutex(mtx);
        if (progressShown) DestroyWindow(g_wnd);
        MessageBoxW(nullptr, W(msg).c_str(), L"ゲーム攻略くん - 旧版の片付け", MB_OK | MB_ICONINFORMATION);
        return 0;
    }
    if (checkOnly) {
        if (mtx) ReleaseMutex(mtx);
        if (progressShown) DestroyWindow(g_wnd);
        bool appOk = fs::exists(appExe, ec);
        MessageBoxW(nullptr, W(report + "\n場所: " + U(runtimeDir.wstring())).c_str(), L"ゲーム攻略くん - 確認",
                    MB_OK | ((need.empty() && appOk) ? MB_ICONINFORMATION : MB_ICONWARNING));
        return (need.empty() && appOk) ? 0 : 1;
    }

    if (!need.empty()) {
        ensureProgress();
        for (;;) {
            InstallResult result; result.ok = true;
            g_prog.cancel = false; g_workerDone = false; EnableWindow(g_cancel, TRUE);
            std::thread worker([&] {
                for (size_t k = 0; k < need.size(); k++) {
                    auto r = InstallComponent(comps[need[k]], runtimeDir, k, need.size());
                    if (!r.ok) { result = r; break; }
                }
                PostMessageW(g_wnd, WM_TIMER, TIMER_ID, 0);
                PostMessageW(g_wnd, WM_WORKER_DONE, 0, 0);
            });
            MSG msg;
            while (!g_workerDone && GetMessageW(&msg, nullptr, 0, 0) > 0) {  // 100ms タイマーで定期的に起きる
                if (!IsDialogMessageW(g_wnd, &msg)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
            }
            worker.join();
            if (result.ok) break;
            if (result.cancelled) { Log("cancelled by user"); if (mtx) ReleaseMutex(mtx); return 3; }
            Log("install failed: " + result.error);
            if (MessageBoxW(g_wnd, W(result.error + "\n\n「再試行」で続きからやり直します（ダウンロード済みの分は再利用）。").c_str(), L"ゲーム攻略くん - 準備に失敗",
                            MB_RETRYCANCEL | MB_ICONWARNING) != IDRETRY) { if (mtx) ReleaseMutex(mtx); return 4; }
            need.clear();
            for (size_t i = 0; i < comps.size(); i++) if (CheckComponent(runtimeDir, comps[i]).health != Health::Ok) need.push_back(i);
            if (need.empty()) break;
        }
        DestroyWindow(g_wnd); progressShown = false;
        MSG msg; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&msg);
        fs::remove(g_writable / "runtime-download", ec);  // 空なら消える
    } else if (progressShown) {
        DestroyWindow(g_wnd); progressShown = false;
        MSG msg; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&msg);
    }
    // 全部品が正常なときだけ旧版を片付ける（新しい版が使える状態を確かめてから消す）
    if (!RetiredLocks(runtimeDir).empty()) {
        bool allOk = true;
        for (auto& c : comps) if (CheckComponent(runtimeDir, c).health != Health::Ok) allOk = false;
        if (allOk) { auto cr = CleanupRetired(runtimeDir); Log("cleanup: " + cr.detail); }
        else Log("cleanup postponed: new runtime not complete");
    }
    if (mtx) ReleaseMutex(mtx);

    // 環境変数（子プロセスにだけ効く。システム設定は変えない）
    SetEnvironmentVariableW(L"DOTNET_ROOT", runtimeDir.wstring().c_str());
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", runtimeDir.wstring().c_str());
    SetEnvironmentVariableW(L"DOTNET_MULTILEVEL_LOOKUP", L"0");
    SetEnvironmentVariableW(L"DOTNET_CLI_TELEMETRY_OPTOUT", L"1");
    SetEnvironmentVariableW(L"GKK_HOME", g_root.wstring().c_str());
    SetEnvironmentVariableW(L"GKK_LAUNCHER", self);

    std::wstring cmd = Quote(appExe.wstring());
    for (auto& a : pass) cmd += L" " + Quote(a);
    STARTUPINFOW si{sizeof si}; PROCESS_INFORMATION pi{};
    std::wstring wd = appExe.parent_path().wstring();
    if (!CreateProcessW(appExe.wstring().c_str(), &cmd[0], nullptr, nullptr, FALSE, 0, nullptr, wd.c_str(), &si, &pi)) {
        Fail("本体を起動できません: " + WinErr(GetLastError())); return 5;
    }
    Log("launched " + U(appExe.filename().wstring()) + " pid=" + std::to_string(pi.dwProcessId));
    DWORD code = 0;
    // 起動直後に落ちた場合（ランタイム不整合など）を検出して案内する
    if (WaitForSingleObject(pi.hProcess, 3000) == WAIT_OBJECT_0) {
        GetExitCodeProcess(pi.hProcess, &code);
        Log("exited early code=" + std::to_string((long)code));
        if (code != 0)
            Fail("本体がすぐに終了しました（終了コード " + std::to_string((long)code) + "）。\n.NET が壊れている可能性があります。"
                 "「ゲーム攻略くん.exe --repair」で入れ直せます。");
    }
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return (int)code;
}
