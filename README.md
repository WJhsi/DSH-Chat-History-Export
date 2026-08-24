# DSH Chat-History Export

Export chat sessions saved by DeepSeek Harness (DSH) on local disk into readable Markdown transcripts.
A native **Win32 desktop app** delivered as a single-file exe — no runtime installation needed (Windows 10/11 ship with .NET Framework built in).

## Quick Start

Double-click `dist\dsh-chat-history-export.exe` to launch:

- **Session list (left pane)**: automatically scans every session under `C:\Users\<you>\.dsh\sessions` (newest first). Each row shows the session's **topic**, ID and last-updated time; the topic matches what the DSH sidebar shows (the latest `session/title` event) and fills in progressively in the background. Click a session to preview its transcript on the right in real time.
- **Export directory**: defaults to the exe's own folder. Pick a folder via the system folder picker ("Browse…") or type a path manually. Your choice is remembered in `dsh-chat-history-export.config.json` next to the exe — delete that file to restore the default.
- **Export & Save**: writes `<session-ID>-transcript.md`, then offers to open the containing folder.
- **Choose session file…**: manually pick a `session.jsonl` / `session.jsonl.zstd` file when the session is not in the default location.
- Double-clicking a list item exports it directly.

Both compressed (zstd) and uncompressed session files are supported; zstd decompression uses the embedded official libzstd library — single-file distribution, no external DLL needed.

## Command-Line Self-Test

The GUI program also keeps a windowless self-test entry point that runs the exact same logic as the UI:

```
dsh-chat-history-export.exe --selftest <session-file> <output.md>
```

Returns 0 on success and writes the transcript; returns 1 on failure and writes the error to `<output.md>.err.txt`.

## Rebuilding

Double-click `build.cmd`, or run manually:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /unsafe /codepage:65001 ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /resource:native\libzstd.dll,libzstd.dll ^
  /out:dist\dsh-chat-history-export.exe src\dsh-chat-history-export-gui.cs
```

After changing the UI/logic, recompile — the artifact lands in `dist\`.

## Directory Layout

```
recover\
├── build.cmd                           one-click build script
├── sea-config.json                     node SEA packaging config for the CLI variant (standby)
├── src\
│   ├── dsh-chat-history-export-gui.cs  WinForms GUI source (main program)
│   └── dsh-chat-history-export.cjs     CLI variant source (node, standby)
├── native\
│   └── libzstd.dll                     zstd library (embedded into the exe at build time)
└── dist\
    └── dsh-chat-history-export.exe     build artifact (ready to run)
```

## Notes

- `dsh-chat-history-export.config.json` is a machine-local runtime config (export directory); it is not distributed with the program.
- Session files use DSH's private format (zstd-compressed JSONL event stream). This tool only **reads** them and never modifies any session file.
- On a crash, error details are written to `%TEMP%\dsh-chat-history-export-crash.log` for troubleshooting.
