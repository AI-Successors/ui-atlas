# Third-party notices

This inventory is derived from the committed package lock files. Runtime redistribution includes the following components:

| Component | Version | Author/vendor | License | Use |
| --- | --- | --- | --- | --- |
| FirebirdSql.Data.FirebirdClient | 10.3.4 | FirebirdSQL | Initial Developer's Public License 1.0 | isolated legacy Firebird migration source adapter |
| Microsoft.Data.Sqlite | 10.0.10 | Microsoft | MIT | managed SQLite API |
| Microsoft.Data.Sqlite.Core | 10.0.10 | Microsoft | MIT | managed SQLite implementation |
| Microsoft.Windows.SDK.NET | 10.0.19041.57 | Microsoft | Microsoft Windows SDK license | Windows Runtime projection used only by the optional Windows OCR enrichment path |
| WinRT.Runtime | 2.2.0 | Microsoft | MIT | C#/WinRT runtime support required by the Windows OCR projection |
| Interop.UIAutomationClient | 10.19041.0 | Roman | MIT | native UI Automation 3 COM interop |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Eric Sink and contributors | Apache-2.0 | native-provider bundle |
| SQLitePCLRaw.config.e_sqlite3 | 3.0.5 | Eric Sink and contributors | Apache-2.0 | provider configuration |
| SQLitePCLRaw.core | 3.0.5 | Eric Sink and contributors | Apache-2.0 | low-level managed API |
| SQLitePCLRaw.provider.e_sqlite3 | 3.0.5 | Eric Sink and contributors | Apache-2.0 | native-provider binding |
| SQLite | 3.53.4 | SQLite authors | Public domain | native database library |

Test-only dependencies are not shipped in the runtime archive: Microsoft.NET.Test.Sdk 17.14.1 and its Microsoft test-platform/code-coverage dependencies (MIT); xunit 2.9.3, xunit.runner.visualstudio 3.1.4, xunit.assert 2.9.3, xunit.core 2.9.3, xunit.extensibility.core 2.9.3, xunit.extensibility.execution 2.9.3, and xunit.analyzers 1.18.0 (Apache-2.0); xunit.abstractions 2.0.3 (Apache-2.0); and Newtonsoft.Json 13.0.3 (MIT).

Package metadata, lock files, and the SDK-generated runtime manifest are the version authority. Redistribution texts and notices are committed under `licenses/` and included in release archives: `Apache-2.0.txt`, `CsWinRT-MIT.txt`, `FirebirdSql.Data.FirebirdClient-IDPL-1.0.txt`, `Microsoft-Windows-SDK-NET-NOTICE.txt`, `Microsoft.Data.Sqlite-MIT.txt`, `Interop.UIAutomationClient-MIT.txt`, and `SQLite-public-domain.txt`. `Microsoft.Windows.SDK.NET.dll` is governed by Microsoft's Windows SDK license linked in its notice; it is not relicensed as MIT. The native Firebird engine is not bundled or downloaded by UiAtlas. No third-party application source code or graphical asset is copied into this repository.

The repository `.gitignore` is adapted from the CC0-1.0 GitHub Visual Studio template identified in `provenance/files.csv`. It is repository configuration and is not included in the runtime archive.
