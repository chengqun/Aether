> **存档说明**：本文是产品愿景文档《AI Agent 知识对象最终架构（精简版）》（用户提供，2026-09-22 存档进仓库），作为知识对象实现对齐基准。
> 实现状态逐项对照见 [knowledge-objects-design.md](knowledge-objects-design.md) §14（已做 / 未来标记）。

# AI Agent 知识对象最终架构

## 1. 设计目标

知识对象（Knowledge Object）是 AI Agent 的**可复用领域知识单元**。

核心目标：

1. **按需加载**
   - 不把整个领域知识放入 System Prompt。
   - Agent 根据当前任务动态搜索、读取相关知识对象。

2. **跨场景复用**
   - Feature、Method、Assets 等底层对象可以被多个 Scenario 复用。
   - 知识修改后，引用它的场景可以获得最新版本。

3. **人机同源**
   - 人可以通过编辑器创建和维护知识。
   - Agent 可以通过 Knowledge Tools 创建、读取和修改知识。
   - 人和 Agent 操作的是同一套 Knowledge Object。

4. **领域无关**
   - 模型不绑定信贷、风控、网络安全等具体领域。
   - 同一套模型可以用于金融、网络安全、运维、营销、数据分析等企业场景。

---

# 2. 最终 Knowledge Object 类型

最终采用五类 Knowledge Object：

```text
Knowledge Object
│
├── Scenario
├── Feature
├── Method
├── Procedure
└── Assets
```

不设置 `Intelligence` 作为独立类型。

---

# 3. Scenario

## 定义

Scenario 表示一个需要解决的**问题、业务情境或分析场景**。

它是知识体系中的主要入口。

一个 Scenario 描述：

> 在什么情境下，为了解决什么问题，需要关注什么、采用什么方法，并最终产生什么产物。

基本结构：

```text
Scenario
│
├── Context
├── Inputs
├── Features
├── Methods
├── Procedures
├── Assets
└── Outputs
```

其中：

- `Features`：该场景关注什么
- `Methods`：该场景如何分析
- `Procedures`：复杂场景需要执行的流程，可选
- `Assets`：需要调用的能力
- `Outputs`：场景最终产生的产物

---

# 4. Feature

## 定义

Feature 表示：

> **需要观察什么。**

Feature 不是原始数据字段，而是 Agent 在某类问题中应该关注的：

- 现象
- 属性
- 信号
- 特征维度

例如：

```text
Feature:
  设备异常行为
```

底层可能对应：

```text
device_id
device_count
device_change_frequency
device_cluster
```

但 Feature 本身不等于这些数据库字段。

Feature 应尽量与 Scenario 解耦，从而被多个 Scenario 复用。

---

# 5. Method

## 定义

Method 表示：

> **如何进行分析、推理或判断。**

例如：

```text
Method:
  客群分层

Method:
  时间窗口分析

Method:
  关联网络分析

Method:
  异常检测
```

Method 应尽可能独立于具体 Scenario。

例如：

```text
时间窗口分析
```

可以被：

```text
逾期分析
欺诈分析
营销分析
网络安全分析
```

等多个 Scenario 使用。

---

# 6. Procedure

## 定义

Procedure 是：

> **可复用的复杂执行流程模板。**

Procedure 不是必经节点。

不是：

```text
Scenario
  ↓
Feature
  ↓
Method
  ↓
Procedure
  ↓
Assets
```

而是：

```text
Scenario
├── Feature
├── Method
├── Procedure（可选）
└── Assets
```

简单问题可以直接使用 Feature、Method 和 Assets。

复杂问题才需要 Procedure。

例如：

```text
Procedure:
  欺诈风险深度分析

Steps:
  1. 查询目标客群
  2. 计算设备特征
  3. 计算 APP 行为特征
  4. 构建设备关联网络
  5. 执行风险聚类
  6. 对比历史欺诈客群
  7. 输出分析结果
```

Procedure 的定位是**执行计划模板**，而不是知识图谱的固定层级。

---

# 7. Assets

## 定义

Assets 表示：

> **Agent 可以实际调用的能力或资源。**

例如：

```text
Assets
├── MAF Tool
├── MCP Tool
├── API
├── Database
├── Agent
└── Workflow
```

例如：

