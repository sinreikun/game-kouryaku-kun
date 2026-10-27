#include "core.h"
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <sstream>
#define MINIZ_NO_ZLIB_COMPATIBLE_NAMES
#include "third_party/miniz/miniz.h"

namespace gkk {

static FILE* OpenFile(const fs::path& p, const char* mode) {
#ifdef _WIN32
    std::wstring m(mode, mode + strlen(mode));
    return _wfopen(p.wstring().c_str(), m.c_str());
#else
    return fopen(p.string().c_str(), mode);
#endif
}

std::string ToLower(std::string s) { for (auto& c : s) if (c >= 'A' && c <= 'Z') c = char(c - 'A' + 'a'); return s; }

static std::string Hex(const uint8_t* d, size_t n) {
    static const char* k = "0123456789abcdef"; std::string s(n * 2, '0');
    for (size_t i = 0; i < n; i++) { s[2 * i] = k[d[i] >> 4]; s[2 * i + 1] = k[d[i] & 15]; }
    return s;
}

// ================= SHA-256 (FIPS 180-4) =================
static const uint32_t K256[64] = {
 0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,
 0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
 0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,
 0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
 0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,
 0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2};
static inline uint32_t R32(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
Sha256::Sha256() { const uint32_t i[8] = {0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19}; memcpy(h_, i, sizeof i); }
void Sha256::block(const uint8_t* p) {
    uint32_t w[64];
    for (int i = 0; i < 16; i++) w[i] = uint32_t(p[4*i]) << 24 | uint32_t(p[4*i+1]) << 16 | uint32_t(p[4*i+2]) << 8 | p[4*i+3];
    for (int i = 16; i < 64; i++) {
        uint32_t s0 = R32(w[i-15],7) ^ R32(w[i-15],18) ^ (w[i-15] >> 3), s1 = R32(w[i-2],17) ^ R32(w[i-2],19) ^ (w[i-2] >> 10);
        w[i] = w[i-16] + s0 + w[i-7] + s1;
    }
    uint32_t a=h_[0],b=h_[1],c=h_[2],d=h_[3],e=h_[4],f=h_[5],g=h_[6],h=h_[7];
    for (int i = 0; i < 64; i++) {
        uint32_t t1 = h + (R32(e,6)^R32(e,11)^R32(e,25)) + ((e&f)^(~e&g)) + K256[i] + w[i];
        uint32_t t2 = (R32(a,2)^R32(a,13)^R32(a,22)) + ((a&b)^(a&c)^(b&c));
        h=g; g=f; f=e; e=d+t1; d=c; c=b; b=a; a=t1+t2;
    }
    h_[0]+=a; h_[1]+=b; h_[2]+=c; h_[3]+=d; h_[4]+=e; h_[5]+=f; h_[6]+=g; h_[7]+=h;
}
void Sha256::update(const void* data, size_t len) {
    auto p = static_cast<const uint8_t*>(data); total_ += len;
    while (len) { size_t k = std::min(len, 64 - n_); memcpy(buf_ + n_, p, k); n_ += k; p += k; len -= k; if (n_ == 64) { block(buf_); n_ = 0; } }
}
std::string Sha256::hex() {
    uint64_t bits = total_ * 8; uint8_t pad = 0x80; update(&pad, 1); uint8_t z = 0;
    while (n_ != 56) update(&z, 1);
    uint8_t l[8]; for (int i = 0; i < 8; i++) l[i] = uint8_t(bits >> (56 - 8*i)); update(l, 8);
    uint8_t out[32]; for (int i = 0; i < 8; i++) for (int j = 0; j < 4; j++) out[4*i+j] = uint8_t(h_[i] >> (24 - 8*j));
    return Hex(out, 32);
}

// ================= SHA-512 =================
static const uint64_t K512[80] = {
 0x428a2f98d728ae22ULL,0x7137449123ef65cdULL,0xb5c0fbcfec4d3b2fULL,0xe9b5dba58189dbbcULL,0x3956c25bf348b538ULL,0x59f111f1b605d019ULL,
 0x923f82a4af194f9bULL,0xab1c5ed5da6d8118ULL,0xd807aa98a3030242ULL,0x12835b0145706fbeULL,0x243185be4ee4b28cULL,0x550c7dc3d5ffb4e2ULL,
 0x72be5d74f27b896fULL,0x80deb1fe3b1696b1ULL,0x9bdc06a725c71235ULL,0xc19bf174cf692694ULL,0xe49b69c19ef14ad2ULL,0xefbe4786384f25e3ULL,
 0x0fc19dc68b8cd5b5ULL,0x240ca1cc77ac9c65ULL,0x2de92c6f592b0275ULL,0x4a7484aa6ea6e483ULL,0x5cb0a9dcbd41fbd4ULL,0x76f988da831153b5ULL,
 0x983e5152ee66dfabULL,0xa831c66d2db43210ULL,0xb00327c898fb213fULL,0xbf597fc7beef0ee4ULL,0xc6e00bf33da88fc2ULL,0xd5a79147930aa725ULL,
 0x06ca6351e003826fULL,0x142929670a0e6e70ULL,0x27b70a8546d22ffcULL,0x2e1b21385c26c926ULL,0x4d2c6dfc5ac42aedULL,0x53380d139d95b3dfULL,
 0x650a73548baf63deULL,0x766a0abb3c77b2a8ULL,0x81c2c92e47edaee6ULL,0x92722c851482353bULL,0xa2bfe8a14cf10364ULL,0xa81a664bbc423001ULL,
 0xc24b8b70d0f89791ULL,0xc76c51a30654be30ULL,0xd192e819d6ef5218ULL,0xd69906245565a910ULL,0xf40e35855771202aULL,0x106aa07032bbd1b8ULL,
 0x19a4c116b8d2d0c8ULL,0x1e376c085141ab53ULL,0x2748774cdf8eeb99ULL,0x34b0bcb5e19b48a8ULL,0x391c0cb3c5c95a63ULL,0x4ed8aa4ae3418acbULL,
 0x5b9cca4f7763e373ULL,0x682e6ff3d6b2b8a3ULL,0x748f82ee5defb2fcULL,0x78a5636f43172f60ULL,0x84c87814a1f0ab72ULL,0x8cc702081a6439ecULL,
 0x90befffa23631e28ULL,0xa4506cebde82bde9ULL,0xbef9a3f7b2c67915ULL,0xc67178f2e372532bULL,0xca273eceea26619cULL,0xd186b8c721c0c207ULL,
 0xeada7dd6cde0eb1eULL,0xf57d4f7fee6ed178ULL,0x06f067aa72176fbaULL,0x0a637dc5a2c898a6ULL,0x113f9804bef90daeULL,0x1b710b35131c471bULL,
 0x28db77f523047d84ULL,0x32caab7b40c72493ULL,0x3c9ebe0a15c9bebcULL,0x431d67c49c100d4cULL,0x4cc5d4becb3e42b6ULL,0x597f299cfc657e2aULL,
 0x5fcb6fab3ad6faecULL,0x6c44198c4a475817ULL};
static inline uint64_t R64(uint64_t x, int n) { return (x >> n) | (x << (64 - n)); }
Sha512::Sha512() {
    const uint64_t i[8] = {0x6a09e667f3bcc908ULL,0xbb67ae8584caa73bULL,0x3c6ef372fe94f82bULL,0xa54ff53a5f1d36f1ULL,
                           0x510e527fade682d1ULL,0x9b05688c2b3e6c1fULL,0x1f83d9abfb41bd6bULL,0x5be0cd19137e2179ULL};
    memcpy(h_, i, sizeof i);
}
void Sha512::block(const uint8_t* p) {
    uint64_t w[80];
    for (int i = 0; i < 16; i++) { w[i] = 0; for (int j = 0; j < 8; j++) w[i] = (w[i] << 8) | p[8*i+j]; }
    for (int i = 16; i < 80; i++) {
        uint64_t s0 = R64(w[i-15],1) ^ R64(w[i-15],8) ^ (w[i-15] >> 7), s1 = R64(w[i-2],19) ^ R64(w[i-2],61) ^ (w[i-2] >> 6);
        w[i] = w[i-16] + s0 + w[i-7] + s1;
    }
    uint64_t a=h_[0],b=h_[1],c=h_[2],d=h_[3],e=h_[4],f=h_[5],g=h_[6],h=h_[7];
    for (int i = 0; i < 80; i++) {
        uint64_t t1 = h + (R64(e,14)^R64(e,18)^R64(e,41)) + ((e&f)^(~e&g)) + K512[i] + w[i];
        uint64_t t2 = (R64(a,28)^R64(a,34)^R64(a,39)) + ((a&b)^(a&c)^(b&c));
        h=g; g=f; f=e; e=d+t1; d=c; c=b; b=a; a=t1+t2;
    }
    h_[0]+=a; h_[1]+=b; h_[2]+=c; h_[3]+=d; h_[4]+=e; h_[5]+=f; h_[6]+=g; h_[7]+=h;
}
void Sha512::update(const void* data, size_t len) {
    auto p = static_cast<const uint8_t*>(data); total_ += len;
    while (len) { size_t k = std::min(len, 128 - n_); memcpy(buf_ + n_, p, k); n_ += k; p += k; len -= k; if (n_ == 128) { block(buf_); n_ = 0; } }
}
std::string Sha512::hex() {
    uint64_t bits = total_ * 8; uint8_t pad = 0x80; update(&pad, 1); uint8_t z = 0;
    while (n_ != 112) update(&z, 1);
    uint8_t l[16] = {0}; for (int i = 0; i < 8; i++) l[8 + i] = uint8_t(bits >> (56 - 8*i)); update(l, 16);
    uint8_t out[64]; for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) out[8*i+j] = uint8_t(h_[i] >> (56 - 8*j));
    return Hex(out, 64);
}

bool HashFile(const fs::path& p, std::string& s256, std::string& s512, const std::function<void(uint64_t, uint64_t)>& progress) {
    FILE* f = OpenFile(p, "rb"); if (!f) return false;
    std::error_code ec; uint64_t total = fs::file_size(p, ec), done = 0;
    Sha256 a; Sha512 b; std::vector<uint8_t> buf(1 << 20); size_t n;
    while ((n = fread(buf.data(), 1, buf.size(), f)) > 0) { a.update(buf.data(), n); b.update(buf.data(), n); done += n; if (progress) progress(done, total); }
    bool ok = !ferror(f); fclose(f);
    s256 = a.hex(); s512 = b.hex();
    return ok;
}

// ================= zip =================
bool SafeRelativePath(const std::string& name, fs::path& out) {
    if (name.empty() || name.size() > 400) return false;
    if (name[0] == '/' || name[0] == '\\') return false;
    if (name.find(':') != std::string::npos) return false;  // ドライブ指定・代替データストリーム
    for (unsigned char c : name) if (c < 0x20) return false;
    fs::path r;
    std::string seg;
    auto flush = [&](bool last) -> bool {
        if (seg.empty()) return last;  // 末尾の "/"（ディレクトリ）だけ許す
        if (seg == "." || seg == "..") return false;
        if (seg.back() == ' ' || seg.back() == '.') return false;  // Windows で別名になる
        std::string up = seg; for (auto& ch : up) if (ch >= 'a' && ch <= 'z') ch = char(ch - 'a' + 'A');
        std::string stem = up.substr(0, up.find('.'));
        static const char* reserved[] = {"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"};
        for (auto* rv : reserved) if (stem == rv) return false;
#if defined(__cpp_char8_t)
        r /= fs::path(std::u8string(reinterpret_cast<const char8_t*>(seg.data()), seg.size()));
#else
        r /= fs::u8path(seg);
#endif
        seg.clear(); return true;
    };
    for (size_t i = 0; i < name.size(); i++) {
        char c = name[i];
        if (c == '/' || c == '\\') { if (seg.empty()) return false; if (!flush(false)) return false; }
        else seg += c;
    }
    if (!seg.empty() && !flush(true)) return false;
    if (r.empty()) return false;
    out = r; return true;
}

namespace {
struct WriteCtx { FILE* f; uint64_t done; uint64_t* global; uint64_t total; const std::function<void(uint64_t, uint64_t)>* progress; const std::function<bool()>* cancel; bool cancelled; };
size_t WriteCb(void* opaque, mz_uint64, const void* buf, size_t n) {
    auto* c = static_cast<WriteCtx*>(opaque);
    if (c->cancel && *c->cancel && (*c->cancel)()) { c->cancelled = true; return 0; }
    if (fwrite(buf, 1, n, c->f) != n) return 0;
    *c->global += n;
    if (c->progress && *c->progress) (*c->progress)(*c->global, c->total);
    return n;
}
}  // namespace

ExtractResult ExtractZip(const fs::path& zipPath, const fs::path& dest,
                         const std::function<void(uint64_t, uint64_t)>& progress, const std::function<bool()>& cancel) {
    ExtractResult res;
    std::string data = ReadAll(zipPath);
    if (data.empty()) { res.error = "zip を読めません"; return res; }
    mz_zip_archive z; memset(&z, 0, sizeof z);
    if (!mz_zip_reader_init_mem(&z, data.data(), data.size(), 0)) { res.error = "zip の形式が不正です"; return res; }
    mz_uint n = mz_zip_reader_get_num_files(&z);
    uint64_t total = 0;
    for (mz_uint i = 0; i < n; i++) { mz_zip_archive_file_stat st; if (mz_zip_reader_file_stat(&z, i, &st)) total += st.m_uncomp_size; }
    uint64_t done = 0;
    std::error_code ec;
    for (mz_uint i = 0; i < n; i++) {
        mz_zip_archive_file_stat st;
        if (!mz_zip_reader_file_stat(&z, i, &st)) { res.error = "zip の目次を読めません"; break; }
        fs::path rel;
        if (!SafeRelativePath(st.m_filename, rel)) { res.error = std::string("安全でないパスを含みます: ") + st.m_filename; break; }
        fs::path out = dest / rel;
        if (mz_zip_reader_is_file_a_directory(&z, i)) { fs::create_directories(out, ec); continue; }
        fs::create_directories(out.parent_path(), ec);
        FILE* f = OpenFile(out, "wb");
        if (!f) { res.error = std::string("書き込めません（ほかのプログラムが使用中の可能性）: ") + st.m_filename; break; }
        WriteCtx ctx{f, 0, &done, total, &progress, &cancel, false};
        mz_bool ok = mz_zip_reader_extract_to_callback(&z, i, WriteCb, &ctx, 0);  // miniz が CRC32 を照合する
        bool closed = fclose(f) == 0;
        if (ctx.cancelled) { res.error = "中断しました"; break; }
        if (!ok || !closed) { res.error = std::string("展開に失敗（CRC 不一致または書き込みエラー）: ") + st.m_filename; break; }
        std::string relu8 = std::string(st.m_filename);
        for (auto& ch : relu8) if (ch == '\\') ch = '/';
        res.files.push_back({relu8, st.m_uncomp_size, st.m_crc32});
        res.bytes += st.m_uncomp_size;
    }
    mz_zip_reader_end(&z);
    res.ok = res.error.empty();
    return res;
}

// ================= lock =================
fs::path LockPath(const fs::path& runtimeDir, const std::string& id) { return runtimeDir / ".gkk" / (id + ".lock"); }

std::string FormatLock(const LockInfo& l) {
    std::ostringstream o;
    o << "# ゲーム攻略くん ランチャーの導入記録。手で編集しないでください（消すと次回起動時に再導入します）\n";
    o << "id=" << l.id << "\nversion=" << l.version << "\nzip_sha256=" << l.sha256 << "\ninstalled_at=" << l.installed_at << "\n";
    for (auto& f : l.files) { char crc[9]; snprintf(crc, sizeof crc, "%08x", f.crc32); o << "F\t" << f.size << "\t" << crc << "\t" << f.rel << "\n"; }
    o << "end\n";
    return o.str();
}

bool ParseLock(const std::string& text, LockInfo& out) {
    std::istringstream in(text); std::string line; bool end = false; LockInfo l;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        if (line == "end") { end = true; break; }
        if (line.rfind("F\t", 0) == 0) {
            size_t a = line.find('\t', 2), b = a == std::string::npos ? a : line.find('\t', a + 1);
            if (b == std::string::npos) return false;
            InstalledFile f; f.size = std::stoull(line.substr(2, a - 2)); f.crc32 = uint32_t(std::stoul(line.substr(a + 1, b - a - 1), nullptr, 16)); f.rel = line.substr(b + 1);
            l.files.push_back(f); continue;
        }
        auto eq = line.find('='); if (eq == std::string::npos) return false;
        auto k = line.substr(0, eq), v = line.substr(eq + 1);
        if (k == "id") l.id = v; else if (k == "version") l.version = v; else if (k == "zip_sha256") l.sha256 = v; else if (k == "installed_at") l.installed_at = v;
    }
    if (!end || l.id.empty() || l.files.empty()) return false;  // 書きかけ（"end" なし）は無効
    out = l; return true;
}

