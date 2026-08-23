#!/usr/bin/env python3
# 生成桌宠 Lottie 动画到 Assets/pet/（与脚本同目录）。
# 形象：Q 版卡通小人（大头小身体 / 发型 / 亮眼+高光 / 腮红 / 圆润手脚 / 衣服 / 情绪嘴）
# 6 个 mood：idle / thinking / working / speaking / happy / error
# 关键修正：所有 scale 动画关键帧都从 t=0 开始定义（含眨眼），否则 Skottie 把 t<首帧 区间当
# 「未定义」→ 缩成 0 → 眼睛/嘴在前半周期消失（上一版「没眼睛嘴」的根因）。
# 运行：python3 gen_pet.py
import json, os

FR = 30          # 帧率
W = H = 100      # 画布尺寸（方形）

# ---------- 配色（Claude 定的暖萌系） ----------
# 注意：所有颜色数组必须是 float（含 alpha 的 1 要写 1.0），否则 json.dump 输出 int，
# Skottie 解析 fill 颜色失败 → 该图层被整体丢弃（上一版「没眼睛/嘴」的根因就是 WHITE=[1,1,1,1] 全 int）。
SKIN    = [0.98, 0.85, 0.72, 1.0]   # 蜜桃肤色
SKIN_D  = [0.92, 0.74, 0.60, 1.0]   # 深蜜桃（耳朵/脚）
HAIR    = [0.28, 0.18, 0.13, 1.0]   # 深棕头发（更深，对比强）
HAIR_HI = [0.46, 0.32, 0.22, 1.0]   # 头发高光
SHIRT   = [0.98, 0.82, 0.36, 1.0]   # 奶黄上衣
SHIRT_D = [0.90, 0.70, 0.24, 1.0]   # 上衣阴影/袖
PANTS   = [0.93, 0.50, 0.30, 1.0]   # 珊瑚橙裤子
RUBY    = [0.88, 0.30, 0.30, 1.0]   # 嘴/错误态
WHITE   = [1.0, 1.0, 1.0, 1.0]
BLACK   = [0.12, 0.10, 0.09, 1.0]   # 眼珠/嘴（更深）
PINK    = [0.98, 0.52, 0.58, 1.0]   # 腮红

def _shape(name, pos, size, fill, ty="el", r=0, rotation=0, opacity=100):
    """静态形状层（scale 固定 [100,100]，不参与眨眼等缩放）。
    所有缩放动画层一律走 make 里 _apply_state 单独注入关键帧（从 t=0 起）。"""
    geom = {"ty": ty, "p": {"a": 0, "k": [0, 0]}, "s": {"a": 0, "k": size}, "d": 1,
            "nm": name + ("ellipse" if ty == "el" else "rect")}
    if ty == "rc":
        geom["r"] = {"a": 0, "k": r}
    return {
        "ddd": 0, "ind": 0, "ty": 4, "nm": name, "sr": 1,
        "ks": {"o": {"a": 0, "k": opacity}, "r": {"a": 0, "k": rotation},
               "p": {"a": 0, "k": pos}, "a": {"a": 0, "k": [0, 0]},
               "s": {"a": 0, "k": [100, 100]}},
        "ao": 0,
        "shapes": [{
            "ty": "gr", "nm": name + "g",
            "it": [
                geom,
                {"ty": "fl", "c": {"a": 0, "k": fill}, "o": {"a": 0, "k": 100},
                 "r": 1, "nm": name + "fill"},
                {"ty": "tr", "p": {"a": 0, "k": [0, 0]}, "a": {"a": 0, "k": [0, 0]},
                 "s": {"a": 0, "k": [100, 100]}, "r": {"a": 0, "k": 0},
                 "o": {"a": 0, "k": 100}, "sk": {"a": 0, "k": 0}, "sa": {"a": 0, "k": 0},
                 "nm": "tr"},
            ], "np": 2, "cix": 2, "bm": 0, "ix": 1, "mn": "ADBE Vector Group"
        }],
        "ip": 0, "op": 9999, "st": 0, "bm": 0,
    }

