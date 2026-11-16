/* 大肥鱼·学业提醒 — 北交大数据采集器
 *
 * 链路：
 *   1. MIS 统一认证（SM2 国密加密密码）→ 拿到 SSO 会话
 *   2. 课程平台 SSO → 课程列表 → 逐门课拉作业（含截止时间 / 提交状态）
 *   3. 邮箱 SSO → coremail 收件箱 → 未读邮件
 *
 * 只读，不修改任何数据。所有请求走同一个 cookie 池（按域名隔离）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import smCrypto from 'sm-crypto';
import { readCredentials } from './credentials.mjs';

const sm2 = smCrypto.sm2;
const __dirname = path.dirname(fileURLToPath(import.meta.url));

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0';

/* ---------- 学校端点配置（外置，便于适配 / 校方改版时社区维护） ---------- */
const DEFAULTS = {
  mis: 'https://mis.bjtu.edu.cn',
  coursePlatform: 'http://123.121.147.7:88',
  mail: 'https://mail.bjtu.edu.cn',
  sso: 'https://sso.bjtu.edu.cn/sso/login',
  aa: 'https://aa.bjtu.edu.cn',
  classrooms: 'https://aa.bjtu.edu.cn/classroom/timeholdresult/room_view/'
};
function loadEndpoints() {
  try {
    const raw = JSON.parse(fs.readFileSync(path.join(__dirname, 'endpoints.json'), 'utf8'));
    return { ...DEFAULTS, ...raw };
  } catch (_) { return { ...DEFAULTS }; }
}
const EP = loadEndpoints();
const MIS = EP.mis;
const CP = EP.coursePlatform;
const MAIL = EP.mail;
const AA = EP.aa;
const ROOM_VIEW = EP.classrooms;
const SSO_LOGIN = EP.sso;

/* 学校适配层：现在内置北交大（MIS 统一认证 + 课程平台 + coremail + 教务系统）。
 * 端点来自 endpoints.json；将来接第二所学校时，改配置 + 按需扩展钩子即可，
 * 上层接口、UI、桌宠都不用改。 */
export const SCHOOL = {
  id: EP.school?.id || 'bjtu',
  name: EP.school?.name || '北京交通大学',
  short: EP.school?.short || 'BJTU',
  loginMode: EP.school?.loginMode || 'MIS 统一认证（SM2 国密加密）',
  coursePlatform: `${CP}/ve/back/coursePlatform/coursePlatform.shtml?method=toCoursePlatformIndex`,
  mail: `${MAIL}/`,
  schedule: `${AA}/`,
  classrooms: ROOM_VIEW
};

/* 设置界面要展示的「这个学校支持什么」，也让用户一眼看懂数据来源。 */
export function schoolProfile() {
  return {
    id: SCHOOL.id,
    name: SCHOOL.name,
    short: SCHOOL.short,
    loginMode: SCHOOL.loginMode,
    coursePlatform: SCHOOL.coursePlatform,
    mail: SCHOOL.mail,
    schedule: SCHOOL.schedule,
    classrooms: SCHOOL.classrooms,
    capabilities: ['作业', '实验', '课程报告', '未读邮件', '课表', '空教室自习'],
    accountHint: '学号（MIS 统一认证账号）',
    passwordHint: 'MIS 统一认证密码'
  };
}

/* ---------- 配置 ----------
 * 账号密码来自加密凭证（credentials.mjs），其余偏好来自 config.json。
 * 兼容旧版：如果凭证文件里还没有账号，而 config.json 里还留着明文字段，会兜底使用。 */
export function loadConfig() {
  const p = path.join(__dirname, 'config.json');
  let cfg = {};
  try { cfg = JSON.parse(fs.readFileSync(p, 'utf8')); } catch (_) { cfg = {}; }
  let cred = { username: '', password: '' };
  try { cred = readCredentials(); } catch (err) { console.warn('[学业账号] 读取凭证失败：' + err.message); }
  return {
    ...cfg,
    enabled: cfg.enabled !== false,
    school: cfg.school || SCHOOL.id,
    username: cred.username || cfg.username || '',
    password: cred.password || cfg.password || '',
    accountError: cred.error || '',
    intervalMinutes: Number(cfg.intervalMinutes) || 30,
    dueSoonHours: Number(cfg.dueSoonHours) || 72,
    onLaunchDelaySec: Number(cfg.onLaunchDelaySec) || 90,
    /* 教学周校准：学校教务系统返回的 beginDate 是学期/注册起始日，未必等于
     * 「教学第 1 周」的周一。若不一致，填这里（YYYY-MM-DD，留空=用 beginDate 自动推算），
     * 当前周就能和教务系统对齐。例如北交大 2026 秋实际第 1 周从 2026-09-14 开始。 */
    week1Monday: (cfg.week1Monday && String(cfg.week1Monday).trim()) ? String(cfg.week1Monday).trim() : ''
  };
}