static uint32_t Crc32File(const fs::path& p, bool& ok) {
    FILE* f = OpenFile(p, "rb"); ok = f != nullptr; if (!f) return 0;
    std::vector<uint8_t> buf(1 << 20); size_t n; mz_ulong crc = MZ_CRC32_INIT;
    while ((n = fread(buf.data(), 1, buf.size(), f)) > 0) crc = mz_crc32(crc, buf.data(), n);
    fclose(f); return uint32_t(crc);
}

HealthResult CheckComponent(const fs::path& runtimeDir, const Component& c, bool deep) {
    HealthResult r;
    auto lp = LockPath(runtimeDir, c.id);
    std::error_code ec;
    if (!fs::exists(lp, ec)) { r.health = Health::Missing; r.detail = "未導入"; return r; }
    LockInfo l;
    if (!ParseLock(ReadAll(lp), l)) { r.health = Health::Broken; r.detail = "導入記録が壊れています"; return r; }
    if (l.id != c.id || l.version != c.version || ToLower(l.sha256) != ToLower(c.sha256)) { r.health = Health::Broken; r.detail = "版が違います（" + l.version + "）"; return r; }
    for (auto& rf : c.required_files) {
        bool found = false; for (auto& f : l.files) if (f.rel == rf) { found = true; break; }
        if (!found) { r.health = Health::Broken; r.detail = "必須ファイルの記録がありません: " + rf; return r; }
    }
    for (auto& f : l.files) {
        fs::path rel; if (!SafeRelativePath(f.rel, rel)) { r.health = Health::Broken; r.detail = "記録のパスが不正"; return r; }
        auto p = runtimeDir / rel;
        auto sz = fs::file_size(p, ec);
        if (ec) { r.health = Health::Broken; r.detail = "ファイルがありません: " + f.rel; return r; }
        if (sz != f.size) { r.health = Health::Broken; r.detail = "サイズが違います: " + f.rel; return r; }
        if (deep) { bool ok; uint32_t crc = Crc32File(p, ok); if (!ok || crc != f.crc32) { r.health = Health::Broken; r.detail = "内容が違います（CRC32）: " + f.rel; return r; } }
    }
    r.health = Health::Ok; r.detail = "導入済み（" + std::to_string(l.files.size()) + " ファイル" + (deep ? "・CRC32 照合済み" : "") + "）";
    return r;
}

