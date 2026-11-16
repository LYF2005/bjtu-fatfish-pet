/* 邮课叮 · 课件资料（课程 PPT / 文档下载）
 *
 * 参考同学写的 `new/_internal/crawler/课件爬虫.py`，改写成 Node 原生模块：
 *   - 复用 bjtu.mjs 的 MIS 登录与「会话守卫」，不另造一套账号 / cookie 体系
 *   - 目录树 → 文件列表 → getFilePlayUrl 直链 → 原件推导 → 预览版兜底
 *   - 文件名清洗、已存在跳过、*.part 原子落盘
 *   - 模块级单例会话 + 串行队列，避免连续点「课程 / 目录 / 下载」时反复登录顶号
 *
 * 安全：接口只接受课程 key 和文件 id，服务端从自己的课程列表 / 文件列表里查，
 * 不接受前端传任意 URL，避免变成任意地址下载器。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { openStudySession, loadConfig } from './bjtu.mjs';
import { atomicWrite, coursewareDir, defaultCoursewareDir, safeName } from './storage.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const CP = 'http://123.121.147.7:88';
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';
const SESSION_TTL_MS = 25 * 60 * 1000;

/* ---------- 单例会话 + 串行队列 ---------- */
let cached = null;
let queue = Promise.resolve();

function serialize(fn) {
  const run = queue.then(fn, fn);
  queue = run.catch(() => {});        // 单个任务失败不能卡死后面的请求
  return run;
}

function courseKey(c) {
  return String(c.course_num || c.id || '');
}

async function fetchTermsAndCourses(sess, ctx) {
  let terms = [];
  try {
    const r = await sess.text(`${CP}/ve/back/rp/common/teachCalendar.shtml?method=queryCurrentXq`);
    terms = JSON.parse(r.txt).result || [];
  } catch (_) { /* 学期接口挂了也能靠课程列表兜底 */ }
  const cur = terms.find((x) => String(x.currentFlag) === '2') || terms[0] || {};
  const xqCode = String(cur.xqCode || '');
  ctx.xqCode = xqCode;

  let courses = [];
  try {
    const r = await sess.text(
      `${CP}/ve/back/coursePlatform/course.shtml?method=getCourseList&pagesize=100&page=1&xqCode=${encodeURIComponent(xqCode)}`);
    courses = JSON.parse(r.txt).courseList || [];
  } catch (_) { /* 下面会用「刷新」重试 */ }
  if (!courses.length && xqCode) {
    try {
      const r = await sess.text(`${CP}/ve/back/coursePlatform/course.shtml?method=getCourseList&pagesize=100&page=1&xqCode=`);
      courses = JSON.parse(r.txt).courseList || [];
    } catch (_) {}
  }
  return { terms, xqCode, xqName: String(cur.xqName || cur.CNAME || ''), courses };
}

async function getSession(force = false) {
  const cfg = loadConfig();
  /* 会话绑定的是「登录那一刻的账号」。换学号后即使还在 TTL 内也必须重登，
   * 否则 /study/courseware/* 会继续返回上一个账号的课程与课件。 */
  const account = String(cfg.username || '');
  if (!force && cached && Date.now() - cached.at < SESSION_TTL_MS &&
      cached.ctx.sessionId && cached.account === account) return cached;
  const { sess, ctx } = await openStudySession(cfg, (m) => console.log('[课件]', m));
  const t = await fetchTermsAndCourses(sess, ctx);
  cached = { sess, ctx, cfg, account, at: Date.now(), ...t };
  return cached;
}

/** 账号保存 / 清除时由桥接服务调用，立刻丢弃旧账号的会话与课程缓存。 */
export function invalidate() {
  cached = null;
}

/**
 * 开机预热：后台提前完成 SSO 登录 + 拉取学期/课程列表，把首次点「课程课件」/「课表」
 * 的十几秒实时登录挪到开机后台。返回是否命中了已缓存的会话。
 * 只走单例会话（serialize），不会与前台请求并发顶号。
 */
export async function prewarm() {
  try {
    const hadCache = !!(cached && cached.ctx && cached.ctx.sessionId);
    await withSession(async () => {});   // 仅建立/刷新会话与课程列表
    console.log('[课件] 开机预热完成：' + ((cached && cached.courses && cached.courses.length) || 0) + ' 门课');
    return { ok: true, warmed: !hadCache };
  } catch (err) {
    console.warn('[课件] 开机预热失败（不影响首次点击时重试）：' + err.message);
    return { ok: false, error: err.message };
  }
}

