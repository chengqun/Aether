# 桌宠（Desktop Pet）设计文档

> 状态：现行设计（2026-09-22 整理）。
> 权威来源：代码（`apps/Desktop/Acme.Desktop.Shared/Shell/Pet/` 四文件 + `Assets/pet/`）+ CLAUDE.md「桌宠」节。形象素材的**权威源是 `gen_pet.py` 生成脚本**（JSON 是其产物）。

---

## 1. 现状总览（一句话）

主窗的**最小化驻留形态**：一个 120×140 的独立无边框透明置顶悬浮窗（`PetWindow`），里面是一个 **100×100 Lottie 动画小人**（六个情绪态）+ 气泡。Agent 执行的关键节点实时驱动表情；聊天的逐字回答驱动口型。形象不是图片/视频，是**六个 Lottie JSON 文件**（每 mood 一份），由仓库内的 Python 脚本生成。

```
Agent 侧（Harness 统一发射）                桌宠侧
─────────────────────────                ─────────────────
RunProgress（关键节点，Core 契约）  ──┐
IPetTokenStream（逐字回答流）      ──┼→ PetStatusFeedRouter（可换目标路由器单例，
                                    │    未建宠物时静默丢弃）
                                    └→ PetWindow（信号→语义映射 + 气泡 + 拖拽/菜单）
                                         ↓
                                        IAvatarEngine（唯一抽象边界：SetMood/AppendSpeakToken/EndSpeak/StopAndDispose）
                                         ↓
                                        LottiePetControl（唯一实现：Skottie 渲染 6 mood 动画）
```

替换形象引擎（Lottie→Spine/Live2D/VRM）= 新写一个 `IAvatarEngine` 实现类 + 改 `PetWindow.OnOpened` 一处 setup（`_avatar = PetLottie as IAvatarEngine`），信号链零改动。

## 2. 窗口与交互（PetWindow）

- `Width=120 Height=140`，`CanResize=False`，`WindowDecorations=None`、透明底、`Topmost=True`、`ShowInTaskbar=False`、`Focusable=False`（点它 macOS 会抢 key window，激活后立即把焦点还给主窗）。
- **进入桌宠**：主窗 X 关闭被 `OnClosing` 拦截为 `EnterPetMode()`（Hide 主窗 + Show 桌宠）；账号菜单「桌宠」同路径。**回来**：双击桌宠 / 右键「回到主界面」→ `RestoreRequested`。
- **右键菜单**：回到主界面 / 退出程序（真退出路径——X 不退出）。
- **位置持久化**：`pet.x`/`pet.y` 写 `IPreferencesStore`（500ms `DispatcherTimer` 对比 `Position` 变化后保存，`Task.Run` 包裹避免 UI 线程 sync-over-async 死锁）；无保存则主屏工作区右上角留 16px 边距。
- **气泡**：`MaxWidth=140`、`FontSize=12`；50ms 节流 flush；说话时只保留滚动尾巴 ~30 字（**绝不驻留整段回答原文**）；自动收起用 `IDisposable`（`DispatcherTimer.RunOnce` 返回的不是 timer）。

## 3. 信号→表情映射

| 信号（RunProgress.Kind） | 表情 | 气泡 |
|---|---|---|
| Reasoning | Thinking（歪头） | — |
| ToolStarted / ApprovalRequested / PlanUpdated | Working（手敲击） | 短句 |
| RunDone | Happy（弹跳+露齿笑） | 「搞定啦 ✨」 |
| RunError | Error（摇头+撇嘴） | 截短错误 |
| 逐字 token（IPetTokenStream） | Speaking（口型） | 气泡逐字累积 |
| （流结束 EndSpeak） | Idle（呼吸+眨眼） | — |

说话是持续流：`AppendSpeakToken` 每次刷新 `Speaking` 相位让口型持续跳动（`SetMood` 同 mood 短路对 Speaking/Happy/Error 例外——瞬态要重演）。

