# CTP 线协议逆向分析

> 2026-09-29 从本地官方 SDK ↔ SimNow 真实前置的完整会话抓包推导（22 帧交易腿 + 8 帧行情腿）。
> 工具：`apps/CtpSim/Acme.Ctp.ProtoCapture`（TCP 记录代理 + 官方 SDK 驱动）。
> 成果：`business/Ctp/Acme.Ctp.Front`——真实 CTP 线协议前置服务器（里程碑 1）。

## 帧格式（已确认）

```
标准帧:      [0x02][0x00][payload_len: 2B 大端][payload]
             payload_len = 帧总长 - 4（全部 22 帧精确验证 ✓）

初始握手:    [0x00][0x06]...[内嵌 0x0200 帧]
             含 ASCII "API_20171207_V1"（API 版本标识）

客户端心跳:  精确 6B [00 02 00 00 05 00]
             等待服务端响应期间 ~15s 一次；服务端回射同款 6B（2026-10-03 实证：
             S→C 心跳确实存在——20261002-184410 抓包 18:45:02.842 有一条 6B
             [00 02 00 00 05 00]，旧「未见 S→C 心跳」是短窗口漏看；本地前置
             已实现逐拍回射保活）

80 族应答实例: ⑯/⑱/查合约应答 op 后的 `E3 <n>` = 请求侧 80 族查询计数的回显
             （1,2,3…逐次 +1）。客户端按它丢弃重复帧——应答实例不推进 ⇒ 第二次
             同型查询起被读而不派发（30s 超时、无断线事件）。2026-10-03 根因修复
             （DirectTest idle 复现 + 真前置录制对照定案）。

复合帧:      一个 TCP 段可含多个帧——需流式分帧器
             消息路由 = payload 前 4B（如 0103334C = [1B 类型][1B 保留][2B 功能码/TID]）
```

**心跳陷阱**：心跳可能出现在任意两帧之间（同一 TCP 批内 `[帧A][心跳][帧B]`）——
分帧器必须在解析循环内每轮剥头部心跳，只入口剥一次会漏。不剥的话心跳落进
「未知帧头逐字节跳过」分支，残余 `[02 00 00 05 00]` 幻影解析成 9B 假标准帧，
吃掉下一真实帧的前几字节 → 流静默错位（长会话 ~15s 内必现）。

## payload 字段编码（从抓包推导）

tag-based，非固定长度（区别于 C 结构体的 char[N] 声明）：

| 标记 | 含义 | 示例 |
|------|------|------|
| `E1 xx` | 字段标记（xx = 字段编号/类型） | `E1 01`、`E1 2E`、`E1 A6` |
| `E7 ...` | ASCII 字符串值（到下一标记止） | `E7 73696D30303031` → "sim0001" |
| `E4 xx / E4 ...` | 整数/枚举值，或字符串（⑤ 的 AppID） | `E4 30 24`（⑨ op 签名段） |
| `E5 ...` | ASCII 字符串值（⑦ 的 AppID 即 E5 标记） | `E5 simnow_client_test` |
| `E6 ...` | 加密/二进制数据（Base64 ASCII） | 登录密码密文 |
| `EF` | 空字段填充 | 连续 EF = 连续空字段 |
| `FE ...` | ASCII 字符串值（BrokerID 等） | `FE 39393939` → "9999" |

高位标记集 **E1..EF + FE 全集**统一作为字段值终止符——缺 E5/E8..EE 会让
⑦ 的 E6 密文 run 吞掉紧随的 E5+AppID。提取字符串时须限定可打印 ASCII
（0x20-0x7E）+ 最小长度（防 op 签名字节误报，如 `E2 30 10` 中 E2 后 run 仅 1B）。

### E0 转义资金编码（⑯ 余额字段，2026-09-30 SDK 即裁判实证）

余额字段 = **E0 转义的大端 double**，不是裸 8B：

- 逻辑值 `999927.69` = 大端 `41 2E 83 EF 61 47 AE 14`；线上为 `41 2E 83 E0 EF 61 47 AE 14`
  ——值内出现 tag 字母表字节（**E0..EF + FE**）时其**前插 E0**（EF 由抓包实证；
  E0 自身与 FE 由「读取方按 tag 分界」的必然性推出；F0..FD/FF 未观察到、推定无需转义）。