static bool IsRetiredName(const std::string& n) { return n.find(".old-") != std::string::npos && n.size() > 5 && n.substr(n.size() - 5) == ".lock"; }

bool RetireLock(const fs::path& runtimeDir, const std::string& id, const std::string& newVersion) {
    std::error_code ec;
    auto lp = LockPath(runtimeDir, id);
    if (!fs::exists(lp, ec)) return false;
    LockInfo l;
    bool retired = false;
    if (ParseLock(ReadAll(lp), l) && l.version != newVersion && !l.version.empty()) {
        std::string safe; for (char ch : l.version) safe += (isalnum((unsigned char)ch) || ch == '.' || ch == '-') ? ch : '_';
        auto old = runtimeDir / ".gkk" / (id + ".old-" + safe + ".lock");
        retired = WriteAllAtomic(old, ReadAll(lp));
        if (!retired) return false;  // 退避できないなら現行 lock を残す（片付けの手がかりを失わない）
    }
    fs::remove(lp, ec);
    return retired;
}

std::vector<fs::path> RetiredLocks(const fs::path& runtimeDir) {
    std::vector<fs::path> v; std::error_code ec;
    auto d = runtimeDir / ".gkk";
    if (!fs::is_directory(d, ec)) return v;
    for (auto& e : fs::directory_iterator(d, ec)) if (e.is_regular_file(ec) && IsRetiredName(e.path().filename().u8string())) v.push_back(e.path());
    std::sort(v.begin(), v.end());
    return v;
}