## 4. 形象现状（gen_pet.py 的 build_kid 逐图层）

画布 **100×100**、帧率 **30fps**、~24 个椭圆/圆角矩形图层。Q 版卡通小人（蜜桃脸/深棕刘海/亮眼+高光/粉腮红/奶黄上衣/珊瑚裤）。绘制顺序 = `build_kid` 加入顺序（先加在下层，`make` 末尾整体反转——Skottie 数组第一个在最上层）：

| 图层 | 形状 | 位置 [x,y] | 尺寸 [w,h] | 颜色 |
|---|---|---|---|---|
| body 躯干（上衣） | 圆角矩形 r=11 | [50,63] | 34×24 | SHIRT 奶黄 |
| pants 裤子 | 圆角矩形 r=6 | [50,73] | 24×12 | PANTS 珊瑚橙 |
| **hand_l / hand_r** | 椭圆 | **[33,62] / [67,62]** | **9×9** | SHIRT_D 袖色 |
| **foot_l / foot_r** | 椭圆 | **[42,83] / [58,83]** | **13×7** | SKIN_D 深蜜桃 |
| head 脸 | 椭圆 | [50,36] | 38×38 | SKIN 蜜桃 |
| ear_l / ear_r 耳朵 | 椭圆 | [31,36] / [69,36] | 8×10 | SKIN_D |
| hair_top 头顶发 | 椭圆 | [50,23] | 39×22 | HAIR 深棕 |
| hair_fringe 刘海 | 椭圆 | [50,34] | 38×12 | HAIR |
| hair_hi 发高光 | 椭圆 | [42,20] | 10×6 | HAIR_HI |
| eye_l_w / eye_r_w 眼白 | 椭圆 | [42,37] / [58,37] | 12×14 | WHITE |
| eye_l / eye_r 眼珠 | 椭圆 | [42,37] / [58,37] | 7×8 | BLACK |
| glint_l / glint_r 高光 | 椭圆 | [44,34] / [60,34] | 2.5×2.5 | WHITE |
| blush_l / blush_r 腮红 | 椭圆 | [38,45] / [62,45] | 6.5×4.5 | PINK（85% 透明度） |
| mouth 嘴 | 椭圆 | [50,46]（happy [50,47]） | 6×3（happy 12×9） | BLACK（说话/错误态 RUBY 红） |
| tongue 舌（仅 happy） | 椭圆 | [50,50] | 7×5 | 粉红 |

> **「手脚太短」的结构性原因**：当前小人**没有手臂/小腿图层**——手是两个 9×9 圆直接贴在躯干两侧（y=62 与躯干同高），脚是两个 13×7 扁椭圆贴在裤子正下（y=83）。视觉上「手=小圆点、脚=小拖鞋」，显得手脚极短。调整方法见 §7。

### 六个 mood 的动画（关键帧 @30fps）

| mood | op（帧） | 周期 | 动画 |
|---|---|---|---|
| idle | 60 | 2.0s | body/head/hair 呼吸 scale 100↔103；眼白+眼珠眨眼（scale_y 压扁到 6%） |
| thinking | 80 | 2.67s | head 旋转 -7° + 上移 2px；眼珠上移看上方 |
| working | 18 | 0.6s | 双手交替 y 62↔60（3 帧一换，敲键盘）；body 微沉 |
| speaking | 16 | 0.53s | mouth scale_y 100↔220（张合）；body 微沉；嘴 RUBY 红 |
| happy | 32 | 1.07s | head 弹跳 y 36↔32；双手绕自身中心旋转 ∓20°；**happy 专属大嘴 + 舌头** |
| error | 16 | 0.53s | head 摇头 ±4°；嘴 RUBY 红 + 位置微晃 |

动画实现：`_apply_state` 把 `{"s"/"p"/"r"/"o": [(t, val), ...]}` 关键帧注入图层 `ks`；插值曲线 `i/o` 固定 (0.4,1)/(0.4,0)。

