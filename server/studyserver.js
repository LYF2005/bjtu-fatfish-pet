/* 北交大肥鱼 · 桌宠独立后端服务
 * 职责（仅供桌宠使用，与壁纸项目完全独立）：
 *  1. HTTP(默认 23336，可配置)：
 *     - /study/*    学业接口：账号、作业 DDL、课件、课表、空教室、提醒队列
 *     - /stats/mood、/event/latest、/profile、/kb/*   桌宠「大脑」增强端点
 *     - /health     健康检查
 *  2. SQLite(better-sqlite3，可选)：本地缓存事件 / 知识笔记；不可用时自动降级内存。
 *  3. 学业采集器 notify/：北交大 MIS 统一认证（可配置），凭证仅存本机（DPAPI/AES）。
 *
 * 隐私：所有数据只在本机处理，不上传、不中转。课件版权归原作者所有。
 * 用法：node server/studyserver.js  （Node >= 18。首次运行 npm install） */
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createKnowledge } from './knowledge.js';
import { createPoller, readAlerts } from './notify/poller.mjs';
import { credentialsInfo, saveCredentials, clearCredentials, readCredentials } from './notify/credentials.mjs';
import {
  verifyCredentials, SCHOOL, schoolProfile, collectEmptyRooms,
  fetchHomeworksForCli, remainingInfo, homeworkUrl, loadConfig as loadStudyConfig
} from './notify/bjtu.mjs';
import * as courseware from './notify/courseware.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');

/* ---------- 配置（桌宠独立配置，与壁纸完全解耦） ----------
 * 优先读 server/config.json（本地、git 忽略），不存在时读 config.example.json 模板。
 * 端口默认 23336，避免与壁纸桥接(23334)冲突；两者可同时运行互不干扰。 */
function loadConfig() {
  for (const name of ['config.json', 'config.example.json']) {
    const cfgPath = path.join(__dirname, name);
    try {
      return JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
    } catch (_) { /* 试下一个 */ }
  }
  return { server: {} };
}
const config = loadConfig();
const WS_PORT = config.server.wsPort || 0;               // 0 = 不启动 WebSocket（桌宠不需要）
const HTTP_PORT = Number(config.server.httpPort) || 23336;
const DB_PATH = path.resolve(__dirname, config.server.dbPath || 'study-data.db');
const LLM = config.llm || { enabled: false, provider: 'openai-compatible', baseUrl: '', model: '', apiKey: '' };

/* ---------- 健康监控（Watchdog）配置 ---------- */
const WATCHDOG = config.server.watchdog || {
  enabled: true,
  heartbeatTimeoutMs: 90000,  // 壁纸页面心跳超时阈值：超过即认为页面卡死
  checkIntervalMs: 30000,     // 服务端检查周期
  graceMs: 120000,            // 服务启动宽限期：期间不检查（等壁纸连上来）
  cooldownMs: 180000          // 重启后冷却期：避免连环重启
};

/* ---------- SQLite ---------- */
let db = null;
let knowledge = null;
try {
  const Database = (await import('better-sqlite3')).default;
  db = new Database(DB_PATH);
  db.pragma('journal_mode = WAL');
  db.exec(`
    CREATE TABLE IF NOT EXISTS events (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      time TEXT NOT NULL,
      type TEXT,
      kind TEXT,
      payload TEXT
    );
    CREATE TABLE IF NOT EXISTS approvals (
      id TEXT PRIMARY KEY,
      time TEXT NOT NULL,
      payload TEXT,
      decision TEXT,
      decision_time TEXT
    );
    CREATE TABLE IF NOT EXISTS always (
      command TEXT PRIMARY KEY,
      created TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS heartbeats (
      time TEXT NOT NULL,
      state TEXT
    );
  `);
  knowledge = createKnowledge(db);
} catch (err) {
  console.warn('[大肥鱼] better-sqlite3 不可用，降级为内存存储：', err.message);
  db = null;
}

/* ---------- 内存态（无 SQLite 时兜底） ---------- */
const memEvents = [];
const memApprovals = new Map();
const memAlways = new Map();
let lastHeartbeatAt = 0; // 最近一次收到壁纸心跳的时间戳（watchdog 用）

function sqlRun(sql, params) {
  if (db) return db.prepare(sql).run(params);
  // ---- 内存降级 ----
  if (sql.includes('INSERT INTO events')) {
    const e = { id: memEvents.length + 1, time: new Date().toISOString(), type: params[0], kind: params[1], payload: params[2] };
    memEvents.push(e);
    return { lastInsertRowid: e.id };
  }
  if (sql.includes('INSERT INTO approvals')) {
    const [id, time, payload, decision, decisionTime] = params;
    memApprovals.set(id, { id, time, payload, decision, decision_time: decisionTime });
    return { changes: 1 };
  }
  if (sql.includes('INSERT INTO always')) {
    memAlways.set(params[0], { command: params[0], created: params[1] });
    return { changes: 1 };
  }
  if (sql.includes('UPDATE approvals')) {
    const prev = memApprovals.get(params[0]);
    if (prev) { prev.decision = params[1]; prev.decision_time = params[2]; }
    return { changes: 1 };
  }
  return { changes: 0 };
}

function sqlGet(sql, params) {
  if (db) return db.prepare(sql).get(params);
  const first = (sql.match(/(\w+)/g) || [])[0] || '';
  if (first === 'approvals') return memApprovals.get(params[0]);
  return undefined;
}

function sqlAll(sql) {
  if (db) return db.prepare(sql).all();
  const first = (sql.match(/(\w+)/g) || [])[0] || '';
  if (first === 'events') return memEvents.slice(-50);
  if (first === 'always') return Array.from(memAlways.values());
  return [];
}

/* ---------- 事件广播（桌宠独立版：无 WebSocket， observational no-op） ----------
 * 壁纸用 WebSocket 把 Agent 事件推给前端；桌宠不需要这条通道，事件仍照常入库，
 * /event/latest 与 /stats/mood 照常可用。保留 broadcast 空实现以免改动业务分支。 */
function broadcast(_obj) { /* 桌宠无为 WebSocket 前端，事件已落库即可 */ }

/* ---------- 学业提醒轮询（作业 / 邮件） ---------- */
const poller = createPoller({
  onNotify(payload) {
    /* 作业 / 邮件提醒现在只给桌宠「北交大肥鱼」：写进 state.alerts 由 /study/alerts 增量拉取。
     * 壁纸不再接收学业提醒：既不再往 WS 广播，也不再塞进 events 表
     * （events 表是壁纸 /event/latest 的兜底通道，写进去壁纸仍会收到）。
     * 保留一条日志方便排查自动提醒是否在跑。 */
    console.log('[大肥鱼] 学业提醒（仅桌宠）：' + payload.title);
  },
  log: (...args) => console.log('[学业提醒]', ...args)
});

/* 空教室查询结果缓存：每次都要重新登录学校系统（数秒），且与轮询器并发登录会互相顶掉会话，
 * 因此同一组条件 10 分钟内复用结果；桌宠点「刷新」时带 refresh=1 强制重取。 */
const roomsCache = new Map();
const roomsPending = new Map();   // key → Promise；按条件合并并发请求，避免同时发起多个登录
const ROOMS_TTL_MS = 10 * 60 * 1000;

/* 账号变更时清掉所有「绑定旧账号」的内存缓存：课件模块有 25 分钟单例会话，
 * 空教室结果也有 10 分钟缓存，不清就会让新账号看到上一个人的数据。 */
function invalidateStudyCaches() {
  try { courseware.invalidate(); } catch (_) {}
  roomsCache.clear();
}

/* ---------- 事件入库 + 广播 ---------- */
/* 去抖：Trae/Claude 的 Hooks 可能对同一事件触发多次（全局+项目双配置文件等）。
 * 同 type 的重复事件在 800ms 内只接受第一条，避免壁纸重复排队过渡片段。 */
const lastEventByType = new Map();
const DEDUP_WINDOW_MS = 800;
function recentDuplicate(type) {
  if (type === 'approval_request') return false; // 审批事件按 id 区分，不去抖
  const now = Date.now();
  const last = lastEventByType.get(type) || 0;
  if (now - last < DEDUP_WINDOW_MS) return true;
  lastEventByType.set(type, now);
  return false;
}

