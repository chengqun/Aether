# -*- coding: utf-8 -*-
"""ko.py 基线行为测试（stdlib unittest——保持零依赖哲学，`python3 -m unittest discover -s tests` 运行）。

锁定 2026-09-16 基线（.deepbank 版，833 行）的现有行为，给未来升级（五类型布局 / search / relate）
提供回归安全网。三组：纯函数（解析/序列化/路径助手）、根目录探测、CLI 端到端（子进程）。
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "scripts"))
import ko  # noqa: E402

KO_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "scripts", "ko.py")

SAMPLE = """---
kind: 流程
links:
- role: uses
  targetId: Hive表/fin_dw
- role: then
  targetId: 规则/退款
  expand: true
---
# 退款流程

先查表再套规则。
"""


def make_labs_root(tmp):
    """临时 labs 根（labs 布局：knowledge/ 子树 + 旧式任意树样例 + 五类型子树样例）。"""
    root = os.path.join(tmp, "labs")
    os.makedirs(os.path.join(root, "knowledge", "业务分析"))
    os.makedirs(os.path.join(root, "knowledge", "Hive表"))
    # 五类型子树（新布局与旧任意树在同一 knowledge/ 下共存）
    os.makedirs(os.path.join(root, "knowledge", "scenarios"))
    os.makedirs(os.path.join(root, "knowledge", "features"))
    with open(os.path.join(root, "README.md"), "w", encoding="utf-8") as f:
        f.write("# labs\n\n说明文档（无 kind/links front-matter，不应触发 KO 签名）。\n")
    with open(os.path.join(root, "knowledge", "业务分析", "退款.md"), "w", encoding="utf-8") as f:
        f.write(SAMPLE)
    with open(os.path.join(root, "knowledge", "Hive表", "fin_dw.md"), "w", encoding="utf-8") as f:
        f.write("---\nkind: 数据源\nlinks: []\n---\n# fin_dw\n\n字段表正文。\n")
    with open(os.path.join(root, "knowledge", "Hive表", "fin_dw2.md"), "w", encoding="utf-8") as f:
        f.write("---\nkind: 数据源\nlinks: []\n---\n# fin_dw2\n\n第二张表。\n")
    with open(os.path.join(root, "knowledge", "scenarios", "逾期分析.md"), "w", encoding="utf-8") as f:
        f.write("---\nkind: scenario\nlinks:\n- role: uses\n  targetId: features/设备异常行为\n---\n# 逾期分析\n\n为什么最近逾期率上涨。\n")
    with open(os.path.join(root, "knowledge", "features", "设备异常行为.md"), "w", encoding="utf-8") as f:
        f.write("---\nkind: feature\nlinks: []\n---\n# 设备异常行为\n\n设备指纹异常信号。\n")
    return root


class TestParseKo(unittest.TestCase):
    def test_block_links_with_expand(self):
        d = ko.parse_ko(SAMPLE)
        self.assertEqual(d.kind, "流程")
        self.assertEqual(len(d.links), 2)
        self.assertEqual(d.links[0], {"role": "uses", "target": "Hive表/fin_dw", "expand": False})
        self.assertEqual(d.links[1], {"role": "then", "target": "规则/退款", "expand": True})
        self.assertIn("退款流程", d.body)

    def test_flow_links(self):
        d = ko.parse_ko("---\nkind: 数据源\nlinks: [{role: 详解, targetId: Hive表/a}]\n---\n正文\n")
        self.assertEqual(len(d.links), 1)
        self.assertEqual(d.links[0]["target"], "Hive表/a")

    def test_no_frontmatter_whole_text_is_body(self):
        d = ko.parse_ko("# 只是正文\n\n没有 front-matter。\n")
        self.assertEqual(d.kind, "")
        self.assertEqual(d.links, [])
        self.assertIn("只是正文", d.body)

    def test_unclosed_frontmatter_falls_back_to_body(self):
        d = ko.parse_ko("---\nkind: 流程\n没有闭合\n")
        self.assertEqual(d.kind, "")
        self.assertIn("kind: 流程", d.body)

    def test_unknown_keys_ignored(self):
        d = ko.parse_ko("---\nkind: 数据源\nsubject: 额外键\nlinks: []\n---\n正文\n")
        self.assertEqual(d.kind, "数据源")
        self.assertEqual(d.links, [])

    def test_target_fallback_when_no_targetid(self):
        d = ko.parse_ko("---\nkind: k\nlinks:\n- role: r\n  target: 裸目标\n---\nb\n")
        self.assertEqual(d.links[0]["target"], "裸目标")

    def test_empty_text_raises(self):
        with self.assertRaises(ko.KoError):
            ko.parse_ko("   ")

    def test_empty_link_item_skipped(self):
        d = ko.parse_ko("---\nkind: k\nlinks:\n- role: r\n---\nb\n")  # 无 targetId/target → 跳过
        self.assertEqual(d.links, [])


class TestSerialize(unittest.TestCase):
    def test_roundtrip_stable(self):
        d = ko.parse_ko(SAMPLE)
        src = ko.serialize_ko(d)
        d2 = ko.parse_ko(src)
        self.assertEqual(d2.kind, d.kind)
        self.assertEqual(d2.links, d.links)
        self.assertEqual(d2.body, d.body)

    def test_special_chars_quoted(self):
        d = ko.KoDef("带:冒号", [{"role": "r", "target": "路径'引号", "expand": False}], "正文")
        src = ko.serialize_ko(d)
        d2 = ko.parse_ko(src)
        self.assertEqual(d2.kind, "带:冒号")
        self.assertEqual(d2.links[0]["target"], "路径'引号")

    def test_no_links_serializes_empty_list(self):
        src = ko.serialize_ko(ko.KoDef("数据源", [], "b"))
        self.assertIn("links: []", src)

    def test_output_structure_csharp_compatible(self):
        # 输出结构：kind + links[role/targetId]（标准 YAML，任何解析器可读）
        src = ko.serialize_ko(ko.KoDef("流程", [{"role": "uses", "target": "A/B", "expand": True}], "b"))
        self.assertIn("- role: uses", src)
        self.assertIn("  targetId: A/B", src)
        self.assertIn("  expand: true", src)


class TestParseLinksJson(unittest.TestCase):
    def test_valid(self):
        links, warn = ko.parse_links_json('[{"role":"uses","targetId":"A","expand":true}]')
        self.assertIsNone(warn)
        self.assertEqual(links, [{"role": "uses", "target": "A", "expand": True}])

    def test_invalid_json_tolerated(self):
        links, warn = ko.parse_links_json("[broken")
        self.assertEqual(links, [])
        self.assertIsNotNone(warn)

    def test_non_array_tolerated(self):
        links, warn = ko.parse_links_json('{"role":"uses"}')
        self.assertEqual(links, [])
        self.assertIsNotNone(warn)

    def test_targetid_priority(self):
        links, _ = ko.parse_links_json('[{"role":"r","targetId":"A","target":"B"}]')
        self.assertEqual(links[0]["target"], "A")


class TestPathHelpers(unittest.TestCase):
    def test_strip_ok_ext(self):
        self.assertEqual(ko.strip_ok_ext("a/b.md"), "a/b")
        self.assertEqual(ko.strip_ok_ext("a/b"), "a/b")

    def test_stem_of(self):
        self.assertEqual(ko.stem_of("Hive表/fin_dw.md"), "fin_dw")
        self.assertEqual(ko.stem_of("退款"), "退款")

    def test_strip_knowledge_prefix(self):
        self.assertEqual(ko.strip_knowledge_prefix("knowledge/业务分析/退款"), "业务分析/退款")
        self.assertEqual(ko.strip_knowledge_prefix("业务分析/退款"), "业务分析/退款")

    def test_is_under(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = os.path.join(tmp, "ko")
            os.makedirs(base)
            self.assertTrue(ko.is_under(os.path.join(base, "a.md"), base))
            self.assertFalse(ko.is_under(os.path.join(tmp, "other.md"), base))

    def test_candidate_paths_order(self):
        cands = ko.candidate_paths("a/b", "/root", "/dir")
        self.assertEqual(cands[0], os.path.join("/root", "a/b.md"))
        self.assertEqual(cands[1], os.path.join("/dir", "a/b.md"))


class TestRootDetection(unittest.TestCase):
    def test_labs_layout_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            self.assertTrue(ko._is_ko_root(root))

    def test_ko_signature_detected(self):
        # 无 knowledge/ 子目录的普通目录：带 kind:/links: front-matter 的 .md 即 KO 库签名
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "对象.md"), "w", encoding="utf-8") as f:
                f.write("---\nkind: 数据源\nlinks: []\n---\n正文\n")
            self.assertTrue(ko._is_ko_root(tmp))

    def test_plain_dir_not_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "README.md"), "w", encoding="utf-8") as f:
                f.write("---\nname: x\ndescription: y\n---\n正文\n")  # 无 kind/links 键
            self.assertFalse(ko._is_ko_root(tmp))

    def test_walk_up_from_subdirectory(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            sub = os.path.join(root, "knowledge", "业务分析")
            self.assertEqual(ko._walk_up_ko_root(sub), root)


class TestCliEndToEnd(unittest.TestCase):
    def run_ko(self, *args, root=None):
        argv = [sys.executable, KO_PATH, *args]
        if root:
            argv += ["--root", root]
        return subprocess.run(argv, capture_output=True, text=True, timeout=30)

    def test_list_json(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("list", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            data = json.loads(r.stdout)
            paths = [o["path"] for o in data["knowledgeObjects"]]
            self.assertIn(os.path.join("knowledge", "业务分析", "退款.md"), paths)
            self.assertIn(os.path.join("knowledge", "scenarios", "逾期分析.md"), paths)

    def test_read_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("read", "业务分析/退款", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertIn("kind: 流程", r.stdout)
            self.assertIn("退款流程", r.stdout)
            self.assertIn("[uses] fin_dw", r.stdout)  # 未标 expand 只列引用

    def test_read_depth_inline(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("read", "业务分析/退款", "--depth", "1", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertIn("字段表正文", r.stdout)  # depth 覆盖模式内联目标正文

    def test_read_bare_name_across_typedirs(self):
        # 裸名（无路径）→ 五类型目录逐一查找（create scenario xxx 后 read xxx 即命中）
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("read", "逾期分析", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertIn("kind: scenario", r.stdout)
            self.assertIn("[uses] 设备异常行为", r.stdout)

    def test_create_five_type_lands_in_typedir(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("create", "method", "客群分层",
                            "--body", "按客群分层对比",
                            "--links", '[{"role":"uses","targetId":"features/设备异常行为"}]',
                            root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            full = os.path.join(root, "knowledge", "methods", "客群分层.md")
            self.assertTrue(os.path.isfile(full))
            r2 = self.run_ko("read", "methods/客群分层", root=root)
            self.assertIn("kind: method", r2.stdout)
            self.assertIn("按客群分层对比", r2.stdout)

    def test_create_invalid_type_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("create", "bogus", "xxx", root=root)
            self.assertEqual(r.returncode, 1)
            self.assertIn("五类型", r.stderr)

    def test_create_duplicate_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("create", "scenario", "逾期分析", root=root)
            self.assertEqual(r.returncode, 1)
            self.assertIn("已存在", r.stderr)

    def test_create_escape_guard(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("create", "scenario", "../../etc/evil", root=root)
            self.assertEqual(r.returncode, 1)
            self.assertIn("越界", r.stderr)

    def test_update_partial_preserves_rest(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("update", "业务分析/退款", "--body", "替换后的正文", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            r2 = self.run_ko("read", "业务分析/退款", root=root)
            self.assertIn("替换后的正文", r2.stdout)
            self.assertIn("kind: 流程", r2.stdout)          # kind 不在 update 范围，保留
            self.assertIn("[uses] fin_dw", r2.stdout)        # 未传 links 保留

    def test_update_bare_name(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("update", "设备异常行为", "--body", "新描述", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            r2 = self.run_ko("read", "features/设备异常行为", root=root)
            self.assertIn("新描述", r2.stdout)

    def test_search_by_name_kind_body(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("search", "逾期", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertIn("逾期分析", r.stdout)      # name 命中
            self.assertIn("命中:name", r.stdout)      # 排序标注
            r2 = self.run_ko("search", "scenario", root=root)
            self.assertEqual(r2.returncode, 0, r2.stderr)
            self.assertIn("逾期分析", r2.stdout)     # kind 命中
            r3 = self.run_ko("search", "字段表", root=root)
            self.assertEqual(r3.returncode, 0, r3.stderr)
            self.assertIn("fin_dw", r3.stdout)        # body 命中

    def test_search_type_filter(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("search", "异常", "--type", "feature", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertIn("设备异常行为", r.stdout)
            self.assertNotIn("逾期分析", r.stdout)  # scenario 过滤掉
            r2 = self.run_ko("search", "x", "--type", "bogus", root=root)
            self.assertEqual(r2.returncode, 1)

    def test_search_no_match(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("search", "不存在的关键词xyz", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            self.assertIn("无命中", r.stdout)

    def test_relate_adds_link(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("relate", "scenarios/逾期分析", "methods/客群分层", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            r2 = self.run_ko("read", "scenarios/逾期分析", root=root)
            self.assertIn("[uses] 客群分层", r2.stdout)

    def test_relate_bare_names(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("relate", "逾期分析", "设备异常行为", "--role", "requires", root=root)
            self.assertEqual(r.returncode, 0, r.stderr)
            r2 = self.run_ko("read", "逾期分析", root=root)
            self.assertIn("[requires] 设备异常行为", r2.stdout)

    def test_relate_idempotent(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            self.run_ko("relate", "逾期分析", "设备异常行为", root=root)
            r = self.run_ko("relate", "逾期分析", "设备异常行为", root=root)
            self.assertEqual(r.returncode, 0)
            self.assertIn("未重复添加", r.stdout)

    def test_relate_missing_target_warns_not_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("relate", "逾期分析", "features/还不存在", root=root)
            self.assertEqual(r.returncode, 0)  # 警告不失败（增量建集合法）
            self.assertIn("警告", r.stderr)

    def test_read_missing_exits_1(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("read", "不存在/的对象", root=root)
            self.assertEqual(r.returncode, 1)
            self.assertIn("找不到", r.stderr)

    def test_read_out_of_scope_rejected(self):
        # 越界守卫：labs 根的 README.md（非 knowledge/ 子树）不可读
        with tempfile.TemporaryDirectory() as tmp:
            root = make_labs_root(tmp)
            r = self.run_ko("read", "README", root=root)
            self.assertEqual(r.returncode, 1)
            self.assertIn("找不到", r.stderr)


class TestFiveTypeRootDetection(unittest.TestCase):
    def test_typed_layout_detected(self):
        # 独立公司目录（无 knowledge/ 子目录）：≥2 个类型目录即知识库根
        with tempfile.TemporaryDirectory() as tmp:
            for t in ("scenarios", "features", "methods"):
                os.makedirs(os.path.join(tmp, t))
            self.assertTrue(ko._is_ko_root(tmp))
            # 子目录上溯也能到根
            self.assertEqual(ko._walk_up_ko_root(os.path.join(tmp, "features")), tmp)

    def test_single_assets_dir_not_detected(self):
        # 单一 assets 目录（普通项目常见）不误报
        with tempfile.TemporaryDirectory() as tmp:
            os.makedirs(os.path.join(tmp, "assets"))
            self.assertFalse(ko._is_ko_root(tmp))


if __name__ == "__main__":
    unittest.main()