## 5. 渲染管线（LottiePetControl）

- **加载**：`LoadAllFromAssets()` 按约定路径 `avares://Acme.Desktop.Shared/Assets/pet/{mood}.json`（**编译期 `AvaloniaResource` 嵌入**）→ `SkiaSharp.Skottie.Animation.Create(SKData)`；缺某 mood 回退 idle。
- **驱动**：`DispatcherTimer` 33ms（~30fps）tick → `SeekFrameTime(_elapsed)` —— **刻意不用** `Animation.RunAsync`（Avalonia `TransformAnimator` 的 cast 坑，同 aurora 教训）。
- **离屏渲染**（核心设计）：每帧 Lottie 画进 `SKBitmap`（透明底，`canvas.Scale` 适配控件尺寸）→ `Marshal.Copy` 进 `WriteableBitmap`（96dpi、Rgba8888、Unpremul）→ `context.DrawImage`。**不走 `context.Custom`/Skia 租借**——透明 Topmost 窗口里每帧重合成实测狂闪；离屏位图零合成抖动。
- **mood 切换**：换 `_active` 引用 + 重置相位（`_elapsed=0`），零读盘（六份动画构造期全加载）。
- 控件 100×100 = 画布 1:1；`Mood` 情绪叠加（`ApplyMoodMotion`）如欢快弹跳已并入动画本身。

## 6. 素材生成与再生（gen_pet.py 工作流）

**是的，形象就是 JSON 文件——但权威源是脚本，不是 JSON**：

```
gen_pet.py（201 行，随 JSON 一起入库）
  python3 gen_pet.py
    → build_kid() 逐图层组装静态形状
    → s_idle/s_thinking/... 注入各 mood 关键帧
    → layers.reverse()（渲染顺序修正）
    → 写出 idle/thinking/working/speaking/happy/error.json（各 ~18KB）
```

**改形象的完整流程**（改任何尺寸/位置/颜色都一样）：

1. 编辑 `apps/Desktop/Acme.Desktop.Shared/Assets/pet/gen_pet.py`
2. 运行 `python3 gen_pet.py`（同目录重新生成 6 份 JSON）
3. **必须 `dotnet build`**——JSON 是 `AvaloniaResource` **编译期嵌入**，改文件不重编不生效（运行时不读磁盘）
4. 重启 app（动画在 `LottiePetControl` 构造期加载，桌宠窗懒建但建后常驻）

不推荐手改 JSON：六份文件坐标重复、每次再生覆盖手改。

### 生成器四个坑（历史踩坑，改脚本必读）

1. **图层顺序**：Skottie 渲染顺序 = `layers` 数组**第一个在最上层**（与 Lottie 规范的 ind 越大越在上相反）。`build_kid` 先加底层后加顶层 → `make` 里必须 `layers.reverse()`。**新加图层按「底层先加」插入 `build_kid`**，位置决定遮挡关系。
2. **颜色必须 float**：`c.k` 数组含 int（如 `[1,1,1,1]`）→ Skottie 解析失败**静默丢弃整个图层**（「没眼睛/嘴」的历史根因）。alpha 的 `1` 尤其要写 `1.0`。
3. **scale 关键帧必须从 t=0 起**：t=0 没有关键帧 → Skottie 把 t<首帧当「未定义」→ 缩成 0 → 该图层前半周期消失（眨眼曾中招）。
4. **`op`（out point）必须= 实际循环周期帧数**：否则 duration≈9999 帧≈5 分半，循环几乎不回卷。

### 命名空间速查

- 层类型 `ty:4`（shape layer）+ shape `ty:"el"`（椭圆）/`"rc"`（圆角矩形）+ `"fl"`（fill）+ `"tr"`（group transform）。
- 图层变换 `ks`：`o` 透明度 / `r` 旋转 / `p` 位置 / `a` 锚点 / `s` 缩放；关键帧态 `{"a":1, "k":[{t, s, i, o}]}`。
- 每图层几何中心在 group 原点（椭圆 p=[0,0]），层 `p` 定位、旋转绕形状中心。