- SDK 解析前剥转义。槽宽**随值可变**：8B 逻辑 → 8+N 线上（N = 值内 tag 字节数）。
  例：`1e6` = `41 2E 84 80 00 00 00 00`（无 tag 字节 ⇒ 8B 线上）；`999927.69` ⇒ 9B 线上。
- 生成器 `DecodeEscapedMoney` 对全部 6 槽断言同宽同值 + 解码值像钱（1000 < |v| < 1e9）；
  `EscapedMoney(v)` 生成转义形式——**整槽替换**（`Edit(off, 9, EscapedMoney(balance))`，
  槽宽可变、SDK 容忍整槽伸缩：1e6 下每槽 −1B、帧 138B→132B，E2E 实测通过）。

⑯ 资金应答字节图（134B payload，生成器偏移权威）：

```
@0   01 03 34 4C        路由
@4   E1 04
@6   E2 8007            op（资金应答）
@9   E3 01
@11  E1 01 01 8E        reqId 回显标签
@15  E3 05              reqId（Edit(16,1)）
@17  E1 0C 01 8A        BrokerID 标签
@21  "9999"             BrokerID（Edit(21,4)）
@25  E1 + "24716"       账户#1（Edit(26,5)——语义未知（用户名去尾字符），等宽守卫截断）
@31  E1 + "247163"      账户#2 = AccountID（Edit(32,6)）
@38/40/42  E1 + "0"/"0"/"9"
@44  EF
@45  E2 + 余额#1 @46..54（9B 线上） + 余额#2 @55..63
@64..69  EF ×6
@70  EE + 余额#3 @71..79 + 余额#4 @80..88 + 余额#5 @89..97
@98  E8 + "20260929"    交易日（Edit(99,8)）
@107 E4 01
@109..111  EF ×3
@112 E3 + "CNY"
@116..117  EF ×2
@118 EB + 余额#6 @119..127
@128..132  EF ×5
@133 EE
```

**⑱ 空表语义（SDK 即裁判实证）**：⑱ 是真 SimNow 对无持仓账户的原样应答（13B payload）——
官方 SDK 解析后**回调一次「非空指针但全零」的持仓结构体**（isLast=true），包装层无条件入列
⇒ 空表呈现为 **1 条全零占位记录**（Instr=""、Pos=0）而非 0 条。E2E 断言语义空
（无任何记录持有非零仓位），不断言 `Empty`。

## 会话序列（交易腿 22 帧全还原）

Body = payload[4..]（去 4B 路由）。⑤⑦ 共享 op 签名 `E1 01 E2 30 10`，按状态机区分。

| 步 | 方向 | 内容 | 识别（C→S 请求侧） | 应答（S→C） |
|----|------|------|--------------------|--------------|
| ① | C→S | 初始握手 | `00 06` 帧头 + "API_20171207_V1" | ② |
| ② | S→C | 挑战（~128B 二进制 nonce） | — | — |
| ③ | C→S | 挑战响应（二进制） | — | ④ |
| ④ | S→C | 连接确认（19B） | — | — |
| ⑤ | C→S | 认证请求 | op `E1 01 E2 30 10`；body[5]=E3、body[6]=reqId；FE=BrokerID、E7=UserID、E4=AppID | ⑥ |
| ⑥ | S→C | 认证响应（含 Base64 token） | — | op `E4 30 11` |
| ⑦ | C→S | 登录请求 | 同 ⑤ op；FE/E7 身份、E6=密码密文、E5=AppID | ⑧ |
| ⑧ | S→C | 登录预响应 | — | op `E4 30 11`（仅预响应） |
| ⑨ | C→S | 系统信息请求（SDK 收 ⑧ 后自动发） | body[0..2] = `E4 30 24` | ⑩ |
| ⑩ | S→C | **登录结果**（交易日/FrontID/SessionID/MaxOrderRef，回显 ⑦ reqId） | — | op `E4 30 01` 主帧 206B + 42B 伴随帧，**两帧一次写** |
| ⑪⑬ | C→S | 结算确认（⑪=SDK 自动、⑬=显式调用） | op `E1 01 E2 40 13`；reqId 在 `E1 01 E1 43 E3` 后 | ⑫⑭ |
| ⑫⑭ | S→C | 结算确认应答 | — | op `E2 40 14`；instance 自增（⑫=01、⑭=02）；reqId 回显字段 `E19C E3` |
| ⑮ | C→S | 资金查询 | op `E1 04 E2 80 06`；reqId 在 `E1 2E E3` 后 | ⑯ |
| ⑯ | S→C | 资金应答 | — | op `E2 80 07`；6 个 **E0 转义**大端 double 余额字段（见上节）；reqId 回显字段 `E1 01 01 8E E3` |
| ⑰ | C→S | 持仓查询 | op `E1 04 E2 80 04`；reqId 在 `E1 A6 E3` 后 | ⑱ |
| ⑱ | S→C | 持仓应答（空表 = 1 条全零占位回调，见上节） | — | op `E2 80 05` |
| ⑲ | C→S | 报单 | op `E1 01 E2 40 E4 05` | ⑳（里程碑 2） |
| ㉑ | C→S | 撤单 | op `E1 01 E2 40` + body[4]=06 | ㉒（里程碑 2） |

