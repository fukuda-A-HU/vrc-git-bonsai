#!/usr/bin/env node
/**
 * generate-bonsai-json.mjs
 *
 * GitHub リポジトリの commit グラフを集計し、VRChat ワールド側で盆栽として
 * 可視化するための軽量 JSON (out/bonsai.json) を生成する。
 *
 * 依存パッケージなし。Node.js 標準モジュール (child_process, fs, path) のみ使用。
 *
 * 環境変数:
 *   BONSAI_REPO_DIR  対象 git リポジトリのパス（省略時は cwd）
 */

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const repoDir = process.env.BONSAI_REPO_DIR
  ? path.resolve(process.env.BONSAI_REPO_DIR)
  : process.cwd();

/** git コマンドを実行し stdout (trim 済み文字列) を返す。失敗時は throw。 */
function git(args) {
  return execFileSync('git', args, {
    cwd: repoDir,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  }).trim();
}

/** git コマンドを実行し、失敗時は null を返す（存在チェック等に使用）。 */
function gitOrNull(args) {
  try {
    return git(args);
  } catch {
    return null;
  }
}

/** 改行区切りの git 出力を配列にする（空文字列は空配列扱い）。 */
function gitLines(args) {
  const out = git(args);
  return out === '' ? [] : out.split('\n');
}

function fail(message) {
  console.error(`[generate-bonsai-json] ERROR: ${message}`);
  process.exit(1);
}