CleanupResult CleanupRetired(const fs::path& runtimeDir) {
    CleanupResult r; std::error_code ec;
    auto retired = RetiredLocks(runtimeDir);
    if (retired.empty()) { r.detail = "片付けるものはありません"; return r; }
    // 現行 lock の全ファイル（これは絶対に消さない）
    std::vector<std::string> keep;
    for (auto& e : fs::directory_iterator(runtimeDir / ".gkk", ec)) {
        auto n = e.path().filename().u8string();
        if (!e.is_regular_file(ec) || IsRetiredName(n) || n.size() < 5 || n.substr(n.size() - 5) != ".lock") continue;
        LockInfo l;
        if (!ParseLock(ReadAll(e.path()), l)) { r.skipped = true; r.detail = "現行の導入記録が読めないため片付けを見送りました: " + n; return r; }
        for (auto& f : l.files) keep.push_back(ToLower(f.rel));  // Windows は大文字小文字を区別しない
    }
    std::sort(keep.begin(), keep.end());
    for (auto& lp : retired) {
        LockInfo old;
        if (!ParseLock(ReadAll(lp), old)) { fs::remove(lp, ec); continue; }  // 中身の分からない退避記録は捨てる（ファイルには触れない）
        int fail = 0;
        std::vector<fs::path> dirs;
        for (auto& f : old.files) {
            if (std::binary_search(keep.begin(), keep.end(), ToLower(f.rel))) continue;
            fs::path rel; if (!SafeRelativePath(f.rel, rel)) continue;
            auto p = runtimeDir / rel;
            for (auto d = rel.parent_path(); !d.empty(); d = d.parent_path()) dirs.push_back(d);
            if (!fs::exists(p, ec)) continue;
            if (!fs::is_regular_file(p, ec)) { fail++; continue; }
            if (fs::remove(p, ec) && !ec) r.removedFiles++; else { fail++; r.inUse++; }
        }
        std::sort(dirs.begin(), dirs.end(), [](const fs::path& a, const fs::path& b) {
            auto da = std::distance(a.begin(), a.end()), db = std::distance(b.begin(), b.end());
            return da != db ? da > db : a < b;
        });
        dirs.erase(std::unique(dirs.begin(), dirs.end()), dirs.end());
        for (auto& d : dirs) { auto p = runtimeDir / d; if (fs::is_directory(p, ec) && fs::is_empty(p, ec) && fs::remove(p, ec)) r.removedDirs++; }
        if (fail == 0) { fs::remove(lp, ec); r.finishedLocks++; } else r.pendingLocks++;
    }
    r.detail = "旧版の片付け: " + std::to_string(r.removedFiles) + " ファイル・" + std::to_string(r.removedDirs) + " フォルダを削除" +
               (r.pendingLocks ? "、使用中などで残ったもの " + std::to_string(r.inUse) + " 個（次回の起動時に再試行）" : "");
    return r;
}

