#!/usr/bin/env node
/* 邮课叮 · 作业 DDL 同步（无界面 CLI）
 *
 * 与参考工具 `_internal/crawler/cc_homework.py` 完全相同的契约，方便被任何前端复用：
 *   node cc-homework.mjs                        → JSON 打印到 stdout
 *   node cc-homework.mjs --json "D:\x\hw.json" → 原子写到文件（*.part → rename）
 *   node cc-homework.mjs --quiet               → 关闭 stderr 日志
 *   node cc-homework.mjs --term 2026202701    → 指定学期
 *   node cc-homework.mjs --only-pending       → 只输出「未交且未过期」
 *
 * 数据走 stdout，日志走 stderr，异常输出结构化 {"error":...} 并 exit 2；
 * 单门课失败不影响其它课程（采集器已做容错）。
 */
import { fetchHomeworksForCli, loadConfig } from './bjtu.mjs';
import { atomicWriteJSON } from './storage.mjs';

function parseArgs(argv) {
  const o = { out: '', quiet: false, term: '', onlyPending: false };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--json' && argv[i + 1]) { o.out = argv[++i]; }
    else if (a === '--term' && argv[i + 1]) { o.term = argv[++i]; }
    else if (a === '--quiet') { o.quiet = true; }
    else if (a === '--only-pending') { o.onlyPending = true; }
  }
  return o;
}

const args = parseArgs(process.argv);
const log = (msg) => { if (!args.quiet) process.stderr.write(String(msg) + '\n'); };

function normalize(item) {
  return {
    homework_id: item.homeworkId,
    title: item.title,
    course_name: item.courseName,
    course_num: item.courseNum,
    kind: item.kind,
    kind_label: item.kindLabel,
    open_date: item.openDate,
    end_time: item.endTime,
    submitted: item.submitted,
    sub_time: item.submittedAt,
    sub_status: item.subStatus,
    score: item.score,
    remaining: item.remainingText,
    remaining_days: item.remainingDays,
    overdue: item.overdue,
    due_soon: item.dueSoon,
    url: item.url
  };
}

try {
  log('正在登录课程平台并同步作业…');
  const data = await fetchHomeworksForCli(loadConfig(), { xqCode: args.term, log });
  const picked = args.onlyPending
    ? data.items.filter((x) => !x.submitted && !x.overdue)
    : data.items;
  const out = {
    fetched_at: data.fetchedAt,
    xq_code: data.xqCode,
    xq_name: data.xqName,
    course_count: data.courseCount,
    count: picked.length,
    total: data.items.length,
    failed_courses: data.failedCourses,
    items: picked.map(normalize)
  };
  log(`共 ${data.items.length} 条作业，当前输出 ${picked.length} 条`);

  if (args.out) atomicWriteJSON(args.out, out);
  else process.stdout.write(JSON.stringify(out, null, 2) + '\n');
} catch (err) {
  const failure = { error: String(err && err.message ? err.message : err) };
  if (args.out) {
    try { atomicWriteJSON(args.out, failure); } catch (_) { /* 目录不可写也要输出错误 */ }
  }
  process.stderr.write('作业同步失败：' + failure.error + '\n');
  process.stdout.write(JSON.stringify(failure) + '\n');
  process.exitCode = 2;
}