def _el(name, pos, size, fill, rotation=0, opacity=100):
    return _shape(name, pos, size, fill, "el", rotation=rotation, opacity=opacity)

def _rc(name, pos, size, r, fill, rotation=0):
    return _shape(name, pos, size, fill, "rc", r=r, rotation=rotation)

# ---------- 组装小人图层（绘制顺序 = 加入顺序，先加在下层） ----------
def build_kid(mouth_color=None, happy_mouth=False):
    L = []
    L.append(_rc("body", [50, 63], [34, 24], 11, SHIRT))           # 躯干（上衣）
    L.append(_rc("pants", [50, 73], [24, 12], 6, PANTS))           # 裤子
    L.append(_el("hand_l", [33, 62], [9, 9], SHIRT_D))             # 左手
    L.append(_el("hand_r", [67, 62], [9, 9], SHIRT_D))             # 右手
    L.append(_el("foot_l", [42, 83], [13, 7], SKIN_D))             # 左脚
    L.append(_el("foot_r", [58, 83], [13, 7], SKIN_D))             # 右脚
    # 头（脸）
    L.append(_el("head", [50, 36], [38, 38], SKIN))
    # 耳朵
    L.append(_el("ear_l", [31, 36], [8, 10], SKIN_D))
    L.append(_el("ear_r", [69, 36], [8, 10], SKIN_D))
    # 头发
    L.append(_el("hair_top", [50, 23], [39, 22], HAIR))
    L.append(_el("hair_fringe", [50, 34], [38, 12], HAIR))
    L.append(_el("hair_hi", [42, 20], [10, 6], HAIR_HI))
    # 眼睛（眼白 + 珠 + 高光）—— 眼白/眼珠参与眨眼（scale 动画），但关键帧从 t=0 起
    L.append(_el("eye_l_w", [42, 37], [12, 14], WHITE))
    L.append(_el("eye_r_w", [58, 37], [12, 14], WHITE))
    L.append(_el("eye_l", [42, 37], [7, 8], BLACK))
    L.append(_el("eye_r", [58, 37], [7, 8], BLACK))
    L.append(_el("glint_l", [44, 34], [2.5, 2.5], WHITE))
    L.append(_el("glint_r", [60, 34], [2.5, 2.5], WHITE))
    # 腮红
    L.append(_el("blush_l", [38, 45], [6.5, 4.5], PINK, opacity=85))
    L.append(_el("blush_r", [62, 45], [6.5, 4.5], PINK, opacity=85))
    # 嘴
    if happy_mouth:
        L.append(_el("mouth", [50, 47], [12, 9], RUBY))
        L.append(_el("tongue", [50, 50], [7, 5], [0.98, 0.42, 0.42, 1.0]))
    else:
        L.append(_el("mouth", [50, 46], [6, 3], mouth_color or BLACK))
    return L

def _kf(vals):
    """把 [(t, val), ...] 转成 lottie 关键帧。val 内部已含完整结构。"""
    return [{"t": t, "s": v, "i": {"x": [0.4], "y": [1]}, "o": {"x": [0.4], "y": [0]}}
            for t, v in vals]

def _apply_state(layers, states):
    """states: dict layer_name -> {"s": [(t,[x,y])], "p": [...], "r": [...], "o": [...]}"""
    for ln in layers:
        nm = ln["nm"]
        if nm in states:
            for prop, kf in states[nm].items():
                ln["ks"][prop] = {"a": 1, "k": _kf(kf)}

def make(state_fn, op, mouth_color=None, happy_mouth=False):
    layers = build_kid(mouth_color, happy_mouth)
    state_fn(layers)
    # 关键：Skottie（3.119.4）的图层渲染顺序 = 数组第一个在最上层（与 Lottie 规范的
    # 「ind 越大越在上」相反）。build_kid 是「先加底层、后加顶层」，故这里必须反转，
    # 否则眼睛/嘴/腮红（最后加、应在最上层）会被头/身体盖住 → 上一版「没眼睛嘴」的根因。
    layers.reverse()
    out = []
    for i, ln in enumerate(layers, 1):
        ln["ind"] = i
        ln["ip"] = 0
        ln["op"] = op
        out.append(ln)
    return {
        "v": "5.7.0", "fr": FR, "ip": 0, "op": op, "w": W, "h": H,
        "nm": "pet", "ddd": 0, "assets": [], "layers": out,
    }

