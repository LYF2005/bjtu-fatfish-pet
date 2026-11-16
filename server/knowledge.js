/* 大肥鱼·赛博工友 — 知识笔记本核心（服务端模块）
 * 实现《知识笔记本与漫画日记方案》v1.2 定稿：
 *  - knowledge 表（含 UNIQUE(source, link) 幂等锚点）
 *  - FTS5 external content 表 + 3 触发器（DELETE/UPDATE 侧补 tokenize —— 二轮评审 Bug A 修法）
 *  - tokenize() UDF：Intl.Segmenter 词级分词，small-icu 环境降级 bigram（评审缺口 3）
 *  - buildMatch() 查询构造：AND 优先、命中<3 降级 OR + bm25 排序，词加双引号（评审缺口 1）
 *  - rebuildKb() 自建重建管道，禁用 FTS5 原生 rebuild（评审缺口 2）
 *  - /kb/add 内 link 幂等键：自动来源给稳定 key、手动没给生成一次性 key（评审 Bug B 修法）
 * 用法：import { createKnowledge } from './knowledge.js';
 * 依赖：better-sqlite3（由 studyserver.js 创建 db 后传入，桌宠后端独立实例）。 */

/* ---------- 分词（Intl.Segmenter 优先，small-icu 降级 bigram） ---------- */
let cachedTokenize = null;

function segmenterTokenize(text) {
  const seg = new Intl.Segmenter('zh', { granularity: 'word' });
  const parts = [];
  for (const w of seg.segment(String(text || ''))) {
    const t = w.segment.trim();
    if (t) parts.push(t);
  }
  return parts.join(' ');
}