/** 所有对外的采集动作都走这里：串行 + 失效时强制重登重试一次。 */
async function withSession(fn, force = false) {
  return serialize(async () => {
    let lastErr;
    for (let attempt = 0; attempt < 2; attempt++) {
      const s = await getSession(force || attempt > 0);
      try {
        return await fn(s);
      } catch (err) {
        lastErr = err;
        if (attempt === 1 || !/会话|登录|session|过期/i.test(err.message)) throw err;
        cached = null;
        console.warn('[课件] 会话异常，重新登录后重试：' + err.message);
      }
    }
    throw lastErr;
  });
}

function courseByKey(s, key) {
  const hit = (s.courses || []).find((c) => courseKey(c) === String(key));
  if (!hit) throw new Error('课程不存在或列表已过期，请点「刷新」重新加载课程');
  return hit;
}

function resourceUrl(s, course, method, extra = {}) {
  const q = new URLSearchParams({
    method,
    courseId: String(course.course_num || ''),
    cId: String(course.id || ''),
    xkhId: String(course.fz_id || ''),
    xqCode: String(course.xq_code || s.xqCode || ''),
    docType: '1',
    ...extra
  });
  return `${CP}/ve/back/coursePlatform/courseResource.shtml?${q}`;
}

async function parseJSON(r) {
  try { return JSON.parse(r.txt) || {}; } catch (_) { return {}; }
}

/* ---------- 对外：课程 / 目录 / 文件 ---------- */

export async function listCourses({ xqCode = '', refresh = false } = {}) {
  return withSession(async (s) => {
    if (xqCode && xqCode !== s.xqCode) {
      // 指定历史学期：重新拉该学期课程，不重建会话
      try {
        const r = await s.sess.text(
          `${CP}/ve/back/coursePlatform/course.shtml?method=getCourseList&pagesize=100&page=1&xqCode=${encodeURIComponent(xqCode)}`);
        const list = (await parseJSON(r)).courseList || [];
        if (list.length) {
          s.xqCode = xqCode;
          s.courses = list;
          s.xqName = (s.terms.find((t) => String(t.xqCode) === xqCode) || {}).xqName || xqCode;
        }
      } catch (_) { /* 保持当前学期 */ }
    }
    return {
      ok: true,
      xqCode: s.xqCode,
      xqName: s.xqName,
      terms: (s.terms || []).map((t) => ({
        code: String(t.xqCode || ''),
        name: String(t.xqName || t.xqCode || ''),
        current: String(t.currentFlag) === '2'
      })),
      courses: (s.courses || []).map((c) => ({
        key: courseKey(c),
        name: String(c.name || c.course_num || '未命名课程'),
        teacher: String(c.teacher_name || ''),
        courseNum: String(c.course_num || '')
      })),
      downloadDir: coursewareDir(s.cfg),
      cached: !refresh
    };
  }, refresh);
}

async function fetchFoldersRaw(s, course) {
  const r = await s.sess.text(resourceUrl(s, course, 'stuQueryCourseResourceBag'));
  const nodes = (await parseJSON(r)).nodes || [];
  return nodes.map((n) => ({
    id: String(n.id ?? ''),
    name: String(n.name || '未命名目录'),
    parentId: String(n.pId ?? n.pid ?? '0')
  }));
}

export async function listFolders({ course, refresh = false } = {}) {
  return withSession(async (s) => {
    const c = courseByKey(s, course);
    const folders = await fetchFoldersRaw(s, c);
    return { ok: true, course: String(c.name || ''), courseKey: course, count: folders.length, folders };
  }, refresh);
}

function normalizeFile(f) {
  return {
    id: String(f.resId ?? f.id ?? ''),
    name: String(f.rpName ?? f.name ?? '未命名文件'),
    ext: String(f.extName ?? '').replace(/^\./, '').toLowerCase(),
    sizeMB: Number(f.rpSize) || 0,
    uploadAt: String(f.uploadTime ?? f.createTime ?? '')
  };
}