OUT = os.path.dirname(os.path.abspath(__file__))

def blink(period):
    """眨眼：scale_y 在周期中点快速压扁再恢复。关键帧从 t=0 到 t=period 全覆盖，
    t=0 即睁眼（[100,100]），保证整个周期任何时刻 scale 都有定义（不缩没）。"""
    t = period // 2
    return [(0, [100, 100]), (t, [100, 100]), (t + 2, [100, 6]), (t + 4, [100, 100]),
            (period, [100, 100])]

# ---------- 6 个 mood 的状态关键帧 ----------
def s_idle(L):
    st = {}
    # 身体/头/头发呼吸：scale 100<->103，t=0 起
    for nm in ["body", "head", "hair_top", "hair_fringe"]:
        st[nm] = {"s": [(0, [100, 100]), (30, [103, 103]), (60, [100, 100])]}
    # 眨眼：眼白+眼珠 scale_y 压扁，t=0 起
    for nm in ["eye_l_w", "eye_r_w", "eye_l", "eye_r"]:
        st[nm] = {"s": blink(60)}
    _apply_state(L, st)

def s_thinking(L):
    st = {}
    st["head"] = {"r": [(0, 0), (40, -7), (80, 0)],
                  "p": [(0, [50, 36]), (40, [50, 34]), (80, [50, 36])]}
    st["body"] = {"p": [(0, [50, 63]), (40, [50, 61]), (80, [50, 63])]}
    for nm in ["eye_l", "eye_r"]:
        x = 42 if nm == "eye_l" else 58
        st[nm] = {"p": [(0, [x, 37]), (40, [x, 35]), (80, [x, 37])]}
    _apply_state(L, st)

def s_working(L):
    st = {}
    st["hand_l"] = {"p": [(0, [33, 62]), (3, [33, 60]), (6, [33, 62]), (9, [33, 60]), (12, [33, 62])]}
    st["hand_r"] = {"p": [(0, [67, 62]), (3, [67, 60]), (6, [67, 62]), (9, [67, 60]), (12, [67, 62])]}
    st["body"] = {"p": [(0, [50, 63]), (3, [50, 62]), (6, [50, 63])]}
    _apply_state(L, st)

def s_speaking(L):
    st = {}
    st["mouth"] = {"s": [(0, [100, 100]), (4, [100, 220]), (8, [100, 100]),
                         (12, [100, 220]), (16, [100, 100])]}
    st["body"] = {"p": [(0, [50, 63]), (4, [50, 62]), (8, [50, 63]), (12, [50, 62]), (16, [50, 63])]}
    _apply_state(L, st)

def s_happy(L):
    st = {}
    st["head"] = {"p": [(0, [50, 36]), (8, [50, 32]), (16, [50, 36]), (24, [50, 32]), (32, [50, 36])]}
    st["hand_l"] = {"r": [(0, 0), (8, -20), (16, 0)]}
    st["hand_r"] = {"r": [(0, 0), (8, 20), (16, 0)]}
    _apply_state(L, st)

def s_error(L):
    st = {}
    st["head"] = {"r": [(0, 0), (4, -4), (8, 4), (12, -4), (16, 0)]}
    st["mouth"] = {"p": [(0, [50, 46]), (8, [50, 47]), (16, [50, 46])]}
    _apply_state(L, st)

files = [
    ("idle",     make(s_idle, 60)),
    ("thinking", make(s_thinking, 80)),
    ("working",  make(s_working, 18)),
    ("speaking", make(s_speaking, 16, mouth_color=RUBY)),
    ("happy",    make(s_happy, 32, happy_mouth=True)),
    ("error",    make(s_error, 16, mouth_color=RUBY)),
]

for name, data in files:
    path = os.path.join(OUT, name + ".json")
    with open(path, "w") as f:
        json.dump(data, f)
    print(f"wrote {path} ({os.path.getsize(path)} bytes)")

print("done — 6 moods")
