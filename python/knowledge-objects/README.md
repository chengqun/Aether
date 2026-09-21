# knowledge-objects（知识对象便携 skill——单一源）

知识对象（五类型领域知识图谱）**可移植读写工具**的项目家：`SKILL.md` + `scripts/ko.py`（零依赖单文件 CLI）。
本目录是**唯一源**——各宿主的技能目录都是它的部署副本（或软链）。

> **设计文档见 [DESIGN.md](DESIGN.md)**（五类型模型 / 六命令 / 解耦原则 / ADR 决策记录）——本 README 只讲结构与开发流程。

## 这是什么

- `ko.py` 与 Aether 桌面端 C# 工具（`DocumentKnowledgeTool` / `DocumentKnowledgeSourceParser` /
  `KnowledgeExpansionBuilder` / `OkTargetResolver`）**语义逐条对齐**的可移植实现——零第三方依赖
  （front-matter 手写 YAML 子集解析），任何能跑 `python3` 的宿主都能用。
- 知识对象 = 一个 `.md` 文件（front-matter `kind:` + `links:`（role/targetId/expand）+ markdown 正文），
  存放在知识库根的 `knowledge/` 子树（labs 布局）或自定义目录（KO 签名识别）。
- 设计立场：**tools 是 tools、文档是文档**——本目录是能力（脚本+指令），知识库目录是文档（纯 markdown + git），
  两者只靠根解析链连接，互不拥有。仓库托管源码不构成运行时耦合。

## 结构

```
python/knowledge-objects/
├── SKILL.md          # skill 定义（部署物 1——Agent Skills 规范，Claude Code/Aether 直接消费）
├── scripts/ko.py     # 零依赖 CLI（部署物 2）
├── tests/test_ko.py  # 基线行为测试（stdlib unittest，开发物，不进部署）
├── install.sh        # 部署脚本
└── README.md         # 本文件
```

对 slnx / 17 条层化架构测试完全不可见（它们只扫 csproj/fsproj）——Python 项目对 .NET 解决域零影响。

## 命令（六命令：search / read / list / create / update / relate）

```bash
python3 scripts/ko.py search <query> [--type T] [--limit N] [--root R]
python3 scripts/ko.py read <name> [--depth N]  [--root R]
python3 scripts/ko.py list [folder]            [--root R]
python3 scripts/ko.py create <type> <name> [--body B] [--links JSON] [--root R]
python3 scripts/ko.py update <name> [--body B] [--links JSON] [--root R]
python3 scripts/ko.py relate <name> <target> [--role R] [--root R]
```

**五类型契约**（公司无关）：`type` ∈ scenario / feature / method / procedure / asset——create 落对应
类型目录（`scenarios/` 等）、front-matter kind=type；search --type 过滤；relate 追加边（默认 uses，
同 role+target 幂等去重，目标解析不到仅警告）。裸名（无路径）自动在五类型目录中查找。

根目录解析链：`--root` 显式 > 环境变量 `AETHER_KNOWLEDGE_ROOT` > 脚本位置上溯 > **CWD 上溯**。
根识别：knowledge/ 子目录（labs 布局）、**≥2 个五类型目录**（独立公司目录布局）、或 KO 签名
（kind:/links: front-matter 的 .md）。**全新空目录冷启动**：首条命令带 `--root .`。
安全阀与 C# 一致：list 200 / 深度 4 / 单目标 8000 字 / 累计 20000 字；`create` 带 `../` 逃逸守卫。

## 部署

```bash
./install.sh <目标技能目录>
```

| 宿主 | 方式 |
|---|---|
| Claude Code | 拷贝（install.sh）或**软链** `ln -s …/python/knowledge-objects <技能目录>`（单一源，改一处全局生效） |
| Aether（labs/skills） | **必须拷贝**——MAF skill 路径硬化拒绝 symlink；改 ko.py 后重跑 install.sh |

## 测试

```bash
cd python/knowledge-objects && python3 -m unittest discover -s tests
```

46 例测试：front-matter 解析/序列化（round-trip、特殊字符引号、C# 互读结构）、links JSON 容错、
路径助手、根目录探测（labs 布局 / **五类型布局** / KO 签名 / 单 assets 目录负例）、CLI 端到端
（search 按名/kind/正文 + 类型过滤、五类型 create 落位、裸名跨类型目录查找、relate 追加/幂等/
目标缺失警告、越界守卫、逃逸守卫）。**升级 ko.py 前先跑这套**——它是行为锁。

## 路线图（慢慢来）

- ~~search / relate / 五类型布局~~ ✅ 已完成（2026-09-21）
- 桌面端 C# KO 工具退休（三个 SKILL.md 指令改写 → 双轨验证 → 删 `AddDocumentKnowledgeTool()` 一行；手机端不动）
- Phase 2+：validate / rename / merge 治理面（对齐 Aether C# 版语义）