## 7. 「手脚太短」怎么调（本次诉求的具体改法）

改 `build_kid` 即可，三档方案由轻到重：

### 方案 A：只放大手脚（最小改动，10 分钟）

```python
# 手：9×9 → 11×11，外移下垂（躯干两侧、低于躯干中线）
L.append(_el("hand_l", [31, 66], [11, 11], SHIRT_D))   # 原 [33,62] 9×9
L.append(_el("hand_r", [69, 66], [11, 11], SHIRT_D))
# 脚：13×7 → 15×9，下移（画布 100 高，脚底到 y≈89.5 仍有余量）
L.append(_el("foot_l", [40, 85], [15, 9], SKIN_D))     # 原 [42,83] 13×7
L.append(_el("foot_r", [60, 85], [15, 9], SKIN_D))
```

### 方案 B：加手臂/小腿图层（真正的「长手脚」，推荐）

```python
# 在 pants 之后、hand 之前加（遮挡：臂在躯干上层、手在臂上层）：
L.append(_rc("arm_l", [35, 64], [9, 18], 4.5, SHIRT_D))    # 左臂（袖色圆角条）
L.append(_rc("arm_r", [65, 64], [9, 18], 4.5, SHIRT_D))
L.append(_el("hand_l", [31, 72], [10, 10], SKIN))          # 手=肤色（露手不露袖）
L.append(_el("hand_r", [69, 72], [10, 10], SKIN))
# 小腿同理，在 foot 之前加：
L.append(_rc("leg_l", [43, 79], [8, 10], 4, SKIN_D))
L.append(_rc("leg_r", [57, 79], [8, 10], 4, SKIN_D))
L.append(_el("foot_l", [41, 87], [14, 8], SKIN_D))
L.append(_el("foot_r", [59, 87], [14, 8], SKIN_D))
```

### 方案 C：整体调身体比例（改大头小身程度）

头 38×38 → 34×34（head/眼睛/刘海/腮红全组同步收 ~10%），躯干加长 34×24 → 34×30——「头大身小」的 Q 版比例越夸张手脚越显短，反向调即显长。

### 必须同步的两处动画关键帧

手的位置改了，**这两个 mood 的手部关键帧基点要跟着改**（否则敲击/挥手动在旧坐标，手会「瞬移」）：

- `s_working`：`hand_l/hand_r` 的 `p` 序列（基点 `[33,62]/[67,62]` → 新位置，敲击幅度 y±2 保留）
- `s_happy`：`hand_l/hand_r` 的 `r` 旋转（绕形状中心，位置无关，一般不用改；若想挥手更夸张调 ±20° → ±30°）

### 验证方法

改完 `python3 gen_pet.py && dotnet build apps/Desktop/Acme.Stockapp.Desktop`（或 Qifu）→ 重启进桌宠。肉眼验不过时用**像素分桶探针**（按颜色数像素：手色应数百 px、缺图层=0——「没眼睛嘴」当年就是这么定位的）。

## 8. 已知限制与未来路线

- **伪口型**：Lottie 只能固定张嘴循环（speaking 的 scale_y 100↔220），真音素级 viseme 需 Spine mesh / Live2D 嘴型参数。
- **素材来源**：手写脚本生成（2026-08-23 版 Q 版小人）。换 LottieFiles 精选/设计师产出 = 替换 6 份 JSON（同样走 AvaloniaResource 嵌入），`build_kid` 那套仅服务当前形象。
- **P3 远期**：Spine/Live2D/VRM 形象引擎——`IAvatarEngine` 抽象已铺好路（新实现类 + OnOpened 一处 setup），仅在「Lottie 表达力不够」时才投入。
- **渲染栈版本**：`SkiaSharp.Skottie 4.151.1`（2026-08-31 随 SkiaSharp 4.x 升级，API 全稳定零改）。