```text
Asset:
  BigData SQL Query

Capability:
  执行大数据 SQL 查询
```

Assets 本身不负责执行。

真正的执行由 MAF 和具体 Tool / MCP 完成。

因此：

```text
Knowledge Object
      │
      │ 描述需要什么能力
      ▼
    Assets
      │
      ▼
     MAF
      │
      ▼
 Tool / MCP / API
```

---

# 8. Knowledge Object 的关系

最基本的关系：

```text
                    Scenario
                   /    |    \
                  /     |     \
                 ▼      ▼      ▼
            Feature   Method   Procedure
                │       │        │
                │       │        ▼
                │       │       Steps
                │       │
                └───────┴──────────────┐
                                       ▼
                                     Assets
```

但这不是固定的树，而是一个**可复用的知识图**。

例如：

```text
Scenario A ─────┐
                ├──→ Feature X
Scenario B ─────┤
                │
Scenario C ─────┘
```

同一个 Feature 可以被多个 Scenario 使用。

Method 和 Assets 同样可以被多个 Scenario 复用。

---

# 9. 当前阶段的最小实现

第一阶段不要实现完整的知识进化系统。

只需要完成：

```text
Knowledge Object
│
├── Scenario
├── Feature
├── Method
├── Procedure
└── Assets
```

以及最基本的：

```text
Search
Get
Create
Update
Relate
```

Knowledge Tools。

核心闭环只有：

```text
User Question
      ↓
Scenario
      ↓
Feature / Method
      ↓
Procedure（需要时）
      ↓
Assets
      ↓
MAF Execution
      ↓
Scenario Output
```

先让这条链真正跑通。

---

# 10. 未来路线图

## Phase 1：知识对象基础

目标：

> **先把 Knowledge Object 做出来。**

实现：

```text
Scenario
Feature
Method
Procedure
Assets
```

以及：

```text
knowledge.search
knowledge.get
knowledge.create
knowledge.update
knowledge.relate
```

重点验证：

> Agent 能不能从一个 Scenario 出发，找到正确的 Feature、Method 和 Assets，并完成一次真实分析。

---

## Phase 2：知识图与复用

解决：

```text
多个 Scenario
      ↓
共享 Feature / Method / Assets
```

增加：

- Relation
- Reference
- Dependency
- Version

重点验证：

> 一个知识对象修改后，多个引用它的场景是否能够自然获得更新。

---

## Phase 3：知识自动产生

让 Agent 能够在工作过程中发现：

```text
新的 Scenario
新的 Feature
新的 Method
新的 Procedure
```

但先采用：

```text
Candidate
```

状态，不直接认为 Agent 产生的知识就是正式知识。

---

## Phase 4：Artifact 与知识沉淀

Scenario 执行后产生：

```text
Output / Artifact
```

当某些结果具有长期复用价值时：

```text
Artifact
   ↓
Promote
   ↓
Knowledge Object
```

这时再正式引入：

- Execution
- Artifact
- Provenance
- Evidence
- Promotion

---

## Phase 5：Knowledge Evolution

进一步支持：

```text
Agent A
   ↓
Knowledge A

Agent B
   ↓
Knowledge B

      ↓ Merge

Knowledge C
```

增加：

```text
Parent
Child
Lineage
Merge
Derivation
```

实现你之前提出的：

> 两个 Agent 产生的知识对象“结婚”，生成一个新的知识对象。

---

## Phase 6：自主进化

最终形成：

```text
Question
   ↓
Search
   ↓
Reuse
   ↓
Execute
   ↓
Observe
   ↓
Discover
   ↓
Create / Update / Merge
   ↓
Knowledge
   ↓
Next Question
```

这时候 Knowledge Object 系统才真正开始具备**自主成长能力**。

---

# 当前版本一句话总结

现在不要把系统设计成一个庞大的“知识操作系统”。

先保持：

```text
Scenario
Feature
Method
Procedure
Assets
```

五个核心对象。

然后让：

```text
Scenario
   ↓
Feature / Method
   ↓
Procedure（可选）
   ↓
Assets
   ↓
MAF
   ↓
Output
```

跑通一个真实业务闭环。

**先证明知识对象模型有用，再逐步增加 Execution、Artifact、Provenance、Merge 和自主进化。**