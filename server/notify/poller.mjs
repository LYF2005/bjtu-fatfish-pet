/* 北交大肥鱼·学业提醒 — 轮询与提醒调度
 *
 * 职责：
 *   1. 按 intervalMinutes（默认 30 分钟）拉一次学业数据。
 *   2. 与上次快照对比，只在「有变化」时打扰：
 *        - 新出现的作业（未提交）
 *        - 未提交且进入 dueSoonHours 窗口的作业（同一作业每天最多提醒一次）
 *        - 新到的未读邮件
 *   3. 首次运行只建立基线，不弹窗。
 *   4. 深夜静默（quietHours）只记录不推送。
 *
 * 与壁纸的交互：通过回调 onNotify(payload) 交给 server 广播。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { collect, loadConfig } from './bjtu.mjs';
import { atomicWriteJSON } from './storage.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const STATE_PATH = path.join(__dirname, 'state.json');
const SNAPSHOT_PATH = path.join(__dirname, 'last-snapshot.json');

const DAY_MS = 864e5;

/* ---------- 状态持久化 ---------- */
function readJSON(p, fallback) {
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch (_) { return fallback; }
}
function writeJSON(p, obj) {
  /* 先写 *.part 再原子替换：采集中途崩溃不会留下半个 JSON 让下次启动读失败。
   * 参考同学写的课件爬虫 / cc_homework.py 的 os.replace()。 */
  try { atomicWriteJSON(p, obj); } catch (_) {}
}

const defaultState = () => ({
  initialized: false,
  account: '',          // 这份去重状态属于哪个学号；换账号时整份重置
  lastRun: null,
  lastOk: null,
  lastError: '',
  seenHomework: {},     // uid -> firstSeen ISO
  seenEmails: {},       // key -> ISO
  remindedAt: {},       // uid -> ISO（作业提醒时间，用于每日节流）
  firedTiers: {},       // uid -> ["48","24",...] 已触发过的提醒档位
  alerts: [],           // 提醒队列（桌面宠物按 after=id 增量拉取）
  alertSeq: 0,          // 提醒自增 id
  lastUnread: 0,
  lastPending: 0,
  lastPayload: null,
  notifiedAt: null,
  runs: 0
});

/* ---------- 作业提醒档位：2天 / 1天 / 半天 / 2小时 / 1小时 ---------- */
const TIERS = [
  { key: '48', hours: 48, label: '2 天' },
  { key: '24', hours: 24, label: '1 天' },
  { key: '12', hours: 12, label: '半天' },
  { key: '2', hours: 2, label: '2 小时' },
  { key: '1', hours: 1, label: '1 小时' }
];

/* 选中最紧的那一档：已进入该档位、且还没触发过的最紧迫档位。
 * 若因为开机较晚一次性跨过多档，只报最紧迫的一档，其余档位一并标记为已触发，
 * 避免同一份作业在同一个时间点连发 5 条。
 * 已触发过某一档后，不再回退到比它更宽松的档位（例如已经报过「1 小时」，
 * 不会因为 hoursLeft 反弹而重新报「2 小时」）。 */
function pickTier(hoursLeft, fired) {
  const done = new Set(fired || []);
  const crossed = TIERS.filter((t) => hoursLeft <= t.hours);
  if (!crossed.length) return null;
  const pendingTiers = crossed.filter((t) => !done.has(t.key));
  if (!pendingTiers.length) return null;
  // crossed 按小时数从大到小排列，最后一个就是最紧迫的
  const chosen = pendingTiers[pendingTiers.length - 1];
  // 若已有比它更紧迫的档位触发过，说明已经报过更靠后的提醒，不再重复
  const stricterDone = TIERS.filter((t) => t.hours < chosen.hours).some((t) => done.has(t.key));
  if (stricterDone) return null;
  return chosen;
}

/* ---------- 判断是否处于静默时段 ---------- */
function inQuietHours(cfg, date = new Date()) {
  const q = cfg.quietHours;
  if (!q || q.start == null || q.end == null) return false;
  const h = date.getHours();
  const s = Number(q.start);
  const e = Number(q.end);
  if (s === e) return false;
  return s < e ? (h >= s && h < e) : (h >= s || h < e);
}

