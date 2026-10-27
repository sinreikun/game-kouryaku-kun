#!/usr/bin/env python3
"""runtime-manifest.json から manifest_gen.h（ランチャー埋め込み定数）を作る。"""
import json, sys, pathlib
here = pathlib.Path(__file__).parent
m = json.loads((here / "runtime-manifest.json").read_text(encoding="utf-8"))
def q(s): return json.dumps(s, ensure_ascii=False)
out = ["// 自動生成: gen_manifest.py（runtime-manifest.json から）。手で編集しない", "#pragma once", '#include "core.h"', "namespace gkk {",
       f"inline const char* kRuntimeDir = {q(m['runtime_dir'])};", "inline std::vector<Component> EmbeddedComponents() {", "    std::vector<Component> v;"]
for c in m["components"]:
    for k in ("sha512", "sha256"):
        assert len(c[k]) == (128 if k == "sha512" else 64) and all(ch in "0123456789abcdef" for ch in c[k]), k
    out.append("    { Component c;")
    for k in ("id", "display_name", "version", "homepage", "file_name", "sha512", "sha256"):
        out.append(f"      c.{k} = {q(c[k])};")
    out.append(f"      c.license_spdx = {q(c['license']['spdx'])}; c.license_url = {q(c['license']['url'])};")
    out.append(f"      c.size_bytes = {int(c['size_bytes'])}ULL;")
    out.append("      c.urls = {" + ", ".join(q(u) for u in c["urls"]) + "};")
    out.append("      c.required_files = {" + ", ".join(q(u) for u in c["required_files"]) + "};")
    out.append("      v.push_back(c); }")
out += ["    return v;", "}", "}  // namespace gkk", ""]
(here / "manifest_gen.h").write_text("\n".join(out), encoding="utf-8")
print("manifest_gen.h:", len(m["components"]), "components")