function ingestEvent(body) {
  const type = body.type || body.kind || 'event';
  const kind = body.kind || 'event';
  if (recentDuplicate(type)) return null; // 静默丢弃重复事件
  const time = new Date().toISOString();
  const ins = sqlRun('INSERT INTO events (time, type, kind, payload) VALUES (?, ?, ?, ?)',
    [time, type, kind, JSON.stringify(body)]);
  const seq = Number(ins.lastInsertRowid) || (Date.now() + Math.random());

  const used = Number(body.used) || Number(body.tokenUsed) || 0;

  const payload = {
    seq,
    type,
    kind,
    used,
    id: body.id || body.requestId || null,
    state: body.state,
    summary: body.summary,
    tool: body.tool,
    command: body.command,
    cwd: body.cwd,
    risk: body.risk,
    time
  };
  broadcast(payload);
  return payload;
}

/* ---------- 审批请求（Hooks / MCP 提交） ---------- */
function registerApproval(body) {
  const id = body.id || body.requestId || ('ap_' + Date.now() + '_' + Math.random().toString(36).slice(2, 8));
  const record = {
    id, time: new Date().toISOString(),
    payload: JSON.stringify(body),
    decision: null, decision_time: null
  };
  sqlRun('INSERT INTO approvals (id, time, payload, decision, decision_time) VALUES (?, ?, ?, ?, ?)',
    [record.id, record.time, record.payload, null, null]);
  broadcast({ type: 'approval_request', id, ...body, time: record.time });
  return record;
}

/* ---------- HTTP 服务 ---------- */
/* CORS：Lively 壁纸可能以 file:// 或随机端口的本地源加载，审批回写必须允许跨源。 */
const CORS_HEADERS = {
  'content-type': 'application/json; charset=utf-8',
  'cache-control': 'no-store',
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'GET, POST, OPTIONS',
  'access-control-allow-headers': 'content-type'
};
function sendJSON(res, code, obj) {
  res.writeHead(code, CORS_HEADERS);
  res.end(JSON.stringify(obj));
}
function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (c) => { data += c; if (data.length > 1e6) req.destroy(); });
    req.on('end', () => {
      if (!data) return resolve({});
      try { resolve(JSON.parse(data)); } catch (e) { reject(e); }
    });
  });
}

/* ---------- LLM 日记润色（服务端代理：apiKey 不出 server，前端只拿到文本） ---------- */
const LLM_SYSTEM_PROMPT =
  '你是桌面宠物"大肥鱼"，一条在 Agent 桌面上打工的小鲸鱼娘，正在写第一人称打工日记。' +
  '要求：1) 语气可爱、有打工人的真实疲惫感，保留用户提供的全部事实与数字，不编造新事实；' +
  '2) 融入"我们的关系"记忆引用，让日记有长期连载感；' +
  '3) 全文不超过 {maxLen} 字，中文。只输出日记正文，不要任何前缀或解释。';

async function callLLM(payload) {
  const url = (LLM.baseUrl || '').replace(/\/+$/, '') + '/chat/completions';
  const body = {
    model: LLM.model || 'moonshot-v1-8k',
    messages: [
      { role: 'system', content: LLM_SYSTEM_PROMPT.replace('{maxLen}', payload.rules && payload.rules.maxLen || 200) },
      { role: 'user', content: JSON.stringify({ draft: payload.draft, mood: payload.mood, memory: payload.memory, rules: payload.rules }) }
    ],
    temperature: 0.8,
    max_tokens: 400
  };
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), LLM.timeoutMs || 12000);
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'authorization': 'Bearer ' + (LLM.apiKey || '')
      },
      body: JSON.stringify(body),
      signal: ctl.signal
    });
    if (!res.ok) throw new Error('llm http ' + res.status);
    const json = await res.json();
    const text = (json.choices && json.choices[0] && json.choices[0].message && json.choices[0].message.content) || '';
    return text.trim();
  } finally {
    clearTimeout(timer);
  }
}

