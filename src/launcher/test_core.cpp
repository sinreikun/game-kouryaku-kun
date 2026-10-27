// Linux ネイティブで動かすランチャー中核の単体テスト。実ランタイム zip（環境変数 GKK_TEST_ZIPS_DIR）があれば実展開も行う。
#include "core.h"
#include "manifest_gen.h"
#define MINIZ_NO_ZLIB_COMPATIBLE_NAMES
#include "third_party/miniz/miniz.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
using namespace gkk;
static int fails = 0, passes = 0;
#define CHECK(x) do { if (x) passes++; else { fails++; fprintf(stderr, "FAIL %s:%d %s\n", __FILE__, __LINE__, #x); } } while (0)
static std::string h256(const std::string& s) { Sha256 h; h.update(s.data(), s.size()); return h.hex(); }
static std::string h512(const std::string& s) { Sha512 h; h.update(s.data(), s.size()); return h.hex(); }

int main() {
    // FIPS 180-4 / NIST テストベクタ
    CHECK(h256("") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    CHECK(h256("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    CHECK(h256("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq") == "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1");
    CHECK(h512("abc") == "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f");
    CHECK(h512("") == "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e");
    CHECK(h512("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu") ==
          "8e959b75dae313da8cf4f72814fc143f8f7779c6eb9f7fa17299aeadb6889018501d289e4900f7e4331b99dec4b5433ac7d329eeb6dd26545e96e55b874be909");
    { std::string m(1000000, 'a'); CHECK(h256(m) == "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"); }
    { // 分割 update でも同じ
        std::string s(1000, 'x'); for (size_t i = 0; i < s.size(); i++) s[i] = char(i * 7);
        Sha512 a; for (size_t i = 0; i < s.size(); i += 13) a.update(s.data() + i, std::min<size_t>(13, s.size() - i));
        CHECK(a.hex() == h512(s));
    }
    // パス検査
    fs::path r;
    CHECK(SafeRelativePath("shared/Microsoft.NETCore.App/10.0.13/coreclr.dll", r));
    CHECK(SafeRelativePath("dotnet.exe", r) && r == fs::path("dotnet.exe"));
    CHECK(SafeRelativePath("host/fxr/", r));
    CHECK(!SafeRelativePath("../evil.dll", r));
    CHECK(!SafeRelativePath("a/../../evil", r));
    CHECK(!SafeRelativePath("/etc/passwd", r));
    CHECK(!SafeRelativePath("\\windows\\x", r));
    CHECK(!SafeRelativePath("C:/x", r));
    CHECK(!SafeRelativePath("a//b", r));
    CHECK(!SafeRelativePath("a/CON.txt", r));
    CHECK(!SafeRelativePath("a/b. ", r));
    CHECK(!SafeRelativePath("", r));
    CHECK(!SafeRelativePath("x:stream", r));
    // レジューム
    CHECK(ResumeFrom(0, 100) == 0); CHECK(ResumeFrom(40, 100) == 40); CHECK(ResumeFrom(100, 100) == 0); CHECK(ResumeFrom(150, 100) == 0);
    uint64_t st, tot;
    CHECK(ParseContentRange("bytes 100-199/1000", st, tot) && st == 100 && tot == 1000);
    CHECK(!ParseContentRange("bytes */1000", st, tot));
    CHECK(!ParseContentRange("bytes 5-1/1000", st, tot));
    // lock 往復と書きかけ検出
    LockInfo l{"x", "1.0", "ab", "2026-10-18T01:00:00+09:00", {{"a/b.dll", 10, 0xdeadbeef}, {"日本語/ファイル.txt", 0, 0}}};
    LockInfo l2; CHECK(ParseLock(FormatLock(l), l2));
    CHECK(l2.files.size() == 2 && l2.files[0].crc32 == 0xdeadbeef && l2.files[1].rel == "日本語/ファイル.txt" && l2.version == "1.0");
    { auto t = FormatLock(l); t = t.substr(0, t.size() - 4); CHECK(!ParseLock(t, l2)); }
    // 埋め込みマニフェスト
    auto comps = EmbeddedComponents();
    CHECK(comps.size() == 2 && comps[0].id == "dotnet-runtime" && comps[1].id == "windowsdesktop-runtime");
    for (auto& c : comps) CHECK(c.license_spdx == "MIT" && c.urls.size() >= 1 && c.urls[0].rfind("https://builds.dotnet.microsoft.com/", 0) == 0);

    fs::path tmp = fs::temp_directory_path() / "gkk-launcher-test"; fs::remove_all(tmp); fs::create_directories(tmp);
    // 悪意ある zip（zip-slip）を拒否
    {
        mz_zip_archive z; memset(&z, 0, sizeof z); auto zp = (tmp / "evil.zip").string();
        mz_zip_writer_init_file(&z, zp.c_str(), 0);
        mz_zip_writer_add_mem(&z, "ok.txt", "hi", 2, MZ_DEFAULT_COMPRESSION);
        mz_zip_writer_add_mem(&z, "../escape.txt", "bad", 3, MZ_DEFAULT_COMPRESSION);
        mz_zip_writer_finalize_archive(&z); mz_zip_writer_end(&z);
        auto res = ExtractZip(zp, tmp / "out");
        CHECK(!res.ok && res.error.find("安全でない") != std::string::npos && !fs::exists(tmp / "escape.txt"));
    }
    // 壊れた zip（CRC 不一致）を検出
    {
        mz_zip_archive z; memset(&z, 0, sizeof z); auto zp = (tmp / "crc.zip").string();
        std::string payload(5000, 'q');
        mz_zip_writer_init_file(&z, zp.c_str(), 0);
        mz_zip_writer_add_mem(&z, "f.bin", payload.data(), payload.size(), 0);  // 無圧縮
        mz_zip_writer_finalize_archive(&z); mz_zip_writer_end(&z);
        std::string d = ReadAll(zp); auto pos = d.find(std::string(100, 'q')); d[pos + 50] = 'Z';
        FILE* f = fopen(zp.c_str(), "wb"); fwrite(d.data(), 1, d.size(), f); fclose(f);
        auto res = ExtractZip(zp, tmp / "crcout");
        CHECK(!res.ok);
    }
    // 旧版の退避と片付け（合成データ）
    {
        fs::path rt = tmp / "cleanup";
        auto put = [&](const std::string& rel, const std::string& body) { auto p = rt / rel; fs::create_directories(p.parent_path()); FILE* f = fopen(p.c_str(), "wb"); fwrite(body.data(), 1, body.size(), f); fclose(f); };
        put("dotnet.exe", "new"); put("shared/X/1.0/a.dll", "a"); put("shared/X/1.0/sub/b.dll", "b"); put("shared/X/2.0/c.dll", "c"); put("user-file.txt", "keep");
        LockInfo oldL{"x", "1.0", "s1", "t", {{"dotnet.exe", 3, 0}, {"shared/X/1.0/a.dll", 1, 0}, {"shared/X/1.0/sub/b.dll", 1, 0}, {"../outside.txt", 1, 0}}};
        CHECK(WriteAllAtomic(LockPath(rt, "x"), FormatLock(oldL)));
        CHECK(!RetireLock(rt, "x", "1.0") && !fs::exists(LockPath(rt, "x")) && RetiredLocks(rt).empty());  // 同じ版なら退避せず消すだけ
        CHECK(WriteAllAtomic(LockPath(rt, "x"), FormatLock(oldL)));
        CHECK(RetireLock(rt, "x", "2.0"));
        CHECK(!fs::exists(LockPath(rt, "x")) && RetiredLocks(rt).size() == 1);
        // 新版が未導入（lock なし）でも、現行 lock が無ければ dotnet.exe は守られない → 新版の lock を書いてから片付ける
        LockInfo newL{"x", "2.0", "s2", "t", {{"dotnet.exe", 3, 0}, {"shared/X/2.0/c.dll", 1, 0}}};
        CHECK(WriteAllAtomic(LockPath(rt, "x"), FormatLock(newL)));
        // 片付けを妨げる: 旧版のファイル名の場所にディレクトリがある → 失敗として退避 lock を残す
        fs::remove(rt / "shared/X/1.0/a.dll"); fs::create_directories(rt / "shared/X/1.0/a.dll/blocker");
        auto c1 = CleanupRetired(rt);
        CHECK(c1.pendingLocks == 1 && c1.removedFiles == 1 && RetiredLocks(rt).size() == 1);
        fs::remove_all(rt / "shared/X/1.0/a.dll");
        auto c2 = CleanupRetired(rt);
        CHECK(c2.pendingLocks == 0 && c2.finishedLocks == 1 && RetiredLocks(rt).empty());
        CHECK(fs::exists(rt / "dotnet.exe") && fs::exists(rt / "shared/X/2.0/c.dll") && fs::exists(rt / "user-file.txt"));
        CHECK(!fs::exists(rt / "shared/X/1.0") && fs::exists(rt / "shared/X"));
        CHECK(!fs::exists(tmp / "outside.txt"));
        // 現行 lock が壊れていたら何もしない
        CHECK(WriteAllAtomic(rt / ".gkk" / "y.old-1.lock", FormatLock(LockInfo{"y", "1", "s", "t", {{"dotnet.exe", 3, 0}}})));
        CHECK(WriteAllAtomic(LockPath(rt, "x"), "broken"));
        auto c3 = CleanupRetired(rt);
        CHECK(c3.skipped && fs::exists(rt / "dotnet.exe"));
    }
    // 実ランタイム zip（あれば）: 旧版（GKK_TEST_OLD_ZIPS_DIR + GKK_TEST_OLD_VERSION）を入れてから新版へ更新 → 旧版を片付け
    if (const char* dir = getenv("GKK_TEST_ZIPS_DIR")) {
        fs::path rt = tmp / "runtime";
        const char* oldDir = getenv("GKK_TEST_OLD_ZIPS_DIR"); const char* oldVer = getenv("GKK_TEST_OLD_VERSION");
        if (oldDir && oldVer) {
            for (auto& c : comps) {
                std::string fn = c.file_name; fn.replace(fn.find(c.version), c.version.size(), oldVer);
                fs::path zp = fs::path(oldDir) / fn; if (!fs::exists(zp)) { fprintf(stderr, "skip old %s\n", zp.c_str()); continue; }
                std::string a, b; HashFile(zp, a, b);
                auto res = ExtractZip(zp, rt); CHECK(res.ok);
                CHECK(WriteAllAtomic(LockPath(rt, c.id), FormatLock(LockInfo{c.id, oldVer, a, "old", res.files})));
                auto h = CheckComponent(rt, c); CHECK(h.health == Health::Broken);  // 版違い → 入れ直し対象
                printf("old %s %s: %zu files, %s\n", c.id.c_str(), oldVer, res.files.size(), h.detail.c_str());
            }
        }
        for (auto& c : comps) {
            fs::path zp = fs::path(dir) / c.file_name;
            if (!fs::exists(zp)) { fprintf(stderr, "skip %s\n", zp.c_str()); continue; }
            CHECK(fs::file_size(zp) == c.size_bytes);
            std::string a, b; CHECK(HashFile(zp, a, b));
            CHECK(a == c.sha256); CHECK(b == c.sha512);
            if (!oldDir) CHECK(CheckComponent(rt, c).health == Health::Missing);
            bool retired = RetireLock(rt, c.id, c.version); CHECK(retired == (oldDir != nullptr && oldVer != nullptr));
            uint64_t last = 0; auto res = ExtractZip(zp, rt, [&](uint64_t d, uint64_t) { last = d; });
            CHECK(res.ok); CHECK(last == res.bytes);
            LockInfo li{c.id, c.version, c.sha256, "test", res.files};
            CHECK(WriteAllAtomic(LockPath(rt, c.id), FormatLock(li)));
            auto h = CheckComponent(rt, c, true); CHECK(h.health == Health::Ok);
            printf("%s: %zu files, %llu bytes, %s\n", c.id.c_str(), res.files.size(), (unsigned long long)res.bytes, h.detail.c_str());
        }
        if (oldDir && oldVer) {
            auto cr = CleanupRetired(rt);
            printf("%s\n", cr.detail.c_str());
            CHECK(cr.pendingLocks == 0 && cr.removedFiles > 0 && RetiredLocks(rt).empty());
            bool leftover = false;
            for (auto& e : fs::recursive_directory_iterator(rt)) if (e.path().string().find(oldVer) != std::string::npos) { leftover = true; fprintf(stderr, "leftover %s\n", e.path().c_str()); }
            CHECK(!leftover);
            for (auto& c : comps) CHECK(CheckComponent(rt, c, true).health == Health::Ok);
            CHECK(fs::exists(rt / "dotnet.exe") && fs::exists(rt / "LICENSE.txt"));
        }
        if (fs::exists(rt / "dotnet.exe")) {
            auto c = comps[0];
            // 1バイト改ざん → 浅い検査は通り、深い検査（CRC32）で検出
            auto p = rt / ("host/fxr/" + c.version + "/hostfxr.dll"); std::string d = ReadAll(p); d[d.size() / 2] ^= 1;
            FILE* f = fopen(p.c_str(), "wb"); fwrite(d.data(), 1, d.size(), f); fclose(f);
            CHECK(CheckComponent(rt, c, false).health == Health::Ok);
            CHECK(CheckComponent(rt, c, true).health == Health::Broken);
            // ファイル削除 → 浅い検査で検出
            fs::remove(rt / "dotnet.exe"); CHECK(CheckComponent(rt, c, false).health == Health::Broken);
            // 版違い
            auto c2 = comps[1]; c2.version = "99.0.0"; CHECK(CheckComponent(rt, c2).health == Health::Broken);
        }
    }

    // ---- 本体更新マニフェスト ----
    {
        AppUpdateManifest m; std::string err;
        std::string good = R"({"protocol":1,"version":"0.33.0","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":12345,"url":"https://github.com/sinreikun/game-kouryaku-kun/releases/download/v0.33.0/app-update-0.33.0.zip"})";
        CHECK(ParseAppUpdateManifest(good, m, err));
        CHECK(m.protocol == 1 && m.version == "0.33.0" && m.bytes == 12345 && m.sha256.size() == 64);
        CHECK(IsAllowedAppUpdateUrl(m.url));
        CHECK(!ParseAppUpdateManifest(R"({"protocol":2,"version":"0.33.0","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":1,"url":"https://github.com/sinreikun/game-kouryaku-kun/releases/download/v0.33.0/x.zip"})", m, err));
        CHECK(!ParseAppUpdateManifest(R"({"protocol":1,"version":"0.33","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":1,"url":"https://github.com/sinreikun/game-kouryaku-kun/releases/download/v0.33.0/x.zip"})", m, err));
        CHECK(!ParseAppUpdateManifest(R"({"protocol":1,"version":"0.33.0","sha256":"zz","bytes":1,"url":"https://github.com/sinreikun/game-kouryaku-kun/releases/download/v0.33.0/x.zip"})", m, err));
        CHECK(!ParseAppUpdateManifest(R"({"protocol":1,"version":"0.33.0","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":0,"url":"https://github.com/sinreikun/game-kouryaku-kun/releases/download/v0.33.0/x.zip"})", m, err));
        CHECK(!ParseAppUpdateManifest(R"({"protocol":1,"version":"0.33.0","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":1,"url":"http://github.com/sinreikun/game-kouryaku-kun/x.zip"})", m, err));
        CHECK(!ParseAppUpdateManifest(R"({"protocol":1,"version":"0.33.0","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":1,"url":"https://evil.example/x.zip"})", m, err));
        CHECK(!ParseAppUpdateManifest(R"({"protocol":1,"version":"0.33.0","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bytes":1,"url":"https://github.com/other/repo/releases/download/v1/x.zip"})", m, err));
        CHECK(IsAllowedAppUpdateUrl("https://objects.githubusercontent.com/github-production-release-asset/1/2"));
        CHECK(IsAllowedAppUpdateUrl("https://release-assets.githubusercontent.com/github-production-release-asset/1/2"));
        CHECK(!IsAllowedAppUpdateUrl("https://githubusercontent.com/evil"));
        CHECK(CompareVersions("0.33.0", "0.32.9") == 1);
        CHECK(CompareVersions("0.32.9", "0.33.0") == -1);
        CHECK(CompareVersions("0.33.0", "0.33.0") == 0);
        CHECK(CompareVersions("0.33.0", "0.33.0.0") == 0);
        CHECK(CompareVersions("1.0.0", "1.0.0-beta") == -2);
        CHECK(CompareVersions("bad", "1.0.0") == -2);
    }
    // 原子的な app 入れ替え
    {
        fs::path base = tmp / "appswap"; fs::create_directories(base / "stage");
        FILE* f = fopen((base / "stage" / "GameKouryakuKun.exe").c_str(), "wb"); fwrite("new", 1, 3, f); fclose(f);
        FILE* f2 = fopen((base / "stage" / "a.dll").c_str(), "wb"); fwrite("d", 1, 1, f2); fclose(f2);
        fs::create_directories(base / "app");
        FILE* f3 = fopen((base / "app" / "GameKouryakuKun.exe").c_str(), "wb"); fwrite("old", 1, 3, f3); fclose(f3);
        fs::create_directories(base / "data"); FILE* f4 = fopen((base / "data" / "keep.txt").c_str(), "wb"); fwrite("k", 1, 1, f4); fclose(f4);
        auto sw = SwapAppDir(base / "stage", base / "app", base / "app.old");
        CHECK(sw.ok);
        CHECK(fs::exists(base / "app" / "GameKouryakuKun.exe"));
        CHECK(ReadAll(base / "app" / "GameKouryakuKun.exe") == "new");
        CHECK(fs::exists(base / "data" / "keep.txt") && ReadAll(base / "data" / "keep.txt") == "k");
        CHECK(!fs::exists(base / "stage"));
        AppUpdateManifest m; m.version = "0.33.0"; m.sha256 = std::string(64, 'a');
        CHECK(WriteInstalledApp(base / "app", m, {{"GameKouryakuKun.exe", 3, 0}, {"a.dll", 1, 0}}, "t"));
        std::string ver, sha;
        CHECK(ReadInstalledApp(base / "app", ver, sha) && ver == "0.33.0" && sha == m.sha256);
        // exe 欠けるステージは拒否
        fs::create_directories(base / "stage2");
        FILE* f5 = fopen((base / "stage2" / "only.dll").c_str(), "wb"); fwrite("x", 1, 1, f5); fclose(f5);
        auto sw2 = SwapAppDir(base / "stage2", base / "app", base / "app.old");
        CHECK(!sw2.ok);
    }

    fs::remove_all(tmp);
    printf("launcher core tests: %d passed, %d failed\n", passes, fails);
    return fails ? 1 : 0;
}
