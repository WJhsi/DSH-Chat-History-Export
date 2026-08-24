// dsh-chat-history-manage.cjs — 管理 DSH（DeepSeek Harness）的聊天记录
//
// 用法：
//   dsh-chat-history-manage <会话ID 或 会话文件路径> [输出文件]   （命令行模式）
//   直接双击/无参数运行                                     （交互模式：文件选择器 / 会话列表 / 手动输入）
//
// 功能：
//   1. 自动识别并解压 zstd 压缩的会话文件
//   2. 提取成可读的 Markdown 记录（用户消息 / 助手回复 / 工具调用 / 压缩标记 / 报错）
//
// 会话存储位置（Windows）：C:\Users\<用户名>\.dsh\sessions\<项目路径编码>\<会话ID>\session.jsonl[.zstd]

const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawnSync } = require('child_process');
const { zstdDecompressSync } = require('node:zlib');

// ---------- 配置（自定义导出目录，存 exe 旁边） ----------
function configPath() {
  return path.join(path.dirname(process.execPath), 'json', 'dsh-chat-history-manage.config.json');
}
function loadConfig() {
  try { return JSON.parse(fs.readFileSync(configPath(), 'utf8')); } catch { return {}; }
}
function saveConfig(cfg) {
  try {
    fs.mkdirSync(path.dirname(configPath()), { recursive: true });
    fs.writeFileSync(configPath(), JSON.stringify(cfg, null, 2), 'utf8');
    return true;
  } catch { return false; }
}

// ---------- Windows 目录选择器 ----------
function pickFolderViaDialog(initial) {
  const tmp = path.join(os.tmpdir(), 'dsh-chat-history-manage-folder.txt');
  try { fs.rmSync(tmp, { force: true }); } catch {}
  const ps = `Add-Type -AssemblyName System.Windows.Forms
$d = New-Object System.Windows.Forms.FolderBrowserDialog
$d.Description = "选择导出目录"
if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
  $d.SelectedPath | Out-File -FilePath '${tmp.replace(/'/g, "''")}' -Encoding UTF8
}`;
  const r = spawnSync('powershell.exe', ['-STA', '-NoProfile', '-Command', ps], { encoding: 'utf8', timeout: 180000 });
  if (r.status === 0 && fs.existsSync(tmp)) {
    const p = fs.readFileSync(tmp, 'utf8').trim();
    try { fs.rmSync(tmp, { force: true }); } catch {}
    return p || null;
  }
  return null;
}

// ---------- 会话根目录：配置记忆 > $DSH_HOME > 默认 ~/.dsh/sessions；找不到可手动选择 ----------
function resolveSessionsRoot() {
  const cfg = loadConfig();
  if (cfg.sessionsDir && fs.existsSync(cfg.sessionsDir)) return cfg.sessionsDir;
  const envHome = process.env.DSH_HOME;
  if (envHome && envHome.trim()) {
    const p = path.join(envHome.trim(), 'sessions');
    if (fs.existsSync(p)) return p;
  }
  return path.join(os.homedir(), '.dsh', 'sessions');
}

function pickSessionsRoot() {
  const picked = pickFolderViaDialog();
  if (picked) {
    const cfg = loadConfig();
    cfg.sessionsDir = picked;
    saveConfig(cfg);
  }
  return picked;
}

// ---------- 定位会话文件 ----------
function findSessionFile(input) {
  if (fs.existsSync(input) && fs.statSync(input).isFile()) return input;
  let root = resolveSessionsRoot();
  if (!fs.existsSync(root)) {
    console.log('找不到会话目录: ' + root);
    console.log('是否手动选择会话目录？');
    const picked = pickSessionsRoot();
    if (picked) { root = picked; } else { process.exit(1); }
  }
  const hits = [];
  (function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (/^session\.jsonl(\.zstd)?$/.test(entry.name) && full.includes(input)) hits.push(full);
    }
  })(root);
  if (!hits.length) {
    console.error('未找到会话 ' + input + '（请确认会话ID，或直接给完整文件路径）');
    process.exit(1);
  }
  return hits[0];
}