/* ---------- HTTP 会话（按域名隔离 cookie） ---------- */
function createSession() {
  const jars = new Map();
  const jarOf = (u) => {
    const h = new URL(u).host;
    if (!jars.has(h)) jars.set(h, new Map());
    return jars.get(h);
  };
  function store(u, res) {
    const j = jarOf(u);
    const list = res.headers.getSetCookie ? res.headers.getSetCookie() : [];
    for (const line of list) {
      const [pair] = line.split(';');
      const i = pair.indexOf('=');
      if (i > 0) j.set(pair.slice(0, i).trim(), pair.slice(i + 1).trim());
    }
  }
  const cookieOf = (u) => Array.from(jarOf(u), ([k, v]) => `${k}=${v}`).join('; ');

  async function req(url, opts = {}) {
    const res = await fetch(url, {
      redirect: 'manual',
      ...opts,
      headers: { 'user-agent': UA, cookie: cookieOf(url), ...(opts.headers || {}) }
    });
    store(url, res);
    return res;
  }

  async function text(url, opts = {}) {
    const res = await req(url, opts);
    const buf = Buffer.from(await res.arrayBuffer());
    const ct = res.headers.get('content-type') || '';
    const head = buf.slice(0, 900).toString('latin1');
    let cs = ((ct.match(/charset=([\w-]+)/i) || [])[1] ||
      (head.match(/charset=["']?([\w-]+)/i) || [])[1] || 'utf-8').toLowerCase();
    if (cs === 'gb2312' || cs === 'gb18030') cs = 'gbk';
    let out = '';
    try { out = new TextDecoder(cs).decode(buf); } catch (_) { out = buf.toString('utf8'); }
    return { res, txt: out };
  }

  return { req, text, cookieOf };
}

/* ---------- HTML 转纯文本 ---------- */
function stripHtml(s) {
  return String(s || '')
    .replace(/<[^>]*>/g, ' ')
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/\s+/g, ' ')
    .trim();
}

/** 从 SSO 登录失败页的 `var error = {...};` 里抠出服务器给的真实提示。
 *
 * 参考同学写的课件爬虫 `_login_error()`：message 是 JS 转义串（\uXXXX），
 * 必须还原才能看到中文。比原来「整页文本匹配 用户名|密码|验证码」精确得多，
 * 也能把「学校触发风控需要验证码」这类情况说清楚。
 */
function loginErrorMessage(page) {
  const src = String(page || '');
  const block = src.match(/var\s+error\s*=\s*(\{[\s\S]*?\});/);
  if (!block) return '';
  const hit = block[1].match(/"(?:message|msg|errorMsg|error)"\s*:\s*"((?:\\.|[^"\\])*)"/);
  if (!hit) return '';
  const text = hit[1]
    .replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
    .replace(/\\x([0-9a-fA-F]{2})/g, (_, h) => String.fromCharCode(parseInt(h, 16)))
    .replace(/\\n/g, ' ').replace(/\\t/g, ' ')
    .replace(/\\"/g, '"').replace(/\\\\/g, '\\')
    .trim();
  if (!text) return '';
  if (/验证码|校验码|captcha/i.test(text)) {
    return text + `（请先用浏览器打开 ${MIS} 手动登录一次，或稍后再试）`;
  }
  return text;
}

/* ---------- 1. MIS 统一认证登录 ---------- */
async function loginMIS(sess, cfg) {
  const r1 = await sess.req(`${MIS}/auth/nsso/?next=/home/`);
  const loginUrl = new URL(r1.headers.get('location') || '/auth/nsso/', MIS).href;
  const r2 = await sess.req(loginUrl);
  const html = await r2.text();

  const field = (n) => {
    const m = html.match(new RegExp(`name="${n}"\\s+value="([^"]*)"`));
    return m ? m[1].replace(/&amp;/g, '&') : '';
  };
  const cfgMatch = html.match(/var ssoConfig = (\{[\s\S]*?\});/);
  if (!cfgMatch) throw new Error('MIS 登录页结构变化：未找到 ssoConfig');

  const sso = JSON.parse(cfgMatch[1]);
  const publicKeyHex = Buffer.from(sso.sm2.publicKey, 'base64').toString('hex');
  const encPwd = Buffer.from(sm2.doEncrypt(cfg.password, publicKeyHex, 1), 'hex').toString('base64');

  const body = new URLSearchParams({
    username: cfg.username,
    password: encPwd,
    loginType: 'username_password',
    flowId: field('flowId'),
    client_id: field('client_id'),
    redirect_uri: field('redirect_uri'),
    response_type: 'code',
    scope: '',
    state: field('state'),
    captcha: '',
    delegator: '',
    tokenCode: '',
    continue: '',
    asserts: '',
    pageFrom: '',
    submit: '登录'
  }).toString();

  const r3 = await sess.req(SSO_LOGIN, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body
  });

  const cb = r3.headers.get('location');
  if (!cb) {
    const t = await r3.text();
    // 优先用 SSO 页面里结构化的错误提示；拿不到才退回整页文本猜测
    const precise = loginErrorMessage(t);
    if (precise) throw new Error(precise);
    const text = stripHtml(t);
    /* 登录失败时 SSO 会把登录页原样吐回来（含「用户名 / 密码 / 验证码」等字样）。
     * 直接把整页文本抛给用户太吓人，这里识别成一句人话。 */
    if (/用户名|密码|验证码/.test(text)) {
      throw new Error('学号或密码不正确（如果学校要求验证码，请在浏览器里先登录一次）');
    }
    const msg = text.slice(0, 120) || `HTTP ${r3.status}`;
    throw new Error('MIS 登录失败：' + msg);
  }
  await sess.req(cb);
  return true;
}

/* ---------- 2. 进入课程平台 ---------- */
async function enterCoursePlatform(sess) {
  const entry = await sess.req(`${MIS}/module/module/28/`);
  let cur = new URL(entry.headers.get('location') || '/home/', MIS).href;

  for (let i = 0; i < 6; i++) {
    const r = await sess.req(cur);
    const loc = r.headers.get('location');
    if (loc) { cur = new URL(loc, cur).href; continue; }
    const t = await r.text();
    const js = [...t.matchAll(/location\.href\s*=\s*'([^']+)'/g)]
      .map((m) => m[1])
      .filter((u) => u.includes('index.shtml'));
    if (js.length) { cur = js[0]; break; }
    break;
  }

  const idx = await sess.req(cur);
  const loc = idx.headers.get('location');
  const cpUrl = loc ? new URL(loc, CP).href : cur;
  const page = await sess.text(cpUrl);

  const sessionId =
    (page.txt.match(/setRequestHeader\("sessionId",\s*'([A-Za-z0-9]+)'\)/) || [])[1] ||
    (page.txt.match(/sessionId=([A-Za-z0-9]{20,})/) || [])[1] || '';

  return { cpUrl, sessionId, page: page.txt };
}

/* ---------- 2b. 平台会话守卫（会话失效自动恢复） ----------
 * 参考同学写的课件爬虫：SessionExpired + _ajax() 检测「重新登录」→ refresh_session()
 * → 换新 sessionId 重试一次。这里做成包装 sess.text，而不是逐个改采集函数：
 * 所有函数拿到的 sess 是同一个对象，于是零改动就全部具备自动恢复能力；
 * 而且每次请求都从 ctx 取最新 sessionId，不会用到调用时的旧快照。
 */
function sessionExpired(res, txt) {
  if (res.status !== 200) return true;                 // 302 到登录页 / 401 / 5xx
  const head = String(txt || '').slice(0, 8000);
  // 明显的业务 JSON（课程 / 作业 / 目录 / 文件 / 学期…）一律视为有效，避免误判
  if (/^\s*[{[]/.test(head) &&
      /"(courseList|courseNoteList|result|nodes|resList|taskCountMaps|articleList|xqCode|total|STATUS|url|id)"\s*:/.test(head)) return false;
  const title = (head.match(/<title[^>]*>([\s\S]*?)<\/title>/i) || [])[1] || '';
  if (title && /登录|超时|失效|错误/.test(title)) return true;
  return /请重新登录|请重新登陆|登录已超时|登录超时|会话已失效|会话失效|user_not_login|not_login/i.test(head);
}

async function refreshPlatform(sess, ctx, cfg, log) {
  if (ctx._refreshing) return ctx._refreshing;       // 并发出错时只重登一次
  ctx._refreshing = (async () => {
    // ① SSO 会话通常还在，直接重进课程平台即可
    try {
      const again = await enterCoursePlatform(sess);
      if (again.sessionId) {
        ctx.cpUrl = again.cpUrl;
        ctx.sessionId = again.sessionId;
        log('平台会话已恢复（复用统一认证）');
        return true;
      }
    } catch (_) { /* SSO 也过期了，继续走完整重登 */ }

    // ② 用本机保存的账号密码重新登录一次
    if (!cfg.username || !cfg.password) throw new Error('平台会话已过期，且本机没有可用于自动重登的凭证');
    try { await loginMIS(sess, cfg); }
    catch (err) { throw new Error('自动重新登录失败：' + err.message); }
    const again = await enterCoursePlatform(sess);
    if (!again.sessionId) throw new Error('自动重新登录后仍拿不到课程平台会话');
    ctx.cpUrl = again.cpUrl;
    ctx.sessionId = again.sessionId;
    log('平台会话已恢复（重新登录）');
    return true;
  })().finally(() => { ctx._refreshing = null; });
  return ctx._refreshing;
}

/** 给会话装上守卫：课程平台 AJAX 失效时自动恢复并重试一次。 */
export function installSessionGuard(sess, ctx, cfg, log = () => {}) {
  const rawText = sess.text.bind(sess);
  sess.text = async (url, opts = {}) => {
    // 只拦截课程平台 AJAX；MIS / 邮箱 / 教务系统页面原样放行
    if (!String(url).startsWith(CP)) return rawText(url, opts);
    for (let attempt = 0; attempt < 2; attempt++) {
      const headers = { ...(opts.headers || {}) };
      headers.referer = headers.referer || ctx.cpUrl;
      headers.sessionId = ctx.sessionId;             // 永远取最新值
      headers['x-requested-with'] = headers['x-requested-with'] || 'XMLHttpRequest';
      const r = await rawText(url, { ...opts, headers });
      if (!sessionExpired(r.res, r.txt)) return r;
      if (attempt === 1) throw new Error('平台会话已过期，自动重新登录后仍失败');
      log('平台会话失效，正在自动恢复…');
      await refreshPlatform(sess, ctx, cfg, log);
    }
    throw new Error('平台会话已过期');
  };
  return ctx;
}

/** 开一个完整学业会话：登录 MIS → 进课程平台 → 装会话守卫。
 *  供 collect / verifyCredentials / 课件下载 / CLI 共用，避免各处重复登录。 */
export async function openStudySession(cfg = loadConfig(), log = () => {}) {
  if (!cfg.username || !cfg.password) {
    throw new Error(cfg.accountError || '还没填学业账号：右键桌宠 → 邮课叮 → 账号设置，填自己的学号和密码');
  }
  const sess = createSession();
  /* MIS 登录偶发瞬时失败（与轮询器并发时 SSO 状态竞争），重试一次。 */
  let loginErr;
  for (let attempt = 0; attempt < 2; attempt++) {
    try { await loginMIS(sess, cfg); loginErr = null; break; }
    catch (err) { loginErr = err; if (attempt === 0) await new Promise((r) => setTimeout(r, 900)); }
  }
  if (loginErr) throw loginErr;
  const ctx = await enterCoursePlatform(sess);
  if (!ctx.sessionId) throw new Error('课程平台会话建立失败（sessionId 为空）');
  ctx.xqCode = '';
  installSessionGuard(sess, ctx, cfg, log);
  return { sess, ctx, cfg };
}

/* ---------- 3. 当前学期 + 课程列表 ---------- */
async function fetchCourses(sess, ctx) {
  const hdr = { referer: ctx.cpUrl };
  let xqCode = '';
  let termStart = '';
  let termEnd = '';
  let termName = '';
  try {
    const xq = await sess.text(`${CP}/ve/back/rp/common/teachCalendar.shtml?method=queryCurrentXq`, { headers: hdr });
    const d = JSON.parse(xq.txt);
    const cur = (d.result || []).find((x) => String(x.currentFlag) === '2') || (d.result || [])[0];
    xqCode = cur ? cur.xqCode : '';
    termStart = (cur && cur.beginDate) || '';
    termEnd = (cur && cur.endDate) || '';
    termName = (cur && (cur.xqName || cur.CNAME)) || '';
  } catch (_) { /* 用兜底：从课程列表里取 */ }

  const url = `${CP}/ve/back/coursePlatform/course.shtml?method=getCourseList&pagesize=100&page=1&xqCode=${encodeURIComponent(xqCode)}`;
  const res = await sess.text(url, { headers: { ...hdr, sessionId: ctx.sessionId } });
  let list = [];
  try {
    const d = JSON.parse(res.txt);
    list = d.courseList || [];
  } catch (_) { list = []; }

  if (!list.length && xqCode) {
    const res2 = await sess.text(`${CP}/ve/back/coursePlatform/course.shtml?method=getCourseList&pagesize=100&page=1&xqCode=`, { headers: { ...hdr, sessionId: ctx.sessionId } });
    try { list = (JSON.parse(res2.txt).courseList) || []; } catch (_) {}
  }

  return { xqCode, courses: list, termStart, termEnd, termName };
}

/* ---------- 4. 单门课的作业 ---------- */

/* 取这门课的真实教学班 ID 集合。
 *
 * 坑：课程列表里的 course.id 只是学生选课记录之一。一门课若拆成多个教学班
 * （例如「区块链与通证经济」= 132068 + 132069），作业可能挂在不含 course.id
 * 的那个班上。平台前端每次都是从作业页的 #courseIdList 隐藏域里读全量再请求，
 * 这里照做，否则会静默漏掉整门课的作业。
 */
async function fetchCourseClassIds(sess, ctx, course) {
  const url = `${CP}/ve/back/course/courseInfo.shtml?method=toCourseInfo&courseToPage=5` +
    `&courseId=${course.course_num}&teacherId=${course.teacher_id || ''}` +
    `&xkhId=${course.fz_id}&xqCode=${course.xq_code || ctx.xqCode}`;
  try {
    const r = await sess.text(url, { headers: { referer: ctx.cpUrl, sessionId: ctx.sessionId } });
    const m = r.txt.match(/id="courseIdList"[^>]*value="([^"]*)"/);
    const list = m && m[1] ? m[1].trim() : '';
    if (list) return list;
  } catch (_) { /* 拿不到就退回单 id */ }
  return String(course.id);
}

/* ---------- 4.2 课程考核：作业 / 课程报告 / 实验 ----------
 *
 * 平台有新旧两套界面：
 *   旧：courseInfo.shtml?courseToPage=5 + courseWorkInfo.shtml?method=teachHomeWorkDeatil（只有作业）
 *   新：coursePlatform.shtml?method=toCoursePlatform + homeWork.shtml?method=getHomeWorkList
 *       subType=0 作业 / 1 课程报告 / 2 实验
 *
 * 老师常常把作业放在「课程考核 → 实验」里（例如密码学的三次实验），
 * 旧接口完全看不到，这就是之前漏抓的第二个来源。这里按 subType 全量拉一遍。
 */
const ASSESS_SUBTYPES = [
  { code: '2', kind: 'experiment', label: '实验' },
  { code: '1', kind: 'report', label: '课程报告' },
  { code: '0', kind: 'homework', label: '作业' }
];

function isSubmittedStatus(subStatus) {
  const s = String(subStatus || '');
  if (!s) return false;
  return /已提交|已批改|已评分|已退回|已完成/.test(s);
}

async function fetchCourseAssessments(sess, ctx, course) {
  const out = [];
  for (const { code, kind, label } of ASSESS_SUBTYPES) {
    let pageNo = 1;
    let total = Infinity;
    while (pageNo <= 3 && out.filter((x) => x.kind === kind).length < total) {
      const url = `${CP}/ve/back/coursePlatform/homeWork.shtml?method=getHomeWorkList` +
        `&cId=${course.id}&subType=${code}&page=${pageNo}&pagesize=100`;
      let d = null;
      try {
        const r = await sess.text(url, { headers: { referer: ctx.cpUrl, sessionId: ctx.sessionId } });
        d = JSON.parse(r.txt);
      } catch (_) {
        break;
      }
      if (!d || String(d.STATUS) === '1') break;
      const list = d.courseNoteList || [];
      total = Number(d.total) || list.length;
      for (const x of list) {
        if (!x || !x.id) continue;
        const submitted = isSubmittedStatus(x.subStatus);
        out.push({
          uid: `${course.course_num}:${kind}:${x.id}`,
          course: course.name || course.course_num,
          courseNum: course.course_num,
          hwId: String(x.id),
          kind,
          kindLabel: label,
          title: stripHtml(x.title || '').slice(0, 80) || `${label}任务`,
          openAt: x.open_date || '',
          deadline: x.end_time || '',
          submitted,
          submittedAt: x.subTime || '',
          graded: !!x.scoreId || (x.stu_score && x.stu_score !== '未公布成绩'),
          score: x.stu_score || '',
          submitCount: x.submitCount,
          allCount: x.allCount,
          submittedStatus: x.subStatus || '',
          source: 'assessment'
        });
      }
      if (list.length < 100) break;
      pageNo += 1;
    }
  }
  return out;
}

/* 平台自报的任务计数：预习 / 实验 / 作业 / 讨论 / 笔记 / 公告 / 问卷。
 * 用来发现「实验」类任务——这类任务不在作业接口里，只能靠计数判断是否存在。 */
async function fetchTaskCounts(sess, ctx, course) {
  const url = `${CP}/ve/back/rp/common/teachCalendar.shtml?method=getTaskCountByCourseId` +
    `&courseId=${course.id}&userLevel=1&type=3`;
  try {
    const r = await sess.text(url, { headers: { referer: ctx.cpUrl, sessionId: ctx.sessionId } });
    const d = JSON.parse(r.txt);
    if (String(d.STATUS) !== '0' || !Array.isArray(d.taskCountMaps)) return null;
    return Object.fromEntries(d.taskCountMaps.map((x) => [x.teachTask, Number(x.count) || 0]));
  } catch (_) {
    return null;
  }
}

async function fetchCourseHomework(sess, ctx, course, classIds) {
  const body = new URLSearchParams({
    page: '1',
    rows: '100',
    courseIdList: classIds || String(course.id),
    jxrlId: '',
    infoId: '',
    courseId: course.course_num,
    xkhId: course.fz_id,
    xqCode: course.xq_code || ctx.xqCode,
    allOrMe: 'me',
    allOrMe1: 'all',
    user_level: ''
  }).toString();

  const res = await sess.text(`${CP}/ve/back/course/courseWorkInfo.shtml?method=teachHomeWorkDeatil`, {
    method: 'POST',
    headers: {
      referer: ctx.cpUrl,
      'content-type': 'application/x-www-form-urlencoded',
      sessionId: ctx.sessionId
    },
    body
  });

  let d = null;
  try { d = JSON.parse(res.txt); } catch (_) { return []; }
  if (!d || String(d.STATUS) !== '0' || !Array.isArray(d.result)) return [];

  return d.result
    .filter((x) => x && (String(x.fz) === '0' || String(x.fz) === '2' || String(x.fz) === '1'))
    .map((x) => {
      const title =
        (x.documents && x.documents[0] && x.documents[0].fileName) ||
        stripHtml(x.content).slice(0, 60) ||
        '未命名作业';
      return {
        uid: `${course.course_num}:${x.id}`,
        course: course.name || course.course_num,
        courseNum: course.course_num,
        hwId: String(x.id),
        title: stripHtml(title).slice(0, 80),
        openAt: x.openTime || '',
        deadline: x.endTime || '',
        submitted: String(x.submiterStatus) === '1',
        graded: String(x.piGaiStatus) === '1',
        score: x.score || '',
        contentType: String(x.contentType || '0')
      };
    });
}

/* ---------- 5. 邮箱未读 ---------- */
async function fetchEmails(sess) {
  const out = { ok: false, unread: 0, latest: [], error: '' };
  try {
    const em = await sess.req(`${MIS}/osys_sso_email/`);
    const loc = em.headers.get('location') || '';
    const sid = (loc.match(/sid=([A-Za-z0-9]+)/) || [])[1];
    if (!sid) throw new Error('邮箱 SSO 未返回 sid');
    await sess.req(loc);

    // 收件箱最近 50 封，统计未读
    const url = `${MAIL}/coremail/s?func=mbox:listMessages&sid=${sid}&fid=1&order=date&desc=true&start=0&limit=50`;
    const res = await sess.text(url);
    if (res.res.status !== 200) throw new Error('coremail HTTP ' + res.res.status);

    const xml = res.txt;
    const subjects = [...xml.matchAll(/<string name="subject">([\s\S]*?)<\/string>/g)].map((m) => m[1]);
    const froms = [...xml.matchAll(/<string name="from">([\s\S]*?)<\/string>/g)].map((m) => m[1]);
    const dates = [...xml.matchAll(/<date name="receivedDate">([\s\S]*?)<\/date>/g)].map((m) => m[1]);
    const reads = [...xml.matchAll(/<boolean name="read">(\w+)<\/boolean>/g)].map((m) => m[1]);

    const items = [];
    for (let i = 0; i < subjects.length; i++) {
      items.push({
        subject: String(subjects[i] || '').slice(0, 100),
        from: String(froms[i] || '').slice(0, 60),
        date: String(dates[i] || '').slice(0, 24),
        unread: String(reads[i] || '').toLowerCase() === 'false'
      });
    }
    out.ok = true;
    out.unread = items.filter((x) => x.unread).length;
    out.latest = items.filter((x) => x.unread).slice(0, 5);
    out.total = items.length;
  } catch (err) {
    out.error = err.message;
  }
  return out;
}

/* ---------- 6. 学校通知公告 ---------- */
async function fetchNotices(sess, ctx) {
  try {
    const res = await sess.text(`${CP}/ve/back/coursePlatform/message.shtml?method=getArticleList`, {
      headers: { referer: ctx.cpUrl }
    });
    const d = JSON.parse(res.txt);
    return (d.articleList || []).slice(0, 5).map((x) => ({
      id: String(x.id),
      title: String(x.title || '').slice(0, 80),
      date: String(x.createTime || '').slice(0, 10)
    }));
  } catch (_) { return []; }
}

/* ---------- 7. 课表（MIS → 教务系统 module/10 → aa 单点登录） ---------- */
/* AA 已在顶部由 endpoints.json 定义，此处不再重复声明。 */
const DAY_NAMES = ['星期一', '星期二', '星期三', '星期四', '星期五', '星期六', '星期日'];

/* 把单元格 HTML 拆成多行纯文本（课程代码 / 课名 / 周次+教师 / 地点各自成行）。 */
function cellLines(html) {
  return String(html || '')
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<[^>]+>/g, '\u0001')
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/\u0001/g, '\n')
    .split('\n').map((x) => x.trim()).filter(Boolean);
}