/* 读取用户画像：仅扫描项目目录下的 USER.md / MEMORY.md（避免越权读任意路径）。 */
function readProfile() {
  const out = { name: null, role: null, note: null, source: null };
  for (const name of ['USER.md', 'MEMORY.md', 'CLAUDE.md']) {
    const f = path.join(ROOT, name);
    try {
      const text = fs.readFileSync(f, 'utf8').slice(0, 2000);
      const nameMatch = text.match(/^(?:#|##)?\s*(?:我是|名字|name|role|职位|人称)\s*[:：]\s*(.+)$/m);
      if (!out.name && nameMatch) out.name = nameMatch[1].trim();
      if (!out.role) {
        const roleMatch = text.match(/(开发|工程师|设计师|产品|运营|架构师|学生)/);
        if (roleMatch) out.role = roleMatch[1];
      }
      out.note = (out.note ? out.note + ' / ' : '') + text.split('\n').slice(0, 8).join(' ');
      out.source = (out.source ? out.source + ',' : '') + name;
    } catch (_) { /* 文件不存在则忽略 */ }
  }
  return out;
}

/* 服务端页面 /study/settings —— 浏览器版学业账号设置。
 * 用途：不想开桌宠设置窗口时，直接访问 http://127.0.0.1:23334/study/settings 也能填账号。 */
function studySettingsHtml() {
  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>北交大肥鱼 · 学业账号</title>
<style>
  /* 与桌宠 CompanionTheme 同一套色值：浅色纸面 / 大留白 / 极淡分隔线 / 状态克制 */
  :root {
    --surface:#faf9f6; --raised:#ffffff; --ink:#2a3853; --subtle:#8a93a6;
    --hairline:#e8e7e3; --soft:#f4f5f8; --brand:#485c92; --brand-soft:#ebeffa;
    --ok:#4e7a62; --err:#a83e3e; --warn:#a6762e;
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--surface); color:var(--ink); min-height:100vh;
    font-family:system-ui,-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
    -webkit-font-smoothing:antialiased; }
  .wrap { max-width:720px; margin:0 auto; padding:48px 24px 72px; }
  .eyebrow { font-size:10.5px; letter-spacing:.22em; font-weight:600; color:var(--brand); margin-bottom:10px; }
  h1 { font-size:26px; font-weight:600; letter-spacing:-.01em; margin:0 0 8px; }
  .sub { color:var(--subtle); font-size:13px; line-height:1.75; margin-bottom:32px; }
  .card { background:var(--raised); border:1px solid var(--hairline); border-radius:12px; padding:24px; margin-bottom:18px; }
  .card h2 { font-size:15px; font-weight:600; margin:0 0 5px; }
  .card .desc { font-size:12px; color:var(--subtle); margin:0 0 18px; line-height:1.7; }
  label { display:block; font-size:11.5px; color:var(--subtle); margin:18px 0 7px; }
  input[type=text], input[type=password] { width:100%; background:var(--raised);
    border:1px solid var(--hairline); border-radius:8px; color:var(--ink);
    padding:10px 13px; font-size:13.5px; outline:none; transition:border-color .15s,box-shadow .15s; }
  input[type=text]:focus, input[type=password]:focus { border-color:var(--brand); box-shadow:0 0 0 3px var(--brand-soft); }
  input::placeholder { color:#b3bac7; }
  .hint { color:var(--subtle); font-size:11.5px; margin-top:10px; line-height:1.8; }
  .hint b { color:var(--ink); font-weight:600; }
  .row { display:flex; gap:10px; flex-wrap:wrap; margin-top:22px; }
  button { border-radius:8px; padding:10px 18px; font-size:13.5px; cursor:pointer;
    border:1px solid var(--brand); background:var(--brand); color:#fff; font-weight:500;
    transition:background .15s,border-color .15s; }
  button:hover { background:#3d4c7e; border-color:#3d4c7e; }
  button.ghost { background:var(--raised); border-color:var(--hairline); color:var(--ink); font-weight:400; }
  button.ghost:hover { background:var(--soft); border-color:var(--hairline); }
  button:disabled { opacity:.5; cursor:default; }
  .status { margin-top:16px; font-size:12.5px; min-height:20px; white-space:pre-wrap; line-height:1.7; }
  .ok { color:var(--ok); } .err { color:var(--err); } .warn { color:var(--warn); }
  .stat { font-size:13px; color:var(--ink); line-height:1.9; white-space:pre-wrap; }
  .stat.muted { color:var(--subtle); font-size:12px; }
  .foot { text-align:center; color:var(--subtle); font-size:11px; margin-top:34px; }
</style>
</head>
<body>
<div class="wrap">
  <div class="eyebrow">邮 课 叮</div>
  <h1>学业账号</h1>
  <div class="sub">填一次学号和密码，就能在桌宠里看到作业、实验、课程报告、未读邮件与课表。<br>所有数据只在这台电脑上的桥接服务里流转。</div>

  <div class="card">
    <h2>登录凭据</h2>
    <div class="desc">账号密码只保存在本机，不会上传。</div>
    <label>学校</label>
    <div id="school" class="stat">读取中…</div>
    <label>学号 / 账号</label>
    <input id="username" type="text" placeholder="请输入你的学号" autocomplete="username">
    <label>密码</label>
    <input id="password" type="password" placeholder="已保存过则留空表示不修改" autocomplete="current-password">
    <div class="hint">密码只保存在本机，不会上传。换学号时请把新密码一起填上。<span id="protection"></span></div>
    <div class="row">
      <button id="save">保存账号</button>
      <button id="test" class="ghost">测试登录</button>
      <button id="clear" class="ghost">清除本机凭证</button>
    </div>
    <div id="status" class="status"></div>
  </div>

  <div class="card">
    <h2>教学周校准</h2>
    <div class="desc">选填。课表「当前周」与教务系统不一致时才需要设置。</div>
    <label>教学第 1 周（周一）</label>
    <input id="week1Monday" type="text" placeholder="例如 2026-09-14（留空 = 自动）">
    <div class="hint">填<b>真正开课那一周的周一</b>即可对齐。校历返回的学期起始日未必等于教学第 1 周。<br>例如北交大 2026 秋实际第 1 周从 2026-09-14 开始。</div>
    <div class="row">
      <button id="saveWeek" class="ghost">保存校准</button>
      <button id="clearWeek" class="ghost">恢复自动</button>
    </div>
    <div id="weekStatus" class="status"></div>
  </div>

  <div class="card">
    <h2>运行状态</h2>
    <div class="stat" id="summary">正在读取学业数据…</div>
    <div class="row"><button id="refresh" class="ghost">立即刷新学业数据</button></div>
  </div>

  <div class="foot">北交大肥鱼 · 数据来自本机桥接服务</div>
</div>
<script>
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function setStatus(text, cls) { $('status').textContent = text || ''; $('status').className = 'status ' + (cls || ''); }
function setBusy(v) { for (const id of ['save','test','clear','refresh']) $(id).disabled = v; }
async function api(url, opts) { const r = await fetch(url, opts); const d = await r.json().catch(() => ({})); return d; }

async function loadAccount() {
  const a = await api('/study/account');
  if (a.school) $('school').textContent = a.school.name + '（' + a.school.short + '）';
  // 实际保护方式由服务端返回（DPAPI 或 AES 兜底），不在页面上写死过时的文案
  $('protection').textContent = a.protection ? ' 当前保护方式：' + a.protection + '。' : '';
  $('username').value = a.username || '';
  $('week1Monday').value = a.week1Monday || '';
  if (a.configured) setStatus('当前已保存账号 ' + a.username + '。密码不会显示，留空即保持不变。', 'ok');
  else if (a.error) setStatus(a.error, 'err');
  else setStatus('还没有填写账号。', 'warn');
}

async function loadSummary() {
  const s = await api('/study/summary');
  if (!s.configured) { $('summary').textContent = '还没填账号，所以暂时没有数据。'; return; }
  if (!s.ok) { $('summary').textContent = '最近一次拉取没成功：' + (s.error || '未知原因'); return; }
  const lines = [
    '账号 ' + s.account + '：' + s.courseCount + ' 门课，' + (s.pending || []).length + ' 个待交任务，' + (s.emails ? s.emails.unread : 0) + ' 封未读邮件',
    (s.termName || '') + '  第 ' + (s.week || 0) + ' 周  今天 ' + ((s.schedule && s.schedule.todayEntries) ? s.schedule.todayEntries.length : 0) + ' 节课',
    '最近更新：' + friendlyTime(s.at)
  ];
  $('summary').textContent = lines.join('\\n');
}

function friendlyTime(iso) {
  if (!iso) return '';
  const t = new Date(iso);
  if (isNaN(t.getTime())) return iso;
  const diff = Date.now() - t.getTime();
  if (diff < 60000) return '刚刚';
  if (diff < 3600000) return Math.floor(diff / 60000) + ' 分钟前';
  const pad = (n) => String(n).padStart(2, '0');
  return t.getFullYear() + '-' + pad(t.getMonth() + 1) + '-' + pad(t.getDate()) + ' ' + pad(t.getHours()) + ':' + pad(t.getMinutes());
}

$('save').onclick = async () => {
  const username = $('username').value.trim();
  const password = $('password').value;
  setBusy(true); setStatus('正在登录学校系统验证…（通常 3-10 秒）', 'warn');
  const r = await api('/study/account', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username, password, verify: true }) });
  setBusy(false);
  if (r.ok) { $('password').value = ''; setStatus('保存成功，已通过验证：' + (r.termName || '') + ' 共 ' + r.courseCount + ' 门课。正在后台刷新数据…', 'ok'); loadSummary(); }
  else setStatus(r.error || '保存失败', 'err');
};
$('test').onclick = async () => {
  setBusy(true); setStatus('正在测试登录…', 'warn');
  const r = await api('/study/account/verify', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username: $('username').value.trim(), password: $('password').value }) });
  setBusy(false);
  setStatus(r.ok ? ('登录成功：' + (r.termName || '') + ' 共 ' + r.courseCount + ' 门课。') : ('登录失败：' + (r.error || '')), r.ok ? 'ok' : 'err');
};
$('clear').onclick = async () => {
  if (!confirm('清除后这台电脑就不再记得你的学号密码，北交大肥鱼会停止工作。确定清除？')) return;
  setBusy(true);
  const r = await api('/study/account', { method: 'DELETE' });
  setBusy(false);
  if (r.ok) { $('username').value = ''; $('password').value = ''; setStatus('已清除本机保存的凭证。', 'ok'); $('summary').textContent = ''; loadAccount(); }
  else setStatus(r.error || '清除失败', 'err');
};
$('refresh').onclick = async () => {
  setBusy(true); setStatus('正在登录学校系统拉取作业 / 邮件 / 课表…（10-40 秒）', 'warn');
  const r = await api('/study/refresh', { method: 'POST' });
  setBusy(false);
  setStatus(r.ok ? '刷新完成。' : ('刷新失败：' + (r.error || '')), r.ok ? 'ok' : 'err');
  loadSummary();
};

function setWeekStatus(text, cls) { $('weekStatus').textContent = text || ''; $('weekStatus').className = 'status ' + (cls || ''); }
async function saveWeek(value) {
  setWeekStatus(value ? ('正在保存校准 ' + value + ' 并重新拉取课表…') : '正在恢复为自动推算…', 'warn');
  const r = await api('/study/settings', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ week1Monday: value }) });
  if (r.ok) setWeekStatus('已保存。当前周 = 第 ' + (r.week || 0) + ' 周' + (r.tickOk ? '。' : ('（重新拉取出错：' + (r.tickError || '') + '）')), 'ok');
  else setWeekStatus(r.error || '保存失败', 'err');
  loadSummary();
}
$('saveWeek').onclick = async () => { await saveWeek($('week1Monday').value.trim()); };
$('clearWeek').onclick = async () => { $('week1Monday').value = ''; await saveWeek(''); };

