# DSH Chat-History Manage v1.0.0

## [新增]
- 会话列表：自动查找 DSH 会话目录（记忆目录 → `$DSH_HOME` → `~/.dsh/sessions`），找不到时支持手动选择；显示主题 / 会话 ID / 时间，自动剔除空白会话
- 预览：对话转录实时预览，显示模型版本、行内粗体、emoji、可折叠工具调用、加载进度
- 导出：一键导出 Markdown 转录（支持 zstd 压缩与明文 JSONL），导出目录可自定义并记忆
- 菜单栏：文件 / 编辑 / 语言 / 帮助；界面语言支持 60+ 种并默认跟随系统；关于对话框与项目、网站链接
- 单文件分发：运行所需 zstd 库已内嵌，无需安装运行时

---

# DSH Chat-History Manage v1.0.0

## [Added]
- Session list: auto-detects the DSH sessions folder (remembered path → `$DSH_HOME` → `~/.dsh/sessions`), with manual selection when not found; shows topic / session ID / time and hides blank sessions
- Preview: formatted transcript with model names, inline bold, emoji, collapsible tool calls, and loading progress
- Export: one-click Markdown transcript export (zstd-compressed and plain JSONL supported); export folder is remembered
- Menu bar: File / Edit / Language / Help; 60+ UI languages with system-follow by default; About dialog and project/website links
- Single-file distribution: embedded zstd, no runtime installation needed
