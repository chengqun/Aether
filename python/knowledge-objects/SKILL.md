---
name: knowledge-objects
displayName: "知识对象"
description: "知识对象（五类型领域知识图谱）的检索/读取/创建/更新/关联。当需要查找领域知识（分析维度/方法/流程/资源）、为新问题建立知识、或在执行分析前获取参照知识时使用。"
python: true
requires-python: "3.9"
---

# 知识对象（Knowledge Objects）

知识对象 = **可复用的领域知识单元**，一个 `.md` 文件 = front-matter（kind + links）+ markdown 正文。
五类型模型（**公司无关**——变的是各公司的内容，不变的是这五个桶）：

| 类型 | 回答 | 说明 |
|---|---|---|
| scenario | Why | 问题/业务情境——知识体系**入口** |
| feature | What | 观察维度/信号（不绑具体数据字段） |
| method | How | 单一分析方法（不绑具体业务） |
| procedure | How to execute | 多步执行流程模板（**可选**，非必经节点） |
| asset | Where | 可调用的能力/资源（表/API/工具） |

## 首要动作

**先读知识库根的 `README.md`**——本公司/本领域的约定（术语、命名习惯、范围约束）在那里，不在本 skill。工具是通用的，惯例是公司自治的。

## 核心循环

```
用户问题 → search 找 Scenario（入口）
  ├─ 命中：read 它 + 它 links 指向的 Feature/Method → 直接复用
  └─ 未命中：create 最小集（先 search 防重复）
→ 复杂任务才建 Procedure（简单问题 Scenario→Feature/Method→Asset 直出）
→ Assets 指明用什么能力 → 真实执行（SQL/工具/API）
```

## 脚本用法

脚本位于本技能目录 `scripts/ko.py`：

```bash
python scripts/ko.py search <关键词> [--type T] [--limit N] [--root <知识库根>]
python scripts/ko.py read <名称> [--depth N] [--root <根>]
python scripts/ko.py list [folder] [--root <根>]
python scripts/ko.py create <type> <名称> [--body <markdown>] [--links '<JSON>'] [--root <根>]
python scripts/ko.py update <名称> [--body <markdown>] [--links '<JSON>'] [--root <根>]
python scripts/ko.py relate <名称> <目标> [--role <role>] [--root <根>]
```

- `type` ∈ scenario / feature / method / procedure / asset——create 落对应类型目录（`scenarios/` 等），
  front-matter `kind` 自动写 type 值。
- `名称` = 相对路径不含 `.md`（如 `scenarios/逾期分析`）；**裸名**（如 `逾期分析`）自动在五类型目录中查找。
- `--root` 缺省自动探测（优先级）：环境变量 `AETHER_KNOWLEDGE_ROOT` → 脚本位置上溯 →
  **当前工作目录上溯**（打开的知识目录当工作目录时自动命中，无需 `--root`）。
  **全新空目录冷启动**：目录里还没有任何知识对象时无法自动探测——首条命令带 `--root .`，
  创建第一个对象后类型目录存在，之后即可自动探测。
- `relate` 默认 role=uses；**同 role+target 幂等去重**；目标解析不到会警告但不失败（增量建集合法——先 relate 后 create 目标）。
- `read` 展开双模式：缺省=作者标志（只内联标 `expand: true` 的边）；`--depth N`=覆盖内联 N 层（0~4）。

## 文件格式

```markdown
---
kind: scenario
links:
- role: uses
  targetId: features/设备异常行为
- role: uses
  targetId: methods/时间窗口分析
---
# 标题

正文（markdown：情境描述 / 维度定义 / 方法步骤 / 资源说明等）。
```

- `kind` = 五类型之一（create 自动写）。`links` 的 `targetId` = 相对知识库根的路径（权威引用）。
- 可选 `expand: true` 标记「read 时内联该目标内容」（默认 false=只列引用，按需单独 read）。

## 纪律

1. **先 search 后 create**——防重复（同一概念两份是知识库的头号污染源）。
2. **最小集**——围绕当前问题建最少对象，不为完整而造对象。
3. **Procedure 不轻易造**——只有复杂、重复、稳定的任务才沉淀为流程模板。
4. **Feature 不绑表、Method 不绑业务**——具体数据怎么取归 asset，具体业务情境归 scenario。