// ---------- 会话元信息（主题 + 是否有聊天内容） ----------
// title: 最后一条 session/title 事件的 data.title（与 DSH 侧边栏一致，last-wins），无则 null
// hasContent: 是否存在真实的用户聊天内容（非插件注入、有文本的 user/message）
function getMeta(raw) {
  let title = null;
  let hasContent = false;
  for (const line of raw.split('\n')) {
    if (line.indexOf('session/title') >= 0) {
      let e; try { e = JSON.parse(line); } catch { continue; }
      if (e.type === 'session/title' && e.data && typeof e.data.title === 'string' && e.data.title) title = e.data.title;
    } else if (line.indexOf('user/message') >= 0) {
      let e; try { e = JSON.parse(line); } catch { continue; }
      if (e.type !== 'user/message' || !e.data) continue;
      if (e.data.source && e.data.source.kind === 'plugin') continue; // 插件注入不算聊天内容
      const text = (e.data.content || []).filter(b => b && b.type === 'text' && b.text).map(b => b.text).join('');
      if (text.trim()) hasContent = true;
    }
  }
  return { title, hasContent };
}

// 会话元信息：优先只解压最后一个 zstd 帧（主题事件总在文件尾部附近），
// 尾帧命中主题即视为有内容直接返回；否则全量解压兜底（空白会话判定必须全量扫描）。
function sessionMetaOf(file) {
  const buf = fs.readFileSync(file);
  if (buf.length > 4 && buf.readUInt32LE(0) === 0xfd2fb528) {
    // 从尾部往前找最后一个帧起始魔数；帧内巧合命中会解压失败，随即全量兜底
    for (let i = buf.length - 4; i >= 0; i--) {
      if (buf.readUInt32LE(i) === 0xfd2fb528) {
        try {
          const { title } = getMeta(zstdDecompressSync(buf.subarray(i)).toString('utf8'));
          if (title) return { title, hasContent: true }; // 有主题必然有内容
        } catch { }
        break;
      }
    }
    return getMeta(readSession(file));
  }
  return getMeta(buf.toString('utf8'));
}

// ---------- 主题磁盘缓存（与 GUI 同格式，带版本号；条目含 c: 是否有内容） ----------
const TITLE_CACHE_VERSION = 2;
function titleCachePath() {
  return path.join(__dirname, 'json', 'dsh-chat-history-manage.titles.json');
}
function loadTitleCache() {
  try {
    const raw = JSON.parse(fs.readFileSync(titleCachePath(), 'utf8'));
    if (raw && raw.v === TITLE_CACHE_VERSION && raw.e && typeof raw.e === 'object') return raw.e;
  } catch { }
  return {};
}
function saveTitleCache(entries) {
  try {
    fs.mkdirSync(path.dirname(titleCachePath()), { recursive: true });
    fs.writeFileSync(titleCachePath(), JSON.stringify({ v: TITLE_CACHE_VERSION, e: entries }), 'utf8');
  } catch { }
}

// ---------- 列出所有已保存会话（剔除无主题且无聊天内容的空白会话，附加 filtered 计数） ----------
function listSessions() {
  let root = resolveSessionsRoot();
  if (!fs.existsSync(root)) {
    console.log('找不到会话目录: ' + root);
    console.log('是否手动选择会话目录？');
    const picked = pickSessionsRoot();
    if (!picked) return [];
    root = picked;
  }
  const out = [];
  (function walk(dir) {
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (/^session-[0-9a-f-]+$/i.test(entry.name)) {
          const f = fs.readdirSync(full).find(x => /^session\.jsonl(\.zstd)?$/.test(x));
          if (f) out.push({ id: entry.name, file: path.join(full, f) });
        } else walk(full);
      }
    }
  })(root);
  out.sort((a, b) => fs.statSync(b.file).mtimeMs - fs.statSync(a.file).mtimeMs);
  // 缓存命中（修改时间 + 大小一致）直接复用，只对新增/变化过的会话重新解压
  const cache = loadTitleCache();
  const missing = [];
  let filtered = 0;
  const kept = [];
  for (const s of out) {
    let st = null;
    try { st = fs.statSync(s.file); } catch { }
    const c = st ? cache[s.file] : null;
    if (c && c.m === st.mtimeMs && c.s === st.size && typeof c.t === 'string' && typeof c.c === 'boolean') {
      if (c.t) { s.title = c.t; kept.push(s); }
      else if (c.c) { s.title = null; kept.push(s); } // 有内容但暂无主题，保留
      else filtered++;                                 // 缓存记录为空白会话：剔除
    } else {
      missing.push(s);
    }
  }
  if (missing.length) {
    console.log('正在读取会话主题…');
    for (const s of missing) {
      try {
        const meta = sessionMetaOf(s.file);
        s.title = meta.title;
        try {
          const st = fs.statSync(s.file);
          cache[s.file] = { m: st.mtimeMs, s: st.size, t: s.title || '', c: meta.hasContent };
        } catch { }
        if (!meta.title && !meta.hasContent) { filtered++; continue; } // 无主题且无内容：空白会话，剔除
        kept.push(s);
      } catch {
        kept.push(s); // 读取失败：宁可保留显示
      }
    }
    saveTitleCache(cache);
  }
  kept.filtered = filtered;
  return kept;
}

