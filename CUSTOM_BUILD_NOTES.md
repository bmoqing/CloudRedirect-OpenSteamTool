# Custom Build Notes

This repository is a custom build based on
[Selectively11/CloudRedirect](https://github.com/Selectively11/CloudRedirect) `v2.1.2`.

Target repository:

- `bmoqing/CloudRedirect-OpenSteamTool`

## Custom Scope

- Simplified Chinese localization and a Chinese language option.
- OpenSteamTool loading path:
  - exports `CloudRedirectLua` from `cloud_redirect.dll`
  - installs `Steam\config\lua\cloud_redirect_loader.lua`
  - reads OpenSteamTool Lua paths from `opensteamtool.toml`
  - does not depend on `Steam\config\stplug-in`
- Direct OpenSteamTool hook initialization:
  - installs manifest pinning hook
  - installs Cloud RPC vtable hook without relying on SteamTools `CCMInterface`
  - bootstraps SteamID64/accountId from `config\loginusers.vdf` when packet capture has not happened yet
- WebDAV provider support:
  - HTTPS URL validation
  - Basic Auth and Digest Auth fallback
  - path-prefix and URL encoding support
  - UI test connection that exercises PROPFIND, MKCOL, PUT, GET, overwrite, list, and DELETE
- Runtime diagnostics page:
  - Steam path/version
  - DLL source
  - OpenSteamTool files and loader state
  - Lua directories and namespace lua count
  - current accountId
  - recent `cloud_redirect.log` hook state
- Data restore page:
  - restores playtime into `userdata\<accountId>\config\localconfig.vdf`
  - restores achievement/stat cache into `appcache\stats`
  - restores OpenSteamTool Lua files into `config\lua`
  - recognizes both local `cloud_redirect\storage` layout and exported `0\blobs` sync-layout backups
- Custom DLL source labeling:
  - official DLL
  - custom WebDAV DLL
  - hash mismatch
  - not installed
- Official DLL auto-update protection:
  - settings toggle is disabled for the custom DLL
  - native DLL compiled with `CR_CUSTOM_WEB_DAV_BUILD=1` skips official GitHub DLL updates
- Local WebDAV compatibility tests:
  - `tests/webdav_compat_smoke.ps1`
  - `tests/local_webdav_server.py`
  - `tests/run_local_webdav_compat.ps1`
- App update source is switched to `bmoqing/CloudRedirect-OpenSteamTool`.
- GitHub Actions release workflow builds and uploads:
  - `CloudRedirect.exe`
  - `cloud_redirect.dll`
  - `cloud_redirect_cli.exe`
  - SHA256 files

## Patch Files

Reusable patch:

- `patches/0001-opensteamtool-zh-cn-webdav-diagnostics.patch`

Check before applying:

```powershell
git apply --check patches\0001-opensteamtool-zh-cn-webdav-diagnostics.patch
```

Apply:

```powershell
git apply patches\0001-opensteamtool-zh-cn-webdav-diagnostics.patch
```

For a newer upstream release, first check out the upstream tag, then run `git apply --check`.
Conflicts are most likely in:

- `CMakeLists.txt`
- `src/platform/win/cloud_intercept.cpp`
- `src/platform/win/dllmain.cpp`
- `src/providers/webdav_provider.*`
- `ui/Pages/SetupPage.xaml.cs`
- `ui/Pages/CloudProviderPage.xaml*`
- `ui/Pages/SettingsPage.xaml*`
- `ui/Resources/Strings*.resx`
- `ui/Services/AppUpdater.cs`

When upstream changes Steam hook support, keep the upstream RVA/signature/vtable fixes first,
then reapply the localization, WebDAV, OpenSteamTool loader, diagnostics, and GitHub packaging layer.

## Build

Native DLL and CLI:

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release --target cloud_redirect cloud_redirect_cli
```

Single-file EXE:

```powershell
dotnet publish ui\CloudRedirect.csproj -c Release -r win-x64 --self-contained false -o ui\bin\publish
```

If using the bundled local tools in this workspace:

```powershell
$cmake = Join-Path (Get-Location) '.tools\cmake-3.31.8-windows-x86_64\bin\cmake.exe'
$dotnetRoot = Join-Path (Get-Location) '.tools\dotnet'
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
cmd /d /s /c "set DOTNET_ROOT=$dotnetRoot&& set PATH=$dotnetRoot;%PATH%&& call `"$vcvars`" >nul && `"$cmake`" --build build-nmake --config Release"
```

If `ui\bin\publish\CloudRedirect.exe` is locked by a running app instance, publish to a different
directory for verification:

```powershell
dotnet publish ui\CloudRedirect.csproj -c Release -r win-x64 --self-contained false -o ui\bin\publish-final
```

## Hash Maintenance

After rebuilding `cloud_redirect.dll`, update the custom DLL SHA256 in:

```text
ui/Services/DllIdentity.cs
```

Then rebuild the UI so the embedded DLL and displayed source identity match.

Verify outputs:

```powershell
Get-FileHash ui\bin\publish\CloudRedirect.exe -Algorithm SHA256
Get-FileHash build-nmake\cloud_redirect.dll -Algorithm SHA256
Get-FileHash build-nmake\cloud_redirect_cli.exe -Algorithm SHA256
```

Current verified output hashes from this workspace:

```text
CloudRedirect.exe:      A7AAE615105019F6AE902A24BB0A39AEA756E3273B5CA56463A35AB8D0A314F0
cloud_redirect.dll:     EDC170BAEA0D549700230F8845D334C949F05B4764828D4C3B10F919D3B5A378
cloud_redirect_cli.exe: 88BDF46E90B6742FB34C22FFB16A8D9DD4EF697C4897DF515D895FF7590173F1
```

Verify exports:

```powershell
dumpbin /exports ui\Resources\cloud_redirect.dll | findstr /C:CloudRedirectLua /C:CloudOnSendPkt
```

## Runtime Validation

After deploying the DLL and restarting Steam, inspect `cloud_redirect.log`.

Expected markers:

```text
CloudRedirect loaded via OpenSteamTool Lua
[Build] Custom OpenSteamTool/WebDAV build active; official DLL auto-update is disabled
[Account] Captured SteamID64=... via loginusers.vdf (accountId=...)
[OpenSteamTool] Installing direct Cloud RPC vtable hook...
[VtHook] All hooks ACTIVE -- Cloud RPCs...
[OpenSteamTool] Direct hook install finished: ... vtable=1
```

Bad markers to investigate:

```text
Version Mismatch
Prologue mismatch
RVA mismatch
vtable mismatch
no Steam account ID
```

## WebDAV Test

External WebDAV endpoint:

```powershell
powershell -ExecutionPolicy Bypass -File tests\webdav_compat_smoke.ps1 `
  -BaseUrl "https://example.com/remote.php/dav/files/user/CloudRedirect" `
  -Username "user" `
  -Password "app-password"
```

Local loopback test:

```powershell
powershell -ExecutionPolicy Bypass -File tests\run_local_webdav_compat.ps1
```

The local test intentionally leaves its temporary WebDAV root printed in the terminal for manual
cleanup instead of bulk deleting a directory.

## GitHub Release

The workflow in `.github/workflows/release.yml` creates a release when a tag like `v2.1.2-ost.1`
is pushed.

Manual release trigger is also available from the GitHub Actions page.

Recommended release note wording:

```text
Based on Selectively11/CloudRedirect v2.1.2.
Custom build: OpenSteamTool integration, Simplified Chinese localization, WebDAV provider,
diagnostics page, DLL source labeling, and compatibility tests.
```
