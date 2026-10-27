// ゲーム攻略くん ランチャーの OS 非依存部分（Linux でも単体テストできるようにしてある）。
// ハッシュ（SHA-256 / SHA-512）、zip の安全な展開、導入記録（lock）の読み書き、レジューム判定。
#pragma once
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace gkk {

namespace fs = std::filesystem;

struct Component {
    std::string id, display_name, version, license_spdx, license_url, homepage, file_name, sha512, sha256;
    std::vector<std::string> urls, required_files;
    uint64_t size_bytes = 0;
};

// ---- ハッシュ（逐次計算） ----
class Sha256 {
public:
    Sha256();
    void update(const void* data, size_t len);
    std::string hex();  // 呼び出し後は再利用しない
private:
    void block(const uint8_t* p);
    uint32_t h_[8]; uint8_t buf_[64]; size_t n_ = 0; uint64_t total_ = 0;
};
class Sha512 {
public:
    Sha512();
    void update(const void* data, size_t len);
    std::string hex();
private:
    void block(const uint8_t* p);
    uint64_t h_[8]; uint8_t buf_[128]; size_t n_ = 0; uint64_t total_ = 0;
};
// ファイルの SHA-256 と SHA-512 を1回の読み取りで計算する。progress(done,total) は任意
bool HashFile(const fs::path& p, std::string& sha256, std::string& sha512, const std::function<void(uint64_t, uint64_t)>& progress = {});

// ---- zip ----
// zip 内のパスが安全か（絶対パス・ドライブ指定・".."・制御文字・予約名を拒否）。安全なら相対パスを返す
bool SafeRelativePath(const std::string& utf8, fs::path& out);

struct InstalledFile { std::string rel; uint64_t size; uint32_t crc32; };
struct ExtractResult { bool ok = false; std::string error; std::vector<InstalledFile> files; uint64_t bytes = 0; };
// zip を dest に展開（各ファイルの CRC32 をzip の記録と照合）。progress(doneBytes,totalBytes)、cancel() が true で中断
ExtractResult ExtractZip(const fs::path& zip, const fs::path& dest,
                         const std::function<void(uint64_t, uint64_t)>& progress = {}, const std::function<bool()>& cancel = {});

// ---- 導入記録 runtime\.gkk\<id>.lock ----
struct LockInfo { std::string id, version, sha256, installed_at; std::vector<InstalledFile> files; };
std::string FormatLock(const LockInfo& l);
bool ParseLock(const std::string& text, LockInfo& out);
fs::path LockPath(const fs::path& runtimeDir, const std::string& id);

enum class Health { Ok, Missing, Broken };
struct HealthResult { Health health = Health::Missing; std::string detail; };
// 記録どおりのファイルがそろっているか。deep=true なら CRC32 まで再計算
HealthResult CheckComponent(const fs::path& runtimeDir, const Component& c, bool deep = false);

// ---- 旧版の片付け ----
// 版を入れ替える前に、今の lock を .gkk/<id>.old-<旧版>.lock に退避する（同じ版・壊れた lock は単に消す）。退避したら true
bool RetireLock(const fs::path& runtimeDir, const std::string& id, const std::string& newVersion);
std::vector<fs::path> RetiredLocks(const fs::path& runtimeDir);
struct CleanupResult { int removedFiles = 0, removedDirs = 0, inUse = 0, finishedLocks = 0, pendingLocks = 0; bool skipped = false; std::string detail; };
// 退避した lock に載っていて、現行のどの lock にも載っていないファイルだけを消す（runtime\ の外・記録にないファイルには触れない）。
// 使用中で消せなければ退避 lock を残し、次回また試す。現行 lock のどれかが読めなければ何もしない
CleanupResult CleanupRetired(const fs::path& runtimeDir);

// ---- レジューム ----
// 途中ファイルのサイズから Range 開始位置を決める（期待サイズ以上なら 0 からやり直し）
uint64_t ResumeFrom(uint64_t partSize, uint64_t expectedSize);
// "bytes 100-199/1000" → start=100 total=1000
bool ParseContentRange(const std::string& v, uint64_t& start, uint64_t& total);

std::string ToLower(std::string s);
std::string ReadAll(const fs::path& p);
bool WriteAllAtomic(const fs::path& p, const std::string& data);

// ---- 本体（app\）の自動更新マニフェスト ----
// release/latest.json（GitHub raw）の最小 JSON。protocol / version / sha256 / bytes / url
struct AppUpdateManifest {
    int protocol = 0;
    std::string version;   // 例: 0.33.0
    std::string sha256;    // 小文字 hex 64
    uint64_t bytes = 0;
    std::string url;       // https のみ・許可ホストのみ
};
// 厳密検証。失敗時は error に日本語の理由。成功時 out を埋める
bool ParseAppUpdateManifest(const std::string& json, AppUpdateManifest& out, std::string& error);
// セマンティック版の比較。a>b → 1, a==b → 0, a<b → -1。形式不正なら -2
int CompareVersions(const std::string& a, const std::string& b);
// 更新 zip の URL として許可するか（https + 当リポジトリの GitHub Releases / objects）
bool IsAllowedAppUpdateUrl(const std::string& url);
// app\.gkk\app.lock のパス
fs::path AppLockPath(const fs::path& appDir);
// 導入記録から版と sha256 を読む。無ければ false
bool ReadInstalledApp(const fs::path& appDir, std::string& version, std::string& sha256);
// ステージ先へ展開済みのファイル一覧から app.lock を書く
bool WriteInstalledApp(const fs::path& appDir, const AppUpdateManifest& m, const std::vector<InstalledFile>& files, const std::string& installedAt);
// appDir の中身を空にしてから zip を展開（パス検査付き）。成功時 files を返す
ExtractResult ExtractAppZip(const fs::path& zip, const fs::path& appDir,
                            const std::function<void(uint64_t, uint64_t)>& progress = {},
                            const std::function<bool()>& cancel = {});
// stageDir（中身は新しい app）を destAppDir へ原子的に入れ替える。旧 dest は oldAppDir へ退避（あれば）。
// data\ には触れない。成功後に oldAppDir の削除を試す（失敗しても ok）
struct SwapAppResult { bool ok = false; std::string error; };
SwapAppResult SwapAppDir(const fs::path& stageDir, const fs::path& destAppDir, const fs::path& oldAppDir);

}  // namespace gkk