// ---------- Windows 文件选择器 ----------
function pickFileViaDialog() {
  const tmp = path.join(os.tmpdir(), 'dsh-chat-history-manage-picked.txt');
  try { fs.rmSync(tmp, { force: true }); } catch {}
  const ps = `Add-Type -AssemblyName System.Windows.Forms
$d = New-Object System.Windows.Forms.OpenFileDialog
$d.Title = "选择 DSH 会话文件 (session.jsonl / session.jsonl.zstd)"
$d.Filter = "会话文件 (*.jsonl*)|*.jsonl*|所有文件 (*.*)|*.*"
$d.InitialDirectory = [System.IO.Path]::Combine($env:USERPROFILE, ".dsh", "sessions")
if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
  $d.FileName | Out-File -FilePath '${tmp.replace(/'/g, "''")}' -Encoding UTF8
}`;
  const r = spawnSync('powershell.exe', ['-STA', '-NoProfile', '-Command', ps], { encoding: 'utf8', timeout: 180000 });
  if (r.status === 0 && fs.existsSync(tmp)) {
    const p = fs.readFileSync(tmp, 'utf8').trim();
    try { fs.rmSync(tmp, { force: true }); } catch {}
    return p || null;
  }
  return null;
}

// ---------- 读取（自动解压 zstd） ----------
function readSession(file) {
  const buf = fs.readFileSync(file);
  if (buf.length > 4 && buf.readUInt32LE(0) === 0xfd2fb528) {
    let out = '';
    let offset = 0;
    while (offset + 4 <= buf.length) {
      let next = -1;
      for (let i = offset + 4; i + 4 <= buf.length; i++) {
        if (buf.readUInt32LE(i) === 0xfd2fb528) { next = i; break; }
      }
      const end = next === -1 ? buf.length : next;
      try { out += zstdDecompressSync(buf.subarray(offset, end)).toString('utf8'); }
      catch { break; }
      if (next === -1) break;
      offset = next;
    }
    return out;
  }
  return buf.toString('utf8');
}

// ---------- 提取可读记录 ----------
function buildTranscript(raw) {
  const out = [];
  let turn = 0;
  let model = null; // 最近一次请求的模型（request/header 或 request/context 提供）
  const ts = (t) => new Date(t).toISOString().replace('T', ' ').slice(0, 19);
  const trunc = (s, n) => (s == null ? '' : String(s)).length > n ? String(s).slice(0, n) + '…' : String(s);
  const textOf = (blocks) => (blocks || []).filter(b => b.type === 'text' && b.text).map(b => b.text).join('\n');

  for (const line of raw.split('\n')) {
    if (!line.trim()) continue;
    let e; try { e = JSON.parse(line); } catch { continue; }
    const d = e.data || {};
    if (e.type === 'turn/start') { turn = d.turn; continue; }
    if (e.type === 'request/header') {
      const cfg = d.header && d.header.config;
      if (cfg && cfg.model) model = cfg.model;
      continue;
    }
    if (e.type === 'request/context') {
      if (d.model) model = d.model;
      continue;
    }
    if (e.type === 'user/message') {
      if (d.source && d.source.kind === 'plugin') continue;
      const t = textOf(d.content).trim();
      if (t) out.push(`\n## [T${turn}] 用户 ${ts(e.time)}\n${t}`);
    } else if (e.type === 'assistant/message') {
      const t = textOf(d.message && d.message.content).trim();
      const hasTool = (d.message && d.message.content || []).some(b => b.type === 'tool-call');
      if (t) out.push(`\n### ${model || '助手'} ${ts(e.time)}${hasTool ? ' [含工具调用]' : ''}\n${t}`);
    } else if (e.type === 'tool/call') {
      out.push(`\n> 🔧 ${d.name} ${trunc(JSON.stringify(d.arguments), 200)}`);
    } else if (e.type === 'tool/result') {
      const ok = d.ok === true || d.status === 'ok' || d.error === undefined;
      out.push(`> 📦 ${ok ? 'OK' : 'ERROR'} ${trunc(d.content ?? d.text ?? '', 300)}`);
    } else if (e.type === 'compaction/end') {
      out.push(`\n## ⏳ 上下文压缩 @T${d.turn}${d.error ? ' 失败: ' + d.error : ''}`);
    } else if (e.type === 'turn/end' && d.reason && d.reason.kind === 'error') {
      out.push(`\n## ❌ T${d.turn} 失败: ${trunc(d.reason.error && d.reason.error.message || '', 400)}`);
    }
  }
  return out.join('\n');
}