async function fetchFilesRaw(s, course, folderId) {
  const r = await s.sess.text(resourceUrl(s, course, 'stuQueryUploadResourceForCourseList', {
    up_id: String(folderId || ''),
    searchName: ''
  }));
  const d = await parseJSON(r);
  return { raw: d.resList || [], files: (d.resList || []).map(normalizeFile) };
}

export async function listFiles({ course, folderId = '', refresh = false } = {}) {
  return withSession(async (s) => {
    const c = courseByKey(s, course);
    const { files } = await fetchFilesRaw(s, c, folderId);
    return { ok: true, course: String(c.name || ''), folderId, count: files.length, files };
  }, refresh);
}

/* ---------- 直链与下载 ---------- */

/** getFilePlayUrl 返回 onlinePreview?url=Base64，解码出 kk 服务器上的真实路径。 */
async function getPlayUrl(s, fileId) {
  const r = await s.sess.text(
    `${CP}/ve/back/coursePlatform/dataSynAction.shtml?method=getFilePlayUrl&id=${encodeURIComponent(fileId)}&type=2`);
  const url = String((await parseJSON(r)).url || '');
  if (!url) return '';
  const m = url.match(/onlinePreview\?url=([A-Za-z0-9+/=]+)/);
  if (!m) return url;
  const b = m[1] + '='.repeat((4 - (m[1].length % 4)) % 4);
  return Buffer.from(b, 'base64').toString('utf8');
}

/** 转换版 /rp/YYYY/MM/DD/swf/<id>.pdf → 原件 /rp/YYYY/MM/DD/<id>.<真扩展名> */
export function deriveOriginal(kkUrl, ext) {
  const m = String(kkUrl || '').match(/(.*?\/rp\/\d{4}\/\d{2}\/\d{2})\/swf\/(\d+)\.\w+/);
  if (!m || !ext) return '';
  return `${m[1]}/${m[2]}.${String(ext).replace(/^\./, '')}`;
}

function absoluteUrl(u) {
  return /^https?:\/\//i.test(u) ? u : new URL(u, CP).href;
}

/** 按文件魔数判断真实类型。
 *
 * 实测坑：getFilePlayUrl 的 kk 直链有时返回的就是 .ppt / .pptx 原件（文件头
 * d0cf11e0 / 504b0304），参考工具却一律把直链兜底存成 .pdf，导致 PPT 后缀错误、
 * 双击打不开。这里按内容定后缀，并区分「原件」与「转换后的预览版 PDF」。
 */
function chooseExt(buf, declaredExt, fallback = 'pdf') {
  const e = String(declaredExt || '').replace(/^\./, '').toLowerCase();
  const hex = buf.subarray(0, 8).toString('hex');
  const ascii4 = buf.subarray(0, 4).toString('latin1');
  if (ascii4 === '%PDF') return 'pdf';
  if (hex.startsWith('d0cf11e0a1b11ae1')) {           // 老式 Office 复合文档
    return ['ppt', 'doc', 'xls', 'vsd', 'msg'].includes(e) ? e : (e || 'doc');
  }
  if (hex.startsWith('504b0304')) {                     // OOXML / zip
    return ['pptx', 'docx', 'xlsx', 'zip', 'ppt', 'doc', 'xls'].includes(e) ? e : (e || 'zip');
  }
  if (hex.startsWith('89504e47')) return 'png';
  if (hex.startsWith('ffd8ff')) return 'jpg';
  return e || String(fallback).replace(/^\./, '');
}

