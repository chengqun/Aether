# knowledge-objects（知识对象工具——单一源）

知识对象（五类型领域知识图谱）**可移植读写工具**的项目家：`SKILL.md` + `scripts/ko.py`（零依赖单文件 CLI）。
本目录是**唯一源**——各宿主的技能目录都是它的部署副本（或软链）。

> **设计文档见 [DESIGN.md](DESIGN.md)**（五类型模型 / 六命令 / 解耦原则 / ADR 决策记录）——本 README 只讲结构与开发流程。

## 这是什么

- 一个**公司无关**的知识对象 CLI（六命令），操作任意公司的知识目录。
- 知识对象 = 一个 `.md` 文件（front-matter `kind:` + `links:` + markdown 正文），存放在知识库根的五类型子目录（`scenarios/` 等）。
- 零第三方依赖——单文件拷到任何有 python3 的机器即用。

## 结构

```
python/knowledge-objects/
├── SKILL.md          # skill 定义（部署物 1——Agent Skills 规范）
├── scripts/ko.py     # 零依赖 CLI（部署物 2）
├── tests/test_ko.py  # 行为锁（stdlib unittest，开发物，不进部署）
├── install.sh        # 部署脚本
├── DESIGN.md         # 权威设计文档
└── README.md         # 本文件
```

## 命令（六命令）

```bash
python3 scripts/ko.py search <query> [--type T] [--limit N] [--root R]
python3 scripts/ko.py read <name> [--depth N]  [--root R]
python3 scripts/ko.py list [folder]            [--root R]
python3 scripts/ko.py create <type> <name> [--body B] [--links JSON] [--root R]
python3 scripts/ko.py update <name> [--body B] [--links JSON] [--root R]
python3 scripts/ko.py relate <name> <target> [--role R] [--root R]
```

**五类型契约**：`type` ∈ scenario / feature / method / procedure / asset。
根目录解析链：`--root` > `AETHER_KNOWLEDGE_ROOT` > 脚本位置上溯 > **CWD 上溯**。
安全阀：list 200 / 深度 4 / 单目标 8000 字 / 累计 20000 字；`create` 带 `../` 逃逸守卫。

## 部署

```bash
./install.sh <目标技能目录>
```

| 宿主 | 方式 |
|---|---|
| Claude Code | 拷贝（install.sh）或软链 `ln -s`（单一源） |
| 拒绝 symlink 的宿主 | 拷贝（install.sh）；改 ko.py 后重跑 |

## 测试

```bash
cd python/knowledge-objects && python3 -m unittest discover -s tests
```

46 例：front-matter 解析/序列化、links JSON 容错、路径助手、根目录探测、CLI 端到端。
**升级 ko.py 前先跑这套**——它是行为锁。

## 路线图

- ~~search / relate / 五类型布局~~ ✅ 已完成
- Phase 2+：validate / rename / merge（见 DESIGN.md 路线图）
