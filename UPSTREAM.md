# Upstream Attribution

This repository is a custom build of
[Selectively11/CloudRedirect](https://github.com/Selectively11/CloudRedirect).

The upstream project provides the original CloudRedirect implementation, including the
Steam integration, native DLL hooks, WPF companion app, and core cloud redirection logic.

This custom build adds:

- Simplified Chinese localization and a Chinese language option.
- WebDAV provider support.
- WebDAV connection testing.
- Custom DLL source identification in Settings.
- Protection against replacing the custom WebDAV DLL with the official upstream DLL updater.
- Reusable patch files and custom build notes for future upstream updates.

When publishing releases from this repository, include the upstream base version in the
release notes, for example:

```text
Based on Selectively11/CloudRedirect v2.1.2.
Custom changes: Simplified Chinese localization, WebDAV support, custom DLL source labeling.
```

