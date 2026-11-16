/* 大肥鱼·学业提醒 — 账号凭证存储
 *
 * 设计目标：把「学号 / 密码」从 config.json 里拆出来，做成任何同学都能自己填的通用账号，
 * 并且避免密码以明文形式躺在配置里被截图 / 备份 / 误传出去。
 *
 * 保护方式（2026-11-14 升级，参考同学写的课件爬虫）：
 *   - 首选 Windows DPAPI（CryptProtectData / CurrentUser）：密文与当前 Windows 账户绑定，
 *     拷到别的机器或换账户登录都解不开，比「密钥放在同目录文件里」强一个量级。
 *     Node 没有 FFI，这里通过 PowerShell 调 .NET 的 ProtectedData，只在保存 / 首次读取时开销一次。
 *   - DPAPI 不可用时自动降级到原来的 AES-256-GCM + .credentials.key（v1 格式），保证不炸。
 *   - 旧 v1 凭证读到后会自动升级成 v2；写入前做一次解密自检，自检失败立即回退 AES。
 *   - 所有落盘都走 atomicWriteJSON（先 .part 再原子替换）。
 *   - 旧版把 username/password 写在 config.json，这里做一次性迁移并抹掉明文。
 *
 * 边界说明：DPAPI 能挡住「拷走文件 / 备份泄漏 / 别人账户翻目录」，
 * 但挡不住已经拿到你 Windows 账户权限并在你会话里运行的程序。UI 里也照实说明。
 */
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { atomicWriteJSON } from './storage.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const CREDENTIALS_PATH = path.join(__dirname, 'credentials.json');
const KEY_PATH = path.join(__dirname, '.credentials.key');
const CONFIG_PATH = path.join(__dirname, 'config.json');
const NOTE = '本文件由大肥鱼设置界面维护，密码已加密；请勿手工编辑。';

function readJSON(p, fallback) {
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch (_) { return fallback; }
}

/* ---------- Windows DPAPI（经 PowerShell 调 .NET ProtectedData） ---------- */
let psExe;
function powershell() {
  if (psExe !== undefined) return psExe;
  psExe = '';
  for (const exe of ['powershell.exe', 'pwsh.exe']) {
    try {
      execFileSync(exe, ['-NoProfile', '-NonInteractive', '-Command', 'exit 0'],
        { timeout: 8000, stdio: 'pipe', windowsHide: true });
      psExe = exe;
      break;
    } catch (_) { /* 换下一个 */ }
  }
  return psExe;
}

function dpapi(data, unprotect = false) {
  const exe = powershell();
  if (!exe) throw new Error('找不到 PowerShell，无法调用 Windows DPAPI');
  const b64 = Buffer.from(data).toString('base64');
  const verb = unprotect ? 'Unprotect' : 'Protect';
  // 明文用 base64 传参：密码里的引号 / 中文 / 反斜杠都不会被命令行转义搞坏
  const script = [
    'Add-Type -AssemblyName System.Security',
    `$b = [Convert]::FromBase64String('${b64}')`,
    `$r = [System.Security.Cryptography.ProtectedData]::${verb}($b, [byte[]]$null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)`,
    '[Console]::OutputEncoding = [System.Text.Encoding]::ASCII',
    '[Console]::Out.Write([Convert]::ToBase64String($r))'
  ].join('; ');
  const out = execFileSync(exe, ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', timeout: 20000, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true
  });
  // PowerShell 可能在首尾塞 BOM / 换行，只留 base64 字符
  const clean = String(out || '').replace(/[^A-Za-z0-9+/=]/g, '');
  if (!clean) throw new Error('DPAPI 返回空结果');
  return Buffer.from(clean, 'base64');
}

/* ---------- 降级方案：AES-256-GCM（v1 兼容） ---------- */
function secretKey() {
  try {
    const buf = fs.readFileSync(KEY_PATH);
    if (buf.length === 32) return buf;
  } catch (_) { /* 没密钥就生成一个 */ }
  const key = crypto.randomBytes(32);
  fs.writeFileSync(KEY_PATH, key, { mode: 0o600 });
  try { fs.chmodSync(KEY_PATH, 0o600); } catch (_) {}
  return key;
}

function encrypt(plain) {
  const iv = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv('aes-256-gcm', secretKey(), iv);
  const data = Buffer.concat([cipher.update(plain, 'utf8'), cipher.final()]);
  return {
    alg: 'aes-256-gcm',
    iv: iv.toString('base64'),
    tag: cipher.getAuthTag().toString('base64'),
    data: data.toString('base64')
  };
}

function decrypt(box) {
  if (!box || !box.data) throw new Error('凭证格式不正确');
  const decipher = crypto.createDecipheriv('aes-256-gcm', secretKey(), Buffer.from(box.iv, 'base64'));
  decipher.setAuthTag(Buffer.from(box.tag, 'base64'));
  const out = Buffer.concat([decipher.update(Buffer.from(box.data, 'base64')), decipher.final()]);
  return out.toString('utf8');
}

/** 按文件里的 alg 解开 box，返回凭证对象。 */
function decryptBox(file) {
  if (!file || !file.box) throw new Error('凭证格式不正确');
  let payload;
  if (file.alg === 'aes-256-gcm' || file.v === 1 || typeof file.box === 'object') {
    payload = decrypt(file.box);
  } else {
    const raw = Buffer.from(String(file.box).replace(/^dpapi:/, ''), 'base64');
    payload = dpapi(raw, true).toString('utf8');
  }
  const obj = JSON.parse(payload);
  return {
    username: String(obj.username || ''),
    password: String(obj.password || ''),
    updatedAt: String(obj.updatedAt || '')
  };
}

