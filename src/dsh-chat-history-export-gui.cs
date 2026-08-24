// dsh-chat-history-export-gui.cs — DSH Chat-History Export：导出 DSH 聊天记录（Win32 GUI 程序，单文件）
//
// 编译（Windows 自带 .NET Framework 4.x，无需安装运行时）:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /unsafe /codepage:65001
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll
//     /resource:libzstd.dll,libzstd.dll /out:dsh-chat-history-export.exe dsh-chat-history-export-gui.cs
//
// 功能：
//   1. 左侧会话列表（自动扫描 ~/.dsh/sessions），右侧实时预览转录内容
//   2. 导出目录可浏览选择/手动输入，记住到 exe 旁边的 dsh-chat-history-export.config.json
//   3. 支持 session.jsonl 与 zstd 压缩的 session.jsonl.zstd（内嵌 libzstd.dll 解压）
//   4. 无参数启动 GUI；--selftest <会话> <输出> 走同一逻辑（供自检/脚本调用）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DshChatHistoryExport
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                try
                {
                    string raw = SessionReader.ReadSession(args[1]);
                    string md = SessionReader.BuildTranscript(raw);
                    File.WriteAllText(args[2], md, new UTF8Encoding(false));
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(args[2] + ".err.txt", ex.ToString(), new UTF8Encoding(false)); } catch { }
                    return 1;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            {
                LogCrash(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                LogCrash(e.ExceptionObject as Exception);
            };
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                return 1;
            }
            return 0;
        }

        private static void LogCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "dsh-chat-history-export-crash.log");
                File.WriteAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" + ex.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ---------- zstd 解压（P/Invoke libzstd.dll） ----------
    static class Zstd
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetDllDirectory(string lpPathName);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe ulong ZSTD_findFrameCompressedSize(byte* src, ulong srcSize);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe ulong ZSTD_findDecompressedSize(byte* src, ulong srcSize);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe uint ZSTD_isError(ulong code);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void* ZSTD_createDStream();

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void ZSTD_freeDStream(void* zds);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe ulong ZSTD_initDStream(void* zds);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe ulong ZSTD_decompressStream(void* zds, ZSTD_outBuffer* output, ZSTD_inBuffer* input);

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct ZSTD_inBuffer
        {
            public byte* src;
            public ulong size;
            public ulong pos;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct ZSTD_outBuffer
        {
            public byte* dst;
            public ulong size;
            public ulong pos;
        }

        private static readonly object dllLock = new object();

        internal static void EnsureDll()
        {
            lock (dllLock)
            {
                string dir = Path.Combine(Path.GetTempPath(), "dsh-chat-history-export-native");
                try { Directory.CreateDirectory(dir); }
                catch { dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
                string dll = Path.Combine(dir, "libzstd.dll");
                if (!File.Exists(dll))
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("libzstd.dll"))
                    using (FileStream fs = File.Create(dll))
                        s.CopyTo(fs);
                }
                SetDllDirectory(dir);
            }
        }

        /// <summary>
        /// 流式解压：一次过完文件里拼接的全部 zstd 帧。
        /// 相比逐帧 P/Invoke（大文件有数万帧，开销巨大），单趟流式解压快一到两个数量级。
        /// </summary>
        internal static unsafe byte[] DecompressAll(byte[] src)
        {
            void* ds = ZSTD_createDStream();
            if (ds == null) throw new InvalidDataException("zstd: ZSTD_createDStream 失败");
            try
            {
                fixed (byte* p = src)
                {
                    ZSTD_initDStream(ds);
                    // 多帧拼接时返回总解压大小；未知/出错时走动态扩容兜底
                    ulong total = ZSTD_findDecompressedSize(p, (ulong)src.Length);
                    ulong cap = (Zstd.ZSTD_isError(total) != 0 || total == 0)
                        ? (ulong)src.Length * 16 + 4096
                        : total;
                    byte[] dst = new byte[cap];
                    fixed (byte* d = dst)
                    {
                        ZSTD_inBuffer inb = new ZSTD_inBuffer();
                        inb.src = p;
                        inb.size = (ulong)src.Length;
                        inb.pos = 0;
                        ZSTD_outBuffer outb = new ZSTD_outBuffer();
                        outb.dst = d;
                        outb.size = cap;
                        outb.pos = 0;
                        while (inb.pos < inb.size)
                        {
                            if (outb.pos == outb.size)
                            {
                                // 输出缓冲满（总量未知时），扩容后继续
                                ulong newCap = cap * 2;
                                byte[] big = new byte[newCap];
                                Array.Copy(dst, big, (long)outb.pos);
                                dst = big;
                                cap = newCap;
                                fixed (byte* d2 = dst)
                                {
                                    outb.dst = d2;
                                    ulong r2 = ZSTD_decompressStream(ds, &outb, &inb);
                                    if (ZSTD_isError(r2) != 0)
                                        throw new InvalidDataException("zstd 解压失败 (error " + r2 + ")");
                                }
                                continue;
                            }
                            ulong r = ZSTD_decompressStream(ds, &outb, &inb);
                            if (ZSTD_isError(r) != 0)
                                throw new InvalidDataException("zstd 解压失败 (error " + r + ")");
                        }
                        byte[] res = new byte[outb.pos];
                        Array.Copy(dst, res, (long)outb.pos);
                        return res;
                    }
                }
            }
            finally
            {
                ZSTD_freeDStream(ds);
            }
        }
    }

    // ---------- 会话读取与转录 ----------
    static class SessionReader
    {
        public static string ReadSession(string file)
        {
            byte[] data = File.ReadAllBytes(file);
            if (data.Length < 4 || BitConverter.ToUInt32(data, 0) != 0xfd2fb528)
                return Encoding.UTF8.GetString(data); // 未压缩的 JSONL
            Zstd.EnsureDll();
            return Encoding.UTF8.GetString(Zstd.DecompressAll(data));
        }

        /// <summary>
        /// 单趟扫描会话元信息：
        ///   title      — 最后一个 session/title 事件的 data.title（与 DSH 侧边栏一致，last-wins），无则 null
        ///   hasContent — 是否存在真实的用户聊天内容（非插件注入、有文本的 user/message）
        /// 只解析包含 "session/title" 或 "user/message" 的行，避免整份大文本全量 JSON 解析。
        /// </summary>
        public static void GetMeta(string raw, out string title, out bool hasContent)
        {
            title = null;
            hasContent = false;
            if (string.IsNullOrEmpty(raw)) return;
            JavaScriptSerializer ser = new JavaScriptSerializer();
            int idx = 0;
            while (idx < raw.Length)
            {
                int ti = raw.IndexOf("session/title", idx, StringComparison.Ordinal);
                int ui = raw.IndexOf("user/message", idx, StringComparison.Ordinal);
                int next;
                bool isTitle;
                if (ti < 0 && ui < 0) break;
                if (ti >= 0 && (ui < 0 || ti < ui)) { next = ti; isTitle = true; }
                else { next = ui; isTitle = false; }
                int ls = raw.LastIndexOf('\n', next) + 1;
                int le = raw.IndexOf('\n', next);
                if (le < 0) le = raw.Length;
                string line = raw.Substring(ls, le - ls).Trim();
                idx = next + 1;
                if (line.Length == 0) continue;
                try
                {
                    Dictionary<string, object> e = ser.DeserializeObject(line) as Dictionary<string, object>;
                    if (e == null) continue;
                    string t0 = e.ContainsKey("type") ? e["type"] as string : null;
                    Dictionary<string, object> d = (e.ContainsKey("data") ? e["data"] : null) as Dictionary<string, object>;
                    if (isTitle)
                    {
                        if ("session/title".Equals(t0) && d != null && d.ContainsKey("title"))
                        {
                            string t = Convert.ToString(d["title"]);
                            if (t != null && t.Length > 0) title = t; // last wins
                        }
                    }
                    else if ("user/message".Equals(t0) && d != null)
                    {
                        Dictionary<string, object> src = (d.ContainsKey("source") ? d["source"] : null) as Dictionary<string, object>;
                        if (src != null && "plugin".Equals(src["kind"] as string)) continue; // 插件注入不算聊天内容
                        string t = TextOf(d.ContainsKey("content") ? d["content"] : null).Trim();
                        if (t.Length > 0) hasContent = true;
                    }
                }
                catch { }
            }
        }

        public static string BuildTranscript(string raw)
        {
            StringBuilder sb = new StringBuilder();
            int turn = 0;
            JavaScriptSerializer ser = new JavaScriptSerializer();
            foreach (string line0 in raw.Split('\n'))
            {
                string line = line0.Trim();
                if (line.Length == 0) continue;
                try
                {
                    Dictionary<string, object> e = ser.DeserializeObject(line) as Dictionary<string, object>;
                    if (e == null) continue;
                    string type = e.ContainsKey("type") ? e["type"] as string : null;
                    if (type == null) continue;
                    Dictionary<string, object> d = (e.ContainsKey("data") ? e["data"] : null) as Dictionary<string, object>;

                    if (type == "turn/start")
                    {
                        if (d != null && d.ContainsKey("turn")) turn = Convert.ToInt32(d["turn"]);
                    }
                    else if (type == "user/message")
                    {
                        if (d == null) continue;
                        Dictionary<string, object> src = (d.ContainsKey("source") ? d["source"] : null) as Dictionary<string, object>;
                        if (src != null && "plugin".Equals(src["kind"] as string)) continue; // 跳过插件注入
                        string t = TextOf(d.ContainsKey("content") ? d["content"] : null).Trim();
                        if (t.Length > 0)
                            sb.Append("\n## [T").Append(turn).Append("] 用户 ").Append(Ts(e)).Append("\n").Append(t);
                    }
                    else if (type == "assistant/message")
                    {
                        if (d == null) continue;
                        Dictionary<string, object> msg = (d.ContainsKey("message") ? d["message"] : null) as Dictionary<string, object>;
                        string t = msg != null ? TextOf(msg.ContainsKey("content") ? msg["content"] : null).Trim() : "";
                        bool hasTool = false;
                        if (msg != null && msg.ContainsKey("content"))
                        {
                            object[] blocks = msg["content"] as object[];
                            if (blocks != null)
                                foreach (object b in blocks)
                                {
                                    Dictionary<string, object> bl = b as Dictionary<string, object>;
                                    if (bl != null && "tool-call".Equals(bl["type"] as string)) { hasTool = true; break; }
                                }
                        }
                        if (t.Length > 0)
                            sb.Append("\n### 助手 ").Append(Ts(e)).Append(hasTool ? " [含工具调用]" : "").Append("\n").Append(t);
                    }
                    else if (type == "tool/call")
                    {
                        if (d == null) continue;
                        sb.Append("\n> 🔧 ").Append(d.ContainsKey("name") ? Convert.ToString(d["name"]) : "")
                          .Append(" ").Append(Trunc(Serialize(d.ContainsKey("arguments") ? d["arguments"] : null), 200));
                    }
                    else if (type == "tool/result")
                    {
                        if (d == null) continue;
                        bool ok = d.ContainsKey("ok") ? Convert.ToBoolean(d["ok"]) : !d.ContainsKey("error");
                        object content = d.ContainsKey("content") ? d["content"] : (d.ContainsKey("text") ? d["text"] : null);
                        sb.Append("\n> 📦 ").Append(ok ? "OK" : "ERROR").Append(" ").Append(Trunc(Serialize(content), 300));
                    }
                    else if (type == "compaction/end")
                    {
                        string err = (d != null && d.ContainsKey("error")) ? Convert.ToString(d["error"]) : null;
                        sb.Append("\n## ⏳ 上下文压缩 @T")
                          .Append(d != null && d.ContainsKey("turn") ? Convert.ToString(d["turn"]) : "")
                          .Append(string.IsNullOrEmpty(err) ? "" : " 失败: " + err);
                    }
                    else if (type == "turn/end" && d != null && d.ContainsKey("reason"))
                    {
                        Dictionary<string, object> reason = d["reason"] as Dictionary<string, object>;
                        if (reason != null && "error".Equals(reason["kind"] as string))
                        {
                            string msg = "";
                            Dictionary<string, object> errObj = (reason.ContainsKey("error") ? reason["error"] : null) as Dictionary<string, object>;
                            if (errObj != null && errObj.ContainsKey("message")) msg = Convert.ToString(errObj["message"]);
                            sb.Append("\n## ❌ T").Append(turn).Append(" 失败: ").Append(Trunc(msg, 400));
                        }
                    }
                }
                catch { }
            }
            return sb.ToString();
        }

        private static string Ts(Dictionary<string, object> e)
        {
            object v = e.ContainsKey("time") ? e["time"] : null;
            if (v == null) return "";
            string t = Convert.ToString(v);
            if (string.IsNullOrEmpty(t)) return "";
            try
            {
                long ms;
                DateTimeOffset dt;
                if (long.TryParse(t, out ms)) dt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                else dt = DateTimeOffset.Parse(t);
                return dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { return t; }
        }

        private static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        private static string TextOf(object blocksObj)
        {
            if (blocksObj == null) return "";
            object[] blocks = blocksObj as object[];
            if (blocks == null) return Convert.ToString(blocksObj);
            StringBuilder sb = new StringBuilder();
            foreach (object b in blocks)
            {
                Dictionary<string, object> bl = b as Dictionary<string, object>;
                if (bl == null) continue;
                if ("text".Equals(bl["type"] as string) && bl.ContainsKey("text"))
                    sb.Append(Convert.ToString(bl["text"]));
            }
            return sb.ToString();
        }

        private static string Serialize(object o)
        {
            try { return new JavaScriptSerializer().Serialize(o); }
            catch { return Convert.ToString(o); }
        }

        // ---------- 预览渲染：把转录 Markdown 转成带格式的 RTF（标题着色加粗、工具调用等宽灰字缩进） ----------
        public static string BuildPreviewRtf(string md)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(@"{\rtf1\ansi\deff0");
            sb.Append(@"{\fonttbl{\f0 Microsoft YaHei UI;}{\f1 Consolas;}}");
            sb.Append(@"{\colortbl ;\red31\green78\blue121;\red46\green125\blue50;\red128\green128\blue128;\red192\green0\blue0;\red178\green107\blue0;}");
            sb.Append(@"\viewkind4\uc1\pard\f0\fs19 ");
            bool first = true;
            foreach (string rawLine in md.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                {
                    if (!first) sb.Append(@"\par ");
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    string body = line.Substring(3);
                    string color = @"\cf1 ";
                    if (body.StartsWith("⏳")) color = @"\cf5 ";
                    else if (body.StartsWith("❌")) color = @"\cf4 ";
                    sb.Append(@"\par \b\fs22 ").Append(color).Append(RtfEscape(body)).Append(@"\b0\fs19\par ");
                }
                else if (line.StartsWith("### "))
                {
                    sb.Append(@"\par \b\fs22\cf2 ").Append(RtfEscape(line.Substring(4))).Append(@"\b0\fs19\par ");
                }
                else if (line.StartsWith("> "))
                {
                    string body = line.Substring(2).TrimStart();
                    string label = "";
                    if (body.StartsWith("🔧")) { label = "TOOL  "; body = body.Substring(2).TrimStart(); }
                    else if (body.StartsWith("📦")) { label = "RESULT  "; body = body.Substring(2).TrimStart(); }
                    sb.Append(@"\par \f1\fs16\li720\cf3 ").Append(RtfEscape(label + body)).Append(@"\f0\fs19\li0\par ");
                }
                else
                {
                    sb.Append(RtfEscape(line)).Append(@"\par ");
                }
                first = false;
            }
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>RTF 转义：\ { } 转义、\n 转 \par；CJK 用 \uN?；代理对（emoji）降级为 ?。</summary>
        private static string RtfEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length * 2);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') sb.Append(@"\\");
                else if (c == '{') sb.Append(@"\{");
                else if (c == '}') sb.Append(@"\}");
                else if (c == '\n') sb.Append(@"\par ");
                else if (c < 128) sb.Append(c);
                else if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c)) sb.Append('?'); // emoji 等无法用 \uN 表示
                else sb.Append(@"\u").Append((short)c).Append('?');
            }
            return sb.ToString();
        }
    }

    // ---------- 界面语言（中文 / English） ----------
    static class Lang
    {
        public static string Current = "zh"; // "zh" | "en"，启动时从配置读取

        private static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            { "title", "DSH Chat-History Export — 聊天记录导出工具" },
            { "titleLabel", "DSH Chat-History Export" },
            { "dirLabel", "导出目录:" },
            { "browse", "浏览…" },
            { "openDir", "打开目录" },
            { "refresh", "刷新列表" },
            { "pick", "选择会话文件…" },
            { "exportBtn", "导出并保存" },
            { "colTopic", "主题" },
            { "colId", "会话 ID" },
            { "colTime", "时间" },
            { "menuFile", "文件" },
            { "menuEdit", "编辑" },
            { "menuLang", "语言" },
            { "filePick", "选择会话文件…" },
            { "fileRefresh", "刷新列表" },
            { "fileExport", "导出并保存" },
            { "fileExit", "退出" },
            { "editCopy", "复制" },
            { "editSelectAll", "全选" },
            { "editClearCache", "清除主题缓存" },
            { "langZh", "中文" },
            { "langEn", "English" },
            { "statusReady", "就绪" },
            { "statusLoaded", "已加载 {0} 个会话" },
            { "statusReading", "，正在读取主题…" },
            { "statusNoSessions", "（未找到 ~/.dsh/sessions，可点“选择会话文件…”手动挑选）" },
            { "statusFiltered", "（已剔除 {0} 个空白会话：无主题且无聊天内容）" },
            { "statusSelected", "已选择: " },
            { "statusGenerated", "已生成: " },
            { "msgInfo", "提示" },
            { "msgError", "错误" },
            { "msgExportDone", "导出完成" },
            { "msgOpenFolder", "已生成:\n{0}\n\n是否打开所在文件夹？" },
            { "msgNoSession", "请先在左侧列表选择一个会话，或点“选择会话文件…”" },
            { "msgNoDir", "请先设置导出目录" },
            { "msgReadFail", "读取失败:\n" },
            { "msgExportFail", "导出失败:\n" },
            { "pickDialogTitle", "选择 DSH 会话文件 (session.jsonl / session.jsonl.zstd)" },
            { "pickDialogFilter", "会话文件 (*.jsonl*)|*.jsonl*|所有文件 (*.*)|*.*" },
            { "folderDialogDesc", "选择导出目录" },
            { "previewTruncated", "…（预览已截断，导出文件为完整内容）" },
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { "title", "DSH Chat-History Export — Chat History Export Tool" },
            { "titleLabel", "DSH Chat-History Export" },
            { "dirLabel", "Export directory:" },
            { "browse", "Browse…" },
            { "openDir", "Open folder" },
            { "refresh", "Refresh list" },
            { "pick", "Choose session file…" },
            { "exportBtn", "Export & Save" },
            { "colTopic", "Topic" },
            { "colId", "Session ID" },
            { "colTime", "Time" },
            { "menuFile", "&File" },
            { "menuEdit", "&Edit" },
            { "menuLang", "&Language" },
            { "filePick", "&Choose session file…" },
            { "fileRefresh", "&Refresh list" },
            { "fileExport", "&Export and Save" },
            { "fileExit", "E&xit" },
            { "editCopy", "&Copy" },
            { "editSelectAll", "Select &All" },
            { "editClearCache", "&Clear topic cache" },
            { "langZh", "中文" },
            { "langEn", "English" },
            { "statusReady", "Ready" },
            { "statusLoaded", "Loaded {0} sessions" },
            { "statusReading", ", reading topics…" },
            { "statusNoSessions", " (no ~/.dsh/sessions found — use “Choose session file…”)" },
            { "statusFiltered", " ({0} blank session(s) filtered: no topic and no chat content)" },
            { "statusSelected", "Selected: " },
            { "statusGenerated", "Generated: " },
            { "msgInfo", "Info" },
            { "msgError", "Error" },
            { "msgExportDone", "Export complete" },
            { "msgOpenFolder", "Generated:\n{0}\n\nOpen the containing folder?" },
            { "msgNoSession", "Please select a session from the list, or use “Choose session file…”" },
            { "msgNoDir", "Please set the export directory first" },
            { "msgReadFail", "Failed to read:\n" },
            { "msgExportFail", "Export failed:\n" },
            { "pickDialogTitle", "Choose DSH session file (session.jsonl / session.jsonl.zstd)" },
            { "pickDialogFilter", "Session files (*.jsonl*)|*.jsonl*|All files (*.*)|*.*" },
            { "folderDialogDesc", "Choose export folder" },
            { "previewTruncated", "…(preview truncated; the exported file contains the full content)" },
        };

        public static string T(string key)
        {
            Dictionary<string, string> dict = Current == "en" ? En : Zh;
            string v;
            return dict.TryGetValue(key, out v) ? v : key;
        }
    }

    // ---------- 主窗口 ----------
    class SessionInfo
    {
        public string Id;
        public string File;
        public DateTime Time;
        public string Title; // 主题（最后一条 session/title）
        public string Transcript; // 缓存
    }

    class MainForm : Form
    {
        private ListView list;
        private TextBox dirBox;
        private Button btnBrowse, btnOpenDir, btnRefresh, btnPick, btnExport;
        private RichTextBox preview;
        private StatusStrip status;
        private ToolStripStatusLabel statusLabel;
        private List<SessionInfo> sessions = new List<SessionInfo>();
        private string pickedFile;
        private Label titleLabel, lbDir;
        // 菜单栏（文件 / 编辑 / 语言）
        private MenuStrip menu;
        private ToolStripMenuItem mFile, mEdit, mLang;
        private ToolStripMenuItem filePick, fileRefresh, fileExport, fileExit;
        private ToolStripMenuItem editCopy, editSelectAll, editClearCache;
        private ToolStripMenuItem langZhItem, langEnItem;
        // 主题缓存条目：修改时间（Unix 毫秒）+ 文件大小，两者都匹配才复用，避免文件被改写后误用旧主题
        private class TitleCacheEntry
        {
            public long MtimeMs;
            public long Size;
            public string Title;
            public bool HasContent; // 是否有真实聊天内容（用于剔除空白会话）
        }
        // 内存缓存（启动时从磁盘读入，扫描后写回磁盘，保证重进不重复解压）
        private Dictionary<string, TitleCacheEntry> titleCache = new Dictionary<string, TitleCacheEntry>();
        private int scanGen; // 刷新列表的代数，防止旧扫描的收尾覆盖新状态
        private int filteredCount; // 被剔除的空白会话数（无主题且无聊天内容）

        private string ConfigPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dsh-chat-history-export.config.json"); }
        }

        private string CachePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dsh-chat-history-export.titles.json"); }
        }

        private const int TitleCacheVersion = 2; // 缓存格式版本，结构变化时旧缓存整体作废

        private void LoadTitleCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                Dictionary<string, object> raw =
                    new JavaScriptSerializer().DeserializeObject(File.ReadAllText(CachePath)) as Dictionary<string, object>;
                if (raw == null) return;
                lock (titleCache)
                {
                    titleCache.Clear();
                    object v;
                    if (!raw.TryGetValue("v", out v) || Convert.ToInt32(v) != TitleCacheVersion) return; // 旧格式，作废重扫
                    Dictionary<string, object> entries = (raw.ContainsKey("e") ? raw["e"] : null) as Dictionary<string, object>;
                    if (entries == null) return;
                    foreach (KeyValuePair<string, object> kv in entries)
                    {
                        Dictionary<string, object> e = kv.Value as Dictionary<string, object>;
                        if (e == null) continue;
                        TitleCacheEntry ce = new TitleCacheEntry();
                        ce.MtimeMs = e.ContainsKey("m") ? Convert.ToInt64(e["m"]) : 0;
                        ce.Size = e.ContainsKey("s") ? Convert.ToInt64(e["s"]) : 0;
                        ce.Title = e.ContainsKey("t") ? Convert.ToString(e["t"]) : "";
                        ce.HasContent = e.ContainsKey("c") && Convert.ToBoolean(e["c"]);
                        titleCache[kv.Key] = ce;
                    }
                }
            }
            catch { }
        }

        private void SaveTitleCache()
        {
            try
            {
                Dictionary<string, object> entries = new Dictionary<string, object>();
                lock (titleCache)
                {
                    foreach (KeyValuePair<string, TitleCacheEntry> kv in titleCache)
                    {
                        Dictionary<string, object> e = new Dictionary<string, object>();
                        e["m"] = kv.Value.MtimeMs;
                        e["s"] = kv.Value.Size;
                        e["t"] = kv.Value.Title ?? "";
                        e["c"] = kv.Value.HasContent;
                        entries[kv.Key] = e;
                    }
                }
                Dictionary<string, object> raw = new Dictionary<string, object>();
                raw["v"] = TitleCacheVersion;
                raw["e"] = entries;
                File.WriteAllText(CachePath, new JavaScriptSerializer().Serialize(raw), new UTF8Encoding(false));
            }
            catch { }
        }

        public MainForm()
        {
            Text = Lang.T("title");
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(1280, 720);
            MinimumSize = new Size(1024, 600);
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label title = new Label();
            title.Text = Lang.T("titleLabel");
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.AutoSize = true;
            titleLabel = title;
            root.Controls.Add(title, 0, 0);

            TableLayoutPanel dirRow = new TableLayoutPanel();
            dirRow.ColumnCount = 4;
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.Dock = DockStyle.Fill;
            dirRow.AutoSize = true;
            dirRow.Margin = new Padding(0, 8, 0, 0);

            Label lb = new Label();
            lb.Text = Lang.T("dirLabel");
            lb.AutoSize = true;
            lb.Anchor = AnchorStyles.Left;
            lbDir = lb;
            dirBox = new TextBox();
            dirBox.Dock = DockStyle.Fill;
            btnBrowse = MkButton(Lang.T("browse"), 84);
            btnOpenDir = MkButton(Lang.T("openDir"), 92);
            dirRow.Controls.Add(lb, 0, 0);
            dirRow.Controls.Add(dirBox, 1, 0);
            dirRow.Controls.Add(btnBrowse, 2, 0);
            dirRow.Controls.Add(btnOpenDir, 3, 0);
            root.Controls.Add(dirRow, 0, 1);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.Margin = new Padding(0, 8, 0, 8);
            btnRefresh = MkButton(Lang.T("refresh"), 120);
            btnPick = MkButton(Lang.T("pick"), 140);
            btnExport = MkButton(Lang.T("exportBtn"), 120);
            actions.Controls.Add(btnRefresh);
            actions.Controls.Add(btnPick);
            actions.Controls.Add(btnExport);
            root.Controls.Add(actions, 0, 2);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 640;
            split.Panel1MinSize = 480;

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.Columns.Add(Lang.T("colTopic"), 190);
            list.Columns.Add(Lang.T("colId"), 200);
            list.Columns.Add(Lang.T("colTime"), 110);
            split.Panel1.Controls.Add(list);

            preview = new RichTextBox();
            preview.Dock = DockStyle.Fill;
            preview.ReadOnly = true;
            preview.BorderStyle = BorderStyle.FixedSingle;
            preview.BackColor = Color.White;
            preview.Font = new Font("Microsoft YaHei UI", 9.5f);
            split.Panel2.Controls.Add(preview);

            root.Controls.Add(split, 0, 3);
            Controls.Add(root);

            status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            statusLabel.Text = Lang.T("statusReady");
            status.Items.Add(statusLabel);
            Controls.Add(status);

            btnRefresh.Click += delegate { LoadSessions(); };
            btnPick.Click += delegate { PickFile(); };
            btnExport.Click += delegate { Export(); };
            btnBrowse.Click += delegate { BrowseDir(); };
            btnOpenDir.Click += delegate { OpenExplorer(dirBox.Text, null); };
            list.SelectedIndexChanged += delegate { OnSelect(); };
            list.DoubleClick += delegate { Export(); };

            BuildMenu();
            LoadConfig();
            ApplyLanguage(Lang.Current); // 按配置的语言刷新全部界面文案
            LoadTitleCache();
            LoadSessions();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveTitleCache(); // 收尾写回主题缓存，下次启动直接复用
            base.OnFormClosing(e);
        }

        // ---------- 菜单栏（文件 / 编辑 / 语言） ----------
        private void BuildMenu()
        {
            menu = new MenuStrip();
            menu.Font = Font;

            mFile = new ToolStripMenuItem(Lang.T("menuFile"));
            filePick = new ToolStripMenuItem(Lang.T("filePick"), null, delegate { PickFile(); });
            fileRefresh = new ToolStripMenuItem(Lang.T("fileRefresh"), null, delegate { LoadSessions(); });
            fileRefresh.ShortcutKeys = Keys.F5;
            fileExport = new ToolStripMenuItem(Lang.T("fileExport"), null, delegate { Export(); });
            fileExit = new ToolStripMenuItem(Lang.T("fileExit"), null, delegate { Close(); });
            mFile.DropDownItems.Add(filePick);
            mFile.DropDownItems.Add(fileRefresh);
            mFile.DropDownItems.Add(fileExport);
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(fileExit);

            mEdit = new ToolStripMenuItem(Lang.T("menuEdit"));
            editCopy = new ToolStripMenuItem(Lang.T("editCopy"), null, delegate { try { preview.Copy(); } catch { } });
            editCopy.ShortcutKeys = Keys.Control | Keys.C;
            editSelectAll = new ToolStripMenuItem(Lang.T("editSelectAll"), null, delegate { preview.SelectAll(); });
            editSelectAll.ShortcutKeys = Keys.Control | Keys.A;
            editClearCache = new ToolStripMenuItem(Lang.T("editClearCache"), null, delegate { ClearTitleCache(); });
            mEdit.DropDownItems.Add(editCopy);
            mEdit.DropDownItems.Add(editSelectAll);
            mEdit.DropDownItems.Add(new ToolStripSeparator());
            mEdit.DropDownItems.Add(editClearCache);

            mLang = new ToolStripMenuItem(Lang.T("menuLang"));
            langZhItem = new ToolStripMenuItem(Lang.T("langZh"), null, delegate { ApplyLanguage("zh"); });
            langEnItem = new ToolStripMenuItem(Lang.T("langEn"), null, delegate { ApplyLanguage("en"); });
            mLang.DropDownItems.Add(langZhItem);
            mLang.DropDownItems.Add(langEnItem);

            menu.Items.Add(mFile);
            menu.Items.Add(mEdit);
            menu.Items.Add(mLang);
            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        /// <summary>应用界面语言：更新全部文案并持久化到配置。</summary>
        private void ApplyLanguage(string lang)
        {
            Lang.Current = lang;
            Text = Lang.T("title");
            titleLabel.Text = Lang.T("titleLabel");
            lbDir.Text = Lang.T("dirLabel");
            btnBrowse.Text = Lang.T("browse");
            btnOpenDir.Text = Lang.T("openDir");
            btnRefresh.Text = Lang.T("refresh");
            btnPick.Text = Lang.T("pick");
            btnExport.Text = Lang.T("exportBtn");
            if (list.Columns.Count >= 3)
            {
                list.Columns[0].Text = Lang.T("colTopic");
                list.Columns[1].Text = Lang.T("colId");
                list.Columns[2].Text = Lang.T("colTime");
            }
            ResizeColumns();
            mFile.Text = Lang.T("menuFile");
            filePick.Text = Lang.T("filePick");
            fileRefresh.Text = Lang.T("fileRefresh");
            fileExport.Text = Lang.T("fileExport");
            fileExit.Text = Lang.T("fileExit");
            mEdit.Text = Lang.T("menuEdit");
            editCopy.Text = Lang.T("editCopy");
            editSelectAll.Text = Lang.T("editSelectAll");
            editClearCache.Text = Lang.T("editClearCache");
            mLang.Text = Lang.T("menuLang");
            langZhItem.Text = Lang.T("langZh");
            langEnItem.Text = Lang.T("langEn");
            langZhItem.Checked = lang == "zh";
            langEnItem.Checked = lang == "en";
            statusLabel.Text = Lang.T("statusReady");
            SaveConfig(dirBox.Text.Trim()); // 记住语言选择
        }

        /// <summary>清除主题磁盘/内存缓存并重新扫描。</summary>
        private void ClearTitleCache()
        {
            lock (titleCache) titleCache.Clear();
            try { File.Delete(CachePath); } catch { }
            statusLabel.Text = Lang.T("statusReady");
            LoadSessions();
        }

        private static Button MkButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 30;
            b.Margin = new Padding(0, 0, 8, 0);
            return b;
        }

        private void LoadSessions()
        {
            sessions = new List<SessionInfo>();
            list.BeginUpdate();
            list.Items.Clear();
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
            if (Directory.Exists(root))
            {
                Stack<string> dirs = new Stack<string>();
                dirs.Push(root);
                while (dirs.Count > 0)
                {
                    string dir = dirs.Pop();
                    string[] subs;
                    try { subs = Directory.GetDirectories(dir); } catch { continue; }
                    foreach (string sub in subs)
                    {
                        string name = Path.GetFileName(sub);
                        if (name.StartsWith("session-") && name.Length == "session-".Length + 36)
                        {
                            string[] files;
                            try { files = Directory.GetFiles(sub); } catch { continue; }
                            foreach (string f in files)
                            {
                                string fn = Path.GetFileName(f);
                                if (fn == "session.jsonl" || fn == "session.jsonl.zstd")
                                {
                                    SessionInfo si = new SessionInfo();
                                    si.Id = name;
                                    si.File = f;
                                    try { si.Time = File.GetLastWriteTime(f); } catch { }
                                    sessions.Add(si);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            dirs.Push(sub);
                        }
                    }
                }
            }
            sessions.Sort(delegate (SessionInfo a, SessionInfo b) { return b.Time.CompareTo(a.Time); });
            int gen = ++scanGen;
            filteredCount = 0;
            List<KeyValuePair<SessionInfo, ListViewItem>> pending = new List<KeyValuePair<SessionInfo, ListViewItem>>();
            foreach (SessionInfo si in sessions)
            {
                string topic = null;
                bool cachedBlank = false; // 缓存明确记录为空白会话（无主题且无内容）
                long len = -1;
                try { len = new FileInfo(si.File).Length; } catch { }
                lock (titleCache)
                {
                    TitleCacheEntry c;
                    if (titleCache.TryGetValue(si.File, out c)
                        && c.MtimeMs == new DateTimeOffset(si.Time).ToUnixTimeMilliseconds()
                        && (len < 0 || c.Size == len))
                    {
                        if (!string.IsNullOrEmpty(c.Title)) topic = c.Title;
                        else if (c.HasContent) topic = ""; // 有内容但暂无主题，保留显示
                        else cachedBlank = true;           // 无主题且无内容：空白会话，剔除
                    }
                }
                if (cachedBlank) { filteredCount++; continue; } // 不加入列表
                ListViewItem it = new ListViewItem(topic ?? "");
                it.SubItems.Add(si.Id);
                it.SubItems.Add(si.Time.ToString("yyyy-MM-dd HH:mm"));
                it.Tag = si;
                list.Items.Add(it);
                if (topic == null) pending.Add(new KeyValuePair<SessionInfo, ListViewItem>(si, it));
            }
            list.EndUpdate();
            ResizeColumns();
            statusLabel.Text = string.Format(Lang.T("statusLoaded"), sessions.Count - filteredCount)
                + (sessions.Count - filteredCount == 0 ? Lang.T("statusNoSessions") : "")
                + (filteredCount > 0 ? string.Format(Lang.T("statusFiltered"), filteredCount) : "")
                + (pending.Count > 0 ? Lang.T("statusReading") : "");
            if (pending.Count > 0) ScanTitles(pending, gen);
        }

        /// <summary>
        /// 按「表头 + 列内最宽内容」自适应列宽，保证主题 / 会话 ID / 时间完整显示。
        /// 主题列上限 700px，防止个别超长主题把整列撑爆（其余行仍可横向滚动查看）。
        /// </summary>
        private void ResizeColumns()
        {
            try
            {
                using (Graphics g = list.CreateGraphics())
                {
                    for (int c = 0; c < list.Columns.Count; c++)
                    {
                        int w = TextRenderer.MeasureText(g, list.Columns[c].Text, list.Font).Width + 20;
                        foreach (ListViewItem it in list.Items)
                        {
                            string txt = c < it.SubItems.Count ? it.SubItems[c].Text : "";
                            int tw = TextRenderer.MeasureText(g, txt, list.Font).Width + 24;
                            if (tw > w) w = tw;
                        }
                        if (c == 0 && w > 700) w = 700;
                        list.Columns[c].Width = w;
                    }
                }
            }
            catch { }
        }

        /// <summary>并行读取未缓存会话的元信息（主题 + 是否有聊天内容），进度实时填回列表；空白会话（无主题且无内容）剔除并写明；结束后写回磁盘缓存。</summary>
        private void ScanTitles(List<KeyValuePair<SessionInfo, ListViewItem>> pending, int gen)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 4 }, delegate (KeyValuePair<SessionInfo, ListViewItem> pair)
                {
                    SessionInfo si = pair.Key;
                    ListViewItem it = pair.Value;
                    string topic = null;
                    bool blank = false;
                    try
                    {
                        DateTime mt = File.GetLastWriteTime(si.File);
                        long len = -1;
                        try { len = new FileInfo(si.File).Length; } catch { }
                        bool cached = false;
                        lock (titleCache)
                        {
                            TitleCacheEntry c;
                            if (titleCache.TryGetValue(si.File, out c)
                                && c.MtimeMs == new DateTimeOffset(mt).ToUnixTimeMilliseconds()
                                && (len < 0 || c.Size == len))
                            {
                                cached = true;
                                if (!string.IsNullOrEmpty(c.Title)) topic = c.Title;
                                else if (c.HasContent) topic = "";
                                else blank = true;
                            }
                        }
                        if (!cached)
                        {
                            string raw = SessionReader.ReadSession(si.File);
                            string t;
                            bool hasContent;
                            SessionReader.GetMeta(raw, out t, out hasContent);
                            topic = t ?? "";
                            TitleCacheEntry ce = new TitleCacheEntry();
                            ce.MtimeMs = new DateTimeOffset(mt).ToUnixTimeMilliseconds();
                            ce.Size = len;
                            ce.Title = topic;
                            ce.HasContent = hasContent;
                            lock (titleCache) titleCache[si.File] = ce;
                            if (!hasContent && string.IsNullOrEmpty(topic)) blank = true; // 无主题且无内容：空白会话
                        }
                        si.Title = topic;
                        if (IsDisposed) return;
                        BeginInvoke((Action)delegate
                        {
                            if (blank)
                            {
                                // 从列表剔除空白会话，并在状态栏写明
                                list.Items.Remove(it);
                                Interlocked.Increment(ref filteredCount);
                                statusLabel.Text = string.Format(Lang.T("statusLoaded"), sessions.Count - filteredCount)
                                    + (sessions.Count - filteredCount == 0 ? Lang.T("statusNoSessions") : "")
                                    + (filteredCount > 0 ? string.Format(Lang.T("statusFiltered"), filteredCount) : "");
                                ResizeColumns();
                            }
                            else if (it.Tag == si && it.SubItems.Count > 0)
                            {
                                it.SubItems[0].Text = topic;
                                ResizeColumns(); // 主题逐条填充时同步自适应列宽
                            }
                        });
                    }
                    catch { }
                });
                SaveTitleCache();
                if (IsDisposed || gen != scanGen) return;
                BeginInvoke((Action)delegate
                {
                    statusLabel.Text = string.Format(Lang.T("statusLoaded"), sessions.Count - filteredCount)
                        + (sessions.Count - filteredCount == 0 ? Lang.T("statusNoSessions") : "")
                        + (filteredCount > 0 ? string.Format(Lang.T("statusFiltered"), filteredCount) : "");
                });
            });
        }

        private void OnSelect()
        {
            if (list.SelectedItems.Count == 0) return;
            SessionInfo si = (SessionInfo)list.SelectedItems[0].Tag;
            pickedFile = null;
            ShowPreview(si);
        }

        private void ShowPreview(SessionInfo si)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                if (si.Transcript == null)
                {
                    string raw = SessionReader.ReadSession(si.File);
                    si.Transcript = SessionReader.BuildTranscript(raw);
                }
                SetPreview(si.Transcript);
                statusLabel.Text = si.Id + " | " + si.File;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Lang.T("msgReadFail") + ex.Message, Lang.T("msgError"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void SetPreview(string md)
        {
            if (md.Length > 600000)
                md = md.Substring(0, 600000) + "\n\n" + Lang.T("previewTruncated");
            try
            {
                preview.Rtf = SessionReader.BuildPreviewRtf(md); // 渲染为带格式的 RTF
            }
            catch
            {
                preview.Text = md; // RTF 失败回退纯文本
            }
            preview.SelectionStart = 0;
            preview.ScrollToCaret();
        }

        private void PickFile()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = Lang.T("pickDialogTitle");
                ofd.Filter = Lang.T("pickDialogFilter");
                ofd.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                pickedFile = ofd.FileName;
                list.SelectedItems.Clear();
                SessionInfo si = new SessionInfo();
                si.Id = Path.GetFileName(ofd.FileName);
                si.File = ofd.FileName;
                ShowPreview(si);
                statusLabel.Text = Lang.T("statusSelected") + ofd.FileName;
            }
        }

        private void BrowseDir()
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = Lang.T("folderDialogDesc");
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    dirBox.Text = fbd.SelectedPath;
                    SaveConfig(fbd.SelectedPath);
                }
            }
        }

        private void Export()
        {
            string input = null;
            SessionInfo si = null;
            if (list.SelectedItems.Count > 0)
            {
                si = (SessionInfo)list.SelectedItems[0].Tag;
                input = si.File;
            }
            else if (pickedFile != null)
            {
                input = pickedFile;
            }
            if (input == null)
            {
                MessageBox.Show(this, Lang.T("msgNoSession"), Lang.T("msgInfo"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string dir = dirBox.Text.Trim();
            if (dir.Length == 0)
            {
                MessageBox.Show(this, Lang.T("msgNoDir"), Lang.T("msgInfo"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Cursor = Cursors.WaitCursor;
                if (si == null) si = new SessionInfo();
                if (si.Transcript == null)
                {
                    string raw = SessionReader.ReadSession(input);
                    si.Transcript = SessionReader.BuildTranscript(raw);
                }
                Directory.CreateDirectory(dir);
                string name = Path.GetFileName(input);
                name = Regex.Replace(name, @"\.jsonl(\.zstd)?$", "");
                string outFile = Path.Combine(dir, name + "-transcript.md");
                File.WriteAllText(outFile, si.Transcript, new UTF8Encoding(false));
                SaveConfig(dir);
                SetPreview(si.Transcript);
                statusLabel.Text = Lang.T("statusGenerated") + outFile;
                DialogResult r = MessageBox.Show(this, string.Format(Lang.T("msgOpenFolder"), outFile), Lang.T("msgExportDone"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) OpenExplorer(dir, outFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Lang.T("msgExportFail") + ex.Message, Lang.T("msgError"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OpenExplorer(string dir, string selectFile)
        {
            try
            {
                if (selectFile != null && File.Exists(selectFile))
                    Process.Start("explorer.exe", "/select,\"" + selectFile + "\"");
                else if (Directory.Exists(dir))
                    Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        private void LoadConfig()
        {
            string def = Path.GetDirectoryName(Application.ExecutablePath);
            dirBox.Text = def;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    Dictionary<string, object> cfg =
                        new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath)) as Dictionary<string, object>;
                    if (cfg != null)
                    {
                        if (cfg.ContainsKey("exportDir"))
                        {
                            string d = Convert.ToString(cfg["exportDir"]);
                            if (Directory.Exists(d)) dirBox.Text = d;
                        }
                        if (cfg.ContainsKey("lang"))
                        {
                            string l = Convert.ToString(cfg["lang"]);
                            if (l == "zh" || l == "en") Lang.Current = l;
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveConfig(string exportDir)
        {
            try
            {
                Dictionary<string, object> cfg = new Dictionary<string, object>();
                cfg["exportDir"] = exportDir;
                cfg["lang"] = Lang.Current;
                File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(cfg), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