/* ---------- 邮件去重键 ---------- */
function emailKey(m) {
  return `${m.date}|${m.subject}`.slice(0, 160);
}

/* ---------- 去重记录清理：按时间淘汰，超量再按时间截断 ---------- */
function pruneSeen(map, maxAgeMs, maxCount) {
  const now = Date.now();
  for (const [k, v] of Object.entries(map)) {
    if (now - new Date(v).getTime() > maxAgeMs) delete map[k];
  }
  if (maxCount && Object.keys(map).length > maxCount) {
    const sorted = Object.entries(map).sort((a, b) => new Date(b[1]) - new Date(a[1]));
    Object.keys(map).forEach((k) => delete map[k]);
    for (const [k, v] of sorted.slice(0, maxCount)) map[k] = v;
  }
}

/* ---------- 构造提醒载荷 ---------- */
const TYPE_LABEL = { homework: '作业', experiment: '实验', report: '课程报告' };
const typeLabel = (k) => TYPE_LABEL[k] || '作业';

function buildPayload({ newHomework, tierAlerts, newEmails, notices, pending, unread }) {
  const items = [];

  // 1) 档位提醒（2天/1天/半天/2小时/1小时），最紧迫的排前面
  const sorted = [...tierAlerts].sort((a, b) => a.hoursLeft - b.hoursLeft);
  for (const { homework: h, tier } of sorted.slice(0, 3)) {
    items.push({
      kind: 'homework',
      type: h.kind || 'homework',
      course: h.course,
      title: h.title,
      deadline: h.deadline,
      hoursLeft: h.hoursLeft,
      tier: tier.key,
      tierLabel: tier.label,
      urgent: tier.hours <= 12
    });
  }

  // 2) 新布置的作业/实验/报告（最多 2 条，且不与上面重复）
  const shown = new Set(sorted.map((x) => x.homework.uid));
  for (const h of newHomework.filter((x) => !shown.has(x.uid)).slice(0, 2)) {
    items.push({
      kind: 'homework',
      type: h.kind || 'homework',
      course: h.course,
      title: h.title,
      deadline: h.deadline,
      hoursLeft: h.hoursLeft,
      urgent: false,
      isNew: true
    });
  }

  // 3) 未读邮件
  if (newEmails.length) {
    items.push({
      kind: 'email',
      count: unread,
      latest: newEmails.slice(0, 3).map((m) => ({ subject: m.subject, from: m.from }))
    });
  }

  // 4) 学校通知（只在没有其他内容时提示）
  if (!items.length && notices.length) {
    items.push({
      kind: 'notice',
      title: notices[0].title,
      date: notices[0].date
    });
  }

  if (!items.length) return null;

  const hwCount = items.filter((x) => x.kind === 'homework').length;
  const newCount = items.filter((x) => x.kind === 'homework' && x.isNew).length;
  const tierCount = items.filter((x) => x.kind === 'homework' && x.tier).length;
  const emCount = items.find((x) => x.kind === 'email')?.count || 0;
  const hasUrgent = items.some((x) => x.kind === 'homework' && x.urgent);

  let title;
  if (tierCount) {
    const first = items.find((x) => x.kind === 'homework' && x.tier);
    title = `${first.course}的${typeLabel(first.type)}还有 ${first.tierLabel}截止！`;
  } else if (newCount && emCount) {
    title = `${newCount} 个新任务 · ${emCount} 封未读邮件`;
  } else if (newCount) {
    const first = items.find((x) => x.kind === 'homework' && x.isNew);
    title = `新${typeLabel(first && first.type)}：${(first && first.title || '').slice(0, 22)}`;
  } else if (hwCount && emCount) {
    title = `${hwCount} 个任务待完成 · ${emCount} 封未读邮件`;
  } else if (hwCount) {
    title = `有 ${hwCount} 个任务待完成`;
  } else if (emCount) {
    title = `有 ${emCount} 封未读邮件`;
  } else {
    title = '有新的学校通知';
  }

  return {
    kind: 'study',
    level: hasUrgent ? 'urgent' : 'normal',
    title,
    items,
    counts: { homework: pending.length, email: unread },
    at: new Date().toISOString()
  };
}

