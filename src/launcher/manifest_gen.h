// 自動生成: gen_manifest.py（runtime-manifest.json から）。手で編集しない
#pragma once
#include "core.h"
namespace gkk {
inline const char* kRuntimeDir = "runtime";
inline std::vector<Component> EmbeddedComponents() {
    std::vector<Component> v;
    { Component c;
      c.id = "dotnet-runtime";
      c.display_name = ".NET Runtime 10.0.13 (win-x64)";
      c.version = "10.0.13";
      c.homepage = "https://dotnet.microsoft.com/download/dotnet/10.0";
      c.file_name = "dotnet-runtime-10.0.13-win-x64.zip";
      c.sha512 = "2b98792ce86adc50899764ad2e4d21bd25cfda5432eeb0b4dfe7944f3ea049f9e0570a8f4aee92314d97cdb5f8b7be1363d501fc5012e680371e607af648ef40";
      c.sha256 = "8db74a746baa450925d01325a2108ccf6ac39af4018047475292ae155c013d89";
      c.license_spdx = "MIT"; c.license_url = "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT";
      c.size_bytes = 36970749ULL;
      c.urls = {"https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.zip"};
      c.required_files = {"dotnet.exe", "host/fxr/10.0.13/hostfxr.dll", "shared/Microsoft.NETCore.App/10.0.13/System.Private.CoreLib.dll", "shared/Microsoft.NETCore.App/10.0.13/coreclr.dll"};
      v.push_back(c); }
    { Component c;
      c.id = "windowsdesktop-runtime";
      c.display_name = ".NET Desktop Runtime 10.0.13 (win-x64, WPF)";
      c.version = "10.0.13";
      c.homepage = "https://dotnet.microsoft.com/download/dotnet/10.0";
      c.file_name = "windowsdesktop-runtime-10.0.13-win-x64.zip";
      c.sha512 = "7cd78051b34f12239852e3685de57630aaeca0fd36e6ef3b41df3f256cf45a820377f316696c5e11316f0bb6e4e84e07fa99367e71472623b33c5d07405c4568";
      c.sha256 = "5b68b6739ff71ac862c2e90a50851483da041941ee76cf71b5dbb5d081c15870";
      c.license_spdx = "MIT"; c.license_url = "https://github.com/dotnet/windowsdesktop/blob/main/LICENSE.TXT";
      c.size_bytes = 39169839ULL;
      c.urls = {"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.zip"};
      c.required_files = {"shared/Microsoft.WindowsDesktop.App/10.0.13/PresentationFramework.dll", "shared/Microsoft.WindowsDesktop.App/10.0.13/wpfgfx_cor3.dll"};
      v.push_back(c); }
    return v;
}
}  // namespace gkk
