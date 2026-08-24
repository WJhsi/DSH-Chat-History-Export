# DSH Chat-History Manage

Export and manage chat sessions saved by DeepSeek Harness (DSH) on local disk as readable Markdown transcripts.
A single-file Win32 desktop app — no runtime installation needed (Windows 10/11 ship with .NET Framework built in).

## Features

- **Session list**: automatically finds the DSH sessions folder (remembered choice → `$DSH_HOME` → `~/.dsh/sessions`), or lets you pick it manually when not found. Shows each session's **topic**, ID and time; blank sessions (no topic, no chat content) are hidden and reported.
- **Preview**: formatted transcript — per-turn model names, inline **bold**, emoji, collapsible tool calls, with a loading progress bar.
- **Export**: writes `<session-ID>-transcript.md` to a folder of your choice (remembered).
- **Menu bar**: File / Edit / Language / Help. The UI language follows the system by default, with 60+ languages available; also includes an About dialog and repository/website links.
- Supports both zstd-compressed and plain JSONL session files, via the embedded zstd library — single-file distribution, no external DLLs.

## Quick Start

Double-click `dist\dsh-chat-history-manage.exe`, select a session, then **Export & Save** (or double-click a list item to export directly).

## Command-Line Self-Test

```
dsh-chat-history-manage.exe --selftest <session-file> <output.md>
```

Returns 0 on success and writes the transcript; returns 1 on failure with the error in `<output.md>.err.txt`.

## Rebuilding

Double-click `build.cmd` to recompile `src\dsh-chat-history-manage-gui.cs` into `dist\dsh-chat-history-manage.exe` (the exact `csc.exe` command is in the script).

## Notes

- Runtime files (config, topic cache) are written to a `json` subfolder next to the exe; they are machine-local and not distributed with the program.
- Session files use DSH's private format (zstd-compressed JSONL event stream). This tool only **reads** them and never modifies any session file.
- On a crash, error details are written to `%TEMP%\dsh-chat-history-manage-crash.log`.