function parseScheduleTable(html) {
  const ti = html.indexOf('<table');
  if (ti < 0) return { entries: [], periods: [] };
  const te = html.indexOf('</table>', ti);
  if (te < 0) return { entries: [], periods: [] };
  const tbl = html.slice(ti, te + 8);

  const rows = [...tbl.matchAll(/<tr[^>]*>([\s\S]*?)<\/tr>/gi)].map((m) => m[1]);
  const out = [];
  const periodTimes = new Map(); // 节次 → 上课时间，直接从课表第一列读，避免写死
  let period = 0;

  for (const row of rows) {
    const cells = [...row.matchAll(/<td[^>]*>([\s\S]*?)<\/td>/gi)].map((m) => m[1]);
    if (cells.length < 8) continue;

    const head = cellLines(cells[0]).join(' ');
    const pm = head.match(/第(\d+)节/);
    if (!pm) continue;
    period = Number(pm[1]);
    const time = (head.match(/\[([^\]]+)\]/) || [])[1] || '';
    if (time && !periodTimes.has(period)) periodTimes.set(period, time);

    for (let d = 0; d < 7; d++) {
      const lines = cellLines(cells[d + 1]);
      if (!lines.length) continue;

      const codeLine = lines.find((l) => /^[A-Z0-9]+\s*\[\d+\]$/.test(l)) || '';
      const cm = codeLine.match(/^([A-Z0-9]+)\s*\[(\d+)\]$/);
      // 周次可能写成「第01-08周」「第01-02, 04周」「第01,03,05,07,09,11,13,15周」，
      // 逗号后常带空格，旧正则 /第[\d,\-]+周/ 一遇到空格就断开、整段匹配失败。
      // 这里用「数字/逗号/短横，逗号前后允许空格」的通配，覆盖上述所有写法。
      const WEEK_RE = /第([\d,\-]+(?:\s*,\s*[\d,\-]+)*)周/;
      const weeksLine = lines.find((l) => WEEK_RE.test(l)) || '';
      const wm = weeksLine.match(WEEK_RE);
      const weeks = wm ? wm[1].replace(/\s+/g, '') : '';
      // 老师名在周次行的下一行（节次表标准布局：课程代码/课名/周次/教师/地点），
      // 旧实现只把周次从本行抠掉，结果老师名恒为空。改为取周次行的下一行。
      const wi = weeksLine ? lines.indexOf(weeksLine) : -1;
      const teacher = (wi >= 0 && wi + 1 < lines.length)
        ? lines[wi + 1].replace(/\s*\[(本|辅|双)\]$/, '').trim()
        : '';

      let name = '';
      const ci = lines.indexOf(codeLine);
      for (let i = ci + 1; i < lines.length; i++) {
        if (/第[\d,\-]+周/.test(lines[i])) break;
        const t = lines[i].replace(/\s*\[(本|辅|双)\]$/, '').trim();
        if (t && !/^\d+$/.test(t)) { name = t; break; }
      }

      const place = lines.find((l) => /校区|楼|室|馆|场/.test(l)) || '';

      out.push({
        day: d + 1,
        dayName: DAY_NAMES[d],
        period,
        time,
        code: cm ? cm[1] : codeLine,
        classNo: cm ? cm[2] : '',
        name,
        weeks,
        teacher,
        place
      });
    }
  }
  const periods = [...periodTimes.entries()]
    .map(([p, time]) => ({ period: p, time }))
    .sort((a, b) => a.period - b.period);
  return { entries: out, periods };
}