loadAccount();
loadSummary();
</script>
</body>
</html>`;
}

/* 服务端页面 /kb（P1c：原"托盘层"降级——零原生依赖，浏览器即开即用）。
 * 功能：问答检索 + 手动笔记表单 + 导出按钮 + 周报情绪曲线。 */
function kbPageHtml() {
  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>北交大肥鱼·知识笔记本</title>
<style>
  :root { --c1:#00f0ff; --c2:#ff2bd6; --bg:#0a0e1c; --card:#121a30; --text:#dfe6f5; --dim:#8894b8; }
  * { box-sizing:border-box; }
  body { margin:0; background:radial-gradient(1200px 600px at 80% -10%, #1a2450 0%, var(--bg) 55%); color:var(--text); font-family:system-ui, "Microsoft YaHei", sans-serif; min-height:100vh; }
  .wrap { max-width:860px; margin:0 auto; padding:28px 18px 60px; }
  h1 { font-size:22px; margin:0 0 4px; }
  h1 b { background:linear-gradient(90deg,var(--c1),var(--c2)); -webkit-background-clip:text; background-clip:text; color:transparent; }
  .sub { color:var(--dim); font-size:13px; margin-bottom:20px; }
  .card { background:var(--card); border:1px solid #223; border-radius:14px; padding:16px; margin-bottom:16px; }
  .card h2 { font-size:15px; margin:0 0 12px; color:var(--c1); }
  label { display:block; font-size:12px; color:var(--dim); margin:10px 0 4px; }
  input, textarea, select { width:100%; background:#0d1428; border:1px solid #2a3; border-radius:8px; color:var(--text); padding:9px 11px; font-size:14px; outline:none; }
  input:focus, textarea:focus { border-color:var(--c1); }
  textarea { min-height:70px; resize:vertical; }
  .row { display:flex; gap:10px; }
  .row > * { flex:1; }
  button { background:linear-gradient(90deg,var(--c1),var(--c2)); border:0; color:#061018; font-weight:600; border-radius:8px; padding:10px 18px; font-size:14px; cursor:pointer; margin-top:12px; }
  button.ghost { background:transparent; border:1px solid #2a3; color:var(--text); }
  .hit { border-left:3px solid var(--c1); background:#0d1428; border-radius:0 8px 8px 0; padding:10px 12px; margin:8px 0; }
  .hit .t { font-weight:600; }
  .hit .s { color:#9fb4dd; font-size:13px; margin:4px 0; }
  .hit .meta { color:var(--dim); font-size:11px; }
  .status { font-size:12px; color:var(--dim); min-height:18px; margin-top:8px; }
  .charts { display:flex; gap:6px; align-items:flex-end; height:120px; padding:6px 2px 0; }
  .bar { flex:1; background:linear-gradient(180deg,var(--c1),var(--c2)); border-radius:4px 4px 0 0; position:relative; min-height:2px; }
  .bar .d { position:absolute; top:-18px; left:0; right:0; text-align:center; font-size:10px; color:var(--dim); }
  .bar .v { position:absolute; bottom:-18px; left:0; right:0; text-align:center; font-size:10px; color:var(--dim); }
  .tags { color:var(--c2); font-size:12px; }
</style>
</head>
<body>
<div class="wrap">
  <h1><b>大肥鱼</b> · 知识笔记本</h1>
  <div class="sub">打工第 N 天，我把记住的东西都泡在这里。搜索、记录、回忆。</div>

  <div class="card">
    <h2>问我点什么</h2>
    <div class="row">
      <input id="q" placeholder="例如：限流问题 / 429 / C++" onkeydown="if(event.key==='Enter')search()">
      <button onclick="search()">搜索</button>
    </div>
    <div class="status" id="qstatus"></div>
    <div id="results"></div>
  </div>

  <div class="card">
    <h2>记一条重要信息</h2>
    <label>标题 *</label>
    <input id="t" placeholder="一句话记下这件事">
    <label>正文</label>
    <textarea id="b" placeholder="展开记一下（可选）"></textarea>
    <div class="row">
      <div><label>标签</label><input id="g" placeholder="逗号分隔，如：bug,429"></div>
      <div><label>来源类型</label>
        <select id="s"><option value="note">note（手动笔记）</option><option value="fact">fact（事实卡）</option><option value="concept">concept（概念）</option></select>
      </div>
    </div>
    <button onclick="add()">保存</button>
    <div class="status" id="astatus"></div>
  </div>

  <div class="card">
    <h2>导出 &amp; 备份（/kb/export 即备份路径）</h2>
    <div class="row">
      <button class="ghost" onclick="exports('md')">导出 Markdown</button>
      <button class="ghost" onclick="exports('json')">导出 JSON</button>
      <button class="ghost" onclick="location.href='/kb/rebuild'">重建索引</button>
    </div>
  </div>

  <div class="card">
    <h2>近 7 天情绪（周报曲线取数 /stats/mood）</h2>
    <div class="charts" id="mood"></div>
    <div class="status" id="mstatus"></div>
  </div>
</div>
<script>
const esc = s => String(s||'').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
async function api(url, opts) {
  const r = await fetch(url, opts);
  return r.json().catch(() => ({}));
}
function status(el, msg) { document.getElementById(el).textContent = msg || ''; }

async function search() {
  const q = document.getElementById('q').value.trim();
  status('qstatus', q ? '…' : '请输入关键词');
  if (!q) { document.getElementById('results').innerHTML=''; return; }
  const r = await api('/kb/search?q=' + encodeURIComponent(q));
  const box = document.getElementById('results');
  if (!r.ok) { status('qstatus', '检索失败：' + (r.error||'')); return; }
  status('qstatus', (r.strategy==='or' ? '(宽松召回) ' : '') + '策略=' + r.strategy + '，命中 ' + r.count + ' 条');
  box.innerHTML = r.hits.map(h => '<div class="hit"><div class="t">' + esc(h.title) +
    '</div><div class="s">' + esc(h.snippet || h.body) + '</div>' +
    '<div class="meta">source=' + esc(h.source) + ' · link=' + esc(h.link) + ' · score=' + Math.round(h.score*100)/100 +
    (h.tags ? ' · <span class="tags">' + esc(h.tags) + '</span>' : '') + '</div></div>').join('') ||
    '<div class="status">没找到。换个说法，或者先记一条？</div>';
}

async function add() {
  const t = document.getElementById('t').value.trim();
  if (!t) { status('astatus', '标题必填'); return; }
  const body = {
    title: t,
    body: document.getElementById('b').value.trim(),
    tags: document.getElementById('g').value.replace(/，/g,','),
    source: document.getElementById('s').value
  };
  const r = await api('/kb/add', { method:'POST', headers:{'content-type':'application/json'}, body: JSON.stringify(body) });
  if (r.ok) { status('astatus','已记录 ✓（link=' + r.item.link + '）'); document.getElementById('t').value=''; document.getElementById('b').value=''; document.getElementById('g').value=''; }
  else status('astatus', '失败：' + (r.error||''));
}

function exports(fmt) { location.href = '/kb/export?format=' + fmt; }

(async function loadMood() {
  const r = await api('/stats/mood?days=7');
  if (!r.ok || !r.days) { status('mstatus','数据源未就绪'); return; }
  const box = document.getElementById('mood');
  box.innerHTML = r.days.map(d => {
    const h = d.mood===null ? 4 : Math.max(3, Math.round(8 + d.mood*80)); // null 日显示 4% 高点
    return '<div class="bar" style="height:' + h + '%"><span class="d">' + d.date.slice(5) + '</span><span class="v">' + (d.mood===null?'-':d.mood.toFixed(1)) + '</span></div>';
  }).join('');
})();
</script>
</body>
</html>`;
}