**请求结构**：`op + E3 <链路计数> + E1 01 E1 <reqIdTag> E3 <reqId> + …`。
链路计数（body[6]）≠ reqId。reqId 标签按查询类型不同：结算=`E143`、资金=`E12E`、
持仓=`E1A6`。请求侧 reqId 须用 `E1 01 E1 <tag> E3` needle 定位；
`E19C E3`/`E18E E3` 是**响应**帧的回显字段，请求里没有。

**DirectTest reqId 序列**（与抓包确定性一致，零替换）：auth=1、login=2、
settle=3（SDK 自动）、settle=4（显式）、qryAcct=5、qryPos=6。

## 加密策略（服务端视角——无需逆向算法）

- 密码 = `E6` 前缀 + Base64(95B 二进制) → 用挑战派生的会话密钥加密
- **服务端方案**：发固定挑战 nonce ⇒ 同密码+同 nonce ⇒ 同密文 ⇒ **存密文当哈希比对**（同 bcrypt 模式）
- 挑战/响应（帧②③）是纯二进制（可能 DH）——但服务端发什么挑战自己说了算
- 已知限制：CTP 前置路径（密文 hex）与 JSON-RPC 路径（PBKDF2 明文）密码域不同——
  同一账户不能混用两条路径登录（隔离库测试规避）

## 实现（`business/Ctp/Acme.Ctp.Front`）

**应答策略 = 抓包 verbatim 复刻**：官方 C++ SDK 自己解析帧——服务端把捕获的
S→C 帧原样回放，只替换 broker/user/reqId/交易日/资金等标记界定字段
（值有标记定界 ⇒ 变长替换安全；偏移由 ProtoCapture `gen` 生成器计算，倒序拼接 + 生成期断言无重叠）。

| 文件 | 职责 |
|------|------|
| `CtpFrame.cs` | 流式分帧器（标准/握手/复合/半帧 + 循环内心跳剥离） |
| `CtpFieldCodec.cs` | tag 字段编解码（E1..EF+FE 全集终止符 + 可打印 ASCII + minLen） |
| `CtpFrontServer.cs` | TCP 监听 + 每连接一会话 |
| `CtpFrontSession.cs` | 状态机（握手→挑战→挑战响应→认证→登录→业务分派）+ SimTradingCore 对接 |
| `CtpFrontTemplates.cs` | **生成产物**——抓包帧模板 + 偏移替换（勿手改；由 ProtoCapture `gen` 生成） |

模板生成器（C#，随仓库走——`apps/CtpSim/Acme.Ctp.ProtoCapture/FrontGen.cs`）：

```bash
dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen [trade.txt路径] [输出.cs路径]
#   两参可省略：抓包默认取 /tmp 下帧数最多的捕获目录；输出默认 business/Ctp/Acme.Ctp.Front/
```

全部 needle 生成期断言唯一命中、余额大端自检像钱、替换编辑区无重叠——零人工转录。
验收直连：`dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- tcp://127.0.0.1:17801 <账号> <密码>`。

宿主接线：`apps/CtpSim/Acme.Ctp.Sim.Host` 双协议监听——JSON RPC `:17800`
（既有 `sim` 关键字路径）+ CTP 前置 `:17801`（env `AETHER_CTP_FRONT_PORT`），共享同一 `SimTradingCore`。
客户端路由：前置地址填 `tcp://127.0.0.1:17801` ⇒ 不匹配 `SimAddress.IsSimFront` ⇒
走官方 SDK 链路（`CtpSessionService`）⇒ CtpFrontServer——**桌面端零改动**。