function looksLikeErrorPage(buf) {
  const head = buf.subarray(0, 512).toString('latin1');
  return /<html|<!doctype|^\s*\{\s*"(error|status|message)"/i.test(head);
}

async function fetchBuffer(url) {
  const res = await fetch(absoluteUrl(url), {
    headers: { 'user-agent': UA },
    signal: AbortSignal.timeout(90_000),
    redirect: 'follow'
  });
  if (res.status !== 200) return Buffer.alloc(0);
  const buf = Buffer.from(await res.arrayBuffer());
  if (buf.length < 200 || looksLikeErrorPage(buf)) return Buffer.alloc(0);
  return buf;
}

async function downloadOne(s, f, baseDir) {
  const name = safeName(f.rpName ?? f.name ?? 'unnamed');
  const ext = String(f.extName ?? '').replace(/^\./, '');
  const fileId = String(f.resId ?? f.id ?? '');
  const base = { id: fileId, name, ext };
  try {
    const kk = await getPlayUrl(s, fileId);
    if (!kk) throw new Error('未取到预览直链');
    const original = deriveOriginal(kk, ext);
    const candidates = [];
    if (original) candidates.push({ kind: '原件（推导）', url: original });
    candidates.push({ kind: '平台直链', url: kk });

    /* 优先拿到「非 PDF 的原件」；只有全部都返回 PDF 时才用预览版。
     * 这样 .ppt/.pptx/.docx 下载下来就是能直接双击打开的真文件。 */
    let best = null;
    for (const c of candidates) {
      const buf = await fetchBuffer(c.url);
      if (!buf.length) continue;
      const realExt = chooseExt(buf, ext, 'pdf');
      const preview = realExt === 'pdf' && !!ext && ext.toLowerCase() !== 'pdf';
      const got = { buf, ext: realExt, kind: preview ? '预览版 PDF' : '原件', source: c.kind };
      if (!preview) { best = got; break; }
      if (!best) best = got;
    }
    if (!best) return { ...base, status: 'failed', note: '原件与预览版都下载失败' };

    const dest = path.join(baseDir, `${name}.${best.ext}`);
    if (fs.existsSync(dest) && fs.statSync(dest).size > 200) {
      return { ...base, status: 'skipped', kind: best.kind, ext: best.ext, path: dest,
        size: fs.statSync(dest).size, note: '已存在，跳过' };
    }
    atomicWrite(dest, best.buf);                       // 先 .part 再原子替换
    return { ...base, status: 'ok', kind: best.kind, ext: best.ext, path: dest,
      size: best.buf.length, note: best.kind === '预览版 PDF' ? '原件不可用，已存转换版 PDF' : '' };
  } catch (err) {
    return { ...base, status: 'failed', note: err.message };
  }
}

/** 下载指定课程目录下勾选的文件。ids 为空表示全选。
 *  文件信息一律从平台重新拉取，前端只传 id，不接受 URL。 */
export async function downloadFiles({ course, folderId = '', folderName = '', ids = [], refresh = false } = {}) {
  return withSession(async (s) => {
    const c = courseByKey(s, course);
    const { raw, files } = await fetchFilesRaw(s, c, folderId);
    const want = ids.length
      ? raw.filter((f) => ids.map(String).includes(String(f.resId ?? f.id ?? '')))
      : raw;
    if (!want.length) return { ok: false, error: '没有可下载的文件（请先在文件列表里勾选）', results: [] };

    const baseDir = path.join(
      coursewareDir(s.cfg),
      safeName(c.name || c.course_num || '课程'),
      safeName(folderName || '根目录')
    );
    fs.mkdirSync(baseDir, { recursive: true });

    const results = [];
    for (const f of want) results.push(await downloadOne(s, f, baseDir));
    return {
      ok: true,
      course: String(c.name || ''),
      dir: baseDir,
      count: results.length,
      downloaded: results.filter((r) => r.status === 'ok').length,
      skipped: results.filter((r) => r.status === 'skipped').length,
      failed: results.filter((r) => r.status === 'failed').length,
      results,
      totalFiles: files.length
    };
  }, refresh);
}

/** 扫描下载目录，返回本地已有的课件（用户手工删文件也会同步反映）。 */
export function localFiles() {
  const root = coursewareDir(loadConfig());
  const items = [];
  const walk = (dir, rel) => {
    if (!fs.existsSync(dir)) return;
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) { walk(p, path.join(rel, e.name)); continue; }
      if (e.name.endsWith('.part')) continue;
      try {
        const st = fs.statSync(p);
        items.push({
          name: e.name,
          path: p,
          rel: path.join(rel, e.name),
          size: st.size,
          modifiedAt: st.mtime.toISOString()
        });
      } catch (_) { /* 文件刚被删掉 */ }
    }
  };
  walk(root, '');
  items.sort((a, b) => b.modifiedAt.localeCompare(a.modifiedAt));
  return { ok: true, dir: root, count: items.length, items: items.slice(0, 300) };
}

export { coursewareDir, defaultCoursewareDir };
export default { listCourses, listFolders, listFiles, downloadFiles, localFiles, deriveOriginal };