const server = http.createServer(async (req, res) => {
  const urlObj = new URL(req.url, 'http://127.0.0.1');
  const url = req.url.split('?')[0];
  const method = req.method;

  try {
    if (method === 'OPTIONS') {
      return sendJSON(res, 204, {});
    }

    if (url === '/health' && method === 'GET') {
      return sendJSON(res, 200, { ok: true, wsPort: WS_PORT, httpPort: HTTP_PORT, lastHeartbeatAt, time: new Date().toISOString() });
    }

    /* 壁纸心跳：页面存活时每 5 秒上报一次。watchdog 据此判断页面是否卡死。 */
    if (url === '/heartbeat' && method === 'POST') {
      const body = await readBody(req);
      lastHeartbeatAt = Date.now();
      try {
        sqlRun('INSERT INTO heartbeats (time, state) VALUES (?, ?)', [new Date().toISOString(), String(body.state || '')]);
      } catch (_) {}
      return sendJSON(res, 200, { ok: true });
    }

    if (url === '/profile' && method === 'GET') {
      return sendJSON(res, 200, readProfile());
    }

    if (url === '/llm/health' && method === 'GET') {
      // 不泄露 apiKey：只上报是否启用与模型名
      return sendJSON(res, 200, {
        ok: true,
        llm: { enabled: !!(LLM.enabled && LLM.apiKey && LLM.baseUrl), provider: LLM.provider, model: LLM.model }
      });
    }

    if (url === '/diary/refine' && method === 'POST') {
      const body = await readBody(req);
      if (!(LLM.enabled && LLM.apiKey && LLM.baseUrl)) {
        return sendJSON(res, 503, { ok: false, error: 'llm not enabled (check fish-config.json llm.enabled/apiKey/baseUrl)' });
      }
      if (!body || !body.draft) return sendJSON(res, 400, { ok: false, error: 'missing draft' });
      const text = await callLLM(body).catch((err) => '');
      if (!text) return sendJSON(res, 502, { ok: false, error: 'llm call failed' });
      return sendJSON(res, 200, { ok: true, text });
    }

    if (url === '/event' && method === 'POST') {
      const body = await readBody(req);
      const payload = ingestEvent(body);
      return sendJSON(res, 200, { ok: true, event: payload });
    }

    if (url === '/event/latest' && method === 'GET') {
      const rows = sqlAll('SELECT * FROM events ORDER BY id DESC LIMIT 20');
      return sendJSON(res, 200, {
        events: rows.map((r) => {
          let p = {};
          try { p = JSON.parse(r.payload); } catch (_) { p = {}; }
          return { seq: r.id, time: r.time, ...p };
        })
      });
    }

    /* ---------- 学业提醒（作业 / 邮件） ---------- */
    if (url === '/notify/status' && method === 'GET') {
      return sendJSON(res, 200, { ok: true, ...poller.status() });
    }

    if (url === '/notify/refresh' && method === 'POST') {
      const r = await poller.tick('manual');
      return sendJSON(res, 200, {
        ok: r.ok,
        error: r.error,
        notified: !!r.notified,
        payload: r.payload || null,
        ...poller.status()
      });
    }

    /* 壁纸端已下线提醒卡片，这两个调试接口不再向 WS 广播学业事件；
     * 保留返回体，方便用 curl 查看最近一次提醒内容。 */
    if (url === '/notify/dismiss' && method === 'POST') {
      return sendJSON(res, 200, { ok: true, note: '壁纸端已停用提醒卡片；桌面宠物通过 /study/alerts 独立消费提醒。' });
    }

    /* 预览：返回最近一次提醒内容（不再推给壁纸）。 */
    if (url === '/notify/preview' && method === 'POST') {
      const body = await readBody(req);
      let payload = poller.status().lastPayload;
      if (body && body.demo) {
        payload = {
          kind: 'study', level: 'urgent',
          title: '密码学的作业还有 5 小时截止！',
          items: [
            { kind: 'homework', course: '密码学', title: '第2次作业-第4-7章作业', deadline: '2026-11-03 00:00', hoursLeft: 5, urgent: true },
            { kind: 'email', count: 2, latest: [{ subject: '关于课程调整的通知', from: '教务处' }] }
          ],
          counts: { homework: 1, email: 2 },
          at: new Date().toISOString()
        };
      }
      if (!payload) return sendJSON(res, 200, { ok: false, error: '暂无可预览的提醒（待办为空）' });
      return sendJSON(res, 200, { ok: true, payload, note: '壁纸端已停用提醒卡片；此接口仅返回内容供调试。' });
    }

    /* 一键调参：手动触发一次拉取（同 /notify/refresh，语义更直白） */
    if (url === '/notify/check' && method === 'GET') {
      const r = await poller.tick('manual');
      return sendJSON(res, 200, { ok: r.ok, error: r.error, notified: !!r.notified, ...poller.status() });
    }

    /* ---------- 北交大肥鱼：给桌宠用的学业接口 ---------- */

    /* ---------- 学业账号（设置界面读写） ----------
     * GET    /study/account          当前账号（不返回密码）
     * POST   /study/account          保存账号密码，可选 verify=true 先验证再落盘
     * POST   /study/account/verify   只验证不保存（「测试登录」按钮）
     * DELETE /study/account          清除本机保存的凭证
     * 保存成功后 poller.reload() 会立刻按新账号重排轮询，无需重启服务。 */
    if (url === '/study/account' && method === 'GET') {
      return sendJSON(res, 200, {
        ok: true,
        school: schoolProfile(),
        week1Monday: (poller.getConfig() && poller.getConfig().week1Monday) || '',
        ...credentialsInfo(),
        poller: { enabled: poller.status().enabled, configured: poller.status().configured, account: poller.status().account }
      });
    }

    if (url === '/study/account/verify' && method === 'POST') {
      const body = await readBody(req);
      const username = String(body.username || '').trim() || credentialsInfo().username;
      /* 密码留空时用已保存的密码验证（只改学号 / 只想确认现在能不能登）。 */
      let password = String(body.password || '');
      if (!password) password = readCredentials().password || '';
      if (!username || !password) return sendJSON(res, 400, { ok: false, error: '请填写学号和密码' });
      const r = await verifyCredentials({ username, password });
      return sendJSON(res, r.ok ? 200 : 401, r);
    }

    if (url === '/study/account' && method === 'POST') {
      const body = await readBody(req);
      const username = String(body.username || '').trim();
      const password = String(body.password || '');
      try {
        if (!username) return sendJSON(res, 400, { ok: false, error: '请填写学号 / 账号' });

        /* 默认先验证再保存：避免把错密码写进去，之后每 30 分钟登录失败一次。
         * 关掉「测试登录」开关（verify=false）时不拦。 */
        if (body.verify !== false) {
          const pwd = password || readCredentials().password || '';
          if (!pwd) return sendJSON(res, 400, { ok: false, error: '请填写密码' });
          const v = await verifyCredentials({ username, password: pwd });
          if (!v.ok) return sendJSON(res, 401, { ok: false, error: '登录验证失败：' + v.error, verified: false });
          saveCredentials({ username, password: pwd });
          invalidateStudyCaches();
          const st = poller.reload();
          return sendJSON(res, 200, {
            ok: true, verified: true, ...v,
            account: credentialsInfo(),
            poller: { configured: st.configured, account: st.account }
          });
        }

        saveCredentials({ username, password });
        invalidateStudyCaches();
        const st = poller.reload();
        return sendJSON(res, 200, { ok: true, verified: false, account: credentialsInfo(), poller: { configured: st.configured, account: st.account } });
      } catch (err) {
        return sendJSON(res, 400, { ok: false, error: err.message });
      }
    }

    if (url === '/study/account' && method === 'DELETE') {
      clearCredentials();
      invalidateStudyCaches();
      const st = poller.reload();
      return sendJSON(res, 200, { ok: true, account: credentialsInfo(), poller: { configured: st.configured } });
    }

    if (url === '/study/settings' && method === 'GET') {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      return res.end(studySettingsHtml());
    }

    /* 保存学业偏好（目前是「教学周校准」week1Monday）。只改 notify/config.json，不碰凭证。 */
    if (url === '/study/settings' && method === 'POST') {
      const body = await readBody(req);
      let v = String(body.week1Monday || '').trim();
      if (v) {
        if (!/^\d{4}-\d{2}-\d{2}$/.test(v)) return sendJSON(res, 400, { ok: false, error: '日期格式应为 YYYY-MM-DD' });
        const d = new Date(v + 'T00:00:00+08:00');
        if (isNaN(d.getTime())) return sendJSON(res, 400, { ok: false, error: '不是合法的日期' });
      }
      const cfgPath = path.join(__dirname, 'notify', 'config.json');
      let cur = {};
      try { cur = JSON.parse(fs.readFileSync(cfgPath, 'utf8')); } catch (_) { cur = {}; }
      cur.week1Monday = v; // 空串 = 用学期 beginDate 自动推算
      try { fs.writeFileSync(cfgPath, JSON.stringify(cur, null, 2) + '\n', 'utf8'); }
      catch (err) { return sendJSON(res, 500, { ok: false, error: '写入配置失败：' + err.message }); }
      // 重新加载配置并立刻重新采集一次，让当前周立刻生效
      poller.reload();
      const tick = await poller.tick('study-settings');
      const after = poller.status().snapshot || {};
      return sendJSON(res, 200, {
        ok: true,
        week1Monday: v,
        tickOk: tick.ok,
        tickError: tick.error || '',
        week: after.week || 0
      });
    }

    /* 空教室自习：返回某一教学周所有教室的「7 天 × N 节」空闲网格。
     * 参数：zc（周次，缺省=当前校准周）、jxlh（教学楼 id）、jash（教室名关键字）、refresh=1 强制重取。
     * 附带 periodTimes（课表里的真实节次时间）与 today（用周内日期匹配，避免时区误差）。 */
    if (url === '/study/rooms' && method === 'GET') {
      const snap = poller.status().snapshot || {};
      const zc = Number(urlObj.searchParams.get('zc')) || snap.week || 0;
      const jxlh = String(urlObj.searchParams.get('jxlh') || '').trim();
      const jash = String(urlObj.searchParams.get('jash') || '').trim();
      const force = urlObj.searchParams.get('refresh') === '1';
      const key = `${zc}|${jxlh}|${jash}`;
      const sched = snap.schedule || {};
      const decorate = (data) => {
        const now = new Date();
        const md = `${String(now.getMonth() + 1).padStart(2, '0')}月${String(now.getDate()).padStart(2, '0')}日`;
        const matched = (data.days || []).find((d) => d.date === md);
        return {
          ...data,
          periodTimes: Array.isArray(sched.periods) ? sched.periods : [],
          today: (matched && matched.day) || ((now.getDay() + 6) % 7) + 1
        };
      };
      const hit = roomsCache.get(key);
      if (!force && hit && Date.now() - hit.at < ROOMS_TTL_MS) {
        return sendJSON(res, 200, decorate({ ...hit.data, cached: true }));
      }
      try {
        // 同一组条件并发时只跑一次真实登录
        let job = roomsPending.get(key);
        if (!job) {
          job = collectEmptyRooms({ zc, jxlh, jash })
            .finally(() => roomsPending.delete(key));
          roomsPending.set(key, job);
        }
        const data = await job;
        roomsCache.set(key, { at: Date.now(), data });
        return sendJSON(res, 200, decorate({ ...data, cached: false }));
      } catch (err) {
        // 登录失败时若有旧缓存，仍然返回旧数据并标注，避免界面直接空白
        if (hit) return sendJSON(res, 200, decorate({ ...hit.data, cached: true, stale: true, warning: err.message }));
        return sendJSON(res, 502, { ok: false, error: err.message, configured: !!poller.status().configured });
      }
    }

    /* ---------- 作业 DDL / 课件资料（北交大肥鱼扩展，2026-11-14） ---------- */

    /* 把轮询快照里的作业映射成与 cc-homework.mjs 一致的展示结构。
     * 旧快照可能还没有 remainingText 等新字段，这里用 remainingInfo() 现算兜底，
     * 这样服务刚重启、还没重新采集过时，桌宠也不会显示空白。 */
    const normalizeHomework = (h, catalog = []) => {
      const rem = remainingInfo(h.deadline || h.endTime || '', !!h.submitted);
      const kind = h.kind || 'homework';
      /* 旧快照没有 homeworkUrl：用快照里的课程元数据补出深链，不必重新登录。 */
      const c = catalog.find((x) => x.courseNum === (h.courseNum || ''));
      const fallbackUrl = c ? homeworkUrl({
        course_num: c.courseNum, id: c.id, fz_id: c.fzId,
        teacher_id: c.teacherId, xq_code: c.xqCode
      }, h.hwId || h.homeworkId || '', c.xqCode) : '';
      return {
        uid: h.uid || '',
        homeworkId: h.hwId || h.homeworkId || '',
        title: h.title || '未命名作业',
        courseName: h.course || h.courseName || '',
        courseNum: h.courseNum || '',
        kind,
        kindLabel: h.kindLabel || ({ experiment: '实验', report: '课程报告' }[kind] || '作业'),
        openDate: h.openAt || h.openDate || '',
        endTime: h.deadline || h.endTime || '',
        submitted: !!h.submitted,
        submittedAt: h.submittedAt || '',
        score: h.score || '',
        remainingText: h.remainingText ?? rem.text,
        remainingDays: h.remainingDays ?? rem.days,
        overdue: h.overdue ?? rem.overdue,
        dueSoon: h.dueSoon ?? rem.dueSoon,
        rowTag: h.rowTag || rem.tag,
        url: h.homeworkUrl || h.url || fallbackUrl
      };
    };

    /* 「实验栏覆盖」核对（2026-11-14 新增）。
     * 老师常把作业放进「课程考核 → 实验」，作业页必须能看出：
     *   ① 每门课实验栏实际抓到几条  ② 平台自报几条（getTaskCountByCourseId）
     *   ③ 实验栏条目与作业栏有无同名 / 同截止的对应条目
     * 平台自报的任务计数口径与实际列表未必完全一致，故缺口只作为「待核对」提示，不判定为漏抓。 */
    const normTitle = (s) => String(s || '').toLowerCase()
      .replace(/[\s。．.：:、，,（）()【】\[\]_\-]/g, '');
    const buildExperimentCoverage = (items, raw = []) => {
      const byNum = new Map();
      for (const h of items) {
        const k = String(h.courseNum || h.courseName || '');
        if (!byNum.has(k)) byNum.set(k, []);
        byNum.get(k).push(h);
      }
      const rawByNum = new Map((raw || []).map((c) => [String(c.courseNum || c.course || ''), c]));
      const keys = [...new Set([...byNum.keys(), ...rawByNum.keys()])];
      const courses = keys.map((key) => {
        const own = byNum.get(key) || [];
        const r = rawByNum.get(key) || {};
        const exp = own.filter((x) => x.kind === 'experiment');
        const hw = own.filter((x) => x.kind === 'homework');
        const rep = own.filter((x) => x.kind === 'report');
        const captured = hw.length + exp.length + rep.length;
        /* 平台 getTaskCountByCourseId 把「课程考核 → 实验 / 课程报告」也计入「作业」任务数，
         * 所以拿它跟「作业栏 + 实验栏 + 报告栏」的总数对账，才能发现实验栏漏抓。
         * 例：密码学自报 5，作业栏 2 + 实验栏 3 = 5，恰好补齐。 */
        const tc = r.taskCounts || null;
        const reportedHomework = tc && tc['作业'] !== undefined ? Number(tc['作业']) : null;
        const reportedExperiment = tc && tc['实验'] !== undefined ? Number(tc['实验'])
          : (r.experimentCountFromTask === undefined || r.experimentCountFromTask === null
              ? null : Number(r.experimentCountFromTask));
        let matched = 0;
        for (const e of exp) {
          const t = normTitle(e.title);
          const same = t ? hw.find((h) => normTitle(h.title) === t) : null;
          const byTime = !same && e.endTime ? hw.find((h) => h.endTime && h.endTime === e.endTime) : null;
          if (same || byTime) matched += 1;
        }
        return {
          course: r.course || (own[0] && own[0].courseName) || key,
          courseNum: key,
          homework: hw.length,
          experiment: exp.length,
          report: rep.length,
          captured,
          reportedHomework,
          reportedExperiment,
          gap: reportedHomework === null ? null : Math.max(0, reportedHomework - captured),
          matched,
          taskCounts: tc
        };
      });
      const sum = (f) => courses.reduce((n, c) => n + (Number(f(c)) || 0), 0);
      return {
        checkedCourses: courses.length,
        homeworkTotal: sum((c) => c.homework),
        experimentTotal: sum((c) => c.experiment),
        reportTotal: sum((c) => c.report),
        capturedTotal: sum((c) => c.captured),
        reportedHomeworkTotal: sum((c) => c.reportedHomework),
        reportedExperimentTotal: sum((c) => c.reportedExperiment),
        gapTotal: sum((c) => c.gap),
        matchedTotal: sum((c) => c.matched),
        coursesWithExperiment: courses.filter((c) => c.experiment > 0).length,
        /* 只列「实验/报告栏有东西」或「对账有缺口」的课程，避免 11 门课全是 0 的噪音 */
        courses: courses.filter((c) => c.experiment > 0 || c.report > 0 || (c.gap || 0) > 0)
      };
    };

    /* 完整作业清单（含已交 / 逾期），带剩余天数与课程平台深链。
     * 默认读轮询快照（毫秒级）；refresh=1 才实时登录重拉全部课程。 */
    if (url === '/study/homeworks' && method === 'GET') {
      const force = urlObj.searchParams.get('refresh') === '1';
      const onlyPending = urlObj.searchParams.get('onlyPending') === '1';
      try {
        let items = [];
        let meta = {};
        let coverageRaw = [];
        if (force) {
          const r = await fetchHomeworksForCli(loadStudyConfig(), { log: (m) => console.log('[作业同步]', m) });
          items = r.items;
          coverageRaw = r.coverage || [];
          meta = { at: r.fetchedAt, xqCode: r.xqCode, xqName: r.xqName,
            courseCount: r.courseCount, fromSnapshot: false };
        } else {
          const st = poller.status();
          const snap = st.snapshot || {};
          items = (snap.homework || []).map((h) => normalizeHomework(h, snap.courseCatalog || []));
          coverageRaw = snap.courseCoverage || [];
          meta = { at: snap.at || null, xqCode: snap.term || '', xqName: snap.termName || '',
            courseCount: snap.courseCount || 0, fromSnapshot: true };
        }
        items.sort((a, b) =>
          (a.endTime || '9999-12-31').localeCompare(b.endTime || '9999-12-31') ||
          a.courseName.localeCompare(b.courseName));
        const shown = onlyPending ? items.filter((x) => !x.submitted && !x.overdue) : items;
        return sendJSON(res, 200, {
          ok: true, ...meta,
          total: items.length,
          pending: items.filter((x) => !x.submitted && !x.overdue).length,
          count: shown.length,
          items: shown,
          coverage: buildExperimentCoverage(items, coverageRaw)
        });
      } catch (err) {
        return sendJSON(res, 502, { ok: false, error: err.message, items: [] });
      }
    }

    /* 课件资料：学期 / 课程列表。refresh=1 强制重新登录并重建会话。 */
    if (url === '/study/courseware/courses' && method === 'GET') {
      try {
        const r = await courseware.listCourses({
          xqCode: String(urlObj.searchParams.get('xqCode') || ''),
          refresh: urlObj.searchParams.get('refresh') === '1'
        });
        return sendJSON(res, 200, r);
      } catch (err) {
        return sendJSON(res, 502, { ok: false, error: err.message, courses: [], terms: [] });
      }
    }

    /* 课件目录树。course 是 listCourses 返回的 key，不接受任意 URL。 */
    if (url === '/study/courseware/folders' && method === 'GET') {
      const key = String(urlObj.searchParams.get('course') || '');
      if (!key) return sendJSON(res, 400, { ok: false, error: '缺少 course 参数', folders: [] });
      try {
        return sendJSON(res, 200, await courseware.listFolders({ course: key }));
      } catch (err) {
        return sendJSON(res, 502, { ok: false, error: err.message, folders: [] });
      }
    }

    /* 某个目录下的课件文件列表。 */
    if (url === '/study/courseware/files' && method === 'GET') {
      const key = String(urlObj.searchParams.get('course') || '');
      const folderId = String(urlObj.searchParams.get('folder') || '');
      if (!key) return sendJSON(res, 400, { ok: false, error: '缺少 course 参数', files: [] });
      try {
        return sendJSON(res, 200, await courseware.listFiles({ course: key, folderId }));
      } catch (err) {
        return sendJSON(res, 502, { ok: false, error: err.message, files: [] });
      }
    }

    /* 下载勾选的课件：body = { course, folderId, folderName, ids[] }。
     * 服务端只按 id 从平台重新查文件，不接受前端传 URL，避免任意地址下载。 */
    if (url === '/study/courseware/download' && method === 'POST') {
      const body = await readBody(req);
      try {
        const r = await courseware.downloadFiles({
          course: String(body.course || ''),
          folderId: String(body.folderId || ''),
          folderName: String(body.folderName || ''),
          ids: Array.isArray(body.ids) ? body.ids.map(String) : []
        });
        return sendJSON(res, r.ok ? 200 : 400, r);
      } catch (err) {
        return sendJSON(res, 502, { ok: false, error: err.message, results: [] });
      }
    }

    /* 本地已下载的课件（扫桌面目录，用户手工删文件也会同步反映）。 */
    if (url === '/study/courseware/local' && method === 'GET') {
      try {
        return sendJSON(res, 200, courseware.localFiles());
      } catch (err) {
        return sendJSON(res, 502, { ok: false, error: err.message, items: [] });
      }
    }

    /* 汇总：作业（待交/全部）+ 邮件 + 课表 + 学期周次。
     * 桌宠的「北交大肥鱼 → 作业 / 邮件 / 课表」三个子项都从这里取数。 */
    if (url === '/study/summary' && method === 'GET') {
      const st = poller.status();
      const snap = st.snapshot || {};
      const schedule = snap.schedule || null;
      return sendJSON(res, 200, {
        ok: !!snap.at,
        at: snap.at || null,
        configured: !!(st.configured),
        account: st.account || '',
        school: SCHOOL,
        error: st.lastError || '',
        term: snap.term || '',
        termName: snap.termName || '',
        termStart: snap.termStart || '',
        week: snap.week || 0,
        courseCount: snap.courseCount || 0,
        pending: snap.pending || [],
        homework: snap.homework || [],
        experiments: snap.experiments || [],
        reports: snap.reports || [],
        assessments: snap.assessments || [],
        courseCoverage: snap.courseCoverage || [],
        emails: snap.emails || { ok: false, unread: 0, latest: [] },
        schedule: schedule ? {
          ok: schedule.ok,
          error: schedule.error || '',
          week: schedule.week || 0,
          today: schedule.today || 0,
          todayEntries: schedule.todayEntries || [],
          entries: schedule.entries || [],
          periods: schedule.periods || [],
          termName: schedule.termName || ''
        } : null,
        nextRunAt: st.nextRunAt || null
      });
    }

    /* 立即刷新一次（桌宠里手动点「刷新」时用）：会真实登录学校系统拉一遍，耗时数秒。 */
    if (url === '/study/refresh' && method === 'POST') {
      const r = await poller.tick('desktop-pet');
      return sendJSON(res, 200, { ok: r.ok, error: r.error || '', ...poller.status() });
    }

    /* 提醒队列增量拉取：桌宠带 after=<上一次拿到的 id> 轮询，只返回更新的提醒。 */
    if (url === '/study/alerts' && method === 'GET') {
      const after = Number((urlObj.searchParams.get('after') || '0')) || 0;
      const { seq, alerts } = readAlerts();
      return sendJSON(res, 200, {
        ok: true,
        seq,
        alerts: alerts.filter((a) => Number(a.id) > after)
      });
    }

    /* 清空提醒队列（桌宠里「全部已读」时用）。 */
    if (url === '/study/alerts/clear' && method === 'POST') {
      const r = await poller.clearAlerts();
      return sendJSON(res, 200, { ok: true, ...r });
    }

    /* 壁纸端调试上报：直接落日志，返回 200（替代独立 debug_server.py）。 */
    if (url === '/wallpaper-debug' && method === 'POST') {
      const body = await readBody(req);
      console.log('[debug] ' + (typeof body === 'string' ? body : JSON.stringify(body)));
      return sendJSON(res, 200, { ok: true });
    }

    if (url === '/approve' && method === 'POST') {
      const body = await readBody(req);
      const decision = body.decision || (body.allow ? 'allow' : 'deny');
      const id = body.id || body.requestId;
      const now = new Date().toISOString();
      if (id) {
        sqlRun('UPDATE approvals SET decision = ?, decision_time = ? WHERE id = ?', [decision, now, id]);
        const rec = sqlGet('SELECT * FROM approvals WHERE id = ?', [id]);
        if (!rec) sqlRun('INSERT INTO approvals (id, time, payload, decision, decision_time) VALUES (?, ?, ?, ?, ?)',
          [id, now, JSON.stringify(body), decision, now]);
      }
      if (decision === 'always' && body.command) {
        sqlRun('INSERT INTO always (command, created) VALUES (?, ?)', [body.command, now]);
      }
      broadcast({ type: 'approved', decision, id: id || body.command, time: now });
      return sendJSON(res, 200, { ok: true, decision, id, alwaysRules: sqlAll('SELECT * FROM always').length });
    }

    const m = url.match(/^\/approvals\/([\w\-:]+)$/);
    if (m && method === 'GET') {
      const rec = sqlGet('SELECT * FROM approvals WHERE id = ?', [m[1]]);
      if (!rec) return sendJSON(res, 404, { ok: false, error: 'not found' });
      return sendJSON(res, 200, { ok: true, approval: { ...rec, payload: JSON.parse(rec.payload || '{}') } });
    }

    /* ---------- 知识笔记本（P1a） ---------- */
    if (url.startsWith('/kb')) {
      if (!knowledge) return sendJSON(res, 503, { ok: false, error: 'knowledge unavailable (sqlite down)' });

      // POST /kb/add —— 入知识（upsert 幂等；note 未带 link 时生成一次性 key，Bug B 修法）
      if (url === '/kb/add' && method === 'POST') {
        const body = await readBody(req);
        const rec = knowledge.addKnowledge({
          source: body.source, link: body.link, title: body.title,
          body: body.body, tags: body.tags
        });
        return sendJSON(res, 200, { ok: true, item: rec });
      }

      // GET /kb/search?q= —— 检索问答（buildMatch + snippet）
      if (url === '/kb/search' && method === 'GET') {
        const q = (req.url.split('?')[1] || '').match(/q=([^&]*)/);
        const limit = (req.url.split('?')[1] || '').match(/limit=([^&]*)/);
        const result = knowledge.searchKnowledge(
          q ? decodeURIComponent(q[1]) : '',
          limit ? Number(limit[1]) : 5
        );
        return sendJSON(res, 200, { ok: true, ...result });
      }

      // GET /kb/export?format=md|json —— 导出（= 备份路径）
      if (url === '/kb/export' && method === 'GET') {
        const fmt = (req.url.split('?')[1] || '').match(/format=(\w+)/);
        const f = fmt ? fmt[1] : 'md';
        if (f === 'json') {
          res.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
          return res.end(knowledge.exportJson());
        }
        res.writeHead(200, { 'content-type': 'text/markdown; charset=utf-8', 'cache-control': 'no-store' });
        return res.end(knowledge.exportMd());
      }

      // GET /kb/rebuild —— 自建重建管道（禁用原生 rebuild）
      if (url === '/kb/rebuild' && method === 'GET') {
        knowledge.rebuildKb();
        return sendJSON(res, 200, { ok: true, count: knowledge.count() });
      }

      // GET /kb —— 服务端页面（P1c：问答输入框 + 笔记表单 + 导出按钮）
      if (url === '/kb' && method === 'GET') {
        res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
        return res.end(kbPageHtml());
      }

      // DELETE /kb/:id —— 删除一条
      const dm = url.match(/^\/kb\/(\d+)$/);
      if (dm && method === 'DELETE') {
        const removed = knowledge.removeKnowledge(Number(dm[1]));
        return sendJSON(res, 200, { ok: true, removed });
      }

      return sendJSON(res, 404, { ok: false, error: 'unknown /kb route' });
    }

    /* ---------- 周报情绪数据管道（P1c） ----------
     * /stats/mood?days=7 —— 从 events 表聚合近 N 天每日情绪，供周报情绪曲线格取数。 */
    if (url === '/stats/mood' && method === 'GET') {
      if (!db) return sendJSON(res, 503, { ok: false, error: 'no sqlite' });
      const days = Math.min(Math.max(Number((req.url.split('?')[1] || '').match(/days=(\d+)/)?.[1]) || 7, 1), 90);
      const cutoff = new Date(Date.now() - (days - 1) * 864e5).toISOString().slice(0, 10);
      const rows = db.prepare("SELECT time, type, payload FROM events WHERE time >= ?").all(cutoff);
      const byDay = {};
      for (let i = 0; i < days; i++) {
        const d = new Date(Date.now() - i * 864e5).toISOString().slice(0, 10);
        byDay[d] = { date: d, score: 0, n: 0, mood: null };
      }
      for (const r of rows) {
        const d = (r.time || '').slice(0, 10);
        if (!byDay[d]) continue;
        let delta = 0;
        if (['approved', 'task_complete', 'done', 'commit'].includes(r.type)) delta = 1;
        else if (['deny', 'error', 'rate_limit'].includes(r.type)) delta = -1;
        byDay[d].score += delta; byDay[d].n += 1;
      }
      const out = Object.keys(byDay).sort().map((d) => {
        const x = byDay[d];
        x.mood = x.n === 0 ? null : x.score / x.n; // 1..-1，null = 无事件日
        return x;
      });
      return sendJSON(res, 200, { ok: true, days: out });
    }

    return sendJSON(res, 404, { ok: false, error: 'not found' });
  } catch (err) {
    return sendJSON(res, 400, { ok: false, error: err.message });
  }
});

