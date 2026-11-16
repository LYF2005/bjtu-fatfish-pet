/* 通用落盘工具：先写 *.part 再原子替换，避免采集中途崩掉留下半个 JSON / 半个课件。
 * 参考同学写的课件爬虫 download() 与 cc_homework.py 的 os.replace()。 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

export function atomicWrite(target, data, opts = {}) {
  const p = String(target);
  fs.mkdirSync(path.dirname(p), { recursive: true });
  const tmp = `${p}.part`;
  fs.writeFileSync(tmp, data, opts);
  try {
    fs.renameSync(tmp, p);
  } catch (err) {
    // Windows 上目标被另一进程短暂占用时，退一步：先删再改名
    if (['EEXIST', 'EPERM', 'EACCES'].includes(err.code)) {
      try { fs.unlinkSync(p); } catch (_) { /* 已经不存在了 */ }
      fs.renameSync(tmp, p);
    } else {
      try { fs.unlinkSync(tmp); } catch (_) {}
      throw err;
    }
  }
  return p;
}

export function atomicWriteJSON(target, obj) {
  return atomicWrite(target, JSON.stringify(obj, null, 2) + '\n', 'utf8');
}

export function readJSON(target, fallback = null) {
  try { return JSON.parse(fs.readFileSync(target, 'utf8')); }
  catch (_) { return fallback; }
}

/** 清洗成 Windows 合法的文件名 / 目录名（课件名里常有 / : * 等字符）。 */
export function safeName(s, fallback = '未命名') {
  const out = String(s ?? '')
    .replace(/[\\/:*?"<>|\r\n\t]/g, '_')
    .replace(/[. ]+$/, '')
    .trim();
  return (out && out !== '.' && out !== '..') ? out.slice(0, 120) : fallback;
}

/** 默认下载目录：桌面\北交大课件（与参考工具一致）。 */
export function defaultCoursewareDir() {
  return path.join(os.homedir(), 'Desktop', '北交大课件');
}

/** 可在 notify/config.json 用 coursewareDir 覆盖下载位置。 */
export function coursewareDir(cfg = {}) {
  const v = String(cfg.coursewareDir || '').trim();
  return v || defaultCoursewareDir();
}
