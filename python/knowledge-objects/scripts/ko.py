#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""知识对象（Knowledge Objects）便携 CLI——五类型领域知识图谱的可移植读写工具。

一个公司无关的工具 × N 个公司知识目录。零第三方依赖——front-matter 手写 YAML 子集解析
（KO 只有 kind + links 两键）。SKILL.md + 本脚本即 Agent Skills 规范的完整形态，
任何支持该规范的宿主（Claude Code 等）可直接消费。

知识对象 = 一个 .md 文件（front-matter + markdown 正文）：

    ---
    kind: 数据源            # 分类：流程 / 数据源 / 规则 / Entity 等（自由字符串）
    links:                  # 有向边：role + targetId（相对路径，权威）
    - role: uses
      targetId: Hive表/fin_dw
      expand: true          # 可选：read 时内联该目标内容（默认 false=只列引用）
    ---
    <markdown 正文：说明 / 字段表 / SQL 模板 / 示例输出等>

命令：
    ko.py search <query> [--type T] [--limit N] [--root R]
    ko.py read <name> [--depth N]  [--root R]
    ko.py list [folder]            [--root R]
    ko.py create <type> <name> [--body B] [--links JSON] [--root R]
    ko.py update <name> [--body B] [--links JSON] [--root R]
    ko.py relate <name> <target> [--role R] [--root R]

五类型模型（精简版架构，**公司无关契约**——变的是各公司的内容，不变的是这五个桶）：
    scenario(问题情境 Why) / feature(观察维度 What) / method(分析方法 How) /
    procedure(执行流程 How-to-execute，可选) / asset(可调资源 Where)。
create 按 type 落对应类型目录（scenarios/ 等）且 front-matter kind=type；search --type 按类型过滤；
relate 追加一条 link（默认 role=uses，目标解析不到仅警告——可后续创建）。

知识库根目录解析（优先级）：--root 显式 > 环境变量 AETHER_KNOWLEDGE_ROOT > 脚本位置上溯 >
**当前工作目录上溯**（外部 agent 打开的工作目录——跑命令时 CWD 即打开的目录）。根的识别：
含 knowledge/ 子目录（labs 布局）、或含 ≥2 个五类型目录（scenarios/ 等，独立公司目录布局）、
或目录自身/一级子目录下有带 kind:/links: front-matter 的 .md（KO 库签名）——
**企业知识目录直接当工作目录打开即命中，无需 --root**。
根含 knowledge/ 子目录 = labs 模式（只操作该子树）；否则整目录即知识对象库。

