// dsh-chat-history-manage-gui.cs — DSH Chat-History Manage：管理 DSH 聊天记录（Win32 GUI 程序，单文件）
//
// 编译（Windows 自带 .NET Framework 4.x，无需安装运行时）:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /unsafe /codepage:65001
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll
//     /resource:libzstd.dll,libzstd.dll /out:dsh-chat-history-manage.exe dsh-chat-history-manage-gui.cs
//
// 功能：
//   1. 左侧会话列表（自动扫描 ~/.dsh/sessions），右侧实时预览转录内容
//   2. 导出目录可浏览选择/手动输入，记住到 exe 旁边的 dsh-chat-history-manage.config.json
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

namespace DshChatHistoryManage
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
                string log = Path.Combine(Path.GetTempPath(), "dsh-chat-history-manage-crash.log");
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
                string dir = Path.Combine(Path.GetTempPath(), "dsh-chat-history-manage-native");
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
            return BuildTranscript(raw, null);
        }

        /// <summary>onProgress(已处理行数, 总行数) 可选回调，用于加载进度显示（每 ~200 行触发一次）。</summary>
        public static string BuildTranscript(string raw, Action<int, int> onProgress)
        {
            StringBuilder sb = new StringBuilder();
            int turn = 0;
            string model = null; // 最近一次请求的模型（request/header 或 request/context 提供）
            JavaScriptSerializer ser = new JavaScriptSerializer();
            string[] lines = raw.Split('\n');
            int total = lines.Length;
            int processed = 0;
            foreach (string line0 in lines)
            {
                processed++;
                if (onProgress != null && (processed % 200 == 0 || processed == total))
                    onProgress(processed, total);
                string line = line0.Trim();
                if (line.Length == 0) continue;
                // 预过滤：只对包含目标事件类型的行做 JSON 解析（会话流里大部分行是 chunk/推理等无关事件，
                // JavaScriptSerializer 逐行解析非常慢，跳过可把大会话的转录构建从数秒降到亚秒级）
                if (line.IndexOf("\"type\":\"turn/start\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"user/message\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"assistant/message\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"tool/call\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"tool/result\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"compaction/end\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"turn/end\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"request/header\"", StringComparison.Ordinal) < 0
                    && line.IndexOf("\"type\":\"request/context\"", StringComparison.Ordinal) < 0)
                    continue;
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
                    else if (type == "request/header")
                    {
                        // 记录本轮请求使用的模型
                        Dictionary<string, object> header = (d != null && d.ContainsKey("header") ? d["header"] : null) as Dictionary<string, object>;
                        Dictionary<string, object> cfg = (header != null && header.ContainsKey("config") ? header["config"] : null) as Dictionary<string, object>;
                        if (cfg != null && cfg.ContainsKey("model")) model = Convert.ToString(cfg["model"]);
                    }
                    else if (type == "request/context")
                    {
                        if (d != null && d.ContainsKey("model")) model = Convert.ToString(d["model"]);
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
                            sb.Append("\n### ").Append(string.IsNullOrEmpty(model) ? "助手" : model).Append(" ").Append(Ts(e))
                              .Append(hasTool ? " [含工具调用]" : "").Append("\n").Append(t);
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
    }

    // ---------- 界面语言（60+ 常用语言，缺失键回退英文） ----------
    static class Lang
    {
        public static string Current = "zh"; // 当前语言代码，启动时从配置读取

        public class Language
        {
            public string Code;
            public string NativeName;
            public Dictionary<string, string> Dict;
            public Language(string code, string nativeName, Dictionary<string, string> dict)
            {
                Code = code; NativeName = nativeName; Dict = dict;
            }
        }

        public static readonly List<Language> Languages = new List<Language>();

        // 所有语言都翻译的核心键（其余键缺失时回退英文）
        private static readonly string[] CoreKeys =
        {
            "title", "menuFile", "menuEdit", "menuLang", "menuHelp", "about", "aboutOk",
            "dirLabel", "browse", "openDir", "refresh", "pick", "exportBtn",
            "colTopic", "colId", "colTime",
            "statusReady", "statusLoaded", "statusReading", "statusLoading",
            "msgInfo", "msgError", "msgExportDone", "linkGithub"
        };

        private static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            { "title", "DSH Chat-History Manage — 聊天记录管理工具" },
            { "titleLabel", "DSH Chat-History Manage" },
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
            { "menuHelp", "帮助" },
            { "about", "关于" },
            { "langGroupCommon", "常用" },
            { "langGroupEurope", "欧洲" },
            { "langGroupAsia", "亚洲·中东" },
            { "langGroupAfrica", "非洲·其他" },
            { "aboutTitle", "关于 DSH Chat-History Manage" },
            { "aboutVersion", "版本 1.0.0" },
            { "aboutDesc", "把 DeepSeek Harness（DSH）保存在本地磁盘的会话文件导出成可读的 Markdown 聊天记录。\n单文件 Win32 程序，无需安装运行时；支持 zstd 压缩与明文 JSONL。" },
            { "aboutOk", "确定" },
            { "statusReady", "就绪" },
            { "statusLoading", "正在加载会话…" },
            { "statusLoadingPct", "正在加载会话… {0}%" },
            { "linkGithub", "项目仓库" },
            { "statusLoaded", "已加载 {0} 个会话" },
            { "statusReading", "，正在读取主题…" },
            { "statusNoSessions", "（未找到 ~/.dsh/sessions，可点“选择会话文件…”手动挑选）" },
            { "statusFiltered", "（已剔除 {0} 个空白会话：无主题且无聊天内容）" },
            { "statusSelected", "已选择: " },
            { "statusGenerated", "已生成: " },
            { "statusCopied", "已复制到剪贴板" },
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
            { "title", "DSH Chat-History Manage — Chat History Manage Tool" },
            { "titleLabel", "DSH Chat-History Manage" },
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
            { "menuHelp", "&Help" },
            { "about", "&About" },
            { "langGroupCommon", "Common" },
            { "langGroupEurope", "Europe" },
            { "langGroupAsia", "Asia & Middle East" },
            { "langGroupAfrica", "Africa & Others" },
            { "aboutTitle", "About DSH Chat-History Manage" },
            { "aboutVersion", "Version 1.0.0" },
            { "aboutDesc", "Exports chat sessions saved by DeepSeek Harness (DSH) on local disk into readable Markdown transcripts.\nSingle-file Win32 app, no runtime required; supports zstd-compressed and plain JSONL." },
            { "aboutOk", "OK" },
            { "statusReady", "Ready" },
            { "statusLoading", "Loading session…" },
            { "statusLoadingPct", "Loading session… {0}%" },
            { "linkGithub", "Project Repository" },
            { "statusLoaded", "Loaded {0} sessions" },
            { "statusReading", ", reading topics…" },
            { "statusNoSessions", " (no ~/.dsh/sessions found — use “Choose session file…”)" },
            { "statusFiltered", " ({0} blank session(s) filtered: no topic and no chat content)" },
            { "statusSelected", "Selected: " },
            { "statusGenerated", "Generated: " },
            { "statusCopied", "Copied to clipboard" },
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

        static Lang()
        {
            Languages.Add(new Language("zh", "简体中文", Zh));
            Languages.Add(new Language("en", "English", En));
            // CoreKeys 顺序：title, menuFile, menuEdit, menuLang, menuHelp, about, aboutOk,
            //             dirLabel, browse, openDir, refresh, pick, exportBtn,
            //             colTopic, colId, colTime,
            //             statusReady, statusLoaded, statusReading, statusLoading,
            //             msgInfo, msgError, msgExportDone, linkGithub
            Add("zh-TW", "繁體中文", "DSH Chat-History Manage — 聊天記錄管理工具", "檔案", "編輯", "語言", "說明", "關於", "確定", "匯出目錄:", "瀏覽…", "開啟資料夾", "重新整理", "選擇工作階段檔…", "匯出並儲存", "主題", "工作階段 ID", "時間", "就緒", "已載入 {0} 個工作階段", "，正在讀取主題…", "正在載入工作階段…", "提示", "錯誤", "匯出完成", "專案儲存庫");
            Add("ja", "日本語", "DSH Chat-History Manage — チャット履歴管理ツール", "ファイル", "編集", "言語", "ヘルプ", "バージョン情報", "OK", "エクスポート先:", "参照…", "フォルダーを開く", "リストを更新", "セッションファイルを選択…", "エクスポートして保存", "トピック", "セッションID", "時刻", "準備完了", "{0} 件のセッションを読み込みました", "、トピックを読み込み中…", "セッションを読み込み中…", "情報", "エラー", "エクスポート完了", "プロジェクトリポジトリ");
            Add("ko", "한국어", "DSH Chat-History Manage — 채팅 기록 관리 도구", "파일", "편집", "언어", "도움말", "정보", "확인", "내보내기 폴더:", "찾아보기…", "폴더 열기", "목록 새로고침", "세션 파일 선택…", "내보내고 저장", "주제", "세션 ID", "시간", "준비됨", "세션 {0}개 로드됨", "주제 읽는 중…", "세션 불러오는 중…", "정보", "오류", "내보내기 완료", "프로젝트 저장소");
            Add("fr", "Français", "DSH Chat-History Manage — outil de gestion de l'historique de chat", "Fichier", "Édition", "Langue", "Aide", "À propos", "OK", "Dossier d'export :", "Parcourir…", "Ouvrir le dossier", "Actualiser la liste", "Choisir un fichier de session…", "Exporter et enregistrer", "Sujet", "ID de session", "Heure", "Prêt", "{0} sessions chargées", ", lecture des sujets…", "Chargement de la session…", "Info", "Erreur", "Export terminé", "Dépôt du projet");
            Add("de", "Deutsch", "DSH Chat-History Manage — Chat-Verlauf-Verwaltungstool", "Datei", "Bearbeiten", "Sprache", "Hilfe", "Über", "OK", "Exportordner:", "Durchsuchen…", "Ordner öffnen", "Liste aktualisieren", "Sitzungsdatei auswählen…", "Exportieren und speichern", "Thema", "Sitzungs-ID", "Zeit", "Bereit", "{0} Sitzungen geladen", ", Themen werden gelesen…", "Sitzung wird geladen…", "Info", "Fehler", "Export abgeschlossen", "Projekt-Repository");
            Add("es", "Español", "DSH Chat-History Manage — herramienta de gestión del historial de chat", "Archivo", "Editar", "Idioma", "Ayuda", "Acerca de", "Aceptar", "Carpeta de exportación:", "Examinar…", "Abrir carpeta", "Actualizar lista", "Elegir archivo de sesión…", "Exportar y guardar", "Tema", "ID de sesión", "Hora", "Listo", "{0} sesiones cargadas", ", leyendo temas…", "Cargando sesión…", "Información", "Error", "Exportación completada", "Repositorio del proyecto");
            Add("pt", "Português", "DSH Chat-History Manage — ferramenta de gestão de histórico de chat", "Arquivo", "Editar", "Idioma", "Ajuda", "Sobre", "OK", "Pasta de exportação:", "Procurar…", "Abrir pasta", "Atualizar lista", "Escolher arquivo de sessão…", "Exportar e salvar", "Tópico", "ID da sessão", "Hora", "Pronto", "{0} sessões carregadas", ", lendo tópicos…", "Carregando sessão…", "Informação", "Erro", "Exportação concluída", "Repositório do projeto");
            Add("ru", "Русский", "DSH Chat-History Manage — инструмент управления историей чата", "Файл", "Правка", "Язык", "Справка", "О программе", "ОК", "Папка экспорта:", "Обзор…", "Открыть папку", "Обновить список", "Выбрать файл сессии…", "Экспортировать и сохранить", "Тема", "ID сессии", "Время", "Готово", "Загружено сессий: {0}", ", чтение тем…", "Загрузка сессии…", "Инфо", "Ошибка", "Экспорт завершён", "Репозиторий проекта");
            Add("it", "Italiano", "DSH Chat-History Manage — strumento di gestione della cronologia chat", "File", "Modifica", "Lingua", "Aiuto", "Informazioni", "OK", "Cartella di esportazione:", "Sfoglia…", "Apri cartella", "Aggiorna elenco", "Scegli file sessione…", "Esporta e salva", "Argomento", "ID sessione", "Ora", "Pronto", "{0} sessioni caricate", ", lettura argomenti…", "Caricamento sessione…", "Info", "Errore", "Esportazione completata", "Repository del progetto");
            Add("nl", "Nederlands", "DSH Chat-History Manage — tool voor chatgeschiedenisbeheer", "Bestand", "Bewerken", "Taal", "Help", "Over", "OK", "Exportmap:", "Bladeren…", "Map openen", "Lijst verversen", "Sessiebestand kiezen…", "Exporteren en opslaan", "Onderwerp", "Sessie-ID", "Tijd", "Gereed", "{0} sessies geladen", ", onderwerpen lezen…", "Sessie laden…", "Info", "Fout", "Export voltooid", "Projectrepository");
            Add("pl", "Polski", "DSH Chat-History Manage — narzędzie do zarządzania historią czatu", "Plik", "Edycja", "Język", "Pomoc", "O programie", "OK", "Folder eksportu:", "Przeglądaj…", "Otwórz folder", "Odśwież listę", "Wybierz plik sesji…", "Eksportuj i zapisz", "Temat", "ID sesji", "Czas", "Gotowe", "Załadowano {0} sesji", ", czytanie tematów…", "Ładowanie sesji…", "Informacja", "Błąd", "Eksport zakończony", "Repozytorium projektu");
            Add("uk", "Українська", "DSH Chat-History Manage — інструмент керування історією чату", "Файл", "Правка", "Мова", "Довідка", "Про програму", "ОК", "Папка експорту:", "Огляд…", "Відкрити папку", "Оновити список", "Вибрати файл сесії…", "Експортувати та зберегти", "Тема", "ID сесії", "Час", "Готово", "Завантажено сесій: {0}", ", читання тем…", "Завантаження сесії…", "Інфо", "Помилка", "Експорт завершено", "Репозиторій проекту");
            Add("tr", "Türkçe", "DSH Chat-History Manage — sohbet geçmişi yönetim aracı", "Dosya", "Düzen", "Dil", "Yardım", "Hakkında", "Tamam", "Dışa aktarma klasörü:", "Gözat…", "Klasörü aç", "Listeyi yenile", "Oturum dosyası seç…", "Dışa aktar ve kaydet", "Konu", "Oturum kimliği", "Saat", "Hazır", "{0} oturum yüklendi", ", konular okunuyor…", "Oturum yükleniyor…", "Bilgi", "Hata", "Dışa aktarma tamamlandı", "Proje deposu");
            Add("th", "ไทย", "DSH Chat-History Manage — เครื่องมือจัดการประวัติแชท", "ไฟล์", "แก้ไข", "ภาษา", "ความช่วยเหลือ", "เกี่ยวกับ", "ตกลง", "โฟลเดอร์ส่งออก:", "เรียกดู…", "เปิดโฟลเดอร์", "รีเฟรชรายการ", "เลือกไฟล์เซสชัน…", "ส่งออกและบันทึก", "หัวข้อ", "รหัสเซสชัน", "เวลา", "พร้อม", "โหลด {0} เซสชัน", " กำลังอ่านหัวข้อ…", "กำลังโหลดเซสชัน…", "ข้อมูล", "ข้อผิดพลาด", "ส่งออกเสร็จสิ้น", "คลังโครงการ");
            Add("vi", "Tiếng Việt", "DSH Chat-History Manage — công cụ quản lý lịch sử trò chuyện", "Tệp", "Sửa", "Ngôn ngữ", "Trợ giúp", "Giới thiệu", "OK", "Thư mục xuất:", "Duyệt…", "Mở thư mục", "Làm mới danh sách", "Chọn tệp phiên…", "Xuất và lưu", "Chủ đề", "ID phiên", "Thời gian", "Sẵn sàng", "Đã tải {0} phiên", ", đang đọc chủ đề…", "Đang tải phiên…", "Thông tin", "Lỗi", "Xuất hoàn tất", "Kho lưu trữ dự án");
            Add("id", "Bahasa Indonesia", "DSH Chat-History Manage — alat manajemen riwayat chat", "Berkas", "Edit", "Bahasa", "Bantuan", "Tentang", "OK", "Folder ekspor:", "Telusuri…", "Buka folder", "Segarkan daftar", "Pilih file sesi…", "Ekspor dan simpan", "Topik", "ID sesi", "Waktu", "Siap", "{0} sesi dimuat", ", membaca topik…", "Memuat sesi…", "Info", "Kesalahan", "Ekspor selesai", "Repositori proyek");
            Add("ms", "Bahasa Melayu", "DSH Chat-History Manage — alat pengurusan sejarah chat", "Fail", "Edit", "Bahasa", "Bantuan", "Perihal", "OK", "Folder eksport:", "Semak imbas…", "Buka folder", "Segar semula senarai", "Pilih fail sesi…", "Eksport dan simpan", "Topik", "ID sesi", "Masa", "Sedia", "{0} sesi dimuat", ", membaca topik…", "Memuat sesi…", "Maklumat", "Ralat", "Eksport selesai", "Repositori projek");
            Add("hi", "हिन्दी", "DSH Chat-History Manage — चैट इतिहास प्रबंधन उपकरण", "फ़ाइल", "संपादन", "भाषा", "सहायता", "परिचय", "ठीक है", "निर्यात फ़ोल्डर:", "ब्राउज़ करें…", "फ़ोल्डर खोलें", "सूची ताज़ा करें", "सत्र फ़ाइल चुनें…", "निर्यात करें और सहेजें", "विषय", "सत्र आईडी", "समय", "तैयार", "{0} सत्र लोड हुए", ", विषय पढ़ रहे हैं…", "सत्र लोड हो रहा है…", "जानकारी", "त्रुटि", "निर्यात पूर्ण", "प्रोजेक्ट रिपॉज़िटरी");
            Add("ar", "العربية", "DSH Chat-History Manage — أداة إدارة سجل المحادثة", "ملف", "تحرير", "اللغة", "مساعدة", "حول", "موافق", "مجلد التصدير:", "تصفح…", "فتح المجلد", "تحديث القائمة", "اختيار ملف جلسة…", "تصدير وحفظ", "الموضوع", "معرف الجلسة", "الوقت", "جاهز", "تم تحميل {0} جلسة", "، قراءة المواضيع…", "جارٍ تحميل الجلسة…", "معلومات", "خطأ", "اكتمل التصدير", "مستودع المشروع");
            Add("sv", "Svenska", "DSH Chat-History Manage — verktyg för chatthistorik", "Arkiv", "Redigera", "Språk", "Hjälp", "Om", "OK", "Exportera mapp:", "Bläddra…", "Öppna mapp", "Uppdatera lista", "Välj sessionsfil…", "Exportera och spara", "Ämne", "Sessions-ID", "Tid", "Redo", "{0} sessioner inlästa", ", läser ämnen…", "Läser in session…", "Info", "Fel", "Export klar", "Projektförråd");
            Add("da", "Dansk", "DSH Chat-History Manage — værktøj til chat-historik", "Fil", "Rediger", "Sprog", "Hjælp", "Om", "OK", "Eksportmappe:", "Gennemse…", "Åbn mappe", "Opdater liste", "Vælg sessionsfil…", "Eksporter og gem", "Emne", "Sessions-ID", "Tid", "Klar", "{0} sessioner indlæst", ", læser emner…", "Indlæser session…", "Info", "Fejl", "Eksport fuldført", "Projektlager");
            Add("no", "Norsk", "DSH Chat-History Manage — verktøy for chathistorikk", "Fil", "Rediger", "Språk", "Hjelp", "Om", "OK", "Eksportmappe:", "Bla gjennom…", "Åpne mappe", "Oppdater liste", "Velg øktfil…", "Eksporter og lagre", "Emne", "Økt-ID", "Tid", "Klar", "{0} økter lastet", ", leser emner…", "Laster inn økt…", "Info", "Feil", "Eksport fullført", "Prosjektlager");
            Add("fi", "Suomi", "DSH Chat-History Manage — keskusteluhistorian hallintatyökalu", "Tiedosto", "Muokkaa", "Kieli", "Ohje", "Tietoja", "OK", "Vientikansio:", "Selaa…", "Avaa kansio", "Päivitä luettelo", "Valitse istuntotiedosto…", "Vie ja tallenna", "Aihe", "Istunnon tunnus", "Aika", "Valmis", "{0} istuntoa ladattu", ", luetaan aiheita…", "Ladataan istuntoa…", "Tiedot", "Virhe", "Vienti valmis", "Projektivarasto");
            Add("is", "Íslenska", "DSH Chat-History Manage — verkfæri til að stjórna spjallsögu", "Skrá", "Breyta", "Tungumál", "Hjálp", "Um", "OK", "Útflutningsmappa:", "Fletta…", "Opna möppu", "Endurnýja lista", "Velja lotuskrá…", "Flytja út og vista", "Efni", "Lotuauðkenni", "Tími", "Tilbúið", "{0} lotur hlaðnar", ", les efni…", "Hleð lotu…", "Upplýsingar", "Villa", "Útflutningi lokið", "Verkefnageymsla");
            Add("el", "Ελληνικά", "DSH Chat-History Manage — εργαλείο διαχείρισης ιστορικού συνομιλίας", "Αρχείο", "Επεξεργασία", "Γλώσσα", "Βοήθεια", "Σχετικά", "ΟΚ", "Φάκελος εξαγωγής:", "Αναζήτηση…", "Άνοιγμα φακέλου", "Ανανέωση λίστας", "Επιλογή αρχείου συνεδρίας…", "Εξαγωγή και αποθήκευση", "Θέμα", "Αναγνωριστικό συνεδρίας", "Ώρα", "Έτοιμο", "{0} συνεδρίες φορτώθηκαν", ", ανάγνωση θεμάτων…", "Φόρτωση συνεδρίας…", "Πληροφορίες", "Σφάλμα", "Η εξαγωγή ολοκληρώθηκε", "Αποθετήριο έργου");
            Add("cs", "Čeština", "DSH Chat-History Manage — nástroj pro správu historie chatu", "Soubor", "Úpravy", "Jazyk", "Nápověda", "O aplikaci", "OK", "Složka exportu:", "Procházet…", "Otevřít složku", "Obnovit seznam", "Vybrat soubor relace…", "Exportovat a uložit", "Téma", "ID relace", "Čas", "Připraveno", "Načteno {0} relací", ", čtení témat…", "Načítání relace…", "Informace", "Chyba", "Export dokončen", "Úložiště projektu");
            Add("sk", "Slovenčina", "DSH Chat-History Manage — nástroj na správu histórie chatu", "Súbor", "Upraviť", "Jazyk", "Pomocník", "O aplikácii", "OK", "Priečinok exportu:", "Prehľadávať…", "Otvoriť priečinok", "Obnoviť zoznam", "Vybrať súbor relácie…", "Exportovať a uložiť", "Téma", "ID relácie", "Čas", "Pripravené", "Načítané {0} relácií", ", čítanie tém…", "Načítava sa relácia…", "Informácie", "Chyba", "Export dokončený", "Úložisko projektu");
            Add("hu", "Magyar", "DSH Chat-History Manage — csevegési előzmények kezelő eszköze", "Fájl", "Szerkesztés", "Nyelv", "Súgó", "Névjegy", "OK", "Exportmappa:", "Tallózás…", "Mappa megnyitása", "Lista frissítése", "Munkamenet-fájl kiválasztása…", "Exportálás és mentés", "Téma", "Munkamenet-azonosító", "Idő", "Kész", "{0} munkamenet betöltve", ", témák olvasása…", "Munkamenet betöltése…", "Információ", "Hiba", "Az exportálás kész", "Projekt-tárhely");
            Add("ro", "Română", "DSH Chat-History Manage — instrument de gestionare a istoricului chat", "Fișier", "Editare", "Limbă", "Ajutor", "Despre", "OK", "Folder de export:", "Răsfoire…", "Deschide folder", "Actualizează lista", "Alege fișierul sesiunii…", "Exportă și salvează", "Subiect", "ID sesiune", "Oră", "Gata", "{0} sesiuni încărcate", ", citesc subiectele…", "Se încarcă sesiunea…", "Informații", "Eroare", "Export finalizat", "Depozitul proiectului");
            Add("bg", "Български", "DSH Chat-History Manage — инструмент за управление на чат историята", "Файл", "Редактиране", "Език", "Помощ", "Относно", "ОК", "Папка за експорт:", "Разглеждане…", "Отваряне на папка", "Обновяване на списъка", "Избор на файл на сесия…", "Експортиране и запазване", "Тема", "ID на сесия", "Час", "Готово", "Заредени {0} сесии", ", четене на теми…", "Зареждане на сесия…", "Информация", "Грешка", "Експортът завърши", "Хранилище на проекта");
            Add("sr", "Српски", "DSH Chat-History Manage — алат за управљање историјом ћаскања", "Датотека", "Уређивање", "Језик", "Помоћ", "О програму", "ОК", "Фасцикла за извоз:", "Преглед…", "Отвори фасциклу", "Освежи листу", "Изабери фајл сесије…", "Извези и сачувај", "Тема", "ID сесије", "Време", "Спремно", "{0} сесија учитано", ", читање тема…", "Учитавање сесије…", "Информација", "Грешка", "Извоз завршен", "Репозиторијум пројекта");
            Add("hr", "Hrvatski", "DSH Chat-History Manage — alat za upravljanje poviješću chata", "Datoteka", "Uređivanje", "Jezik", "Pomoć", "O programu", "OK", "Mapa izvoza:", "Pregled…", "Otvori mapu", "Osvježi popis", "Odaberi datoteku sesije…", "Izvezi i spremi", "Tema", "ID sesije", "Vrijeme", "Spremno", "Učitano {0} sesija", ", čitanje tema…", "Učitavanje sesije…", "Informacije", "Greška", "Izvoz dovršen", "Repozitorij projekta");
            Add("sl", "Slovenščina", "DSH Chat-History Manage — orodje za upravljanje zgodovine klepeta", "Datoteka", "Urejanje", "Jezik", "Pomoč", "O programu", "V redu", "Mapa za izvoz:", "Prebrskaj…", "Odpri mapo", "Osveži seznam", "Izberi datoteko seje…", "Izvozi in shrani", "Tema", "ID seje", "Čas", "Pripravljeno", "Naloženih {0} sej", ", branje tem…", "Nalaganje seje…", "Informacije", "Napaka", "Izvoz končan", "Skladišče projekta");
            Add("lt", "Lietuvių", "DSH Chat-History Manage — pokalbių istorijos valdymo įrankis", "Failas", "Redagavimas", "Kalba", "Žinynas", "Apie", "Gerai", "Eksporto aplankas:", "Naršyti…", "Atidaryti aplanką", "Atnaujinti sąrašą", "Pasirinkti sesijos failą…", "Eksportuoti ir išsaugoti", "Tema", "Sesijos ID", "Laikas", "Paruošta", "Įkelta {0} sesijų", ", skaitomos temos…", "Įkeliama sesija…", "Informacija", "Klaida", "Eksportas baigtas", "Projekto saugykla");
            Add("lv", "Latviešu", "DSH Chat-History Manage — tērzēšanas vēstures pārvaldības rīks", "Fails", "Rediģēt", "Valoda", "Palīdzība", "Par programmu", "Labi", "Eksporta mape:", "Pārlūkot…", "Atvērt mapi", "Atsvaidzināt sarakstu", "Izvēlēties sesijas failu…", "Eksportēt un saglabāt", "Tēma", "Sesijas ID", "Laiks", "Gatavs", "Ielādētas {0} sesijas", ", tiek lasītas tēmas…", "Notiek sesijas ielāde…", "Informācija", "Kļūda", "Eksports pabeigts", "Projekta krātuve");
            Add("et", "Eesti", "DSH Chat-History Manage — vestluse ajaloo haldamise tööriist", "Fail", "Redigeeri", "Keel", "Abi", "Teave", "OK", "Ekspordikaust:", "Sirvi…", "Ava kaust", "Värskenda loendit", "Vali seansi fail…", "Ekspordi ja salvesta", "Teema", "Seansi ID", "Kellaaeg", "Valmis", "Laaditud {0} seanssi", ", loetakse teemasid…", "Seansi laadimine…", "Teave", "Viga", "Eksport lõpetatud", "Projekti hoidla");
            Add("ca", "Català", "DSH Chat-History Manage — eina de gestió de l'historial de xat", "Fitxer", "Edita", "Idioma", "Ajuda", "Quant a", "D'acord", "Carpeta d'exportació:", "Navega…", "Obre la carpeta", "Actualitza la llista", "Tria el fitxer de sessió…", "Exporta i desa", "Tema", "ID de sessió", "Hora", "A punt", "{0} sessions carregades", ", llegint temes…", "Carregant la sessió…", "Informació", "Error", "Exportació completada", "Repositori del projecte");
            Add("gl", "Galego", "DSH Chat-History Manage — ferramenta de xestión do historial de chat", "Ficheiro", "Editar", "Idioma", "Axuda", "Acerca de", "Aceptar", "Cartafol de exportación:", "Examinar…", "Abrir cartafol", "Actualizar a lista", "Escoller ficheiro de sesión…", "Exportar e gardar", "Tema", "ID de sesión", "Hora", "Listo", "{0} sesións cargadas", ", lendo temas…", "Cargando sesión…", "Información", "Erro", "Exportación rematada", "Repositorio do proxecto");
            Add("eu", "Euskara", "DSH Chat-History Manage — txat-historiaren kudeaketa tresna", "Fitxategia", "Editatu", "Hizkuntza", "Laguntza", "Honi buruz", "Ados", "Esportazio-karpeta:", "Arakatu…", "Ireki karpeta", "Eguneratu zerrenda", "Hautatu saio-fitxategia…", "Esportatu eta gorde", "Gaia", "Saioaren IDa", "Ordua", "Prest", "{0} saio kargatu dira", ", gaiak irakurtzen…", "Saioa kargatzen…", "Informazioa", "Errorea", "Esportazioa amaituta", "Proiektuaren biltegia");
            Add("eo", "Esperanto", "DSH Chat-History Manage — ilo por administri babilejan historion", "Dosiero", "Redakti", "Lingvo", "Helpo", "Pri", "Bone", "Eksporta dosierujo:", "Foliumi…", "Malfermi dosierujon", "Refreŝigi liston", "Elekti sean dosieron…", "Eksporti kaj konservi", "Tema", "Sean ID", "Tempo", "Preta", "{0} seancoj ŝarĝitaj", ", legas temojn…", "Ŝarĝas seancon…", "Informo", "Eraro", "Eksporto finita", "Projekta deponejo");
            Add("bn", "বাংলা", "DSH Chat-History Manage — চ্যাট ইতিহাস ব্যবস্থাপনা টুল", "ফাইল", "সম্পাদনা", "ভাষা", "সহায়তা", "সম্পর্কে", "ঠিক আছে", "রপ্তানি ফোল্ডার:", "ব্রাউজ…", "ফোল্ডার খুলুন", "তালিকা রিফ্রেশ করুন", "সেশন ফাইল চয়ন করুন…", "রপ্তানি ও সংরক্ষণ", "বিষয়", "সেশন আইডি", "সময়", "প্রস্তুত", "{0}টি সেশন লোড হয়েছে", ", বিষয় পড়া হচ্ছে…", "সেশন লোড হচ্ছে…", "তথ্য", "ত্রুটি", "রপ্তানি সম্পন্ন", "প্রকল্প ভাণ্ডার");
            Add("ta", "தமிழ்", "DSH Chat-History Manage — அரட்டை வரலாறு மேலாண்மை கருவி", "கோப்பு", "திருத்து", "மொழி", "உதவி", "பற்றி", "சரி", "ஏற்றுமதி கோப்புறை:", "உலாவு…", "கோப்புறையைத் திற", "பட்டியலைப் புதுப்பி", "அமர்வுக் கோப்பைத் தேர்ந்தெடு…", "ஏற்றுமதி & சேமி", "தலைப்பு", "அமர்வு ஐடி", "நேரம்", "தயார்", "{0} அமர்வுகள் ஏற்றப்பட்டன", ", தலைப்புகள் படிக்கப்படுகின்றன…", "அமர்வு ஏற்றப்படுகிறது…", "தகவல்", "பிழை", "ஏற்றுமதி முடிந்தது", "திட்டக் களஞ்சியம்");
            Add("te", "తెలుగు", "DSH Chat-History Manage — చాట్ చరిత్ర నిర్వహణ సాధనం", "ఫైల్", "సవరించు", "భాష", "సహాయం", "గురించి", "సరే", "ఎగుమతి ఫోల్డర్:", "బ్రౌజ్…", "ఫోల్డర్ తెరువు", "జాబితా రిఫ్రెష్ చేయి", "సెషన్ ఫైల్ ఎంచుకోండి…", "ఎగుమతి & సేవ్", "అంశం", "సెషన్ ఐడి", "సమయం", "సిద్ధం", "{0} సెషన్లు లోడ్ అయ్యాయి", ", అంశాలు చదువుతున్నాయి…", "సెషన్ లోడ్ అవుతోంది…", "సమాచారం", "లోపం", "ఎగుమతి పూర్తయింది", "ప్రాజెక్ట్ రిపోజిటరీ");
            Add("kn", "ಕನ್ನಡ", "DSH Chat-History Manage — ಚಾಟ್ ಇತಿಹಾಸ ನಿರ್ವಹಣಾ ಸಾಧನ", "ಫೈಲ್", "ಸಂಪಾದಿಸು", "ಭಾಷೆ", "ಸಹಾಯ", "ಬಗ್ಗೆ", "ಸರಿ", "ರಫ್ತು ಫೋಲ್ಡರ್:", "ಬ್ರೌಸ್…", "ಫೋಲ್ಡರ್ ತೆರೆಯಿರಿ", "ಪಟ್ಟಿಯನ್ನು ರಿಫ್ರೆಶ್ ಮಾಡಿ", "ಸೆಶನ್ ಫೈಲ್ ಆಯ್ಕೆಮಾಡಿ…", "ರಫ್ತು ಮತ್ತು ಉಳಿಸಿ", "ವಿಷಯ", "ಸೆಶನ್ ಐಡಿ", "ಸಮಯ", "ಸಿದ್ಧ", "{0} ಸೆಶನ್ಗಳು ಲೋಡ್ ಆಗಿವೆ", ", ವಿಷಯಗಳನ್ನು ಓದಲಾಗುತ್ತಿದೆ…", "ಸೆಶನ್ ಲೋಡ್ ಆಗುತ್ತಿದೆ…", "ಮಾಹಿತಿ", "ದೋಷ", "ರಫ್ತು ಪೂರ್ಣಗೊಂಡಿದೆ", "ಪ್ರಾಜೆಕ್ಟ್ ರಿಪಾಸಿಟರಿ");
            Add("ml", "മലയാളം", "DSH Chat-History Manage — ചാറ്റ് ചരിത്ര മാനേജ്മെന്റ് ടൂൾ", "ഫയൽ", "തിരുത്തുക", "ഭാഷ", "സഹായം", "കുറിച്ച്", "ശരി", "എക്സ്പോർട്ട് ഫോൾഡർ:", "ബ്രൗസ്…", "ഫോൾഡർ തുറക്കുക", "പട്ടിക പുതുക്കുക", "സെഷൻ ഫയൽ തിരഞ്ഞെടുക്കുക…", "എക്സ്പോർട്ട് ചെയ്ത് സംരക്ഷിക്കുക", "വിഷയം", "സെഷൻ ഐഡി", "സമയം", "തയ്യാറാണ്", "{0} സെഷനുകൾ ലോഡ് ചെയ്തു", ", വിഷയങ്ങൾ വായിക്കുന്നു…", "സെഷൻ ലോഡ് ചെയ്യുന്നു…", "വിവരം", "പിശക്", "എക്സ്പോർട്ട് പൂർത്തിയായി", "പ്രോജക്ട് ശേഖരം");
            Add("mr", "मराठी", "DSH Chat-History Manage — चॅट इतिहास व्यवस्थापन साधन", "फाइल", "संपादन", "भाषा", "मदत", "बद्दल", "ठीक आहे", "निर्यात फोल्डर:", "ब्राउझ करा…", "फोल्डर उघडा", "यादी रीफ्रेश करा", "सत्र फाइल निवडा…", "निर्यात आणि जतन करा", "विषय", "सत्र आयडी", "वेळ", "तयार", "{0} सत्र लोड केली", ", विषय वाचत आहे…", "सत्र लोड होत आहे…", "माहिती", "त्रुटी", "निर्यात पूर्ण", "प्रकल्प भांडार");
            Add("ne", "नेपाली", "DSH Chat-History Manage — च्याट इतिहास व्यवस्थापन उपकरण", "फाइल", "सम्पादन", "भाषा", "मदत", "बारेमा", "ठीक छ", "निर्यात फोल्डर:", "ब्राउज…", "फोल्डर खोल्नुहोस्", "सूची रिफ्रेस गर्नुहोस्", "सत्र फाइल छान्नुहोस्…", "निर्यात र सुरक्षित गर्नुहोस्", "विषय", "सत्र आईडी", "समय", "तयार", "{0} सत्र लोड भयो", ", विषय पढ्दै…", "सत्र लोड हुँदैछ…", "जानकारी", "त्रुटि", "निर्यात सम्पन्न", "परियोजना भण्डार");
            Add("si", "සිංහල", "DSH Chat-History Manage — කතාබස් ඉතිහාස කළමනාකරණ මෙවලම", "ගොනුව", "සංස්කරණය", "භාෂාව", "උදව්", "ගැන", "හරි", "අපනයන ෆෝල්ඩරය:", "බ්‍රවුස්…", "ෆෝල්ඩරය විවෘත කරන්න", "ලැයිස්තුව refresh කරන්න", "සැසි ගොනුව තෝරන්න…", "අපනයනය සහ සුරකින්න", "මාතෘකාව", "සැසි අංකය", "වේලාව", "සූදානම්", "සැසි {0} පටවා ඇත", ", මාතෘකා කියවමින්…", "සැසිය පූරණය වෙමින්…", "තොරතුරු", "දෝෂය", "අපනයනය සම්පූර්ණයි", "ව්‍යාපෘති ගබඩාව");
            Add("my", "မြန်မာ", "DSH Chat-History Manage — ချက်တင်မှတ်တမ်း စီမံခန့်ခွဲရေးကိရိယာ", "ဖိုင်", "တည်းဖြတ်", "ဘာသာစကား", "အကူအညီ", "အကြောင်း", "အိုကေ", "ထုတ်ယူမည့်ဖိုင်တွဲ:", "ရှာဖွေ…", "ဖိုင်တွဲဖွင့်", "စာရင်းပြန်ဆန်း", "ဆက်ရှင်ဖိုင်ရွေး…", "ထုတ်ယူ၍သိမ်းရန်", "ခေါင်းစဉ်", "ဆက်ရှင် ID", "အချိန်", "အသင့်", "{0} ဆက်ရှင်တင်ပြီး", ", ခေါင်းစဉ်များဖတ်နေသည်…", "ဆက်ရှင်တင်နေသည်…", "အချက်အလက်", "အမှား", "ထုတ်ယူမှုပြီးပြီ", "ပရောဂျက်သိုလှောင်ရာ");
            Add("km", "ខ្មែរ", "DSH Chat-History Manage — ឧបករណ៍គ្រប់គ្រងប្រវត្តិជជែក", "ឯកសារ", "កែសម្រួល", "ភាសា", "ជំនួយ", "អំពី", "យល់ព្រម", "ថតនាំចេញ:", "រកមើល…", "បើកថត", "ធ្វើឱ្យបញ្ជីស្រស់", "ជ្រើសឯកសារវគ្គ…", "នាំចេញ និងរក្សាទុក", "ប្រធានបទ", "លេខសម្គាល់វគ្គ", "ពេលវេលា", "រួចរាល់", "វគ្គ {0} បានផ្ទុក", ", កំពុងអានប្រធានបទ…", "កំពុងផ្ទុកវគ្គ…", "ព័ត៌មាន", "កំហុស", "ការនាំចេញបានបញ្ចប់", "ឃ្លាំងគម្រោង");
            Add("fa", "فارسی", "DSH Chat-History Manage — ابزار مدیریت تاریخچه چت", "پرونده", "ویرایش", "زبان", "راهنما", "درباره", "تأیید", "پوشه خروجی:", "مرور…", "باز کردن پوشه", "به‌روزرسانی فهرست", "انتخاب فایل نشست…", "خروجی و ذخیره", "موضوع", "شناسه نشست", "زمان", "آماده", "{0} نشست بارگذاری شد", "، خواندن موضوع‌ها…", "در حال بارگذاری نشست…", "اطلاعات", "خطا", "خروجی تکمیل شد", "مخزن پروژه");
            Add("he", "עברית", "DSH Chat-History Manage — כלי ניהול היסטוריית צ'אט", "קובץ", "עריכה", "שפה", "עזרה", "אודות", "אישור", "תיקיית ייצוא:", "עיון…", "פתח תיקייה", "רענן רשימה", "בחר קובץ שיחה…", "ייצוא ושמירה", "נושא", "מזהה שיחה", "שעה", "מוכן", "{0} שיחות נטענו", ", קורא נושאים…", "טוען שיחה…", "מידע", "שגיאה", "הייצוא הושלם", "מאגר הפרויקט");
            Add("az", "Azərbaycanca", "DSH Chat-History Manage — çat tarixi idarəetmə aləti", "Fayl", "Redaktə", "Dil", "Kömək", "Haqqında", "OK", "İxrac qovluğu:", "Gözdən keçir…", "Qovluğu aç", "Siyahını yenilə", "Sessiya faylı seç…", "İxrac et və saxla", "Mövzu", "Sessiya ID", "Vaxt", "Hazır", "{0} sessiya yükləndi", ", mövzular oxunur…", "Sessiya yüklənir…", "Məlumat", "Xəta", "İxrac tamamlandı", "Layihə deposu");
            Add("kk", "Қазақша", "DSH Chat-History Manage — чат тарихын басқару құралы", "Файл", "Өңдеу", "Тіл", "Көмек", "Бағдарлама туралы", "ОК", "Экспорт қалтасы:", "Шолу…", "Қалтаны ашу", "Тізімді жаңарту", "Сессия файлын таңдау…", "Экспорттау және сақтау", "Тақырып", "Сессия ID", "Уақыт", "Дайын", "{0} сессия жүктелді", ", тақырыптар оқылуда…", "Сессия жүктелуде…", "Ақпарат", "Қате", "Экспорт аяқталды", "Жоба репозиторийі");
            Add("uz", "O'zbekcha", "DSH Chat-History Manage — chat tarixini boshqarish vositasi", "Fayl", "Tahrirlash", "Til", "Yordam", "Haqida", "OK", "Eksport papkasi:", "Ko'rib chiqish…", "Papkani ochish", "Ro'yxatni yangilash", "Sessiya faylini tanlash…", "Eksport qilish va saqlash", "Mavzu", "Sessiya ID", "Vaqt", "Tayyor", "{0} sessiya yuklandi", ", mavzular o'qilmoqda…", "Sessiya yuklanmoqda…", "Ma'lumot", "Xato", "Eksport tugadi", "Loyiha ombori");
            Add("mn", "Монгол", "DSH Chat-History Manage — чатын түүхийг удирдах хэрэгсэл", "Файл", "Засварлах", "Хэл", "Тусламж", "Тухай", "Болсон", "Экспорт хавтас:", "Үзэх…", "Хавтас нээх", "Жагсаалт сэргээх", "Сессийн файл сонгох…", "Экспорт хийх ба хадгалах", "Сэдэв", "Сессийн ID", "Цаг", "Бэлэн", "{0} сесс ачаалагдлаа", ", сэдвүүд уншигдаж байна…", "Сесс ачаалагдаж байна…", "Мэдээлэл", "Алдаа", "Экспорт дууслаа", "Төслийн агуулах");
            Add("ka", "ქართული", "DSH Chat-History Manage — ჩატის ისტორიის მართვის ინსტრუმენტი", "ფაილი", "რედაქტირება", "ენა", "დახმარება", "პროგრამის შესახებ", "OK", "ექსპორტის საქაღალდე:", "დათვალიერება…", "საქაღალდის გახსნა", "სიის განახლება", "სესიის ფაილის არჩევა…", "ექსპორტი და შენახვა", "თემა", "სესიის ID", "დრო", "მზადაა", "{0} სესია ჩაიტვირთა", ", თემების კითხვა…", "სესიის ჩატვირთვა…", "ინფორმაცია", "შეცდომა", "ექსპორტი დასრულდა", "პროექტის საცავი");
            Add("hy", "Հայերեն", "DSH Chat-History Manage — զրույցի պատմության կառավարման գործիք", "Ֆայլ", "Խմբագրել", "Լեզու", "Օգնություն", "Ծրագրի մասին", "OK", "Արտահանման թղթապանակ:", "Դիտել…", "Բացել թղթապանակ", "Թարմացնել ցուցակը", "Ընտրել նստաշրջանի ֆայլ…", "Արտահանել և պահել", "Թեմա", "Նստաշրջանի ID", "Ժամանակ", "Պատրաստ է", "Բեռնված է {0} նստաշրջան", ", թեմաների ընթերցում…", "Նստաշրջանի բեռնում…", "Տեղեկություն", "Սխալ", "Արտահանումն ավարտված է", "Նախագծի պահոց");
            Add("sw", "Kiswahili", "DSH Chat-History Manage — chombo cha kusimamia historia ya gumzo", "Faili", "Hariri", "Lugha", "Msaada", "Kuhusu", "Sawa", "Folda ya kupeleka:", "Vinjari…", "Fungua folda", "Burudisha orodha", "Chagua faili la kikao…", "Peleka na uhifadhi", "Mada", "Kitambulisho cha kikao", "Wakati", "Tayari", "{0} vikao vimepakiwa", ", inasoma mada…", "Inapakia kikao…", "Taarifa", "Hitilafu", "Usafirishaji umekamilika", "Hifadhi ya mradi");
            Add("af", "Afrikaans", "DSH Chat-History Manage — hulpmiddel vir bestuur van kletsgeskiedenis", "Lêer", "Wysig", "Taal", "Hulp", "Oor", "OK", "Uitvoer-gids:", "Blaai…", "Maak gids oop", "Verfris lys", "Kies sessielêer…", "Voer uit en stoor", "Onderwerp", "Sessie-ID", "Tyd", "Gereed", "{0} sessies gelaai", ", lees onderwerpe…", "Laai sessie…", "Inligting", "Fout", "Uitvoer voltooi", "Projekbewaarplek");
            Add("fil", "Filipino", "DSH Chat-History Manage — tool sa pamamahala ng kasaysayan ng chat", "File", "I-edit", "Wika", "Tulong", "Tungkol", "OK", "Folder ng pag-export:", "Mag-browse…", "Buksan ang folder", "I-refresh ang listahan", "Pumili ng file ng session…", "I-export at i-save", "Paksa", "Session ID", "Oras", "Handa", "{0} session na na-load", ", nagbabasa ng mga paksa…", "Naglo-load ng session…", "Impormasyon", "Error", "Tapos na ang pag-export", "Repository ng proyekto");
        }

        private static void Add(string code, string native, params string[] vals)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            for (int i = 0; i < CoreKeys.Length && i < vals.Length; i++)
                d[CoreKeys[i]] = vals[i];
            Languages.Add(new Language(code, native, d));
        }

        public static Language ByCode(string code)
        {
            foreach (Language l in Languages)
                if (l.Code == code) return l;
            return null;
        }

        public static string T(string key)
        {
            string v;
            Language cur = ByCode(Current);
            if (cur != null && cur.Dict.TryGetValue(key, out v)) return v;
            Language en = ByCode("en");
            if (en != null && en.Dict.TryGetValue(key, out v)) return v;
            return key;
        }
    }

    // ---------- 预览控件：自绘视图，工具调用块可点击折叠/展开 ----------
    // 字体方案：重点（标题）用黑体 SimHei，正文用微软雅黑 Microsoft YaHei UI
    class PreviewView : ScrollableControl
    {
        private enum BlockKind { Heading, SubHeading, Para, Tool }

        private class Block
        {
            public BlockKind Kind;
            public string Text;    // 要绘制的文本（已清洗）
            public string Header;  // 工具块的折叠头（无箭头，如 "pwsh — OK"）
            public bool Collapsed; // 工具块是否折叠
            public bool UseGdi;    // 含代理对 emoji → 用 GDI 绘制（保真字形）；否则 GDI+（快且 ClearType 清晰）
            public Font Font;
            public Color Color;
            public int Y;
            public int Height;
            public int HeaderHeight; // 工具块头部行高（可点击区域）
            public int DetailHeight; // 工具块细节行高（展开时显示）
        }

        private List<Block> blocks = new List<Block>();
        private string fullText = ""; // 完整转录（供复制）
        private string lastMd;        // 上次解析的显示文本：重复点击同一会话时复用块（含折叠状态）
        private bool blocksDirty = true; // 块内容变化后需重测高度
        private int layoutWidth = -1;
        private const int PadX = 10;
        private const int MaxBlockChars = 6000; // 显示截断上限：限制 CJK 换行布局成本
        private Font fBase, fHead, fToolHead, fToolDetail;

        public PreviewView()
        {
            BackColor = Color.White;
            AutoScroll = true;
            DoubleBuffered = true;
            fHead = new Font("SimHei", 11f);                              // 黑体：重点/标题
            fBase = new Font("Microsoft YaHei UI", 9.5f);                 // 微软雅黑：正文
            fToolHead = new Font("Microsoft YaHei UI", 9f, FontStyle.Italic);
            fToolDetail = new Font("Microsoft YaHei UI", 8.5f);
        }

        /// <summary>displayMd 用于显示（可能截断），fullMd 是完整转录（供复制）。</summary>
        public void SetContent(string displayMd, string fullMd)
        {
            fullText = fullMd ?? displayMd;
            string md = displayMd ?? "";
            if (md != lastMd)
            {
                lastMd = md;
                blocks.Clear();
                BuildBlocks(md);
                blocksDirty = true; // 新块需要测量
            }
            // 同一会话再次点击：块已缓存（含折叠状态），宽度未变则连测量都跳过
            Relayout();
            AutoScrollPosition = new Point(0, 0);
            Invalidate();
        }

        /// <summary>把完整转录复制到剪贴板。</summary>
        public void CopyFull()
        {
            try { Clipboard.SetText(fullText); } catch { }
        }

        public string FullText { get { return fullText; } }

        // ---------- 解析转录为块 ----------
        private void BuildBlocks(string md)
        {
            string[] lines = md.Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) { i++; continue; }
                if (t.StartsWith("## "))
                {
                    string body = t.Substring(3);
                    Color c = Color.FromArgb(31, 78, 121); // 用户轮次：蓝
                    if (body.StartsWith("⏳")) c = Color.FromArgb(178, 107, 0); // 压缩：橙
                    else if (body.StartsWith("❌")) c = Color.FromArgb(192, 0, 0); // 错误：红
                    blocks.Add(MakeBlock(BlockKind.Heading, CapText(Sanitize(body)), c, fHead));
                    i++;
                }
                else if (t.StartsWith("### "))
                {
                    blocks.Add(MakeBlock(BlockKind.SubHeading, CapText(Sanitize(t.Substring(4))), Color.FromArgb(46, 125, 50), fHead)); // 助手：绿（黑体重点）
                    i++;
                }
                else if (t.StartsWith("> "))
                {
                    // 连续工具行归为一个可折叠块
                    StringBuilder sb = new StringBuilder();
                    string name = null;
                    string status = null;
                    while (i < lines.Length)
                    {
                        string l2 = lines[i].Trim();
                        if (!l2.StartsWith("> ")) break;
                        string body = l2.Substring(2).TrimStart();
                        if (body.StartsWith("🔧")) { if (name == null) name = FirstToken(body, "🔧"); } // 保留 emoji，仅提取工具名
                        else if (body.StartsWith("📦")) { if (status == null) status = body.StartsWith("📦 OK") ? "OK" : "ERROR"; }
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(Sanitize(body));
                        i++;
                    }
                    Block b = MakeBlock(BlockKind.Tool, CapText(sb.ToString()), Color.FromArgb(110, 110, 110), fToolDetail);
                    b.Header = (name ?? "tool") + (status != null ? " — " + status : "");
                    b.Collapsed = true; // 默认折叠，点击展开
                    blocks.Add(b);
                }
                else
                {
                    // 连续普通行合并为一个段落
                    StringBuilder sb = new StringBuilder();
                    while (i < lines.Length)
                    {
                        string l2 = lines[i].Trim();
                        if (l2.Length == 0 || l2.StartsWith("## ") || l2.StartsWith("### ") || l2.StartsWith("> ")) break;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(l2);
                        i++;
                    }
                    blocks.Add(MakeBlock(BlockKind.Para, CapText(Sanitize(sb.ToString())), Color.Black, fBase));
                }
            }
        }

        /// <summary>超长块显示截断（预览封顶；导出文件始终为完整内容）。</summary>
        private static string CapText(string s)
        {
            if (s != null && s.Length > MaxBlockChars)
                return s.Substring(0, MaxBlockChars) + "\n" + Lang.T("previewTruncated");
            return s;
        }

        /// <summary>文本是否包含代理对 emoji（GDI+ 无法经字体回退渲染，需走 GDI）。</summary>
        private static bool HasSurrogateEmoji(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (char.IsHighSurrogate(s[i])) return true;
            return false;
        }

        private static Block MakeBlock(BlockKind kind, string text, Color color, Font font)
        {
            Block b = new Block();
            b.Kind = kind;
            b.Text = text;
            b.Color = color;
            b.Font = font;
            b.UseGdi = HasSurrogateEmoji(text); // 含 emoji 的块用 GDI 绘制保真字形
            return b;
        }

        private static string FirstToken(string s, string prefix)
        {
            string r = s.Substring(prefix.Length).TrimStart();
            int end = r.IndexOfAny(new char[] { ' ', '{', '\n' });
            return end < 0 ? r : r.Substring(0, end);
        }

        /// <summary>清洗：仅去除控制字符，保留 emoji（GDI 字体链接可渲染真字形）。</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 32 && c != '\t' && c != '\n') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------- 布局 ----------
        // 测量用 GDI+（MeasureString 对 CJK 换行比 GDI 快 50 倍以上），结果缓存在块上，宽度未变不重测。
        // 绘制直接画到屏幕（保留 ClearType 亚像素渲染，边缘清晰、无位图往返的乱码问题）：
        // 含 emoji 的块用 GDI（保真字形），其余用 GDI+（CJK 布局快）。
        private void Relayout()
        {
            int w = Math.Max(60, ClientSize.Width - PadX * 2);
            if (Math.Abs(w - layoutWidth) > 6 || blocksDirty) // 宽度变化或新块才重测；折叠切换只重排位置
            {
                layoutWidth = w;
                blocksDirty = false;
                using (Graphics g = CreateGraphics())
                {
                    foreach (Block b in blocks)
                    {
                        if (b.Kind == BlockKind.Tool)
                        {
                            b.HeaderHeight = MeasureH(g, (b.Collapsed ? "▶ " : "▼ ") + b.Header, fToolHead, w);
                            b.DetailHeight = MeasureH(g, b.Text, fToolDetail, w);
                        }
                        else
                        {
                            b.Height = MeasureH(g, b.Text, b.Font, w);
                        }
                    }
                }
            }
            int y = 4;
            foreach (Block b in blocks)
            {
                b.Y = y;
                if (b.Kind == BlockKind.Tool)
                    b.Height = b.HeaderHeight + (b.Collapsed ? 0 : b.DetailHeight);
                y += b.Height;
            }
            AutoScrollMinSize = new Size(0, y + 8);
        }

        private static int MeasureH(Graphics g, string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return font.Height + 10;
            SizeF sz = g.MeasureString(text, font, new SizeF(width, 1000000f));
            return (int)Math.Ceiling(sz.Height) + 10; // +10 安全边距
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int scrollY = -AutoScrollPosition.Y;
            int viewH = ClientSize.Height;
            Graphics g = e.Graphics;
            foreach (Block b in blocks)
            {
                if (b.Y + b.Height < scrollY) continue;
                if (b.Y > scrollY + viewH) break;
                int by = b.Y - scrollY;
                if (b.Kind == BlockKind.Tool)
                {
                    string head = (b.Collapsed ? "▶ " : "▼ ") + b.Header;
                    DrawBlock(g, head, fToolHead, new RectangleF(PadX, by, layoutWidth, b.HeaderHeight), b.Color, b.UseGdi);
                    if (!b.Collapsed)
                    {
                        DrawBlock(g, b.Text, fToolDetail, new RectangleF(PadX + 12, by + b.HeaderHeight, layoutWidth - 12, b.DetailHeight), b.Color, b.UseGdi);
                    }
                }
                else
                {
                    DrawBlock(g, b.Text, b.Font, new RectangleF(PadX, by, layoutWidth, b.Height), b.Color, b.UseGdi);
                }
            }
        }

        private static void DrawBlock(Graphics g, string text, Font font, RectangleF rect, Color color, bool useGdi)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (useGdi)
            {
                TextRenderer.DrawText(g, text, font, Rectangle.Ceiling(rect), color,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
            else
            {
                using (SolidBrush brush = new SolidBrush(color))
                    g.DrawString(text, font, brush, rect);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (blocks.Count > 0) { Relayout(); Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int y = e.Y - AutoScrollPosition.Y;
            foreach (Block b in blocks)
            {
                if (y >= b.Y && y < b.Y + b.Height)
                {
                    if (b.Kind == BlockKind.Tool && y < b.Y + b.HeaderHeight)
                    {
                        b.Collapsed = !b.Collapsed; // 点击折叠头展开/收起
                        Relayout();
                        Invalidate();
                    }
                    return;
                }
            }
        }
    }

    // ---------- 关于对话框 ----------
    class AboutForm : Form
    {
        public AboutForm()
        {
            Text = Lang.T("aboutTitle");
            Font = new Font("Microsoft YaHei UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 260);

            Label name = new Label();
            name.Text = "DSH Chat-History Manage";
            name.Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
            name.AutoSize = true;
            name.Location = new Point(24, 20);

            Label ver = new Label();
            ver.Text = Lang.T("aboutVersion");
            ver.AutoSize = true;
            ver.Location = new Point(24, 56);

            Label desc = new Label();
            desc.Text = Lang.T("aboutDesc");
            desc.Location = new Point(24, 84);
            desc.Size = new Size(382, 70);
            desc.TextAlign = ContentAlignment.TopLeft;

            LinkLabel gh = new LinkLabel();
            gh.Text = "GitHub: WJhsi/DSH-Chat-History-Export"; // 短格式，避免溢出
            gh.AutoSize = true;
            gh.LinkBehavior = LinkBehavior.HoverUnderline;
            gh.Location = new Point(24, 160);
            ToolTip ghTip = new ToolTip();
            ghTip.SetToolTip(gh, "https://github.com/WJhsi/DSH-Chat-History-Export");
            gh.LinkClicked += delegate { Open("https://github.com/WJhsi/DSH-Chat-History-Export"); };

            LinkLabel site = new LinkLabel();
            site.Text = "www.hsij.cn";
            site.AutoSize = true;
            site.LinkBehavior = LinkBehavior.HoverUnderline;
            site.Location = new Point(24, 184);
            site.LinkClicked += delegate { Open("https://www.hsij.cn"); };

            Button ok = new Button();
            ok.Text = Lang.T("aboutOk");
            ok.DialogResult = DialogResult.OK;
            ok.Width = 90;
            ok.Height = 30;
            ok.Location = new Point(ClientSize.Width - ok.Width - 24, ClientSize.Height - ok.Height - 20);
            AcceptButton = ok;

            Controls.Add(name);
            Controls.Add(ver);
            Controls.Add(desc);
            Controls.Add(gh);
            Controls.Add(site);
            Controls.Add(ok);
        }

        private static void Open(string url)
        {
            try { Process.Start(url); } catch { }
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
        private PreviewView preview;
        private SplitContainer split; // 左列表 / 右预览分割（左宽随列宽自适应）
        private ToolStripStatusLabel lnkGithub, lnkSite; // 右下角链接（蓝色下划线，点击打开）
        private StatusStrip status;
        private ToolStripStatusLabel statusLabel;
        private ToolStripProgressBar progressBar; // 会话加载进度条
        private int loadToken; // 加载代数：切换会话时作废旧加载
        private List<SessionInfo> sessions = new List<SessionInfo>();
        private string pickedFile;
        private Label titleLabel, lbDir;
        // 菜单栏（文件 / 编辑 / 语言）
        private MenuStrip menu;
        private ToolStripMenuItem mFile, mEdit, mLang, mHelp;
        private ToolStripMenuItem filePick, fileRefresh, fileExport, fileExit;
        private ToolStripMenuItem editCopy, editClearCache;
        private List<ToolStripMenuItem> langItems = new List<ToolStripMenuItem>();
        private ToolStripMenuItem aboutItem;
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
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dsh-chat-history-manage.config.json"); }
        }

        private string CachePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dsh-chat-history-manage.titles.json"); }
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
            // 默认窗口按屏幕自适应放大（目标 1440x840，不超过工作区）
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int defW = Math.Min(1440, Math.Max(1024, wa.Width - 80));
            int defH = Math.Min(840, Math.Max(600, wa.Height - 80));
            ClientSize = new Size(defW, defH);
            MinimumSize = new Size(Math.Min(1100, defW), Math.Min(640, defH));
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
            split.FixedPanel = FixedPanel.Panel1; // 左侧列表宽度由 FitLeftPanel 自适应，右侧预览拿剩余空间
            split.SplitterDistance = 640;
            split.Panel1MinSize = 480;
            this.split = split;

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.Columns.Add(Lang.T("colTopic"), 190);
            list.Columns.Add(Lang.T("colId"), 200);
            list.Columns.Add(Lang.T("colTime"), 110);
            split.Panel1.Controls.Add(list);

            preview = new PreviewView();
            preview.Dock = DockStyle.Fill;
            split.Panel2.Controls.Add(preview);

            root.Controls.Add(split, 0, 3);
            Controls.Add(root);

            status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            statusLabel.Text = Lang.T("statusReady");
            statusLabel.TextAlign = ContentAlignment.MiddleLeft; // 文字靠左（ToolStripStatusLabel 默认居中，必须显式设置）
            statusLabel.Spring = true; // 占满剩余宽度，把右侧链接推到右下角
            status.Items.Add(statusLabel);
            progressBar = new ToolStripProgressBar();
            progressBar.Width = 140;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Visible = false;
            status.Items.Add(progressBar);

            // 右下角链接：用普通 ToolStripStatusLabel 伪装链接（蓝色+下划线+点击事件），
            // 比 ToolStripControlHost(LinkLabel) 渲染可靠得多
            lnkGithub = new ToolStripStatusLabel();
            lnkGithub.Text = Lang.T("linkGithub");
            lnkGithub.ForeColor = Color.FromArgb(0, 102, 204);
            lnkGithub.Font = new Font(Font, FontStyle.Underline);
            lnkGithub.Margin = new Padding(8, 0, 4, 0);
            lnkGithub.ToolTipText = "https://github.com/WJhsi/DSH-Chat-History-Export";
            lnkGithub.Click += delegate { OpenUrl("https://github.com/WJhsi/DSH-Chat-History-Export"); };
            lnkGithub.MouseEnter += delegate { status.Cursor = Cursors.Hand; };  // 悬停变手型（选择光标）
            lnkGithub.MouseLeave += delegate { status.Cursor = Cursors.Default; };
            lnkSite = new ToolStripStatusLabel();
            lnkSite.Text = "www.hsij.cn";
            lnkSite.ForeColor = Color.FromArgb(0, 102, 204);
            lnkSite.Font = new Font(Font, FontStyle.Underline);
            lnkSite.Margin = new Padding(4, 0, 10, 0);
            lnkSite.ToolTipText = "https://www.hsij.cn";
            lnkSite.Click += delegate { OpenUrl("https://www.hsij.cn"); };
            lnkSite.MouseEnter += delegate { status.Cursor = Cursors.Hand; };
            lnkSite.MouseLeave += delegate { status.Cursor = Cursors.Default; };
            status.Items.Add(lnkGithub);
            status.Items.Add(lnkSite);
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
            editCopy = new ToolStripMenuItem(Lang.T("editCopy"), null, delegate { CopyTranscript(); });
            editCopy.ShortcutKeys = Keys.Control | Keys.C;
            editClearCache = new ToolStripMenuItem(Lang.T("editClearCache"), null, delegate { ClearTitleCache(); });
            mEdit.DropDownItems.Add(editCopy);
            mEdit.DropDownItems.Add(new ToolStripSeparator());
            mEdit.DropDownItems.Add(editClearCache);

            mLang = new ToolStripMenuItem(Lang.T("menuLang"));
            ToolStripMenuItem grpCommon = new ToolStripMenuItem(Lang.T("langGroupCommon"));
            ToolStripMenuItem grpEurope = new ToolStripMenuItem(Lang.T("langGroupEurope"));
            ToolStripMenuItem grpAsia = new ToolStripMenuItem(Lang.T("langGroupAsia"));
            ToolStripMenuItem grpAfrica = new ToolStripMenuItem(Lang.T("langGroupAfrica"));
            foreach (string code in new[] { "zh", "en", "zh-TW", "ja", "ko", "fr", "de", "es", "pt", "ru", "it", "nl", "pl", "uk", "tr", "th", "vi", "id", "ms", "hi", "ar", "sv" })
                grpCommon.DropDownItems.Add(MakeLangItem(code));
            foreach (string code in new[] { "da", "no", "fi", "is", "el", "cs", "sk", "hu", "ro", "bg", "sr", "hr", "sl", "lt", "lv", "et", "ca", "gl", "eu", "eo" })
                grpEurope.DropDownItems.Add(MakeLangItem(code));
            foreach (string code in new[] { "bn", "ta", "te", "kn", "ml", "mr", "ne", "si", "my", "km", "fa", "he", "az", "kk", "uz", "mn", "ka", "hy" })
                grpAsia.DropDownItems.Add(MakeLangItem(code));
            foreach (string code in new[] { "sw", "af", "fil" })
                grpAfrica.DropDownItems.Add(MakeLangItem(code));
            mLang.DropDownItems.Add(grpCommon);
            mLang.DropDownItems.Add(grpEurope);
            mLang.DropDownItems.Add(grpAsia);
            mLang.DropDownItems.Add(grpAfrica);

            mHelp = new ToolStripMenuItem(Lang.T("menuHelp"));
            aboutItem = new ToolStripMenuItem(Lang.T("about"), null, delegate { ShowAbout(); });
            mHelp.DropDownItems.Add(aboutItem);

            menu.Items.Add(mFile);
            menu.Items.Add(mEdit);
            menu.Items.Add(mLang);
            menu.Items.Add(mHelp);
            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        /// <summary>弹出「关于」对话框。</summary>
        private void ShowAbout()
        {
            using (AboutForm f = new AboutForm())
            {
                f.ShowDialog(this);
            }
        }

        /// <summary>创建一个语言菜单项（原生语言名显示，点击切换语言）。</summary>
        private ToolStripMenuItem MakeLangItem(string code)
        {
            Lang.Language l = Lang.ByCode(code);
            ToolStripMenuItem it = new ToolStripMenuItem(l != null ? l.NativeName : code);
            it.Tag = code;
            it.Click += delegate { ApplyLanguage(code); };
            langItems.Add(it);
            return it;
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
            editClearCache.Text = Lang.T("editClearCache");
            mLang.Text = Lang.T("menuLang");
            foreach (ToolStripMenuItem it in langItems)
                it.Checked = (string)it.Tag == lang;
            mHelp.Text = Lang.T("menuHelp");
            aboutItem.Text = Lang.T("about");
            if (lnkGithub != null)
            {
                lnkGithub.Text = Lang.T("linkGithub");
                status.PerformLayout();
            }
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

        /// <summary>复制完整转录到剪贴板。</summary>
        private void CopyTranscript()
        {
            try
            {
                if (preview.FullText.Length == 0) return;
                Clipboard.SetText(preview.FullText);
                statusLabel.Text = Lang.T("statusCopied");
            }
            catch { }
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
        /// 按「表头 + 列内最宽内容」自适应列宽，并让左面板跟随三列总宽（主题 / 会话 ID / 时间永远完整可见，不被预览框遮挡）。
        /// 窗口有空间就加宽左面板；空间不足则优先压缩主题列，保证三列都在可视区内。
        /// </summary>
        private void ResizeColumns()
        {
            try
            {
                using (Graphics g = list.CreateGraphics())
                {
                    int[] w = new int[list.Columns.Count];
                    for (int c = 0; c < list.Columns.Count; c++)
                    {
                        int width = TextRenderer.MeasureText(g, list.Columns[c].Text, list.Font).Width + 20;
                        foreach (ListViewItem it in list.Items)
                        {
                            string txt = c < it.SubItems.Count ? it.SubItems[c].Text : "";
                            int tw = TextRenderer.MeasureText(g, txt, list.Font).Width + 24;
                            if (tw > width) width = tw;
                        }
                        if (c == 0 && width > 700) width = 700; // 主题列上限
                        w[c] = width;
                    }
                    int total = w[0] + w[1] + w[2] + 30; // 垂直滚动条 + 右边距
                    int maxLeft = (int)(ClientSize.Width * 0.65); // 最多占窗口 65%，给预览留空间
                    if (total > maxLeft)
                    {
                        int overflow = total - maxLeft;
                        w[0] = Math.Max(70, w[0] - overflow); // 空间不足时压缩主题列
                        total = w[0] + w[1] + w[2] + 30;
                    }
                    for (int c = 0; c < list.Columns.Count; c++) list.Columns[c].Width = w[c];
                    split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(total, maxLeft));
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
            if (si.Transcript != null)
            {
                // 已缓存：直接显示
                SetPreview(si.Transcript);
                statusLabel.Text = si.Id + " | " + si.File;
                return;
            }
            // 后台加载：解压 + 转录构建，进度实时显示在底部状态栏
            int token = ++loadToken;
            statusLabel.Text = Lang.T("statusLoading");
            progressBar.Value = 0;
            progressBar.Visible = true;
            Cursor = Cursors.AppStarting;
            Task.Run(delegate
            {
                try
                {
                    string raw = SessionReader.ReadSession(si.File);
                    string md = SessionReader.BuildTranscript(raw, delegate (int done, int total)
                    {
                        if (token != loadToken) return; // 已切换到其他会话
                        int pct = total <= 0 ? 0 : Math.Min(100, done * 100 / total);
                        BeginInvoke((Action)delegate
                        {
                            if (token != loadToken) return;
                            progressBar.Value = pct;
                            statusLabel.Text = string.Format(Lang.T("statusLoadingPct"), pct);
                        });
                    });
                    si.Transcript = md;
                    BeginInvoke((Action)delegate
                    {
                        if (token != loadToken) return;
                        progressBar.Visible = false;
                        Cursor = Cursors.Default;
                        SetPreview(md);
                        statusLabel.Text = si.Id + " | " + si.File;
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)delegate
                    {
                        if (token != loadToken) return;
                        progressBar.Visible = false;
                        Cursor = Cursors.Default;
                        MessageBox.Show(this, Lang.T("msgReadFail") + ex.Message, Lang.T("msgError"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        private void SetPreview(string md)
        {
            string full = md;
            if (md.Length > 600000)
                md = md.Substring(0, 600000) + "\n\n" + Lang.T("previewTruncated");
            preview.SetContent(md, full);
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

        /// <summary>用默认浏览器打开链接（右下角 GitHub / 网站）。</summary>
        private void OpenUrl(string url)
        {
            try { Process.Start(url); }
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
                            if (Lang.ByCode(l) != null) Lang.Current = l;
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