// ---------- 主流程 ----------
function run(input, outArg) {
  const file = findSessionFile(input);
  const raw = readSession(file);
  const md = buildTranscript(raw);
  let outFile;
  if (outArg) {
    outFile = outArg;
  } else {
    const cfg = loadConfig();
    const dir = cfg.exportDir && fs.existsSync(cfg.exportDir) ? cfg.exportDir : process.cwd();
    outFile = path.join(dir, path.basename(file).replace(/\.jsonl(\.zstd)?$/, '') + '-transcript.md');
  }
  fs.mkdirSync(path.dirname(outFile), { recursive: true });
  fs.writeFileSync(outFile, md, 'utf8');
  const lines = raw.split('\n').filter(Boolean).length;
  const turns = (raw.match(/"type":"turn\/start"/g) || []).length;
  console.log('会话文件: ' + file);
  console.log('事件行数: ' + lines + ' | 轮次: ' + turns);
  console.log('已生成: ' + outFile + ' (' + Math.round(md.length / 1024) + ' KB)');
}

(async function main() {
  const args = process.argv.slice(2);
  if (args[0]) { run(args[0], args[1]); process.exit(0); }

  const readline = require('readline');
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  // 手写提问器：管道输入时 readline 会在提问挂起前把缓冲行全部 emit 掉，
  // rl.question 因此丢掉后续输入、永久挂起。这里用「行队列 + 提问队列」
  // 保证行绝不丢失，交互终端和管道输入都可用。
  const promptQueue = [];
  const lineQueue = [];
  rl.on('line', (line) => {
    if (promptQueue.length) promptQueue.shift()(line);
    else lineQueue.push(line);
  });
  const ask = (q) => new Promise((resolve) => {
    process.stdout.write(q);
    if (lineQueue.length) resolve(lineQueue.shift());
    else promptQueue.push(resolve);
  });

  console.log('=== DSH Chat-History Manage 聊天记录管理 ===');
  console.log('1) 用文件选择器挑选会话文件');
  console.log('2) 从已保存会话列表选择');
  console.log('3) 手动输入会话ID或文件路径');
  const choice = (await ask('请选择 (1/2/3): ')).trim();

  let input = null;
  if (choice === '1') {
    console.log('正在打开文件选择器…');
    input = pickFileViaDialog();
    if (!input) { console.log('未选择文件'); process.exit(0); }
  } else if (choice === '2') {
    const sessions = listSessions();
    if (!sessions.length) { console.log('未找到任何已保存会话'); process.exit(0); }
    sessions.forEach((s, i) => {
      const when = new Date(fs.statSync(s.file).mtime).toISOString().replace('T', ' ').slice(0, 16);
      console.log(`${String(i + 1).padStart(2)}) ${s.title ? s.title + ' | ' : ''}${s.id}  (${when})`);
    });
    if (sessions.filtered) console.log(`（已剔除 ${sessions.filtered} 个空白会话：无主题且无聊天内容）`);
    const idx = parseInt(await ask('选择序号: '), 10);
    if (!idx || idx < 1 || idx > sessions.length) { console.log('无效序号'); process.exit(0); }
    input = sessions[idx - 1].file;
  } else if (choice === '3') {
    input = (await ask('输入会话ID或文件路径: ')).trim();
    if (!input) { console.log('未输入'); process.exit(0); }
  } else {
    console.log('无效选择'); process.exit(0);
  }

  // 导出目录：回车用默认，b 浏览，输入用新目录（保存到配置）
  const cfg = loadConfig();
  const defaultDir = (cfg.exportDir && fs.existsSync(cfg.exportDir)) ? cfg.exportDir : process.cwd();
  const ans = (await ask(`导出目录（回车=默认 ${defaultDir}，输入=新目录，b=浏览选择）: `)).trim();
  let exportDir = defaultDir;
  if (ans.toLowerCase() === 'b') {
    const picked = pickFolderViaDialog();
    if (!picked) { console.log('未选择目录，使用默认'); }
    else exportDir = picked;
  } else if (ans) {
    exportDir = path.resolve(ans);
  }
  if (!fs.existsSync(exportDir)) {
    try { fs.mkdirSync(exportDir, { recursive: true }); console.log('已创建目录: ' + exportDir); }
    catch { console.log('目录无效，使用当前目录'); exportDir = process.cwd(); }
  }
  if (exportDir !== (cfg.exportDir || '')) {
    if (saveConfig({ exportDir })) console.log('已记住导出目录: ' + exportDir);
  }

  rl.close();
  run(input);
  process.exit(0);
})();