/* ---------- 当前状态载荷（用于徽标 / 开机回显，不触发弹窗） ---------- */
function buildStatusPayload(pending, unread, latestEmails, notices) {
  const items = [];
  for (const h of pending.slice(0, 3)) {
    items.push({
      kind: 'homework',
      course: h.course,
      title: h.title,
      deadline: h.deadline,
      hoursLeft: h.hoursLeft,
      urgent: h.hoursLeft !== null && h.hoursLeft <= 24
    });
  }
  if (unread > 0) {
    items.push({
      kind: 'email',
      count: unread,
      latest: (latestEmails || []).slice(0, 2).map((m) => ({ subject: m.subject, from: m.from }))
    });
  }
  if (!items.length && notices && notices.length) {
    items.push({ kind: 'notice', title: notices[0].title, date: notices[0].date });
  }
  if (!items.length) return null;

  const hasUrgent = items.some((x) => x.kind === 'homework' && x.urgent);
  const hwN = pending.length;
  let title;
  if (hasUrgent) {
    const first = items.find((x) => x.kind === 'homework' && x.urgent);
    const hl = first.hoursLeft;
    const when = hl <= 0 ? '已过期' : hl < 1 ? '不到 1 小时' : hl < 24 ? Math.round(hl) + ' 小时' : Math.round(hl / 24) + ' 天';
    title = `${first.course}的作业还有 ${when} 截止！`;
  } else if (hwN && unread) {
    title = `${hwN} 个作业待交 · ${unread} 封未读邮件`;
  } else if (hwN) {
    title = `有 ${hwN} 个作业待交`;
  } else if (unread) {
    title = `有 ${unread} 封未读邮件`;
  } else {
    title = '有新的学校通知';
  }

  return {
    kind: 'study',
    level: hasUrgent ? 'urgent' : 'normal',
    title,
    items,
    counts: { homework: hwN, email: unread },
    at: new Date().toISOString()
  };
}

