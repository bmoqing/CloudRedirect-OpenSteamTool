# CloudRedirect OpenSteamTool 自定义版

这是基于 [Selectively11/CloudRedirect](https://github.com/Selectively11/CloudRedirect) v2.1.5 制作的自定义构建。上游项目提供核心 Steam Cloud 重定向、DLL hook、第三方接入 API 和 WPF 管理程序；本仓库在此基础上加入 OpenSteamTool 加载方式、简体中文界面、WebDAV 云服务、诊断页和维护工具。

使用前请先备份重要存档。CloudRedirect 会拦截 Steam Cloud 相关调用，并把 Lua/假入库应用的云存档重定向到你配置的云服务或本地目录。它适合你确实需要这些应用进行云同步的场景；如果只是不想看到 Steam Cloud 报错，请优先考虑关闭该游戏属性里的 Steam Cloud。

## 基于上游 v2.1.5

上游 v2.1.5 的主要变化：

- 修复中文、日文等非 ASCII 存档路径可能导致崩溃的问题。
- 修复 Unicode 游戏安装目录下 AutoCloud 扫描失败的问题。
- 移除剩余可用原生行为替代的自定义 AutoCloud 逻辑。
- 新增第三方非 SteamTools 解锁方案的 CloudRedirect API 起步支持。
- 首次设置后新增自动更新提示。
- 已通过 Google Drive 或 OneDrive 认证时跳过云服务配置提示。

本自定义版保留并适配：

- OpenSteamTool 加载方式：通过 `Steam\config\lua\cloud_redirect_loader.lua` 加载 `cloud_redirect.dll`，不再依赖 `config\stplug-in`。
- 简体中文界面，并在设置中提供中文选项。
- WebDAV 云服务提供商，适合 Nextcloud、AList、坚果云等支持 WebDAV 的服务。
- WebDAV 测试连接：执行 PROPFIND、MKCOL、PUT、GET、覆盖、列目录和删除验证。
- 设置页显示当前 DLL 来源：官方版、自定义 WebDAV 版、哈希不匹配或未安装。
- 自定义 DLL 构建禁用官方 DLL 自动更新，避免 WebDAV 版被上游官方 DLL 覆盖。
- 诊断页：检查 DLL 加载、OpenSteamTool 安装、Lua 目录、当前 Steam accountId、WebDAV 初始化和最近 hook 日志。
- 数据恢复页：一键恢复游玩时间、成就/统计缓存和 OpenSteamTool Lua 文件。
- 本地 WebDAV 兼容性测试脚本。
- `patches/` 和 `CUSTOM_BUILD_NOTES.md`，用于后续上游升级时复用改动。

## 安装方式

1. 安装 OpenSteamTool，把 `dwmapi.dll`、`xinput1_4.dll`、`OpenSteamTool.dll` 放到 Steam 根目录，例如 `C:\Program Files (x86)\Steam`。
2. 运行本项目 Release 里的 `CloudRedirect.exe`。
3. 在“安装设置”里点击“安装 OpenSteamTool 集成”。
4. 在“云服务提供商”里选择 Google Drive、OneDrive、WebDAV、文件夹/网络驱动器或仅本地。
5. 如果选择 WebDAV，请填写 HTTPS WebDAV URL、用户名和密码/应用密码，然后点击“测试连接”。
6. 重启 Steam 后，在“诊断”页查看 DLL 是否已通过 OpenSteamTool Lua 加载、vtable hook 是否激活、accountId 是否正确。

OpenSteamTool 默认读取：

```text
C:\Program Files (x86)\Steam\config\lua
```

不要把 Lua 文件放到旧 SteamTools 目录：

```text
C:\Program Files (x86)\Steam\config\stplug-in
```

## 多账号提醒

CloudRedirect 的本地缓存和云端路径会按 Steam `accountId` 分目录。切换 Steam 账号前，请在“设置”或“诊断”页确认当前显示的 accountId 是目标账号，避免把云存档写到另一个账号目录。

## WebDAV 配置

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

核心实现来自：

[Selectively11/CloudRedirect](https://github.com/Selectively11/CloudRedirect)

如果上游发布新版本，请先保留上游 hook/RVA/signature/API 更新，再按 `CUSTOM_BUILD_NOTES.md` 和 `patches/` 重新套用本项目的汉化、OpenSteamTool 和 WebDAV 改动。
