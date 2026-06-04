# CloudRedirect OpenSteamTool 自定义版

这是基于 [Selectively11/CloudRedirect](https://github.com/Selectively11/CloudRedirect) v2.1.2 制作的自定义版本。上游项目提供核心 Steam Cloud 重定向、DLL hook 和 WPF 管理程序；这个仓库在此基础上加入 OpenSteamTool 加载方式、简体中文界面、WebDAV 云服务和诊断/维护工具。

请先阅读风险提示：CloudRedirect 会拦截 Steam Cloud 相关调用并把 lua 游戏的云存档重定向到你配置的云服务或本地目录。使用前请备份重要存档，尤其是已经被 SteamTools 污染过的 userdata 目录。

## 主要改动

- 适配 OpenSteamTool：通过 `Steam\config\lua\cloud_redirect_loader.lua` 加载 `cloud_redirect.dll`，不再依赖 `config\stplug-in`。
- 增加 WebDAV 云服务提供商，适合 Nextcloud、Alist、坚果云等支持 WebDAV 的服务。
- 增加 WebDAV 测试连接：会实际执行 PROPFIND、MKCOL、PUT、GET、覆盖、列目录和删除。
- 增加简体中文界面，并在设置里提供中文选项。
- 设置页显示当前 DLL 来源：官方版、自定义 WebDAV 版、哈希不匹配或未安装。
- 自定义 DLL 构建会禁用官方 DLL 自动更新，避免 WebDAV 版被上游官方 DLL 覆盖。
- 增加诊断页：检查 DLL 加载、OpenSteamTool 安装、Lua 目录、当前 Steam accountId、WebDAV 初始化和最近 hook 日志。
- 增加本地 WebDAV 兼容性测试脚本，方便开发时验证中文文件名、空目录、多级目录和冲突覆盖。
- 增加 `patches/` 和 `CUSTOM_BUILD_NOTES.md`，后续上游更新时可以按步骤复用改动。

## 安装方式

1. 安装 OpenSteamTool，把 `dwmapi.dll`、`xinput1_4.dll`、`OpenSteamTool.dll` 放到 Steam 根目录，例如 `C:\Program Files (x86)\Steam`。
2. 运行本项目 Release 里的 `CloudRedirect.exe`。
3. 在“安装设置”里点击“安装 OpenSteamTool 集成”。
4. 在“云服务提供商”里选择 Google Drive、OneDrive、WebDAV、文件夹/网络驱动器或仅本地。
5. 如果选择 WebDAV，请填写 HTTPS WebDAV URL、用户名和密码/应用密码，然后点击“测试连接”。
6. 重启 Steam 后，在“诊断”页查看 DLL 是否已通过 OpenSteamTool Lua 加载、vtable hook 是否激活、accountId 是否正确。

OpenSteamTool 读取的 Lua 目录默认是：

```text
C:\Program Files (x86)\Steam\config\lua
```

不要把 lua 放到旧的 SteamTools 目录：

```text
C:\Program Files (x86)\Steam\config\stplug-in
```

## 多账号提醒

CloudRedirect 的本地缓存和云端路径会按 Steam `accountId` 分目录。切换 Steam 账号前，请在“设置”或“诊断”页确认当前显示的 accountId 是目标账号，避免把云存档写到另一个账号目录。

## WebDAV 配置说明

WebDAV URL 必须是 HTTPS，不能包含 query string 或 fragment。常见示例：

```text
https://example.com/remote.php/dav/files/user/CloudRedirect
```

当前实现支持 Basic Auth，并带有 Digest Auth 兼容逻辑。很多服务需要使用“应用密码”，不要直接使用网页登录密码。

## 构建

需要：

- Visual Studio 2022 或 Build Tools，包含 C++ 工具链
- .NET 8 Windows Desktop Runtime/SDK
- CMake 3.20+

构建 native DLL 和 CLI：

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release --target cloud_redirect cloud_redirect_cli
```

发布单文件 EXE：

```powershell
dotnet publish ui/CloudRedirect.csproj -c Release -r win-x64 --self-contained false -o ui/bin/publish
```

输出：

```text
ui/bin/publish/CloudRedirect.exe
build/Release/cloud_redirect.dll
build/Release/cloud_redirect_cli.exe
```

## 本地 WebDAV 兼容性测试

```powershell
powershell -ExecutionPolicy Bypass -File tests\run_local_webdav_compat.ps1
```

这个脚本会启动一个本地 WebDAV 测试服务，验证创建目录、上传、下载、中文文件名、空目录、多级目录、覆盖冲突和删除。测试结束后会打印临时目录路径，按项目规则不会自动批量删除目录。

## 上游来源

本项目是自定义构建，核心实现来自：

[Selectively11/CloudRedirect](https://github.com/Selectively11/CloudRedirect)

如果上游发布新版本，请先保留上游 hook/RVA/signature 更新，再按 `CUSTOM_BUILD_NOTES.md` 和 `patches/` 重新套用本项目的汉化、OpenSteamTool 和 WebDAV 改动。