/* ---------- 轮询器 ---------- */
export function createPoller(opts = {}) {
  const onNotify = opts.onNotify || (() => {});
  const onStatus = opts.onStatus || (() => {});
  const logger = opts.log || ((...a) => console.log('[学业提醒]', ...a));

  let cfg = loadConfig();
  let state = { ...defaultState(), ...readJSON(STATE_PATH, {}) };
  let snapshot = readJSON(SNAPSHOT_PATH, null);
  let timer = null;
  let running = false;
  let lastResult = null;

  function save() {
    writeJSON(STATE_PATH, state);
    if (lastResult) {
      snapshot = lastResult;
      writeJSON(SNAPSHOT_PATH, lastResult);
    }
  }

  function updateConfig(next) {
    cfg = { ...cfg, ...next };
    return { ...cfg, password: cfg.password ? '********' : '' };
  }

  function status() {
    return {
      enabled: cfg.enabled,
      configured: !!(cfg.username && cfg.password),
      account: cfg.username || '',
      school: cfg.school || 'bjtu',
      initialized: state.initialized,
      intervalMinutes: cfg.intervalMinutes,
      lastRun: state.lastRun,
      lastOk: state.lastOk,
      lastError: state.lastError,
      runs: state.runs,
      unread: state.lastUnread,
      pending: state.lastPending,
      lastPayload: state.lastPayload || null,
      nextRunAt: timer ? new Date(Date.now() + cfg.intervalMinutes * 60000).toISOString() : null,
      snapshot: snapshot ? {
        at: snapshot.at,
        school: snapshot.school || null,
        account: snapshot.account || '',
        term: snapshot.term,
        termName: snapshot.termName,
        termStart: snapshot.termStart,
        week: snapshot.week,
        courseCount: snapshot.courseCount,
        homework: snapshot.homework,
        pending: snapshot.pending,
        experiments: snapshot.experiments || [],
        reports: snapshot.reports || [],
        courseCoverage: snapshot.courseCoverage || [],
        emails: snapshot.emails,
        notices: snapshot.notices,
        schedule: snapshot.schedule
      } : null
    };
  }

  /* 一次完整轮询。manual=true 表示用户手动刷新（首轮也按基线处理） */
  async function tick(reason = 'auto') {
    if (running) return { ok: false, error: 'busy' };
    running = true;
    const started = Date.now();
    try {
      logger(`开始拉取（${reason}）…`);
      const data = await collect(cfg);

      /* ---- 换了账号：旧账号的「已读 / 已提醒」记录不能复用 ----
       * 否则新账号的作业会被旧账号的 seenHomework 当成「早就见过」，静默漏报。
       * 重置后这一轮走「首次运行 = 建立基线」分支，不打扰。 */
      if (state.account !== cfg.username) {
        if (state.account) logger(`账号已切换（${state.account} → ${cfg.username}），重置提醒基线`);
        state = { ...defaultState(), account: cfg.username, runs: state.runs };
      }

      const nowIso = new Date().toISOString();
      state.lastRun = nowIso;
      state.lastOk = nowIso;
      state.lastError = '';
      state.runs += 1;
      lastResult = data;

      const unread = data.emails && data.emails.ok ? data.emails.unread : 0;
      const pending = data.pending || [];

      /* ---- 首次运行：建立基线，不打扰 ---- */
      if (!state.initialized) {
        for (const h of data.homework) state.seenHomework[h.uid] = nowIso;
        // 基线时把已经进入的档位记为「已触发」，避免首次启动就把所有档位补发一遍
        for (const h of pending) {
          if (h.hoursLeft === null || h.hoursLeft <= 0) continue;
          state.firedTiers[h.uid] = TIERS.filter((t) => h.hoursLeft <= t.hours).map((t) => t.key);
        }
        for (const m of data.emails.latest || []) state.seenEmails[emailKey(m)] = nowIso;
        state.lastUnread = unread;
        state.lastPending = pending.length;
        state.lastPayload = buildStatusPayload(pending, unread, data.emails.latest, data.notices);
        state.initialized = true;
        state.account = cfg.username || '';
        state.notifiedAt = nowIso;
        save();
        logger(`基线已建立：${data.homework.length} 个作业（${pending.length} 待交），${unread} 封未读邮件`);
        onStatus(status());
        return { ok: true, baseline: true, data };
      }

      /* ---- 差异识别 ---- */
      const newHomework = pending.filter((h) => !state.seenHomework[h.uid]);

      /* 五档提醒：2天 / 1天 / 半天 / 2小时 / 1小时。
       * 每个作业每档只触发一次；跨档时只报最紧迫的那一档。 */
      const tierAlerts = [];
      for (const h of pending) {
        if (h.hoursLeft === null || h.hoursLeft <= 0) continue;
        const fired = state.firedTiers[h.uid] || [];
        const tier = pickTier(h.hoursLeft, fired);
        if (!tier) continue;
        tierAlerts.push({ homework: h, tier });
        // 把当前已经跨过的、比它更宽松的档位一并标记为已触发，避免后续补发
        const crossed = TIERS.filter((t) => h.hoursLeft <= t.hours).map((t) => t.key);
        state.firedTiers[h.uid] = Array.from(new Set([...fired, ...crossed]));
      }

      // 只把真正新的未读邮件算进去
      const newEmails = (data.emails.latest || []).filter((m) => !state.seenEmails[emailKey(m)]);

      // 实验与课程报告已并入 homework/pending，统一走「新任务 + 五档提醒」，无需单独逻辑

      // 记录所有已见任务（含已提交的，避免下次重复判定为“新”）
      for (const h of data.homework) {
        if (!state.seenHomework[h.uid]) state.seenHomework[h.uid] = nowIso;
      }
      for (const m of data.emails.latest || []) {
        if (!state.seenEmails[emailKey(m)]) state.seenEmails[emailKey(m)] = nowIso;
      }

      state.lastUnread = unread;
      state.lastPending = pending.length;
      state.lastPayload = buildStatusPayload(pending, unread, data.emails.latest, data.notices);

      /* ---- 组装提醒 ---- */
      let payload = null;
      if (newHomework.length || tierAlerts.length || newEmails.length) {
        payload = buildPayload({
          newHomework,
          tierAlerts,
          newEmails,
          notices: data.notices || [],
          pending,
          unread
        });
        if (payload) state.lastPayload = payload;
      }

      if (payload && inQuietHours(cfg)) {
        logger(`静默时段内（${cfg.quietHours.start}:00-${cfg.quietHours.end}:00），本次提醒只记录不弹窗`);
        payload = null;
      }

      if (payload) {
        for (const it of payload.items) {
          if (it.kind !== 'homework') continue;
          const h = pending.find((x) => x.course === it.course && x.title === it.title);
          if (h) state.remindedAt[h.uid] = nowIso;
        }
        state.notifiedAt = nowIso;
        logger(`推送提醒：${payload.title}`);
        // 存进提醒队列，供桌面宠物按 id 增量拉取（壁纸走 WS 广播）
        state.alertSeq = (state.alertSeq || 0) + 1;
        state.alerts = [...(state.alerts || []), {
          id: state.alertSeq,
          at: nowIso,
          ...payload
        }].slice(-50);
        try { onNotify(payload); } catch (err) { logger('推送失败：' + err.message); }
      } else {
        logger(`无新增（作业 ${pending.length} 待交 / 邮件 ${unread} 未读）`);
      }

      // 清理过期的 remindedAt / firedTiers（超过 30 天）
      for (const [k, v] of Object.entries(state.remindedAt)) {
        if (Date.now() - new Date(v).getTime() > 30 * DAY_MS) delete state.remindedAt[k];
      }
      const pendingUids = new Set(pending.map((h) => h.uid));
      for (const k of Object.keys(state.firedTiers)) {
        if (!pendingUids.has(k)) delete state.firedTiers[k]; // 作业已交/已过期：档位记录一并清掉
      }

      // 清理长期累积的去重记录，避免文件无限膨胀
      pruneSeen(state.seenHomework, 180 * DAY_MS);
      pruneSeen(state.seenEmails, 90 * DAY_MS, 800);

      save();
      onStatus(status());
      return { ok: true, notified: !!payload, payload, data, elapsedMs: Date.now() - started };
    } catch (err) {
      state.lastRun = new Date().toISOString();
      state.lastError = err.message;
      save();
      logger('拉取失败：' + err.message);
      onStatus(status());
      return { ok: false, error: err.message };
    } finally {
      running = false;
    }
  }

  function schedule() {
    if (timer) clearInterval(timer);
    timer = setInterval(() => tick('auto'), cfg.intervalMinutes * 60000);
    if (timer.unref) timer.unref();
  }

  function start() {
    if (!cfg.enabled) {
      logger('已在 config.json 中关闭（enabled=false）');
      return;
    }
    if (!cfg.username || !cfg.password) {
      logger('未配置账号密码，跳过');
      return;
    }
    const delay = Math.max(5, cfg.onLaunchDelaySec) * 1000;
    logger(`已启用：每 ${cfg.intervalMinutes} 分钟检查一次，${Math.round(delay / 1000)} 秒后首次运行`);
    const boot = setTimeout(() => { tick('startup'); }, delay);
    if (boot.unref) boot.unref();
    schedule();
  }

  function stop() {
    if (timer) clearInterval(timer);
    timer = null;
  }

  /* 清空提醒队列（桌面宠物点「全部已读」时调用），保留 alertSeq 不回退。 */
  function clearAlerts() {
    const cleared = (state.alerts || []).length;
    state.alerts = [];
    save();
    return { seq: state.alertSeq || 0, cleared };
  }

  /* 设置界面保存新账号后调用：重新读盘、清掉旧账号基线、立刻重排定时器。
   * 不重启服务进程，改完账号几秒内就生效。 */
  function reload() {
    const prevAccount = cfg.username;
    cfg = loadConfig();
    stop();

    if (cfg.username !== prevAccount && state.initialized) {
      // 换人了：旧账号的已读记录、提醒队列、快照全部作废，避免张冠李戴
      const seq = state.alertSeq || 0;
      state = defaultState();
      state.alertSeq = seq; // 提醒 id 不回退，桌宠游标才不会有重复
      state.account = cfg.username || '';
      save();
      lastResult = null;
      snapshot = null;
      try { fs.unlinkSync(SNAPSHOT_PATH); } catch (_) {}
      logger('账号已变更，已清空旧账号的提醒基线与快照');
    }

    start();
    onStatus(status());
    return status();
  }

  return { start, stop, tick, status, reload, clearAlerts, getConfig: () => cfg };
}

/* 供服务端只读访问提醒队列：桌面宠物用 after 做增量拉取。 */
export function readAlerts() {
  const st = readJSON(STATE_PATH, {});
  return { seq: Number(st.alertSeq) || 0, alerts: Array.isArray(st.alerts) ? st.alerts : [] };
}

export default { createPoller };