/* 判断某一周是否落在 "01-08" / "09-16" / "1,3,5-8" 这类周次描述里。 */
function weekInRange(weeks, week) {
  if (!weeks) return true;              // 没写周次：视为每周都有
  if (!week || week < 1) return true;
  for (const part of String(weeks).split(',')) {
    const m = part.trim().match(/^(\d+)\s*-\s*(\d+)$/);
    if (m) {
      if (week >= Number(m[1]) && week <= Number(m[2])) return true;
    } else if (Number(part) === week) {
      return true;
    }
  }
  return false;
}

/* 以学期起始日推算当前教学周（第 1 周从起始日所在周一开始）。 */
function weekOfTerm(termStart, now = new Date()) {
  if (!termStart) return 0;
  const start = new Date(termStart + 'T00:00:00+08:00');
  if (isNaN(start.getTime())) return 0;
  // 归一到周一
  const dow = (start.getDay() + 6) % 7; // 周一=0
  const monday = new Date(start.getTime() - dow * 864e5);
  const diff = now.getTime() - monday.getTime();
  if (diff < 0) return 0;
  return Math.floor(diff / (7 * 864e5)) + 1;
}

async function fetchSchedule(sess) {
  const out = { ok: false, entries: [], periods: [], week: 0, termStart: '', termName: '', error: '' };
  try {
    const mod = await sess.text(`${MIS}/module/module/10/`);
    const action = (mod.txt.match(/<form[^>]*action=["']([^"']*aa\.bjtu\.edu\.cn[^"']*)["']/i) || [])[1];
    if (!action) throw new Error('教务系统未返回单点登录入口');

    await sess.req(action.replace(/&amp;/g, '&'));
    const page = await sess.text(`${AA}/course_selection/courseselect/stuschedule/`);
    if (!page.txt || page.txt.length < 800) throw new Error('课表页为空（可能会话失效）');

    const parsed = parseScheduleTable(page.txt);
    const entries = parsed.entries;
    if (!entries.length) throw new Error('课表里没有解析到课程');

    // 当前显示的学期（页面上有个按钮写 "2026-2027-1 学期"）
    const termName = (page.txt.match(/btn-white btn-xs">\s*([\d]{4}-[\d]{4}-[12])\s*学期/) || [])[1] || '';

    out.ok = true;
    out.entries = entries;
    out.periods = parsed.periods; // 每天上几节、每节几点，都以教务系统实际表格为准
    out.termName = termName;
  } catch (err) {
    out.error = err.message;
  }
  return out;
}

/* ---------- 7b. 空教室自习（教务系统「教室使用查询」） ----------
 * 页面：/classroom/timeholdresult/room_view/?zc=<教学周>
 * 需要 MIS 登录 + aa.bjtu.edu.cn 单点登录（与课表同一入口 module/10）。
 * 表格是「7 天 × 每天 N 节」的占用网格，节数从表头 colspan 读，不写死。
 *
 * 颜色语义严格照抄页面图例原文（widget-header 里 5 个色块）：
 *   #e46868 排课占用 / #9e6868 调课占用 / #394ed6 考试占用
 *   #77bf6d 实验占用 / #d8cc56 其他安排；白色 #fff 为唯一空闲色。
 * 注意：网页的绿色 #77bf6d 是「实验占用」，不是空闲！客户端不得自行把绿色当成空闲。
 * 每格除了 free 位，还保留一位 status 码，前端可以原样还原网页颜色与状态名。 */
/* ROOM_VIEW 已在顶部由 endpoints.json 定义，此处不再重复声明。 */

const ROOM_LEGEND = [
  { code: '0', label: '空闲', color: '#ffffff' },
  { code: '1', label: '排课占用', color: '#e46868' },
  { code: '2', label: '调课占用', color: '#9e6868' },
  { code: '3', label: '考试占用', color: '#394ed6' },
  { code: '4', label: '实验占用', color: '#77bf6d' },
  { code: '5', label: '其他安排', color: '#d8cc56' },
  { code: '9', label: '其他占用', color: '#8a8f98' }
];
const ROOM_COLOR_CODE = new Map([
  ['#e46868', '1'], ['#9e6868', '2'], ['#394ed6', '3'],
  ['#77bf6d', '4'], ['#d8cc56', '5']
]);

/** 把格子底色翻译成网页的状态码；没有底色 / 白底 = 空闲。 */
function roomCellStatus(attrs) {
  const m = attrs.match(/background-color:[ ]*(#[0-9a-fA-F]{3,8})/i);
  if (!m) return '0';
  const c = m[1].toLowerCase();
  if (c === '#fff' || c === '#ffffff') return '0';
  return ROOM_COLOR_CODE.get(c) || '9';
}

function roomCellIsFree(attrs) {
  return roomCellStatus(attrs) === '0';
}

function parseRoomView(html) {
  const out = { ok: false, days: [], periods: [], buildings: [], rooms: [], total: 0, legend: ROOM_LEGEND, error: '' };
  const ti = html.indexOf('<table class="table table-bordered">');
  if (ti < 0) { out.error = '教务系统没有返回教室占用表（可能未登录或会话失效）'; return out; }
  const te = html.indexOf('</table>', ti);
  if (te < 0) { out.error = '教室占用表不完整'; return out; }
  const tbl = html.slice(ti, te + 8);
  const rows = [...tbl.matchAll(/<tr[^>]*>([\s\S]*?)<\/tr>/gi)].map((m) => m[1]);
  if (rows.length < 3) { out.error = '教室占用表没有数据'; return out; }

  /* 表头第 1 行：每天一个 colspan=N 的 th，N 就是每天节数。 */
  const dayHeads = [...rows[0].matchAll(/<th[^>]*colspan="(\d+)"[^>]*>([\s\S]*?)<\/th>/gi)];
  let perDay = 7;
  for (const h of dayHeads) {
    perDay = Number(h[1]) || 7;
    const label = stripHtml(h[2]).trim();          // 例：星期一 11月02日
    const parts = label.split(/\s+/);
    out.days.push({ day: out.days.length + 1, label: parts[0] || '', date: parts.slice(1).join('') || '' });
  }
  if (out.days.length === 0) { out.error = '无法解析星期表头'; return out; }

  /* 表头第 2 行：教室/节次 + 7*perDay 个节次编号。 */
  const periodCells = [...rows[1].matchAll(/<td[^>]*>([\s\S]*?)<\/td>/gi)].map((m) => stripHtml(m[1]));
  for (let p = 0; p < perDay; p++) out.periods.push(periodCells[p] || String(p + 1));

  for (let r = 2; r < rows.length; r++) {
    const cells = [...rows[r].matchAll(/<td([^>]*)>([\s\S]*?)<\/td>/gi)];
    if (cells.length < 2) continue;
    const head = stripHtml(cells[0][2]).trim();    // 例：SY101 (90)
    const sm = head.match(/^(.*?)[ （(]+[（(]([^）)]*)[）)][ ）)]*$/);
    const name = (sm ? sm[1] : head.replace(/[ （(].*$/, '')).trim();
    const seats = sm ? (Number(String(sm[2]).replace(/\D/g, '')) || 0) : 0;
    if (!name) continue;

    const free = Array.from({ length: out.days.length }, () => '0'.repeat(perDay));
    /* status 与 free 一一对应，但保留网页的 5 种占用原因，不再统一压成「占用」。 */
    const status = Array.from({ length: out.days.length }, () => '0'.repeat(perDay));
    for (let i = 1; i < cells.length; i++) {
      const attrs = cells[i][1];
      const t = attrs.match(/title="星期(\d+)[ ]*第(\d+)节"/);
      let d, p;
      if (t) { d = Number(t[1]); p = Number(t[2]); }
      else { const idx = i - 1; d = Math.floor(idx / perDay) + 1; p = (idx % perDay) + 1; }
      if (d < 1 || d > out.days.length || p < 1 || p > perDay) continue;
      const code = roomCellStatus(attrs);
      const sarr = status[d - 1].split(''); sarr[p - 1] = code; status[d - 1] = sarr.join('');
      if (code === '0') {
        const arr = free[d - 1].split(''); arr[p - 1] = '1'; free[d - 1] = arr.join('');
      }
    }
    out.rooms.push({ name, seats, free, status });
  }

  /* 教学楼下拉（供界面筛选，id 传给 jxlh）。 */
  const sel = html.match(/<select[^>]*name="jxlh"[^>]*>([\s\S]*?)<\/select>/i);
  if (sel) {
    for (const o of sel[1].matchAll(/<option[^>]*value="([^"]*)"[^>]*>([\s\S]*?)<\/option>/gi))
      out.buildings.push({ id: o[1], name: stripHtml(o[2]).trim() });
  }
  const pg = html.match(/page_button\([\s]*\d+[\s]*,[\s]*\d+[\s]*,[\s]*\d+[\s]*,[\s]*(\d+)[\s]*\)/);
  out.total = pg ? Number(pg[1]) : out.rooms.length;
  out.ok = out.rooms.length > 0;
  if (!out.ok) out.error = '没有解析到教室数据';
  return out;
}

/** 拉取某一教学周的空教室占用表。zc 省略时按校准后的 week1Monday 推算。 */
export async function collectEmptyRooms({ zc, jxlh = '', jash = '' } = {}) {
  const cfg = loadConfig();
  if (!cfg.username || !cfg.password) throw new Error('还没填学业账号：右键桌宠 → 邮课叮 → 账号设置');
  const started = Date.now();
  const sess = createSession();

  /* MIS 登录偶发瞬时失败（SSO 状态竞争），重试一次。 */
  let loginErr;
  for (let attempt = 0; attempt < 2; attempt++) {
    try { await loginMIS(sess, cfg); loginErr = null; break; }
    catch (err) { loginErr = err; if (attempt === 0) await new Promise((r) => setTimeout(r, 900)); }
  }
  if (loginErr) throw loginErr;
  try { await enterCoursePlatform(sess); } catch (_) { /* 教室查询不依赖课程平台会话 */ }

  const mod = await sess.text(`${MIS}/module/module/10/`);
  const action = (mod.txt.match(/<form[^>]*action=["']([^"']*aa\.bjtu\.edu\.cn[^"']*)["']/i) || [])[1];
  if (!action) throw new Error('教务系统未返回单点登录入口');
  await sess.req(action.replace(/&amp;/g, '&'));

  const week = Number(zc) || weekOfTerm(cfg.week1Monday) || 1;
  const base = { zc: String(week), perpage: '500', page: '1' };
  if (jxlh) base.jxlh = jxlh;
  if (jash) base.jash = jash;

  const first = await sess.text(`${ROOM_VIEW}?${new URLSearchParams(base)}`);
  const parsed = parseRoomView(first.txt);
  if (!parsed.ok) throw new Error(parsed.error);

  /* 兜底：若教室总数超过单页上限，继续翻页（最多 8 页，避免异常时打爆学校系统）。 */
  const perPage = parsed.rooms.length;
  if (perPage > 0 && parsed.total > perPage) {
    const pages = Math.min(8, Math.ceil(parsed.total / perPage));
    for (let p = 2; p <= pages; p++) {
      const q = { ...base, page: String(p) };
      const next = parseRoomView((await sess.text(`${ROOM_VIEW}?${new URLSearchParams(q)}`)).txt);
      if (!next.ok) break;
      parsed.rooms.push(...next.rooms);
    }
  }

  const tm = first.txt.match(/val\("([0-9]{4}-[0-9]{4}-[12][^"]*)"\)/);
  return {
    ok: true,
    at: new Date().toISOString(),
    elapsedMs: Date.now() - started,
    week,
    term: tm ? tm[1] : '',
    pageUrl: `${ROOM_VIEW}?zc=${week}`,
    legend: parsed.legend,
    days: parsed.days,
    periods: parsed.periods,
    buildings: parsed.buildings,
    total: parsed.total,
    count: parsed.rooms.length,
    rooms: parsed.rooms,
    filters: { zc: week, jxlh: jxlh || '', jash: jash || '' }
  };
}

/* ---------- 主入口 ---------- */
export async function collect(cfgOverride) {
  const cfg = cfgOverride || loadConfig();
  if (!cfg.username || !cfg.password) {
    throw new Error(cfg.accountError || '还没填学业账号：右键桌宠 → 设置 → 学业账号，填自己的学号和密码');
  }

  const started = Date.now();
  const { sess, ctx } = await openStudySession(cfg, (m) => console.log('[学业采集]', m));

  const { xqCode, courses, termStart, termEnd, termName } = await fetchCourses(sess, ctx);
  ctx.xqCode = xqCode;

  const homework = [];
  const assessments = [];
  const failedCourses = [];
  const courseCoverage = [];
  for (const course of courses) {
    try {
      const classIds = await fetchCourseClassIds(sess, ctx, course);
      const taskCounts = await fetchTaskCounts(sess, ctx, course);
      const oldList = await fetchCourseHomework(sess, ctx, course, classIds);
      // 课程考核下的三类任务（作业/课程报告/实验）——旧接口看不到实验与课程报告
      const assess = await fetchCourseAssessments(sess, ctx, course);
      assessments.push(...assess);

      /* 新旧接口会返回同一批「作业」，但标题口径不同：
       *   旧接口（courseWorkInfo）= 附件文件名，如「完成 C_05实践内容-扩展界面设计.ppt 中任务。」
       *   新接口（coursePlatform）= 作业名，如「4. 数据库访问技术」
       * 两者截止时间相同。因此先去标题归一化比对，再用截止时间兜底配对，
       * 配对成功时保留旧 uid（提醒去重状态已按它建立），字段以新接口为准。 */
      const norm = (s) => stripHtml(String(s || '')).replace(/[\s。．.：:、，,（）()【】\[\]-]/g, '');
      const byTitle = new Map();
      const byDeadline = new Map();
      for (const h of oldList) {
        byTitle.set(norm(h.title), h);
        const k = h.deadline || '';
        if (k) byDeadline.set(k, byDeadline.has(k) ? null : h); // null = 同截止时间的有多条，不可配对
      }
      const usedOld = new Set();
      const mergedList = [];
      for (const a of assess) {
        if (a.kind !== 'homework') {
          mergedList.push({ ...a });
          continue;
        }
        let prev = byTitle.get(norm(a.title));
        if (prev && !usedOld.has(prev.uid)) {
          usedOld.add(prev.uid);
        } else {
          const cand = byDeadline.get(a.deadline || '');
          prev = cand && !usedOld.has(cand.uid) ? cand : null;
          if (prev) usedOld.add(prev.uid);
        }
        mergedList.push(prev
          ? { ...a, uid: prev.uid, hwId: prev.hwId, kind: 'homework',
              openAt: prev.openAt || a.openAt }
          : { ...a, kind: 'homework' });
      }
      // 旧接口里没被配对的作业（新接口可能没覆盖到的教学班）全部保留
      for (const h of oldList) if (!usedOld.has(h.uid)) mergedList.push(h);
      const list = mergedList;

      // 记录覆盖信息，便于在桌宠接口中确认每门课实际检查到的教学班与任务数。
      // 每条作业补上「打开作业页面」的深链（双击用），以及展示用的课程号
      for (const h of list) {
        h.courseNum = course.course_num || '';
        h.homeworkUrl = homeworkUrl(course, h.hwId, ctx.xqCode);
      }

      courseCoverage.push({
        course: course.name,
        courseNum: course.course_num,
        classIds,
        homeworkCount: list.filter((x) => !x.kind || x.kind === 'homework').length,
        experimentCount: assess.filter((x) => x.kind === 'experiment').length,
        reportCount: assess.filter((x) => x.kind === 'report').length,
        experimentCountFromTask: taskCounts ? (taskCounts['实验'] || 0) : null,
        taskCounts
      });
      homework.push(...list);
    } catch (err) {
      failedCourses.push({ course: course.name, error: err.message });
    }
  }

  // assess 中的实验/课程报告已经在每门课的 mergedList 中并入 homework，不能再次追加，避免重复提醒。

  const emails = await fetchEmails(sess);
  const notices = await fetchNotices(sess, ctx);
  const schedule = await fetchSchedule(sess);

  const now = Date.now();
  for (const h of homework) {
    h.deadlineTs = deadlineTs(h.deadline);
    h.expired = h.deadlineTs ? h.deadlineTs < now : false;
    h.hoursLeft = h.deadlineTs ? Math.round((h.deadlineTs - now) / 36e5 * 10) / 10 : null;
    /* 桌宠「作业 DDL」页要用的展示字段：剩 N 天 / 今天截止 / 已过期 / 逾期标红。 */
    const rem = remainingInfo(h.deadline, h.submitted, now);
    h.remainingText = rem.text;
    h.remainingDays = rem.days;
    h.overdue = rem.overdue;
    h.dueSoon = rem.dueSoon;
    h.rowTag = rem.tag;
    // 旧接口独有的条目在循环里没补到深链，这里按课程号兜底
    if (!h.homeworkUrl) {
      const c = courses.find((x) => x.course_num === h.courseNum);
      if (c) h.homeworkUrl = homeworkUrl(c, h.hwId, ctx.xqCode);
    }
  }

  const pending = homework
    .filter((h) => !h.submitted && !h.expired)
    .sort((a, b) => (a.deadlineTs || 9e15) - (b.deadlineTs || 9e15));

  /* 实验与课程报告的清单（含已提交），已并入 homework/pending 走五档提醒。
   * experiments 保留给前端做「实验进度」展示：真实条目，带截止时间。 */
  const experiments = homework
    .filter((h) => h.kind === 'experiment')
    .map((h) => ({
      uid: h.uid,
      kind: 'experiment',
      course: h.course,
      courseNum: h.courseNum,
      title: h.title,
      deadline: h.deadline,
      deadlineTs: h.deadlineTs,
      hoursLeft: h.hoursLeft,
      submitted: h.submitted,
      expired: h.expired
    }));
  const reports = homework
    .filter((h) => h.kind === 'report')
    .map((h) => ({
      uid: h.uid,
      course: h.course,
      title: h.title,
      deadline: h.deadline,
      hoursLeft: h.hoursLeft,
      submitted: h.submitted,
      expired: h.expired
    }));

  /* 课表加当前周信息 + 今天要上的课。
   * 优先用「教学第 1 周周一」校准值（week1Monday），否则退回学期 beginDate 自动推算。 */
  const week = weekOfTerm(cfg.week1Monday || termStart);
  if (schedule.ok) {
    schedule.termStart = termStart;
    schedule.termEnd = termEnd;
    schedule.termName = schedule.termName || termName;
    schedule.week = week;
    for (const e of schedule.entries) e.activeThisWeek = weekInRange(e.weeks, week);
    const today = ((new Date().getDay() + 6) % 7) + 1; // 周一=1
    schedule.today = today;
    schedule.todayEntries = schedule.entries
      .filter((e) => e.day === today && e.activeThisWeek)
      .sort((a, b) => a.period - b.period);
  }

  return {
    ok: true,
    at: new Date().toISOString(),
    elapsedMs: Date.now() - started,
    school: { id: SCHOOL.id, name: SCHOOL.name, short: SCHOOL.short },
    account: cfg.username,          // 只回显学号，用于界面确认登录的是谁
    term: xqCode,
    termName: termName || '',
    termStart,
    termEnd,
    week,
    courseCount: courses.length,
    homework,
    pending,
    experiments,
    reports,
    assessments,
    courseCoverage,
    /* 课程元数据：让旧快照里的作业也能补出「打开作业页面」深链，
     * 桌宠首次打开窗口不必为了一个 URL 重新登录学校系统。 */
    courseCatalog: courses.map((c) => ({
      courseNum: String(c.course_num || ''),
      id: String(c.id || ''),
      fzId: String(c.fz_id || ''),
      teacherId: String(c.teacher_id || ''),
      xqCode: String(c.xq_code || xqCode || '')
    })),
    emails,
    notices,
    schedule,
    failedCourses
  };
}

/* ---------- 验证账号（设置界面「测试登录」用） ----------
 * 只走关键路径：MIS 登录 → 课程平台会话 → 当前学期课程列表。
 * 不抓作业 / 邮件 / 课表，几秒内返回，避免测试按钮等太久。
 * 返回 { ok, courseCount, termName, error }，不抛异常（由接口层决定状态码）。 */
export async function verifyCredentials({ username, password }) {
  if (!username || !password) return { ok: false, error: '请先填写学号和密码' };
  const started = Date.now();
  try {
    const { sess, ctx } = await openStudySession({ username, password });
    const { courses, termName, xqCode } = await fetchCourses(sess, ctx);
    return {
      ok: true,
      courseCount: courses.length,
      termName: termName || '',
      term: xqCode || '',
      elapsedMs: Date.now() - started
    };
  } catch (err) {
    return { ok: false, error: err.message, elapsedMs: Date.now() - started };
  }
}

/* ---------- 作业辅助：深链 / 剩余天数 / 轻量同步 ---------- */

/** 解析平台截止时间。兼容 "YYYY-MM-DD HH:mm" 与 "YYYY-MM-DD HH:mm:ss"，统一按北京时间。 */
export function deadlineTs(s) {
  let v = String(s || '').trim().replace(' ', 'T');
  if (!v) return 0;
  if (/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(v)) v += ':00';
  if (!/(?:[+-]\d{2}:?\d{2}|Z)$/.test(v)) v += '+08:00';
  const t = new Date(v).getTime();
  return Number.isFinite(t) ? t : 0;
}

/** 该作业在课程平台的深链（courseToPage=10460）。浏览器需已登录学校账号。
 *  参考 cc_homework.py 的 homework_url()，双击作业行时直接打开。 */
export function homeworkUrl(course, hwId, xqCode = '') {
  const q = new URLSearchParams({
    method: 'toCoursePlatform',
    courseToPage: '10460',
    courseId: course.course_num || '',
    dataSource: '1',
    cId: String(course.id || ''),
    xkhId: course.fz_id || '',
    xqCode: course.xq_code || xqCode || '',
    teacherId: course.teacher_id || '',
    homeworkId: String(hwId || '')
  });
  return `${CP}/ve/back/coursePlatform/coursePlatform.shtml?${q}`;
}

/** 「剩 N 天 / 今天截止 / 已过期 / —」+ 行颜色标签，参考课件爬虫 _hw_remaining()。 */
export function remainingInfo(deadline, submitted, now = Date.now()) {
  if (submitted) return { text: '—', days: null, overdue: false, dueSoon: false, tag: 'done' };
  const t = deadlineTs(deadline);
  if (!t) return { text: '—', days: null, overdue: false, dueSoon: false, tag: '' };
  if (t < now) return { text: '已过期', days: -1, overdue: true, dueSoon: false, tag: 'overdue' };
  const a = new Date(now); a.setHours(0, 0, 0, 0);
  const b = new Date(t); b.setHours(0, 0, 0, 0);
  const days = Math.round((b - a) / 864e5);
  if (days <= 0) return { text: '今天截止', days: 0, overdue: true, dueSoon: true, tag: 'overdue' };
  return {
    text: `剩 ${days} 天`, days,
    overdue: false, dueSoon: days <= 2,
    tag: days <= 2 ? 'soon' : ''
  };
}

/** 轻量作业同步：只登录一次、只拉作业，不碰邮件 / 课表 / 空教室。
 *  供 cc-homework.mjs CLI 与 /study/homeworks 复用；会话守卫已装在 sess 上。 */
export async function fetchHomeworksForCli(cfg = loadConfig(), { xqCode = '', log = () => {} } = {}) {
  const { sess, ctx } = await openStudySession(cfg, log);
  const term = await fetchCourses(sess, ctx);
  const code = xqCode || term.xqCode || '';
  let courses = term.courses;
  if (code && code !== term.xqCode) {
    try {
      const r = await sess.text(`${CP}/ve/back/coursePlatform/course.shtml?method=getCourseList&pagesize=100&page=1&xqCode=${encodeURIComponent(code)}`);
      courses = JSON.parse(r.txt).courseList || [];
    } catch (_) { /* 保持当前学期课程 */ }
  }
  ctx.xqCode = code;

  const items = [];
  const failedCourses = [];
  for (const course of courses) {
    try {
      for (const sub of ASSESS_SUBTYPES) {
        const url = `${CP}/ve/back/coursePlatform/homeWork.shtml?method=getHomeWorkList` +
          `&cId=${course.id}&subType=${sub.code}&page=1&pagesize=100`;
        const r = await sess.text(url);
        let d = null;
        try { d = JSON.parse(r.txt); } catch (_) { continue; }
        for (const raw of (d.courseNoteList || [])) {
          if (!raw || !raw.id) continue;
          const end = String(raw.end_time || '');
          const submitted = isSubmittedStatus(raw.subStatus) || !!(raw.subTime || '').trim();
          const rem = remainingInfo(end, submitted);
          items.push({
            uid: `${course.course_num}:${sub.kind}:${raw.id}`,
            homeworkId: String(raw.id),
            title: stripHtml(raw.title || '').slice(0, 120) || '未命名作业',
            courseName: course.name || course.course_num || '',
            courseNum: course.course_num || '',
            kind: sub.kind,
            kindLabel: sub.label,
            openDate: raw.open_date || '',
            endTime: end,
            submitted,
            submittedAt: raw.subTime || '',
            subStatus: raw.subStatus || '',
            score: raw.lastScore || raw.stu_score || '',
            remainingText: rem.text,
            remainingDays: rem.days,
            overdue: rem.overdue,
            dueSoon: rem.dueSoon,
            url: homeworkUrl(course, raw.id, code)
          });
        }
      }
    } catch (err) {
      // 单门课失败不影响其它课程，与参考工具 CLI 的容错一致
      failedCourses.push({ course: course.name || '', error: err.message });
    }
  }
  /* 「实验栏覆盖」核对：每门课实际从 subType=2 抓到几条、平台自报几条。
   * 作业页会用它回答「实验里到底有没有相应的作业」以及有没有漏抓。 */
  const coverage = [];
  for (const course of courses) {
    const num = String(course.course_num || '');
    const own = items.filter((x) => String(x.courseNum || '') === num);
    let taskCounts = null;
    try { taskCounts = await fetchTaskCounts(sess, ctx, course); } catch (_) { /* 计数接口不可用时置 unknown */ }
    coverage.push({
      course: course.name || num,
      courseNum: num,
      classIds: '',
      homeworkCount: own.filter((x) => x.kind === 'homework').length,
      experimentCount: own.filter((x) => x.kind === 'experiment').length,
      reportCount: own.filter((x) => x.kind === 'report').length,
      experimentCountFromTask: taskCounts ? (taskCounts['实验'] || 0) : null,
      taskCounts
    });
  }

  items.sort((a, b) =>
    (a.endTime || '9999-12-31').localeCompare(b.endTime || '9999-12-31') ||
    a.courseName.localeCompare(b.courseName));

  return {
    ok: true,
    fetchedAt: new Date().toISOString(),
    xqCode: code,
    xqName: term.termName || '',
    courseCount: courses.length,
    items,
    coverage,
    failedCourses
  };
}

/* 内部函数导出：仅供调试脚本与课件模块复用登录 / 抓取流程，业务代码不应依赖。 */
export const __internals = {
  createSession,
  loginMIS,
  loginErrorMessage,
  enterCoursePlatform,
  installSessionGuard,
  openStudySession,
  fetchCourses,
  fetchCourseClassIds,
  fetchTaskCounts,
  fetchCourseHomework,
  fetchCourseAssessments,
  fetchHomeworksForCli,
  homeworkUrl,
  remainingInfo,
  deadlineTs,
  parseScheduleTable,
  stripHtml,
  CP,
  MIS,
  SCHOOL
};

export default { collect, loadConfig };
