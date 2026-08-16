using System.Collections.Generic;
using IconPacks.Avalonia.Codicons;
using IconPacks.Avalonia.Core;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// VSCode codicon path data 集中访问点（填充型几何 glyph）。经 <c>PackIconDataFactory&lt;PackIconCodiconsKind&gt;.DataIndex</c>
/// 懒加载 codicon 几何，供 XAML <c>{x:Static shell:Codicons.X}</c> 引用。codicon 是**填充型** glyph →
/// <c>&lt;Path Fill=...&gt;</c> 渲染（非 Lucide 描边 Stroke）。失败兜底空串不崩（图标缺显，不影响功能）。
/// 语言/品牌 logo（Python/JS 等）codicon 无对应，仍走 NavIcons 的 Simple Icons（见 NoteIcons/FileIcons）。
/// </summary>
public static class Codicons
{
    private static IReadOnlyDictionary<PackIconCodiconsKind, string>? _data;

    /// <summary>取单个 codicon path data。DataIndex 懒加载可能抛（资源缺失）——兜底空串。</summary>
    public static string Get(PackIconCodiconsKind kind)
    {
        try
        {
            _data ??= PackIconDataFactory<PackIconCodiconsKind>.DataIndex.Value;
            return _data.TryGetValue(kind, out var p) ? p : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string R(PackIconCodiconsKind k) => Get(k);

    // —— chevron / 折叠指示 ——
    public static readonly string ChevronDown = R(PackIconCodiconsKind.ChevronDown);     // ▾
    public static readonly string ChevronRight = R(PackIconCodiconsKind.ChevronRight);   // ▸
    public static readonly string ArrowRight = R(PackIconCodiconsKind.ArrowRight);       // →
    public static readonly string ArrowDown = R(PackIconCodiconsKind.ArrowDown);         // ↓
    public static readonly string Dot = R(PackIconCodiconsKind.CircleSmallFilled);       // ● 小实心点
    public static readonly string Loading = R(PackIconCodiconsKind.Loading);             // ⏳ pending/等待
    public static readonly string CircleOutline = R(PackIconCodiconsKind.CircleLarge);   // ○ 空心 pending
    public static readonly string Terminal = R(PackIconCodiconsKind.Terminal);           // 终端面板 toggle
    public static readonly string Check = R(PackIconCodiconsKind.Check);                 // ✓
    public static readonly string Close = R(PackIconCodiconsKind.Close);                 // ✕
    public static readonly string Add = R(PackIconCodiconsKind.Add);                     // ✚

    // —— 功能 / 卡片图标 ——
    public static readonly string Sparkle = R(PackIconCodiconsKind.Sparkle);             // 💭 思考 / ⚡
    public static readonly string Tools = R(PackIconCodiconsKind.Tools);                 // 🔧
    public static readonly string ListFlat = R(PackIconCodiconsKind.ListFlat);           // 📋 plan
    public static readonly string Play = R(PackIconCodiconsKind.Play);                   // ▶️ 执行
    public static readonly string Folder = R(PackIconCodiconsKind.Folder);               // 📁📂
    public static readonly string File = R(PackIconCodiconsKind.File);                   // 📄
    public static readonly string Save = R(PackIconCodiconsKind.Save);                   // 💾
    public static readonly string Search = R(PackIconCodiconsKind.Search);               // 🔍
    public static readonly string Refresh = R(PackIconCodiconsKind.Refresh);             // ↻↺
    public static readonly string ColorMode = R(PackIconCodiconsKind.ColorMode);         // 🎨 主题
    public static readonly string Key = R(PackIconCodiconsKind.Key);                     // 🔑
    public static readonly string SignOut = R(PackIconCodiconsKind.SignOut);             // 🚪 退出
    public static readonly string SignIn = R(PackIconCodiconsKind.SignIn);               // 登录
    public static readonly string SettingsGear = R(PackIconCodiconsKind.SettingsGear);   // ⚙ 设置
    public static readonly string Link = R(PackIconCodiconsKind.Link);                   // 🔗
    public static readonly string Extensions = R(PackIconCodiconsKind.Extensions);       // 🧩
    public static readonly string Megaphone = R(PackIconCodiconsKind.Megaphone);         // 🛒 市场
    public static readonly string Hubot = R(PackIconCodiconsKind.Hubot);                 // 🤖
    public static readonly string Graph = R(PackIconCodiconsKind.Graph);                 // 📊 柱/通用
    public static readonly string GraphLine = R(PackIconCodiconsKind.GraphLine);         // 📈 折线
    public static readonly string Target = R(PackIconCodiconsKind.Target);               // 🎯
    public static readonly string Warning = R(PackIconCodiconsKind.Warning);             // ⚠
    public static readonly string Inbox = R(PackIconCodiconsKind.Inbox);                 // 📭 空态
    public static readonly string Edit = R(PackIconCodiconsKind.Edit);                   // ✏️📝
    public static readonly string Trash = R(PackIconCodiconsKind.Trash);
    public static readonly string Eye = R(PackIconCodiconsKind.Eye);
    public static readonly string EyeClosed = R(PackIconCodiconsKind.EyeClosed);
    public static readonly string Send = R(PackIconCodiconsKind.Send);                   // 发送
    public static readonly string RadioTower = R(PackIconCodiconsKind.RadioTower);       // 📡
    public static readonly string Lock = R(PackIconCodiconsKind.Lock);                   // 🔐
    public static readonly string Question = R(PackIconCodiconsKind.Question);           // ❓
    public static readonly string Menu = R(PackIconCodiconsKind.ThreeBars);              // hamburger ☰
    public static readonly string Calendar = R(PackIconCodiconsKind.Calendar);
    public static readonly string Rocket = R(PackIconCodiconsKind.Rocket);
    public static readonly string Beaker = R(PackIconCodiconsKind.Beaker);
    public static readonly string Pulse = R(PackIconCodiconsKind.Pulse);
    public static readonly string Book = R(PackIconCodiconsKind.Book);
    public static readonly string Files = R(PackIconCodiconsKind.Files);
    public static readonly string CommentDiscussion = R(PackIconCodiconsKind.CommentDiscussion);

    // —— SCM 源代码管理 ——
    public static readonly string RepoPull = R(PackIconCodiconsKind.RepoPull);       // ↓ 拉取
    public static readonly string RepoPush = R(PackIconCodiconsKind.RepoPush);       // ↑ 推送
    public static readonly string Sync = R(PackIconCodiconsKind.Sync);               // ⟳ 同步（pull+push）
    public static readonly string GitCommit = R(PackIconCodiconsKind.GitCommit);     // ● 提交节点/提交
    public static readonly string Diff = R(PackIconCodiconsKind.Diff);               // ∓ 对比差异
    public static readonly string Discard = R(PackIconCodiconsKind.Discard);         // ↺ 撤销改动
    public static readonly string Remove = R(PackIconCodiconsKind.Remove);           // － 取消暂存
    public static readonly string History = R(PackIconCodiconsKind.History);         // 🕘 提交历史
    public static readonly string ArrowUp = R(PackIconCodiconsKind.ArrowUp);         // ↑ ahead
}
