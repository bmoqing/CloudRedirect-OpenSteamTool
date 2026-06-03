# CloudRedirect Custom Build Notes

This workspace contains a custom build based on upstream
`Selectively11/CloudRedirect` `v2.1.2`.

Custom changes:

- Adds Simplified Chinese localization and a Chinese language option.
- Adds a WebDAV cloud provider alongside Google Drive, OneDrive, folder, and local modes.
- Shows the deployed DLL source in Settings: official, custom WebDAV, hash mismatch, or missing.
- Disables the official GitHub DLL auto-update path for the custom WebDAV build.
- Adds a WebDAV "Test Connection" button that runs a real `PROPFIND`.
- Adds WebDAV Digest authentication fallback in addition to Basic Auth.
- Adds a manual WebDAV compatibility smoke test script.
- Publishes the WPF app as a single-file Windows executable with the custom native DLL embedded.

Upstream `v2.1.2` keeps Steam client version `1780352834` support and fixes Manifest Pinning for
that Steam build. The upstream `BuildDepotDependency` RVA is preserved at `0x4AC910`.

## Patch Files

Reusable patch:

- `patches/0001-add-zh-cn-localization-and-webdav.patch`

The patch was generated against upstream `v2.1.2` and should be checked with `git apply --check`
before applying to any future upstream release.

## Build Outputs

- Single-file EXE: `ui/bin/publish/CloudRedirect.exe`
- Native DLL: `build/Release/cloud_redirect.dll`
- Native CLI: `build/Release/cloud_redirect_cli.exe`

Current output hashes:

- `CloudRedirect.exe`: `B8E598A11A866C3F9FE89492B69C69DB985789C3DC0EA2C86E802F6D5108ED7A`
- `cloud_redirect.dll`: `352869A8509A022C9337D11FB5F701F59A90011C2D1E4D265C16885376B05C05`
- `cloud_redirect_cli.exe`: `C6D2D725E3745AC92229ED102A46B6B973BDC154FD0880545C92BB021330F463`

## Apply To A Fresh Upstream Release

From a fresh upstream source tree:

```powershell
git apply --check D:\Downloads\Compressed\CloudRedirect-2.1.2\patches\0001-add-zh-cn-localization-and-webdav.patch
git apply D:\Downloads\Compressed\CloudRedirect-2.1.2\patches\0001-add-zh-cn-localization-and-webdav.patch
```

If applying to a newer upstream version, run `git apply --check` first. If conflicts occur, likely
areas are:

- `CMakeLists.txt`
- `src/common/cli.cpp`
- `src/common/cloud_storage.cpp`
- `src/platform/win/cloud_intercept.cpp`
- `ui/Pages/CloudProviderPage.xaml`
- `ui/Pages/CloudProviderPage.xaml.cs`
- `ui/Pages/SettingsPage.xaml`
- `ui/Pages/SettingsPage.xaml.cs`
- `ui/Resources/Strings*.resx`

When upstream changes Steam hook support again, keep the upstream core hook/RVA/signature updates
and only reapply the WebDAV/localization layer.

## WebDAV Configuration

The WebDAV provider reads these fields from `%AppData%/CloudRedirect/config.json`:

```json
{
  "provider": "webdav",
  "webdav_url": "https://example.com/remote.php/dav/files/user/CloudRedirect",
  "webdav_username": "user",
  "webdav_password": "app-password-or-password",
  "webdav_auth_mode": "auto"
}
```

Notes:

- `webdav_url` must be HTTPS.
- Query strings and fragments are rejected.
- The current implementation supports Basic Auth and Digest Auth fallback.
- The current implementation stores the WebDAV password in plain text in `config.json`.

## Build Native DLL And CLI

```powershell
cmd /c 'call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" && "D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\cmake-3.31.8-windows-x86_64\bin\cmake.exe" -S . -B build-nmake -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release'
cmd /c 'call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" && "D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\cmake-3.31.8-windows-x86_64\bin\cmake.exe" --build build-nmake --config Release --target cloud_redirect cloud_redirect_cli'
```

Copy native outputs to the path expected by `ui/CloudRedirect.csproj`:

```powershell
New-Item -ItemType Directory -Force build\Release | Out-Null
Copy-Item build-nmake\cloud_redirect.dll build\Release\cloud_redirect.dll -Force
Copy-Item build-nmake\cloud_redirect_cli.exe build\Release\cloud_redirect_cli.exe -Force
```

After rebuilding the DLL, update `ui/Services/DllIdentity.cs` with the new custom DLL SHA256.

## Publish Single-File EXE

```powershell
$env:DOTNET_CLI_HOME='D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES='D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\nuget-packages'
$env:APPDATA='D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\AppData\Roaming'
$env:LOCALAPPDATA='D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\AppData\Local'
$env:USERPROFILE='D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\UserProfile'
D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\dotnet\dotnet.exe restore ui\CloudRedirect.csproj --configfile D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\NuGet.Config
D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\.tools\dotnet\dotnet.exe publish ui\CloudRedirect.csproj -c Release -r win-x64 --self-contained false -o ui\bin\publish --configfile D:\Downloads\Compressed\CloudRedirect-2.1.0-preview\NuGet.Config --no-restore
```

This framework-dependent single-file build requires the `.NET 8 Windows Desktop Runtime` on the
target machine.

## Validation Checklist

```powershell
git apply --check D:\Downloads\Compressed\CloudRedirect-2.1.2\patches\0001-add-zh-cn-localization-and-webdav.patch
Get-FileHash ui\bin\publish\CloudRedirect.exe -Algorithm SHA256
Get-FileHash build\Release\cloud_redirect.dll -Algorithm SHA256
Get-FileHash build\Release\cloud_redirect_cli.exe -Algorithm SHA256
```

Manual WebDAV compatibility smoke test:

```powershell
.\tests\webdav_compat_smoke.ps1 `
  -BaseUrl "https://example.com/remote.php/dav/files/user/CloudRedirect" `
  -Username "user" `
  -Password "app-password-or-password"
```

After deploying the custom DLL and starting Steam, inspect `cloud_redirect.log` and confirm there
are no `Version Mismatch`, `Prologue mismatch`, `RVA mismatch`, or `vtable` mismatch messages.

## Runtime Notes

To keep WebDAV support, the UI disables the official DLL auto-update toggle when it detects this
custom WebDAV build. The DLL itself also skips the official GitHub DLL updater when compiled with
`CR_CUSTOM_WEB_DAV_BUILD`.

If Steam still has the upstream official DLL or an older custom DLL, the UI may show a DLL source
mismatch. Deploy or update the DLL from the app so Steam loads this custom WebDAV-enabled DLL.