/* ---------- 写凭证 ---------- */
function writeCredentials({ username, password }, opts = {}) {
  const payload = JSON.stringify({
    username: String(username || '').trim(),
    password: String(password || ''),
    updatedAt: new Date().toISOString()
  });

  // 先算出 AES 版本作为兜底，再尝试 DPAPI
  const aesBox = encrypt(payload);
  let file = { v: 1, alg: 'aes-256-gcm', note: NOTE, box: aesBox };
  try {
    const token = 'dpapi:' + dpapi(Buffer.from(payload, 'utf8')).toString('base64');
    file = { v: 2, alg: 'dpapi', note: NOTE, box: token };
  } catch (err) {
    console.warn('[学业账号] Windows DPAPI 不可用，降级 AES-256-GCM：' + err.message);
  }

  // 写完立刻自检；解不开就回退到 AES，绝不把账号写坏
  try {
    const back = decryptBox(file);
    if (!back.username || !back.password) throw new Error('自检读回的凭证为空');
  } catch (err) {
    console.warn('[学业账号] DPAPI 自检失败，回退 AES-256-GCM：' + err.message);
    file = { v: 1, alg: 'aes-256-gcm', note: NOTE, box: aesBox };
  }

  atomicWriteJSON(CREDENTIALS_PATH, file);
  try { fs.chmodSync(CREDENTIALS_PATH, 0o600); } catch (_) { /* Windows 上 chmod 只是只读位 */ }
  if (!opts.silent) console.log('[学业账号] 凭证已保存（' + protectionLabel() + '）');
}

function protectionLabel() {
  const f = readJSON(CREDENTIALS_PATH, null);
  return f && f.alg === 'dpapi' ? 'Windows DPAPI' : 'AES-256-GCM';
}

/* 把旧 config.json 里的明文账号迁进加密文件，并从 config.json 删除。 */
function migrateLegacy() {
  const cfg = readJSON(CONFIG_PATH, null);
  if (!cfg || !cfg.username || !cfg.password) return null;

  const username = String(cfg.username).trim();
  const password = String(cfg.password);
  writeCredentials({ username, password }, { silent: true });

  const rest = { ...cfg };
  delete rest.username;
  delete rest.password;
  atomicWriteJSON(CONFIG_PATH, rest);
  console.log('[学业账号] 已把 config.json 里的明文账号迁移到加密凭证，并从配置中移除');
  return { username, password, updatedAt: new Date().toISOString() };
}

/** 读取凭证。优先 credentials.json；v1 自动升级成 DPAPI；没有则尝试迁移旧 config.json。 */
export function readCredentials() {
  const file = readJSON(CREDENTIALS_PATH, null);
  if (file && file.box) {
    try {
      const cred = decryptBox(file);
      // 旧格式一次性升级：密码不换，只是换一种绑定 Windows 账户的加密方式
      if (file.alg !== 'dpapi' && cred.username && cred.password) {
        try {
          writeCredentials({ username: cred.username, password: cred.password }, { silent: true });
          console.log('[学业账号] 旧凭证已自动升级为 Windows DPAPI 加密');
        } catch (err) {
          console.warn('[学业账号] 凭证升级跳过（不影响使用）：' + err.message);
        }
      }
      return cred;
    } catch (err) {
      console.warn('[学业账号] 凭证解密失败（可能换了 Windows 账户 / 电脑）：' + err.message);
      return { username: '', password: '', updatedAt: '', error: '凭证无法解密，请重新填写密码' };
    }
  }

  const migrated = migrateLegacy();
  if (migrated) return migrated;
  return { username: '', password: '', updatedAt: '' };
}

/** 保存凭证。password 传空表示「只改账号 / 保留原密码」，此时若账号也变了则拒绝。 */
export function saveCredentials({ username, password }) {
  const name = String(username || '').trim();
  const pwd = String(password || '');
  if (!name) throw new Error('请填写学号 / 账号');

  if (!pwd) {
    const current = readCredentials();
    if (!current.password) throw new Error('请填写密码');
    if (current.username && current.username !== name) {
      throw new Error('更换账号时请同时填写新密码');
    }
    writeCredentials({ username: name, password: current.password });
    return { username: name, updatedAt: new Date().toISOString() };
  }

  writeCredentials({ username: name, password: pwd });
  return { username: name, updatedAt: new Date().toISOString() };
}

export function clearCredentials() {
  try { fs.unlinkSync(CREDENTIALS_PATH); } catch (_) {}
  console.log('[学业账号] 已清除本机保存的账号凭证');
}

/** 给设置界面用：永远不返回密码，附带当前保护方式便于如实告知安全边界。 */
export function credentialsInfo() {
  const c = readCredentials();
  const f = readJSON(CREDENTIALS_PATH, null);
  const dpapiUsed = !!(f && f.alg === 'dpapi');
  return {
    configured: !!(c.username && c.password),
    username: c.username || '',
    updatedAt: c.updatedAt || '',
    error: c.error || '',
    protection: dpapiUsed ? 'Windows DPAPI（绑定当前 Windows 账户）' : 'AES-256-GCM（本机文件加密）',
    accountBound: dpapiUsed
  };
}

export default { readCredentials, saveCredentials, clearCredentials, credentialsInfo, CREDENTIALS_PATH };
