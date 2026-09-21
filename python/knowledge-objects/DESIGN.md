# 知识对象系统设计

> 本文件是知识对象系统的**权威设计文档**（架构 + 决策记录），随工具同仓自包含。
> README.md 是开发/使用快速上手，SKILL.md 是 agent 使用说明（部署物）——三者分工：DESIGN 讲为什么，README 讲怎么开发，SKILL 讲怎么用。

---

## 1. 一句话

**一个公司无关的工具 × N 个公司知识目录**：五类型领域知识模型（Scenario/Feature/Method/Procedure/Asset）+ 六命令（search/read/list/create/update/relate）——变的是各公司的内容，不变的是这五个桶和这套命令。

## 2. 设计目标

1. **按需加载**——不把领域知识塞 System Prompt；agent 按任务动态 search/read。
2. **跨场景复用**——Feature/Method/Assets 被多个 Scenario 复用；改一处，引用方拿新版。
3. **人机同源**——人用编辑器、agent 用命令，操作同一套 .md 文件。
4. **领域无关**——模型不绑信贷/风控/安全/营销；同一套五类型服务任何企业场景。

## 3. 五类型模型

| 类型 | 回答 | 职责 | 纪律 |
|---|---|---|---|
| scenario | Why | 问题/业务情境——**知识体系入口** | 描述「在什么情境下、为解决什么问题」 |
| feature | What | 观察维度/信号 | **不绑具体数据字段**（怎么取数归 asset） |
| method | How | 单一分析方法 | **不绑具体业务**（不存在「逾期专用 Lift」） |
| procedure | How to execute | 多步执行流程模板 | **可选非必经**——只有复杂/重复/稳定任务才沉淀 |
| asset | Where | 可调用的能力/资源 | 只描述「资源在哪、如何获取」，不写业务逻辑 |

**刻意不设 Intelligence 类型**：「已经知道什么」是公司/工作流特定的**状态**而非通用模式——它属于各公司目录自己的约定，不进公司无关契约。

## 4. 六命令

| 命令 | 语义 |
|---|---|
| `search <query> [--type] [--limit]` | 跨目录关键词检索（name>kind>body 命中排序） |
| `read <name> [--depth N]` | 读完整定义；裸名自动查五类型目录；depth 0~4 覆盖展开 |
| `list [folder]` | 按目录枚举 |
| `create <type> <name>` | 五类型校验 → 落对应类型目录，kind=type |
| `update <name>` | 部分更新 body/links（**type 不可改**——type=目录=身份） |
| `relate <name> <target> [--role]` | 追加边（默认 uses；同 role+target 幂等；目标解析不到仅警告） |

## 5. 文件格式与目录布局

```markdown
---
kind: scenario                    # = 五类型之一（create 自动写）
links:
- role: uses
  targetId: features/设备异常行为  # 相对知识库根路径（权威引用）
  expand: true                    # 可选：read 时内联该目标（默认 false）
---
# 标题

正文（markdown）。
```

```
<公司知识目录>/          ← 一个 git 仓库 = 一家公司的全部领域知识
├── README.md           ← 公司约定（术语/命名/范围）——agent 首要动作是读它
├── scenarios/  features/  methods/  procedures/  assets/
└── …
```

## 6. 解耦原则（什么住在哪）

| 归属 | 内容 |
|---|---|
| **工具**（本目录，公司无关） | 五类型契约、六命令、文件格式、根解析链、SKILL.md 使用纪律 |
| **公司目录**（公司各异） | 全部对象内容、根 README 公司约定、git 历史 |
| **宿主**（可替换） | 只是 python3 运行环境——任何 Agent Skills 规范宿主 |

## 7. 关键设计决策（ADR）

| 决策 | 理由 | 代价与补偿 |
|---|---|---|
| **五类型固定** | 公司无关契约的前提——工具能校验/过滤/落位，agent 有统一心智模型 | 表达力受限 → 正文自由 markdown 兜底 |
| **type=目录=身份** | create 落位即分类，免 front-matter 重复；update 不可改 type 防目录/kind 失同步 | 建错类型须删了重建（低成本，git 兜底） |
| **路径即引用**（非 Guid） | 人可读、git diff 友好、无字典映射 | 改名断链 → relate 目标解析警告 + 未来 rename 命令 |
| **relate 目标缺失仅警告** | 增量建集合法（先 relate 后 create 目标） | 可能引入断链 → 警告可见 + 未来 validate 命令 |
| **裸名跨五类型目录查找** | create scenario xxx 后 read xxx 即命中，免记前缀 | 同名跨类型歧义 → 按类型目录声明序取先 |
| **根解析链**（--root > env > 脚本位置 > CWD） | 打开哪家公司目录就服务哪家，免配置 | 全新空目录冷启动探测不到 → 首条命令 `--root .` |
| **零第三方依赖** | 单文件拷到任何有 python3 的机器即用 | 手写 YAML 子集解析（KO 只有 kind+links 两键，复杂度可控） |

## 8. 路线图

- Phase 2+：`validate`（断链/词表体检）、`rename`（入边自动改写）、`merge`（对象合并 + lineage）
- 纯 MCP 客户端覆盖：FastMCP 薄包装同一 ko.py