function bigramTokenize(text) {
  // 降级：连续中文按两字一切；非中文字符按空白/标点切。与 SinoMem 召回扩展同思路。
  const s = String(text || '');
  const parts = [];
  // 先按空白与常见标点切成"词块"，避免英文单词被打散
  const chunks = s.split(/[\s,，。；;:：!！?？、()（）[\]【】{}"'“”‘’<>《》/\\|~`@#$%^&*+=_-]+/).filter(Boolean);
  for (const chunk of chunks) {
    if (/[\u4e00-\u9fff]/.test(chunk)) {
      const cjk = chunk.match(/[\u4e00-\u9fff]+/g) || [];
      for (const run of cjk) {
        if (run.length === 1) { parts.push(run); continue; }
        for (let i = 0; i < run.length - 1; i++) parts.push(run.slice(i, i + 2));
        if (run.length === 2) parts.push(run); // 两字词直接整词
      }
      // 非中文残片（数字/英文）整块保留
      const others = chunk.split(/[\u4e00-\u9fff]+/).filter(Boolean);
      for (const o of others) parts.push(o);
    } else {
      parts.push(chunk);
    }
  }
  return parts.join(' ');
}

export function getTokenizer() {
  if (cachedTokenize) return cachedTokenize;
  const hasSegmenter = typeof Intl !== 'undefined' && !!Intl.Segmenter;
  cachedTokenize = hasSegmenter ? segmenterTokenize : bigramTokenize;
  return cachedTokenize;
}

/* ---------- 查询构造（评审缺口 1） ---------- */
/* 词必须加双引号：用户输入 "C++ & 429" 不加引号会让 FTS5 语法直接报错。 */
export function buildMatch(query) {
  const terms = getTokenizer()(query).split(' ').filter(Boolean).map((t) => `"${t}"`);
  if (!terms.length) return null;
  return { and: terms.join(' AND '), or: terms.join(' OR ') };
}

/* ---------- 知识库工厂 ---------- */
export function createKnowledge(db) {
  db.exec(`
    CREATE TABLE IF NOT EXISTS knowledge (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      source TEXT NOT NULL,
      link TEXT NOT NULL DEFAULT '',
      title TEXT NOT NULL,
      body TEXT DEFAULT '',
      tags TEXT DEFAULT '',
      created TEXT NOT NULL,
      updated TEXT NOT NULL,
      UNIQUE(source, link)
    );
  `);

  const hasFts = db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='knowledge_fts'").get();
  if (!hasFts) {
    // external content：索引数据另存，主表增删改由触发器同步
    db.exec(`
      CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(
        title, body, tags,
        content='knowledge', content_rowid='id',
        tokenize='unicode61'
      );
    `);
  }

  // 注册分词 UDF（deterministic：触发器 delete 侧必须在删除时刻复现建索引时刻的同一输出 —— Bug A 成立前提）
  if (!db.function) {
    // 兼容 better-sqlite3 低版本
    throw new Error('better-sqlite3 too old: db.function() missing');
  }
  db.function('tokenize', { deterministic: true }, (v) => getTokenizer()(v));

  // Bug A 修法：DELETE/UPDATE 触发器对 old.* 补 tokenize —— 'delete' 特殊插入必须与建索引值完全一致
  db.exec(`
    CREATE TRIGGER IF NOT EXISTS knowledge_ad AFTER DELETE ON knowledge BEGIN
      INSERT INTO knowledge_fts(knowledge_fts, rowid, title, body, tags)
      VALUES ('delete', old.id, tokenize(old.title), tokenize(old.body), tokenize(old.tags));
    END;

    CREATE TRIGGER IF NOT EXISTS knowledge_au AFTER UPDATE ON knowledge BEGIN
      INSERT INTO knowledge_fts(knowledge_fts, rowid, title, body, tags)
      VALUES ('delete', old.id, tokenize(old.title), tokenize(old.body), tokenize(old.tags));
      INSERT INTO knowledge_fts(rowid, title, body, tags)
      VALUES (new.id, tokenize(new.title), tokenize(new.body), tokenize(new.tags));
    END;
  `);

  // knowledge_ai 触发器：INSERT 侧同步入索引（含 index 表中显式字段）
  db.exec(`
    CREATE TRIGGER IF NOT EXISTS knowledge_ai AFTER INSERT ON knowledge BEGIN
      INSERT INTO knowledge_fts(rowid, title, body, tags)
      VALUES (new.id, tokenize(new.title), tokenize(new.body), tokenize(new.tags));
    END;
  `);

  const stmts = {
    upsert: db.prepare(`
      INSERT INTO knowledge (source, link, title, body, tags, created, updated)
      VALUES (@source, @link, @title, @body, @tags, @now, @now)
      ON CONFLICT(source, link) DO UPDATE SET
        title = excluded.title,
        body = excluded.body,
        tags = excluded.tags,
        updated = excluded.updated
    `),
    del: db.prepare('DELETE FROM knowledge WHERE id = ?'),
    get: db.prepare('SELECT * FROM knowledge WHERE id = ?'),
    all: db.prepare('SELECT * FROM knowledge ORDER BY id DESC LIMIT ?'),
    count: db.prepare('SELECT COUNT(*) AS n FROM knowledge')
  };

  /* ---------- 入库（/kb/add） ----------
   * link 语义 = 幂等键（Bug B 修法）：
   *  - 自动来源（fact/concept/event）必须传稳定 key → upsert 覆盖；
   *  - 手动笔记（note）一般不带 link → 服务端生成一次性 key，保证每条独立落库。 */
  function addKnowledge(entry) {
    const source = String(entry.source || 'note');
    const title = String(entry.title || '').trim();
    if (!title) throw new Error('title required');
    const body = String(entry.body || '').trim();
    const tags = Array.isArray(entry.tags)
      ? entry.tags.join(',')
      : String(entry.tags || '').trim();
    const now = new Date().toISOString();
    let link = String(entry.link || '').trim();
    if (!link) link = `${source}:${Date.now().toString(36)}`;
    const info = stmts.upsert.run({ source, link, title, body, tags, now });
    const id = Number(info.lastInsertRowid) || null;
    return { id, source, link, title, body, tags, created: now, updated: now, upserted: !id };
  }

  /* ---------- 检索（/kb/search） ---------- */
  function searchKnowledge(query, limit) {
    const q = String(query || '').trim();
    const n = Math.min(Math.max(Number(limit) || 5, 1), 20);
    if (!q) return { query: q, strategy: 'none', count: 0, hits: [] };
    const m = buildMatch(q);
    if (!m) return { query: q, strategy: 'none', count: 0, hits: [] };

    // 先 AND（精确），命中 < 3 条降级 OR + bm25（召回）
    const pick = (expr) => db.prepare(`
      SELECT k.id, k.source, k.link, k.title, k.body, k.tags,
             snippet(knowledge_fts, 1, '『', '』', '…', 12) AS snip,
             bm25(knowledge_fts) AS score
      FROM knowledge_fts
      JOIN knowledge k ON k.id = knowledge_fts.rowid
      WHERE knowledge_fts MATCH ?
      ORDER BY score
      LIMIT ?
    `).all(expr, n);

    let rows = pick(m.and);
    let strategy = 'and';
    if (rows.length < 3 && m.or !== m.and) {
      rows = pick(m.or);
      strategy = 'or';
    }
    return {
      query: q,
      strategy,
      count: rows.length,
      hits: rows.map((r) => ({
        id: r.id, source: r.source, link: r.link, title: r.title,
        snippet: r.snip, tags: r.tags, score: r.score
      }))
    };
  }

  /* ---------- 重建索引（评审缺口 2：禁用 FTS5 原生 rebuild） ----------
   * external content 模式下执行 'rebuild' 会绕过 UDF，用 unicode61 重新分词
   * 主表原文 → 中文整段单 token 废索引。任何重建必须走这条自建管道。 */
  function rebuildKb() {
    db.exec(`INSERT INTO knowledge_fts(knowledge_fts) VALUES('delete-all')`);
    const ins = db.prepare(`INSERT INTO knowledge_fts(rowid,title,body,tags) VALUES(?,?,?,?)`);
    for (const r of db.prepare('SELECT id,title,body,tags FROM knowledge').all()) {
      ins.run(r.id, getTokenizer()(r.title), getTokenizer()(r.body), getTokenizer()(r.tags));
    }
  }

  /* ---------- 导出（/kb/export，即备份路径） ---------- */
  function exportMd() {
    const rows = db.prepare('SELECT * FROM knowledge ORDER BY id').all();
    const lines = ['# 大肥鱼知识笔记本（导出自 ' + new Date().toISOString() + '）', ''];
    for (const r of rows) {
      lines.push('## [' + r.source + ':' + r.link + '] ' + r.title);
      if (r.tags) lines.push('标签：' + r.tags);
      if (r.body) lines.push('', r.body);
      lines.push('');
    }
    return lines.join('\n');
  }

  function exportJson() {
    return JSON.stringify(db.prepare('SELECT * FROM knowledge ORDER BY id').all(), null, 2);
  }

  return {
    addKnowledge,
    searchKnowledge,
    rebuildKb,
    exportMd,
    exportJson,
    removeKnowledge: (id) => stmts.del.run(id).changes,
    getKnowledge: (id) => stmts.get.get(id),
    allKnowledge: (n) => stmts.all.all(n || 100),
    count: () => stmts.count.get().n,
    tableExists: true
  };
}
