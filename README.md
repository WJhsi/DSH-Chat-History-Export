# DSH Chat-History Export — DSH 聊天记录导出工具

把 DeepSeek Harness（DSH）保存在本地磁盘的会话文件导出成可读的 Markdown 聊天记录。
原生 **Win32 窗口程序**，单文件 exe，无需安装任何运行时（Win10/11 自带 .NET Framework）。

## 快速开始

双击 `dist\dsh-chat-history-export.exe` 即可使用，界面说明：

- **左侧会话列表**：自动扫描 `C:\Users\<你>\.dsh\sessions` 下的全部会话（最新在前），点选即可在右侧实时预览转录内容
- **导出目录**：默认是 exe 所在目录；点「浏览…」用系统文件夹选择器挑选，也可手动输入；选择会被记住（写进 exe 旁边的 `dsh-chat-history-export.config.json`，删除该文件即恢复默认）
- **导出并保存**：生成 `<会话ID>-transcript.md`，完成后可一键打开所在文件夹
- **选择会话文件…**：当会话不在默认位置时，手动挑选 `session.jsonl` / `session.jsonl.zstd` 文件
- 双击列表项 = 直接导出

支持压缩（zstd）与未压缩两种会话文件；zstd 解压使用内嵌的官方 libzstd 库，单文件分发、无需外置 dll。

## 命令行自检模式

GUI 程序同时保留了一个无窗口自检入口，走与界面完全相同的逻辑：

```
dsh-chat-history-export.exe --selftest <会话文件路径> <输出.md>
```

成功返回 0 并写出转录文件；失败返回 1 并把错误写入 `<输出.md>.err.txt`。

## 重新构建

双击 `build.cmd`，或手动执行：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /unsafe /codepage:65001 ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /resource:native\libzstd.dll,libzstd.dll ^
  /out:dist\dsh-chat-history-export.exe src\dsh-chat-history-export-gui.cs
```

改完界面/逻辑后重新编译即可，产物在 `dist\`。

## 目录结构

```
recover\
├── build.cmd                           一键构建脚本
├── sea-config.json                     命令行版（node SEA）打包配置，备用
├── src\
│   ├── dsh-chat-history-export-gui.cs  WinForms GUI 源码（主程序）
│   └── dsh-chat-history-export.cjs     命令行版源码（node，备用）
├── native\
│   └── libzstd.dll                     zstd 解压库（编译时内嵌进 exe）
└── dist\
    └── dsh-chat-history-export.exe     构建产物（即用即走）
```

## 注意事项

- `dsh-chat-history-export.config.json` 是运行时生成的本机配置（导出目录），不会随程序分发
- 会话文件是 DSH 的私有格式（zstd 压缩的 JSONL 事件流），本工具只做**读取**，不会修改任何会话文件
- 崩溃时会在 `%TEMP%\dsh-chat-history-export-crash.log` 留下错误信息，便于排查