// ---------------------------------------------------------------------------
// 1. fetch
// ---------------------------------------------------------------------------
try {
  execFileSync('git', ['fetch', 'origin', '+refs/heads/*:refs/remotes/origin/*'], {
    cwd: repoDir,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
} catch (err) {
  console.warn(
    `[generate-bonsai-json] WARNING: git fetch に失敗しました。ローカルの refs のみで続行します。 (${err.message})`
  );
}

// ---------------------------------------------------------------------------
// 2. default branch 判定
// ---------------------------------------------------------------------------
let defaultBranch = null; // "origin/xxx" 形式（可能な場合）
let defaultRef = null; // rev-list 等に渡す実際の ref 名

{
  const symbolic = gitOrNull(['symbolic-ref', 'refs/remotes/origin/HEAD']);
  if (symbolic) {
    // 例: refs/remotes/origin/main -> origin/main
    defaultBranch = symbolic.replace(/^refs\/remotes\//, '');
    defaultRef = defaultBranch;
  }
}

if (!defaultRef) {
  for (const candidate of ['origin/master', 'origin/main']) {
    if (gitOrNull(['rev-parse', '--verify', '--quiet', candidate])) {
      defaultBranch = candidate;
      defaultRef = candidate;
      break;
    }
  }
}

if (!defaultRef) {
  // ローカル HEAD が指しているブランチにフォールバック
  const localBranch = gitOrNull(['symbolic-ref', '--short', 'HEAD']);
  if (localBranch) {
    defaultBranch = localBranch;
    defaultRef = localBranch;
  }
}

if (!defaultRef) {
  fail('default branch を判定できませんでした（origin/HEAD, origin/master, origin/main, ローカル HEAD すべて失敗）。');
}

// ---------------------------------------------------------------------------
// 3. 幹 (trunk)
// ---------------------------------------------------------------------------
let trunkChain;
try {
  trunkChain = gitLines(['rev-list', '--first-parent', defaultRef]);
} catch (err) {
  fail(`幹の commit chain 取得に失敗しました (${defaultRef}): ${err.message}`);
}

if (trunkChain.length === 0) {
  fail(`幹の commit chain が空です (${defaultRef})。`);
}

const trunkLen = trunkChain.length;
const trunkIndex = new Map(trunkChain.map((sha, i) => [sha, i]));

let recent30 = 0;
{
  const out = gitOrNull(['rev-list', '--first-parent', '--count', '--since=30.days', defaultRef]);
  recent30 = out ? parseInt(out, 10) : 0;
  if (!Number.isFinite(recent30)) recent30 = 0;
}

// 幹の "長さ" (0..1) は commits に対する log スケールで表現する。
// ブランチの len と同じ式 (log2(1+n)/6) を用い、幹らしい大きめの値に自然に寄る。
function clamp(v, lo, hi) {
  return Math.min(hi, Math.max(lo, v));
}
function round3(v) {
  return Math.round(v * 1000) / 1000;
}

const trunkLenNormalized = round3(clamp(Math.log2(1 + trunkLen) / 6, 0.05, 1));

// ---------------------------------------------------------------------------
// 4. ブランチ一覧
// ---------------------------------------------------------------------------
const remoteHeadSymbolic = gitOrNull(['symbolic-ref', '--short', 'refs/remotes/origin/HEAD']);

let remoteBranches;
try {
  remoteBranches = gitLines(['for-each-ref', '--format=%(refname:short)', 'refs/remotes/origin']);
} catch (err) {
  fail(`リモートブランチ一覧の取得に失敗しました: ${err.message}`);
}

remoteBranches = remoteBranches.filter((b) => {
  if (b === 'origin/HEAD') return false;
  if (remoteHeadSymbolic && b === remoteHeadSymbolic) return false;
  if (b === defaultBranch) return false;
  return true;
});

function fnv1a32(str) {
  let hash = 0x811c9dc5;
  for (let i = 0; i < str.length; i++) {
    hash ^= str.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  // unsigned 化
  return hash >>> 0;
}

const now = Date.now();
let excludedCount = 0;
const candidateBranches = [];

for (const branch of remoteBranches) {
  const shortName = branch.startsWith('origin/') ? branch.slice('origin/'.length) : branch;

  const mergeBase = gitOrNull(['merge-base', defaultRef, branch]);
  if (!mergeBase) {
    console.warn(`[generate-bonsai-json] WARNING: merge-base 取得に失敗したためスキップ: ${branch}`);
    excludedCount++;
    continue;
  }

  let index = trunkIndex.get(mergeBase);
  if (index === undefined) {
    // mergeBase から first-parent を辿ってチェーンに乗る最初の祖先を探す
    const mergeBaseChain = gitOrNull(['rev-list', '--first-parent', mergeBase]);
    const ancestors = mergeBaseChain ? mergeBaseChain.split('\n') : [];
    let found;
    for (const sha of ancestors) {
      if (trunkIndex.has(sha)) {
        found = trunkIndex.get(sha);
        break;
      }
    }
    index = found !== undefined ? found : trunkLen - 1;
  }

  const h = round3(clamp(1 - index / Math.max(1, trunkLen - 1), 0, 1));

  const aheadOut = gitOrNull(['rev-list', '--count', `${mergeBase}..${branch}`]);
  const ahead = aheadOut ? parseInt(aheadOut, 10) : 0;

  if (!Number.isFinite(ahead) || ahead === 0) {
    excludedCount++;
    continue;
  }

  const behindOut = gitOrNull(['rev-list', '--count', `${branch}..${defaultRef}`]);
  const behind = behindOut ? parseInt(behindOut, 10) : 0;

  const tipDateOut = gitOrNull(['log', '-1', '--format=%ct', branch]);
  const tipDateSec = tipDateOut ? parseInt(tipDateOut, 10) : null;
  const ageDays = tipDateSec !== null && Number.isFinite(tipDateSec)
    ? (now / 1000 - tipDateSec) / 86400
    : 0;
  const age = round3(clamp(ageDays / 90, 0, 1));

  const len = round3(clamp(Math.log2(1 + ahead) / 6, 0.05, 1));

  const seed = fnv1a32(shortName) % 360;

  candidateBranches.push({
    h,
    len,
    ahead,
    behind: Number.isFinite(behind) ? behind : 0,
    age,
    seed,
    _ageDays: ageDays, // ソート用の内部フィールド（出力前に除去）
  });
}

// ---------------------------------------------------------------------------
// 5. ソート & cap
// ---------------------------------------------------------------------------
candidateBranches.sort((a, b) => a._ageDays - b._ageDays); // age 昇順 = 新しい順

const MAX_BRANCHES = 16;
const cappedByLimit = Math.max(0, candidateBranches.length - MAX_BRANCHES);
const branches = candidateBranches.slice(0, MAX_BRANCHES).map(({ _ageDays, ...rest }) => rest);

// ---------------------------------------------------------------------------
// 6. 出力
// ---------------------------------------------------------------------------
const output = {
  v: 1,
  gen: Math.floor(now / 1000),
  trunk: {
    commits: trunkLen,
    recent30,
    len: trunkLenNormalized,
  },
  branches,
};

// 数値の健全性チェック（NaN / Infinity 禁止）
function assertFiniteDeep(value, keyPath) {
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) {
      fail(`生成された JSON に非有限数値が含まれています: ${keyPath} = ${value}`);
    }
  } else if (Array.isArray(value)) {
    value.forEach((v, i) => assertFiniteDeep(v, `${keyPath}[${i}]`));
  } else if (value && typeof value === 'object') {
    for (const [k, v] of Object.entries(value)) {
      assertFiniteDeep(v, `${keyPath}.${k}`);
    }
  }
}
assertFiniteDeep(output, '$');

const outDir = path.join(repoDir, 'out');
fs.mkdirSync(outDir, { recursive: true });
const outFile = path.join(outDir, 'bonsai.json');
fs.writeFileSync(outFile, JSON.stringify(output) + '\n', 'utf8');

// ---------------------------------------------------------------------------
// 7. サマリ出力
// ---------------------------------------------------------------------------
console.log('[generate-bonsai-json] summary');
console.log(`  repo dir       : ${repoDir}`);
console.log(`  default branch : ${defaultBranch}`);
console.log(`  trunk length   : ${trunkLen}`);
console.log(`  recent30       : ${recent30}`);
console.log(`  branches kept  : ${branches.length}`);
console.log(`  branches excl. : ${excludedCount} (ahead=0 or error), ${cappedByLimit} (over ${MAX_BRANCHES} cap)`);
console.log(`  output         : ${outFile}`);