server.listen(HTTP_PORT, () => {
  console.log('[北交大肥鱼] 桌宠后端已启动 http://127.0.0.1:' + HTTP_PORT);
  console.log('[北交大肥鱼] 学业接口 /study/* · 大脑接口 /stats/mood /kb/* /event/latest');
});

/* ---------- 启动学业提醒轮询 ---------- */
poller.start();

/* ---------- 开机后台预热（消除「首次点学习中心卡十几秒」） ----------
 * 根因：课件模块是内存单例会话，开机必空；第一次点「课程课件」/「课表」要实时
 * SSO 登录学校系统（十几秒），还会和 poller 首次采集并发顶号。
 * 这里在开机后台「串行」预热：先等学业快照就绪（poller 首轮），再预建课件会话。
 * 全程不阻塞 HTTP 服务；失败也无副作用，首次点击时仍会按原逻辑重试。
 * 只在配置了账号时预热；用一个 Promise 合并，避免重复触发。 */
let studyPrewarm = null;
function prewarmStudyOnLaunch() {
  if (studyPrewarm) return studyPrewarm;
  studyPrewarm = (async () => {
    try {
      const st = poller.status();
      if (!st.configured) { console.log('[预热] 未配置账号，跳过'); return; }
      // ① 等学业快照就绪：poller 首轮在 onLaunchDelaySec 后触发，最多再等它采集完。
      const delaySec = Math.max(5, Number(poller.getConfig()?.onLaunchDelaySec ?? 60));
      const waitMs = Math.max(2000, delaySec * 1000 + 45000);
      const deadline = Date.now() + waitMs;
      while (!poller.status().snapshot && Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 2000));
      }
      if (poller.status().snapshot) console.log('[预热] 学业快照已就绪');
      else console.log('[预热] 学业快照尚未就绪，继续预热课件（首次点作业页可能稍慢）');
      // ② 学业采集结束后再建课件会话，避免两个 SSO 并发互相顶号。
      await courseware.prewarm();
      console.log('[预热] 全部完成，首次点学习中心应无需等待登录');
    } catch (err) {
      console.warn('[预热] 异常（不影响功能）：' + err.message);
    }
  })();
  return studyPrewarm;
}
// 启动 3 秒后开始预热，给服务自身一点稳定时间；计时器不阻止进程退出。
const prewarmTimer = setTimeout(prewarmStudyOnLaunch, 3000);
if (prewarmTimer.unref) prewarmTimer.unref();

/* 桌宠独立版刻意不含 watchdog：壁纸的心跳看护属于壁纸项目职责，
 * 桌宠后端绝不能去 kill / 重启 Lively 进程。此处留空，二者互不干扰。 */