成功：结果打 stdout、退出码 0；失败：错误打 stderr、退出码 1。
"""

import argparse
import json
import os
import sys
from pathlib import Path

# —— 安全阀（防撑爆 LLM 上下文）——
MAX_LIST = 200                 # list/search 枚举上限
MAX_EXPAND_DEPTH = 4           # read 递归展开深度硬上限
MAX_TARGET_INLINE_CHARS = 8000     # 单个内联正文上限（截断头部）
MAX_LINKED_INLINE_TOTAL_CHARS = 20000  # 内联正文累计上限（防撑爆 LLM 上下文）
PREVIEW_CHARS = 80             # list/search 的正文预览长度
KNOWLEDGE_SUBDIR = "knowledge"  # labs 模式的知识对象子目录名

# 五类型模型（精简版架构）：type → 类型目录名。create/search/裸名解析的公司无关契约。
TYPE_DIRS = {
    "scenario": "scenarios",    # 问题情境（Why）——知识体系入口
    "feature": "features",      # 观察维度（What）——不绑具体数据字段
    "method": "methods",        # 分析方法（How）——不绑具体业务
    "procedure": "procedures",  # 执行流程（How to execute）——可选，非必经节点
    "asset": "assets",          # 可调资源（Where）——表/API/工具
}

# 递归枚举跳过的目录名（构建产物/VCS/依赖目录；点开头目录一并跳过）
IGNORED_DIRS = {
    "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
    "packages", "testresults", "dist", "build", "target", "__pycache__",
    ".venv", "venv", ".next", ".nuxt",
}


class KoError(Exception):
    """用户可见错误（打 stderr，退出码 1）。"""


class KoDef:
    """知识对象内存模型：kind + links + body。links 元素为 {role, target, expand}。"""

    def __init__(self, kind="", links=None, body=""):
        self.kind = kind or ""
        self.links = links or []
        self.body = body or ""


# ——————————————————————————————————————————————
# front-matter 解析 / 序列化
# ——————————————————————————————————————————————

def parse_ko(raw):
    """解析 .md 源文本 → KoDef。容错语义：
    无 front-matter → 全文为正文；有 --- 无闭合 → 全文为正文；front-matter 坏 → 报错。"""
    if not raw or not raw.strip():
        raise KoError("源文本为空")
    text = raw.lstrip()
    kind, links, body = "", [], text
    if text.startswith("---"):
        rest = text[3:]
        idx = rest.find("\n---")
        if idx >= 0:
            yaml = rest[:idx]
            body = rest[idx + 4:].lstrip("\r\n")
            kind, links = _parse_frontmatter(yaml)
    return KoDef(kind, links, body)


def _parse_frontmatter(yaml):
    kind, links = "", []
    lines = yaml.splitlines()
    i, n = 0, len(lines)
    while i < n:
        s = lines[i].strip()
        if not s or s.startswith("#") or ":" not in s or s.startswith("- "):
            i += 1
            continue
        key, _, val = s.partition(":")
        key, val = key.strip().lower(), val.strip()
        if key == "kind":
            kind = _unquote(val)
            i += 1
        elif key == "links":
            if val.startswith("["):
                links = _parse_flow_links(val)
                i += 1
            else:
                i += 1
                # 块列表：后续 `- ` 行（允许项间空行；遇非列表行结束）
                while i < n:
                    t = lines[i].strip()
                    if t == "-" or t.startswith("- "):
                        item, i = _parse_block_link(lines, i)
                        link = _to_link(item)
                        if link is not None:
                            links.append(link)
                    elif not t:
                        j = i + 1
                        while j < n and not lines[j].strip():
                            j += 1
                        i = j
                        if i < n:
                            t2 = lines[i].strip()
                            if not (t2 == "-" or t2.startswith("- ")):
                                break
                    else:
                        break
        else:
            i += 1  # 未知键忽略
    return kind, links


def _parse_block_link(lines, i):
    """块列表项：`- role: uses`（首行）+ 更深缩进的 `key: value` 行并入。返回 (item, next_i)。"""
    first = lines[i]
    dash_indent = len(first) - len(first.lstrip())
    item = {}
    head = first.strip()[1:].strip()
    if head and ":" in head:
        k, _, v = head.partition(":")
        item[k.strip().lower()] = _unquote(v.strip())
    i += 1
    while i < len(lines):
        ln = lines[i]
        s = ln.strip()
        indent = len(ln) - len(ln.lstrip())
        if indent <= dash_indent:
            break  # 下一个列表项 / 顶层键 → 结束本项
        if s and ":" in s:
            k, _, v = s.partition(":")
            item[k.strip().lower()] = _unquote(v.strip())
        i += 1
    return item, i


def _parse_flow_links(val):
    """flow 风格 links: [{k: v, ...}, ...]（值无嵌套花括号）。"""
    links = []
    inner = val.strip()
    if inner.startswith("[") and inner.endswith("]"):
        inner = inner[1:-1].strip()
    if not inner:
        return links
    for part in _split_top_level(inner):
        part = part.strip()
        if part.startswith("{") and part.endswith("}"):
            part = part[1:-1].strip()
        if not part:
            continue
        item = {}
        for pair in _split_top_level(part):
            if ":" in pair:
                k, _, v = pair.partition(":")
                item[k.strip().lower()] = _unquote(v.strip())
        link = _to_link(item)
        if link is not None:
            links.append(link)
    return links


def _split_top_level(s):
    """按顶层逗号切分（忽略引号内 / 括号内逗号）。"""
    parts, cur, depth, quote = [], [], 0, None
    for ch in s:
        if quote:
            cur.append(ch)
            if ch == quote:
                quote = None
        elif ch in "\"'":
            quote = ch
            cur.append(ch)
        elif ch in "{[":
            depth += 1
            cur.append(ch)
        elif ch in "}]":
            depth -= 1
            cur.append(ch)
        elif ch == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    if cur:
        parts.append("".join(cur))
    return [p for p in (x.strip() for x in parts) if p]


def _to_link(item):
    """link dict → {role, target, expand}。targetId 优先、回退 target；两者皆空跳过。"""
    role = str(item.get("role", "") or "")
    target = str(item.get("targetid", "") or "") or str(item.get("target", "") or "")
    if not target.strip():
        return None
    expand = str(item.get("expand", "") or "").strip().lower() == "true"
    return {"role": role, "target": target, "expand": expand}


def _unquote(v):
    if len(v) >= 2 and v[0] == v[-1] and v[0] in "'\"":
        inner = v[1:-1]
        if v[0] == "'":
            inner = inner.replace("''", "'")  # YAML 单引号转义
        return inner
    return v


def serialize_ko(defn):
    """KoDef → .md 源文本（round-trip 稳定；输出为标准 YAML，兼容任何 YAML 解析器）。"""
    lines = ["---"]
    lines.append("kind: " + _yaml_str(defn.kind))
    if defn.links:
        lines.append("links:")
        for l in defn.links:
            lines.append("- role: " + _yaml_str(l.get("role", "")))
            lines.append("  targetId: " + _yaml_str(l.get("target", "")))
            if l.get("expand"):
                lines.append("  expand: true")
    else:
        lines.append("links: []")
    lines.append("---")
    return "\n".join(lines) + "\n" + (defn.body or "")


def _yaml_str(s):
    s = s or ""
    if s == "":
        return "''"
    if any(c in s for c in ":#{}[]&*!|>'\"%@`,\n") or s != s.strip():
        return "'" + s.replace("'", "''") + "'"
    return s


def parse_links_json(links_json):
    """工具入参 links JSON：[{role, targetId|target, expand}]，targetId 优先；
    非法 JSON → 空 links + 警告（容错，仍创建/更新）。"""
    links = []
    if not links_json or not links_json.strip():
        return links, None
    try:
        arr = json.loads(links_json)
        if not isinstance(arr, list):
            return [], "links JSON 非数组，已按空 links 处理"
        for el in arr:
            if not isinstance(el, dict):
                continue
            role = str(el.get("role") or "")
            target = str(el.get("targetId") or el.get("target") or "")
            if not target.strip():
                continue
            links.append({"role": role, "target": target,
                          "expand": el.get("expand") is True})
        return links, None
    except (ValueError, TypeError):
        return [], "links JSON 非法，已按空 links 处理"


# ——————————————————————————————————————————————
# 路径 / 作用域
# ——————————————————————————————————————————————

def resolve_scope(root):
    """根含 knowledge/ 子目录 = labs 模式（knowledgeRoot=该子目录）；否则整目录为库（向后兼容）。"""
    ksub = os.path.join(root, KNOWLEDGE_SUBDIR)
    if os.path.isdir(ksub):
        return os.path.abspath(ksub), True
    return os.path.abspath(root), False


def strip_knowledge_prefix(path):
    """剥开头的 knowledge/ 段（让裸路径与带前缀路径等价）；无前缀原样返回。"""
    if not path:
        return path
    norm = path.replace("\\", "/")
    pre = KNOWLEDGE_SUBDIR
    if len(norm) > len(pre) + 1 and norm[:len(pre)].lower() == pre and norm[len(pre)] == "/":
        return path[len(pre) + 1:]
    return path


def strip_ok_ext(path):
    """去末尾 .md 扩展名（知识对象相对路径规范 = 无扩展名）。"""
    if len(path) > 3 and path.lower().endswith(".md"):
        return path[:-3]
    return path


def stem_of(path):
    """文件名部分（去目录前缀 + 去 .md）= 显示名称。"""
    stem = path.replace("\\", "/").split("/")[-1] if path else path
    if stem and len(stem) > 3 and stem.lower().endswith(".md"):
        return stem[:-3]
    return stem


def candidate_paths(target, root, cur_dir):
    """候选文件全路径（按优先序）：无 .md 后缀时补；各按 根相对 / 当前文件目录 / 原样 兜底。"""
    has_ext = target.lower().endswith(".md")
    exts = [""] if has_ext else [".md"]
    out = []
    for ext in exts:
        with_ext = target + ext
        if root:
            out.append(os.path.join(root, with_ext))
        if cur_dir:
            out.append(os.path.join(cur_dir, with_ext))
        out.append(with_ext)  # 兜底：绝对路径或相对 cwd
    return out


def _find_ok_file(target, root, cur_dir):
    for p in candidate_paths(target, root, cur_dir):
        if os.path.isfile(p):
            return p
    return None


def _is_bare_name(path):
    """裸名（无分隔符、无 ..、非 .、非绝对）才允许全局 basename 兜底。"""
    return bool(path) and "/" not in path and "\\" not in path \
        and ".." not in path and path != "." and not os.path.isabs(path)


def _find_by_name_global(target, root):
    """裸名全局 basename 匹配（最后兜底：目录迁移后断链仍能找到）。扫描上限 200 文件。"""
    if not root or not os.path.isdir(root):
        return None
    stem = stem_of(target)
    best = None
    scanned = 0
    queue, seen = [root], set()
    while queue and scanned < MAX_LIST:
        d = queue.pop(0)
        if d in seen:  # 符号链接环兜底
            continue
        seen.add(d)
        try:
            entries = os.listdir(d)
        except OSError:
            continue
        for f in entries:
            if not f.lower().endswith(".md"):
                continue
            scanned += 1
            if os.path.splitext(f)[0].lower() != stem.lower():
                continue
            full = os.path.join(d, f)
            if best is None or full < best:
                best = full
        for f in entries:
            full = os.path.join(d, f)
            if not os.path.isdir(full):
                continue
            n = os.path.basename(f)
            if n.startswith(".") or n.lower() in ("bin", "obj", "node_modules"):
                continue
            queue.append(full)
    return best


def resolve_file(target, root, cur_dir):
    """link target → 首个存在的 .md 全路径（含裸名全局兜底）；找不到返 None。"""
    p = _find_ok_file(target, root, cur_dir)
    if p:
        return p
    if _is_bare_name(target):
        return _find_by_name_global(target, root)
    return None


def resolve_target_with_path(target, root, cur_dir, extra_root=None):
    """link target → (stem, 全路径或 None, KoDef 或 None)。找不到/读/解析失败 → 定义为 None。
    extra_root=知识对象目录（labs 布局下 root≠knowledge/ 子目录）：主根 miss 后再试一组候选——
    让「knowledge/ 子树相对（不带前缀，如 Hive表/fin_dw）」的 targetId 同样可解析（宽容超集，
    独立使用时作者最自然的写法）。"""
    if not target or not target.strip():
        return None
    stem = stem_of(target)
    full = resolve_file(target, root, cur_dir)
    if full is None and extra_root and os.path.abspath(extra_root) != os.path.abspath(root):
        full = resolve_file(target, extra_root, cur_dir)
    if full is None:
        return (stem, None, None)
    try:
        raw = _read_text(full)
    except OSError:
        return (stem, full, None)
    try:
        return (stem, full, parse_ko(raw))
    except KoError:
        return (stem, full, None)


def is_under(full, base_dir):
    """越界守卫：full 是否在 base_dir 子树内（大小写不敏感比较规范化全路径）。"""
    try:
        f = os.path.abspath(full).lower()
        b = os.path.abspath(base_dir).lower()
        return f == b or f.startswith(b + os.sep)
    except Exception:
        return False


def _read_text(path):
    with open(path, "r", encoding="utf-8-sig") as fh:  # 容 BOM
        return fh.read()


def _write_text(path, text):
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


# ——————————————————————————————————————————————
# read：递归展开（含全部安全阀与标记文案）
# ——————————————————————————————————————————————

def build_expanded_definition(defn, rel_path, focal_full, root, expand_depth):
    """组装「path + kind + name + 正文 + links」文本。expand_depth=None=作者标志模式
    （只内联标 expand:true 的边，沿标过的链递归）；非 None=覆盖模式 blanket N 层（0~4）。"""
    out = []
    knowledge_root, _ = resolve_scope(root)  # link 解析的宽容第二根（子树相对路径）

    def ci(p):
        return os.path.abspath(p).lower() if p else p

    follow_all = expand_depth is not None
    budget = min(expand_depth, MAX_EXPAND_DEPTH) if expand_depth is not None else MAX_EXPAND_DEPTH
    focal_dir = os.path.dirname(focal_full) if focal_full else ""
    visited = {ci(focal_full)} if focal_full else set()
    linked_total = [0]

    def append_node(defn, target_path, cur_dir, indent, remaining):
        # —— 头部 ——
        out.append(f"{indent}path: {target_path}")
        out.append(f"{indent}kind: {defn.kind}")
        out.append(f"{indent}name: {stem_of(target_path)}")

        # —— 正文（单目标截断 + 累计计数）——
        if defn.body and defn.body.strip():
            body = defn.body.rstrip()
            if len(body) > MAX_TARGET_INLINE_CHARS:
                body = body[:MAX_TARGET_INLINE_CHARS] + (
                    f"\n…（已截断，原文共 {len(defn.body)} 字符；"
                    f"如需完整内容请单独 read \"{target_path}\"）")
            linked_total[0] += len(body)
            out.append("")
            for ln in body.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
                out.append(indent + ln)

        # —— links ——
        if not defn.links:
            return
        out.append("")
        out.append(f"{indent}links:")
        for l in defn.links:
            resolved = resolve_target_with_path(l.get("target"), root, cur_dir, knowledge_root)
            if resolved is None:
                continue  # 空白路径，跳过
            title, child_full, child_def = resolved
            marker = " [expand]" if l.get("expand") else ""
            head = f"{indent}- [{l.get('role', '')}] {title} (path:{l.get('target', '')}){marker}"
            want_inline = follow_all or bool(l.get("expand"))
            if not want_inline:
                # 作者标志模式 + 未标：只列引用，不内联（agent 按需单独 read）
                out.append(head)
                continue
            out.append(head + ":")
            if remaining <= 0:
                out.append(f"{indent}  （已达展开深度上限，如需完整内容请单独 read 对应路径）")
                continue
            if linked_total[0] >= MAX_LINKED_INLINE_TOTAL_CHARS:
                out.append(f"{indent}  （引用过多，已停止展开后续目标；完整内容请单独 read 对应路径）")
                continue
            if child_full and ci(child_full) in visited:
                out.append(f"{indent}  （已在展开路径上，跳过防环）")
                continue
            if child_def is None:
                out.append(f"{indent}  （找不到知识对象文件）")
                continue
            # 内联：递归子节点（缩进 +2，剩余深度 -1）
            visited.add(ci(child_full))
            child_dir = os.path.dirname(child_full) if child_full else cur_dir
            append_node(child_def, l.get("target", ""), child_dir, indent + "  ", remaining - 1)

    append_node(defn, rel_path, focal_dir, "", budget)
    return "\n".join(out).rstrip()


# ——————————————————————————————————————————————
# list：递归枚举（跳构建产物/VCS 目录 + 符号链接，上限 200）
# ——————————————————————————————————————————————

def enumerate_ok_files(base_dir, root, items):
    if len(items) >= MAX_LIST:
        return
    try:
        entries = sorted(os.listdir(base_dir))
    except OSError:
        return
    for f in entries:
        if len(items) >= MAX_LIST:
            return
        full = os.path.join(base_dir, f)
        if not f.lower().endswith(".md") or not os.path.isfile(full):
            continue
        try:
            defn = parse_ko(_read_text(full))
            kind, preview = defn.kind, defn.body
        except (OSError, KoError):
            kind, preview = None, None  # 读/解析失败仍列出
        rel = os.path.relpath(full, root)
        items.append({
            "path": rel,
            "name": stem_of(rel),
            "kind": kind,
            "bodyPreview": (preview.strip()[:PREVIEW_CHARS] + "…")
                          if preview and preview.strip() and len(preview.strip()) > PREVIEW_CHARS
                          else (preview.strip() if preview and preview.strip() else None),
        })
    for f in entries:
        if len(items) >= MAX_LIST:
            return
        full = os.path.join(base_dir, f)
        if not os.path.isdir(full) or os.path.islink(full):
            continue
        n = os.path.basename(f)
        if n.startswith(".") or n.lower() in IGNORED_DIRS:
            continue
        enumerate_ok_files(full, root, items)


# ——————————————————————————————————————————————
# 根目录解析 + 命令
# ——————————————————————————————————————————————

def resolve_root(explicit):
    """根目录解析链：--root 显式 > AETHER_KNOWLEDGE_ROOT 环境变量 > 脚本位置上溯 >
    **当前工作目录上溯**（外部 agent 打开的工作目录——跑命令时 CWD 即打开的目录；
    宿主把 CWD 设为用户主目录时，脚本位置上溯会先命中）。
    「知识库根」的识别见 <see>_is_ko_root。"""
    if explicit:
        return os.path.abspath(explicit)
    env = os.environ.get("AETHER_KNOWLEDGE_ROOT")
    if env:
        return os.path.abspath(env)
    here = Path(__file__).resolve().parent  # <skill 目录>
    root = _walk_up_ko_root(here)
    if root:
        return root
    try:
        cwd = os.getcwd()
    except OSError:
        cwd = None
    if cwd:
        root = _walk_up_ko_root(cwd)
        if root:
            return root
    raise KoError(
        "无法定位知识库根目录（已探测脚本位置与当前目录：含 knowledge/ 子目录、或 .md 带 "
        "kind:/links: front-matter 的目录）。请用 --root 传入知识库根（labs 根或自定义知识"
        "对象目录；agent 已打开知识对象目录时通常 --root . 即可），或设环境变量 "
        "AETHER_KNOWLEDGE_ROOT")


def _is_ko_root(d):
    """目录是否像知识库根：含 knowledge/ 子目录（labs 布局）、或含 ≥2 个五类型目录
    （独立公司目录布局）、或自身/一级子目录下存在带 kind:/links: front-matter 的 .md
    （KO 库签名——plain 目录布局，企业知识目录直接当工作目录打开时靠它命中）。"""
    if os.path.isdir(os.path.join(d, KNOWLEDGE_SUBDIR)):
        return True
    if _has_typed_layout(d):
        return True
    return _has_ko_signature(d)


def _has_typed_layout(d):
    """五类型布局识别：目录下存在 ≥2 个类型目录（scenarios/features/…）——单个 assets
    目录不足以判定（普通项目常见，误报），成对出现才是知识库签名。"""
    return sum(1 for t in TYPE_DIRS.values() if os.path.isdir(os.path.join(d, t))) >= 2


def _has_ko_signature(d, max_subdirs=15, max_files=30):
    """KO 库签名探测（采样封顶，跳点开头/构建产物目录）：目录自身或一级子目录里存在
    front-matter 含 kind:/links: 键的 .md。SKILL.md/README（无 kind/links 键）不会误报。"""
    try:
        entries = sorted(os.listdir(d))
    except OSError:
        return False
    dirs = [d] + [os.path.join(d, f) for f in entries[:max_subdirs]
                  if not f.startswith(".") and f.lower() not in IGNORED_DIRS
                  and os.path.isdir(os.path.join(d, f))]
    checked = 0
    for dd in dirs:
        try:
            names = os.listdir(dd)
        except OSError:
            continue
        for f in names:
            if checked >= max_files:
                return False
            if not f.lower().endswith(".md"):
                continue
            checked += 1
            try:
                raw = _read_text(os.path.join(dd, f))
            except OSError:
                continue
            if _md_has_ko_frontmatter(raw):
                return True
    return False


def _md_has_ko_frontmatter(raw, keys=("kind", "links")):
    """front-matter 是否含 kind:/links: 键（快速判断，不做完整解析）。"""
    t = (raw or "").lstrip()
    if not t.startswith("---"):
        return False
    rest = t[3:]
    idx = rest.find("\n---")
    if idx < 0:
        return False
    for ln in rest[:idx].splitlines():
        k = ln.strip().split(":", 1)[0].strip().lower()
        if k in keys:
            return True
    return False


def _walk_up_ko_root(start, max_levels=4):
    """从 start 逐级向上找知识库根。找到最低匹配后**继续向上取连续匹配链的最上层**——
    agent 打开的常是知识库的子目录（如 <根>/业务分析 或 <根>/knowledge/业务分析），
    根应取整条链最顶上的匹配目录。返回 None 表示没找到。"""
    chain = []
    cur = os.path.abspath(start)
    for _ in range(max_levels + 1):
        chain.append(cur)
        parent = os.path.dirname(cur)
        if parent == cur:
            break
        cur = parent
    for i, d in enumerate(chain):
        if _is_ko_root(d):
            j = i
            while j + 1 < len(chain) and _is_ko_root(chain[j + 1]):
                j += 1
            return chain[j]
    return None


def _resolve_ko_candidates(name, root, knowledge_root, is_labs):
    """read/update/relate 共用的候选构造：根相对 + knowledge/ 相对 + **裸名的五类型目录逐一**
    （create scenario xxx 后 read xxx 即命中，免记类型目录前缀）。"""
    cands = candidate_paths(name, root, root)
    if is_labs:
        cands += candidate_paths(strip_knowledge_prefix(name), knowledge_root, knowledge_root)
    if "/" not in name.replace("\\", "/"):
        ext = "" if name.lower().endswith(".md") else ".md"
        for tdir in TYPE_DIRS.values():
            cands.append(os.path.join(knowledge_root, tdir, name + ext))
    return cands


def cmd_list(args):
    root = resolve_root(args.root)
    knowledge_root, is_labs = resolve_scope(root)
    if args.folder:
        folder_norm = strip_knowledge_prefix(args.folder) if is_labs else args.folder
        base = os.path.join(knowledge_root, folder_norm)
    else:
        base = knowledge_root
    if not os.path.isdir(base):
        raise KoError(f"目录不存在: {args.folder}")
    items = []
    enumerate_ok_files(base, root, items)
    print(json.dumps({"knowledgeObjects": items}, ensure_ascii=False, indent=2))


def cmd_read(args):
    if not args.name or not args.name.strip():
        raise KoError("名称不能为空")
    root = resolve_root(args.root)
    knowledge_root, is_labs = resolve_scope(root)
    cands = _resolve_ko_candidates(args.name, root, knowledge_root, is_labs)
    # 越界守卫：只接受知识对象目录内的文件（防误读 agents/规则/README 等非知识对象 .md）
    full = next((p for p in cands if os.path.isfile(p) and is_under(p, knowledge_root)), None)
    if full is None:
        raise KoError(f"找不到知识对象文件（或不在知识对象目录内）: {args.name}")
    try:
        raw = _read_text(full)
    except OSError as ex:
        raise KoError(f"读取失败: {ex.strerror or ex}")
    try:
        defn = parse_ko(raw)
    except KoError as ex:
        raise KoError(f"解析失败: {ex}")
    rel = os.path.relpath(full, root)
    print(build_expanded_definition(defn, rel, full, root, args.depth))


def cmd_create(args):
    # 五类型契约：type ∈ TYPE_DIRS，落对应类型目录，front-matter kind=type（公司无关）。
    if args.type not in TYPE_DIRS:
        raise KoError(f"type 必须是五类型之一（{', '.join(TYPE_DIRS)}），得到: {args.type}")
    if not args.name or not args.name.strip():
        raise KoError("名称不能为空")
    root = resolve_root(args.root)
    knowledge_root, _ = resolve_scope(root)
    # name 允许类型目录内的子路径（如 分析/逾期），但不得逃逸
    name_no_ext = strip_knowledge_prefix(strip_ok_ext(args.name))
    if not name_no_ext or not name_no_ext.strip():
        raise KoError("名称不能为空")
    full = os.path.join(knowledge_root, TYPE_DIRS[args.type], name_no_ext + ".md")
    # 越界守卫（防 ../ 逃逸出知识对象目录）
    if not is_under(full, knowledge_root):
        raise KoError(f"路径越界（不在知识对象目录内）: {args.name}")
    if os.path.exists(full):
        raise KoError(f"已存在: {TYPE_DIRS[args.type]}/{name_no_ext}")
    links, warning = parse_links_json(args.links)
    defn = KoDef(args.type, links, args.body or "")
    src = serialize_ko(defn)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    _write_text(full, src)
    out = {"success": True, "type": args.type,
           "path": os.path.relpath(full, root), "name": stem_of(name_no_ext)}
    if warning:
        out["warning"] = warning
    print(json.dumps(out, ensure_ascii=False))


def cmd_update(args):
    if not args.name or not args.name.strip():
        raise KoError("名称不能为空")
    root = resolve_root(args.root)
    knowledge_root, is_labs = resolve_scope(root)
    cands = _resolve_ko_candidates(args.name, root, knowledge_root, is_labs)
    full = next((p for p in cands if os.path.isfile(p) and is_under(p, knowledge_root)), None)
    if full is None:
        raise KoError(f"找不到知识对象文件（或不在知识对象目录内）: {args.name}")
    try:
        raw = _read_text(full)
    except OSError as ex:
        raise KoError(f"读取失败: {ex.strerror or ex}")
    try:
        defn = parse_ko(raw)
    except KoError as ex:
        raise KoError(f"解析失败: {ex}")
    # 部分更新：传哪个参数更新哪个，不传保留原值；links 全量替换。
    # type 不在 update 范围（type=目录=身份，建错类型应删了重建）。
    if args.body is not None:
        defn.body = args.body
    if args.links is not None:
        links, warning = parse_links_json(args.links)
        defn.links = links
    else:
        warning = None
    _write_text(full, serialize_ko(defn))
    name_no_ext = strip_ok_ext(os.path.relpath(full, root))
    out = {"success": True, "path": name_no_ext, "name": stem_of(name_no_ext)}
    if warning:
        out["warning"] = warning
    print(json.dumps(out, ensure_ascii=False))


# ——————————————————————————————————————————————
# search：跨目录关键词检索（精简版 knowledge.search）
# ——————————————————————————————————————————————

def _search_walk(base_dir, knowledge_root, q, type_filter, matches, scanned):
    """递归扫描 .md 做关键词匹配（name/kind/body 子串，不区分大小写）。"""
    if scanned[0] >= MAX_LIST * 10:
        return
    try:
        entries = sorted(os.listdir(base_dir))
    except OSError:
        return
    ql = q.lower()
    for f in entries:
        if scanned[0] >= MAX_LIST * 10:
            return
        full = os.path.join(base_dir, f)
        if not f.lower().endswith(".md") or not os.path.isfile(full):
            continue
        scanned[0] += 1
        try:
            defn = parse_ko(_read_text(full))
        except (OSError, KoError):
            continue
        rel = os.path.relpath(full, knowledge_root)
        # 类型过滤：front-matter kind 或路径首段类型目录（两者任一命中即算该类型）
        if type_filter:
            first_seg = rel.replace(os.sep, "/").split("/")[0].lower()
            if defn.kind.lower() != type_filter and first_seg != TYPE_DIRS[type_filter]:
                continue
        name = stem_of(rel)
        matched, score = [], 0
        if ql in (name or "").lower():
            matched.append("name"); score = 3
        if ql in (defn.kind or "").lower():
            matched.append("kind"); score = max(score, 2)
        if ql in (defn.body or "").lower():
            matched.append("body"); score = max(score, 1)
        if not matched:
            continue
        preview = (defn.body or "").strip()
        preview = (preview[:PREVIEW_CHARS] + "…") if len(preview) > PREVIEW_CHARS else preview
        matches.append((score, rel, defn.kind, "+".join(matched), preview or None))
    for f in entries:
        full = os.path.join(base_dir, f)
        if not os.path.isdir(full) or os.path.islink(full):
            continue
        n = os.path.basename(f)
        if n.startswith(".") or n.lower() in IGNORED_DIRS:
            continue
        _search_walk(full, knowledge_root, q, type_filter, matches, scanned)


def cmd_search(args):
    q = (args.query or "").strip()
    if not q:
        raise KoError("query 不能为空")
    type_filter = (args.type or "").strip().lower() or None
    if type_filter and type_filter not in TYPE_DIRS:
        raise KoError(f"--type 必须是五类型之一（{', '.join(TYPE_DIRS)}），得到: {args.type}")
    limit = args.limit if args.limit and args.limit > 0 else 20
    root = resolve_root(args.root)
    knowledge_root, _ = resolve_scope(root)

    matches, scanned = [], [0]
    _search_walk(knowledge_root, knowledge_root, q, type_filter, matches, scanned)
    # 排序：命中位置 name > kind > body；同分按路径（确定性）
    matches.sort(key=lambda m: (-m[0], m[1]))

    if not matches:
        print(f"（无命中——扫过 {scanned[0]} 个 .md；换关键词或 list 看全量）")
        return
    shown = matches[:limit]
    print(f"命中 {len(matches)} 条（显示前 {len(shown)}，扫过 {scanned[0]} 个 .md）：")
    for _score, rel, kind, matched_in, preview in shown:
        print(f"- {rel} [kind={kind or '?'}] 命中:{matched_in}")
        if preview:
            print(f"    {preview}")


# ——————————————————————————————————————————————
# relate：追加一条 link（精简版 knowledge.relate）
# ——————————————————————————————————————————————

def cmd_relate(args):
    if not args.name or not args.name.strip():
        raise KoError("名称不能为空")
    if not args.target or not args.target.strip():
        raise KoError("target 不能为空")
    root = resolve_root(args.root)
    knowledge_root, is_labs = resolve_scope(root)
    cands = _resolve_ko_candidates(args.name, root, knowledge_root, is_labs)
    full = next((p for p in cands if os.path.isfile(p) and is_under(p, knowledge_root)), None)
    if full is None:
        raise KoError(f"找不到知识对象文件（或不在知识对象目录内）: {args.name}")
    try:
        defn = parse_ko(_read_text(full))
    except (OSError, KoError) as ex:
        raise KoError(f"读取/解析失败: {ex}")

    role = (args.role or "uses").strip() or "uses"
    target = args.target.strip()
    rel = os.path.relpath(full, root)

    # 同 role+target 去重（幂等）
    for l in defn.links:
        if l.get("role") == role and l.get("target") == target:
            print(json.dumps({"success": True, "path": strip_ok_ext(rel),
                              "note": "已存在该边，未重复添加"}, ensure_ascii=False))
            return

    defn.links.append({"role": role, "target": target, "expand": False})
    _write_text(full, serialize_ko(defn))

    # 目标可解析性检查（防断链——Qifu 语料 65 条断链的教训）：解析不到仅警告不失败
    # （增量建集场景：先 relate 后 create 目标是合法顺序）
    resolved = resolve_target_with_path(target, root, os.path.dirname(full), knowledge_root)
    note = None
    if resolved is not None and resolved[1] is None:
        note = f"目标「{target}」当前解析不到任何知识对象文件（若非即将创建，请检查路径）"
        print("警告: " + note, file=sys.stderr)
    out = {"success": True, "path": strip_ok_ext(rel), "added": f"[{role}] {target}"}
    if note:
        out["warning"] = note
    print(json.dumps(out, ensure_ascii=False))


def main(argv):
    # 重定向时 Windows 默认按 locale 编码（cp936 等）打 CJK 会 UnicodeEncodeError——统一 UTF-8
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            try:
                stream.reconfigure(encoding="utf-8")
            except Exception:
                pass

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--root", default=None,
                        help="知识库根目录（labs 根或自定义知识对象目录）；缺省自动探测（脚本位置/当前工作目录上溯）")

    p = argparse.ArgumentParser(
        prog="ko.py",
        description="知识对象（五类型领域知识图谱）便携 CLI——公司无关契约：一个工具 × N 个公司知识目录",
        epilog="示例：\n"
               "  ko.py search 设备异常 --root ~/company-knowledge\n"
               "  ko.py read scenarios/逾期分析 --root ~/company-knowledge\n"
               "  ko.py read 逾期分析 --depth 1 --root ~/company-knowledge\n"
               "  ko.py create scenario 新场景 --body '情境描述' --root ~/company-knowledge\n"
               "  ko.py update scenarios/新场景 --body '更新' --root ~/company-knowledge\n"
               "  ko.py relate scenarios/新场景 features/设备异常 --root ~/company-knowledge\n"
               "  ko.py list --root ~/company-knowledge\n",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)

    ps = sub.add_parser("search", parents=[common], help="跨目录关键词搜索（name/kind/正文子串，按命中位置排序）")
    ps.add_argument("query", help="关键词（不区分大小写子串匹配名称/kind/正文）")
    ps.add_argument("--type", default=None,
                    help="五类型过滤（scenario/feature/method/procedure/asset）")
    ps.add_argument("--limit", type=int, default=20, help="返回上限（默认 20）")
    ps.set_defaults(func=cmd_search)

    pr = sub.add_parser("read", parents=[common], help="读取一个知识对象（含 links 展开）")
    pr.add_argument("name", help="相对路径（不含 .md；裸名自动在五类型目录中查找）")
    pr.add_argument("--depth", type=int, default=None,
                    help="展开深度：缺省=作者标志模式（只内联标 expand 的边）；N=覆盖模式内联 N 层（0~4）")
    pr.set_defaults(func=cmd_read)

    pl = sub.add_parser("list", parents=[common], help="列出知识对象（path/kind/正文预览）")
    pl.add_argument("folder", nargs="?", default=None,
                    help="知识对象目录内的子目录相对路径")
    pl.set_defaults(func=cmd_list)

    pc = sub.add_parser("create", parents=[common], help="创建知识对象（五类型，落对应类型目录）")
    pc.add_argument("type", help="五类型之一：scenario/feature/method/procedure/asset")
    pc.add_argument("name", help="名称或类型目录内子路径（不含 .md）")
    pc.add_argument("--body", default="", help="markdown 正文")
    pc.add_argument("--links", default=None, help='links JSON 数组，如 [{"role":"uses","targetId":"features/xxx"}]')
    pc.set_defaults(func=cmd_create)

    pu = sub.add_parser("update", parents=[common], help="部分更新正文/links（type 不在更新范围）")
    pu.add_argument("name", help="相对路径（不含 .md；裸名自动在五类型目录中查找）")
    pu.add_argument("--body", default=None, help="新正文（不传保留原值）")
    pu.add_argument("--links", default=None, help="新 links JSON 数组（不传保留原 links）")
    pu.set_defaults(func=cmd_update)

    pt = sub.add_parser("relate", parents=[common], help="追加一条 link（默认 role=uses；目标解析不到仅警告）")
    pt.add_argument("name", help="源对象（相对路径或裸名）")
    pt.add_argument("target", help="目标对象（相对路径或裸名）")
    pt.add_argument("--role", default=None, help="边角色（默认 uses）")
    pt.set_defaults(func=cmd_relate)

    args = p.parse_args(argv)
    try:
        args.func(args)
    except KoError as ex:
        print(str(ex), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