uint64_t ResumeFrom(uint64_t partSize, uint64_t expected) { return (partSize > 0 && partSize < expected) ? partSize : 0; }

bool ParseContentRange(const std::string& v, uint64_t& start, uint64_t& total) {
    unsigned long long a, b, t;
    if (sscanf(v.c_str(), "bytes %llu-%llu/%llu", &a, &b, &t) != 3 || b < a || t <= b) return false;
    start = a; total = t; return true;
}

std::string ReadAll(const fs::path& p) {
    FILE* f = OpenFile(p, "rb"); if (!f) return {};
    std::string s; char buf[65536]; size_t n;
    while ((n = fread(buf, 1, sizeof buf, f)) > 0) s.append(buf, n);
    fclose(f); return s;
}

bool WriteAllAtomic(const fs::path& p, const std::string& data) {
    std::error_code ec; fs::create_directories(p.parent_path(), ec);
    auto tmp = p; tmp += ".tmp";
    FILE* f = OpenFile(tmp, "wb"); if (!f) return false;
    bool ok = fwrite(data.data(), 1, data.size(), f) == data.size(); ok = (fflush(f) == 0) && ok; ok = (fclose(f) == 0) && ok;
    if (!ok) return false;
    fs::remove(p, ec);
    fs::rename(tmp, p, ec);
    return !ec;
}

}  // namespace gkk
