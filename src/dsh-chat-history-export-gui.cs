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
        internal static extern unsafe ulong ZSTD_decompressBound(byte* src, ulong srcSize);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe ulong ZSTD_decompress(byte* dst, ulong dstCapacity, byte* src, ulong compressedSize);

        [DllImport("libzstd.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint ZSTD_isError(ulong code);

        internal static void EnsureDll()
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

    // ---------- 会话读取与转录 ----------
    static class SessionReader
    {
        public static string ReadSession(string file)
        {
            byte[] data = File.ReadAllBytes(file);
            if (data.Length < 4 || BitConverter.ToUInt32(data, 0) != 0xfd2fb528)
                return Encoding.UTF8.GetString(data); // 未压缩的 JSONL
            Zstd.EnsureDll();
            StringBuilder sb = new StringBuilder();
            int off = 0;
            while (off < data.Length)
            {
                ulong frameSize;
                unsafe
                {
                    fixed (byte* p = data)
                    {
                        frameSize = Zstd.ZSTD_findFrameCompressedSize(p + off, (ulong)(data.Length - off));
                    }
                }
                if (Zstd.ZSTD_isError(frameSize) != 0) break; // 尾部非 zstd 数据，忽略
                byte[] frame = DecompressFrame(data, off, (int)frameSize);
                sb.Append(Encoding.UTF8.GetString(frame));
                off += (int)frameSize;
            }
            return sb.ToString();
        }

        private static unsafe byte[] DecompressFrame(byte[] src, int off, int len)
        {
            ulong n = 0;
            byte[] dst;
            fixed (byte* p = src)
            {
                byte* s = p + off;
                ulong bound = Zstd.ZSTD_decompressBound(s, (ulong)len);
                if (Zstd.ZSTD_isError(bound) != 0) bound = (ulong)len * 4 + 4096;
                dst = new byte[bound];
                fixed (byte* d = dst)
                {
                    n = Zstd.ZSTD_decompress(d, bound, s, (ulong)len);
                }
                if (Zstd.ZSTD_isError(n) != 0)
                    throw new InvalidDataException("zstd 解压失败 (error " + n + ")");
                if (n == bound) return dst;
            }
            byte[] res = new byte[n];
            Array.Copy(dst, res, (long)n);
            return res;
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
    }

    // ---------- 主窗口 ----------
    class SessionInfo
    {
        public string Id;
        public string File;
        public DateTime Time;
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

        private string ConfigPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "dsh-chat-history-export.config.json"); }
        }

        public MainForm()
        {
            Text = "DSH Chat-History Export — 聊天记录导出工具";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(780, 520);
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
            title.Text = "DSH Chat-History Export";
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.AutoSize = true;
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
            lb.Text = "导出目录:";
            lb.AutoSize = true;
            lb.Anchor = AnchorStyles.Left;
            dirBox = new TextBox();
            dirBox.Dock = DockStyle.Fill;
            btnBrowse = MkButton("浏览…", 84);
            btnOpenDir = MkButton("打开目录", 92);
            dirRow.Controls.Add(lb, 0, 0);
            dirRow.Controls.Add(dirBox, 1, 0);
            dirRow.Controls.Add(btnBrowse, 2, 0);
            dirRow.Controls.Add(btnOpenDir, 3, 0);
            root.Controls.Add(dirRow, 0, 1);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.Margin = new Padding(0, 8, 0, 8);
            btnRefresh = MkButton("刷新列表", 120);
            btnPick = MkButton("选择会话文件…", 140);
            btnExport = MkButton("导出并保存", 120);
            actions.Controls.Add(btnRefresh);
            actions.Controls.Add(btnPick);
            actions.Controls.Add(btnExport);
            root.Controls.Add(actions, 0, 2);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 360;
            split.Panel1MinSize = 280;

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.Columns.Add("会话 ID", 250);
            list.Columns.Add("时间", 110);
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
            statusLabel.Text = "就绪";
            status.Items.Add(statusLabel);
            Controls.Add(status);

            btnRefresh.Click += delegate { LoadSessions(); };
            btnPick.Click += delegate { PickFile(); };
            btnExport.Click += delegate { Export(); };
            btnBrowse.Click += delegate { BrowseDir(); };
            btnOpenDir.Click += delegate { OpenExplorer(dirBox.Text, null); };
            list.SelectedIndexChanged += delegate { OnSelect(); };
            list.DoubleClick += delegate { Export(); };

            LoadConfig();
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
            foreach (SessionInfo si in sessions)
            {
                ListViewItem it = new ListViewItem(si.Id);
                it.SubItems.Add(si.Time.ToString("yyyy-MM-dd HH:mm"));
                it.Tag = si;
                list.Items.Add(it);
            }
            list.EndUpdate();
            statusLabel.Text = "已加载 " + sessions.Count + " 个会话"
                + (sessions.Count == 0 ? "（未找到 ~/.dsh/sessions，可点“选择会话文件…”手动挑选）" : "");
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
                MessageBox.Show(this, "读取失败:\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void SetPreview(string md)
        {
            if (md.Length > 600000)
                md = md.Substring(0, 600000) + "\n\n…（预览已截断，导出文件为完整内容）";
            preview.Text = md;
            preview.SelectionStart = 0;
            preview.ScrollToCaret();
        }

        private void PickFile()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "选择 DSH 会话文件 (session.jsonl / session.jsonl.zstd)";
                ofd.Filter = "会话文件 (*.jsonl*)|*.jsonl*|所有文件 (*.*)|*.*";
                ofd.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                pickedFile = ofd.FileName;
                list.SelectedItems.Clear();
                SessionInfo si = new SessionInfo();
                si.Id = Path.GetFileName(ofd.FileName);
                si.File = ofd.FileName;
                ShowPreview(si);
                statusLabel.Text = "已选择: " + ofd.FileName;
            }
        }

        private void BrowseDir()
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择导出目录";
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
                MessageBox.Show(this, "请先在左侧列表选择一个会话，或点“选择会话文件…”", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string dir = dirBox.Text.Trim();
            if (dir.Length == 0)
            {
                MessageBox.Show(this, "请先设置导出目录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                statusLabel.Text = "已生成: " + outFile;
                DialogResult r = MessageBox.Show(this, "已生成:\n" + outFile + "\n\n是否打开所在文件夹？", "导出完成",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) OpenExplorer(dir, outFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败:\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    if (cfg != null && cfg.ContainsKey("exportDir"))
                    {
                        string d = Convert.ToString(cfg["exportDir"]);
                        if (Directory.Exists(d)) dirBox.Text = d;
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
                File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(cfg), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