## 与 SimTradingCore 的映射

| CTP op | SimTradingCore | 备注 |
|--------|----------------|------|
| 登录 ⑦ | `Login(broker, user, pwdHex)` | 首登开户（密文 hex 当密码）；`Login` 无交易时段守卫 |
| 结算确认 ⑪⑬ | （无需——直接回应答） | instance 自增跟踪 |
| 查资金 ⑮ | `QueryAccount(AccountIdOf(user))` | 6 个余额字段 ← `CtpAccount.Balance` |
| 查持仓 ⑰ | `QueryPositions(...)` | 非空逐仓一行 ⑱（2026-10-03 实抓模板：方向三元组+总仓量槽）；空仓走 13B 空表 |
| 报单 ⑲ | `InsertOrder()` | 里程碑 2 |
| 撤单 ㉑ | `CancelOrder()` | 里程碑 2 |

交易日字段：`SimClock.TradingDay`（8 字符 yyyyMMdd，夜盘 ≥21:00 次一工作日）——
与抓包模板值等长 ⇒ 替换长度中性。

## 里程碑

- [x] M0：抓包 + 帧格式/字段编码/会话序列逆向
- [x] M1：握手→挑战→认证→登录→结算→查资金→查持仓 全链路——**E2E 已验证（2026-09-30）**
  （`tests/Acme.Ctp.Front.Tests` 进程内回环 `FrontLoopback` + 官方 SDK 直连：登录/结算/资金
  AccountId+Balance/持仓空表 全断言通过；SDK 即裁判——模板偏移错/签名漏配 ⇒ SDK 回调
  不触发 ⇒ 15~30s 超时失败）
- [ ] M2：报单/撤单应答（⑳㉒ 各两帧/写——需补抓包，RecordingProxy 截断限制）+ OnRtnOrder/OnRtnTrade 推送 + OrderRef 续排
- [ ] M3：行情腿（MD 登录 `EA0C E0E8` + 17B 原始二进制密码、无认证步；订阅； tick 推送）
- [ ] 未来：`sim` 关键字 / RemoteCtpSession JSON-RPC 路径退休（用户定调——CTP 前置全面取代）

已知边界（M1）：FrontID/SessionID/MaxOrderRef verbatim 回放抓包值；
服务端心跳：逐拍回射客户端心跳（2026-10-03 长会话验证——本地前置已实现，idle 150s 查询全通）；
**等宽归一化（2026-09-30 桌面端实测修复）**——verbatim 模板对字段宽度敏感：变长替换缩/撑
payload 会致 SDK 记录遍历失步**无限挂死**（8 字符用户 / 空AppId+4字符用户双实测——桌面端
连接器 AppId 留空即踩中，⑥ 的 18B AppId 槽缩帧后 SDK 连 ⑦ 都不发）。修复 = 生成器发射
`F(value, width)` 等宽归一化（截断/右补空格到抓包槽宽）：broker=4/user=6/AppId=18/交易日=8
全部归一，任意宽度值可登录（>6 字符用户回显截断到 6——回显仅语义展示，账户身份以核心账本
为准）；E2E `桌面端连接器形态_空AppId短用户_等宽归一化后全链路` 钉住（修复前该测试无限
挂死 = 桌面端症状复现）。**变长支持（任意宽度不截断回显）仍需生成器层重算帧内长度字段**
（终局方案，未做）。

## 抓包工具

```bash
dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- \
  --front tcp://<真实前置> --mdfront tcp://<真实行情前置> \
  --user <账号> --password <密码> --broker <经纪商代码>

# 直连验收（里程碑 1）——首参 tcp: 前缀即直连模式（任意宽度账号可登录——模板已等宽归一化，见已知边界）：
dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- tcp://127.0.0.1:17801 <账号> <密码>
```

注意：抓包记录含真实凭据（账号/加密密码/认证 token）——**勿把真实值写入任何入库文档**。
首次需手动复制 native frameworks 到输出目录（macOS .app 打包问题，见 `RecordingProxy.cs` 注释）。
