using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Tests;

/// <summary>录制用 <see cref="IDesktopAutomation"/> mock：记录每个原语调用，供断言序列；返回可控结果。</summary>
public sealed class RecordingDesktopAutomation : IDesktopAutomation
{
    public bool IsConnected { get; set; } = true;
    public List<string> Calls { get; } = new();
    public string? UITreeResult { get; set; } = "{}";
    public string? TextResult { get; set; }
    public bool WindowExistsResult { get; set; } = true;

    private void Rec(string s) => Calls.Add(s);

    public Task FocusWindowAsync(string titleOrId, CancellationToken ct = default) { Rec($"focus:{titleOrId}"); return Task.CompletedTask; }
    public Task<bool> WindowExistsAsync(string titleOrId, CancellationToken ct = default) { Rec($"exists:{titleOrId}"); return Task.FromResult(WindowExistsResult); }
    public Task ClickAsync(DesktopLocator l, CancellationToken ct = default) { Rec($"click:{l}"); return Task.CompletedTask; }
    public Task DoubleClickAsync(DesktopLocator l, CancellationToken ct = default) { Rec($"dblclick:{l}"); return Task.CompletedTask; }
    public Task RightClickAsync(DesktopLocator l, CancellationToken ct = default) { Rec($"rclick:{l}"); return Task.CompletedTask; }
    public Task TypeAsync(string text, CancellationToken ct = default) { Rec($"type:{text}"); return Task.CompletedTask; }
    public Task KeyAsync(string combo, CancellationToken ct = default) { Rec($"key:{combo}"); return Task.CompletedTask; }
    public Task<DesktopElement?> FindElementAsync(DesktopLocator l, CancellationToken ct = default) { Rec($"find:{l}"); return Task.FromResult<DesktopElement?>(null); }
    public Task<string?> GetTextAsync(DesktopLocator l, CancellationToken ct = default) { Rec($"gettext:{l}"); return Task.FromResult(TextResult); }
    public Task<string> GetUITreeAsync(string? scope = null, CancellationToken ct = default) { Rec("uitree"); return Task.FromResult(UITreeResult!); }
    public Task<bool> WaitForElementAsync(DesktopLocator l, int timeoutMs = 5000, CancellationToken ct = default) { Rec($"wait:{l}"); return Task.FromResult(true); }
    public Task SleepAsync(int ms, CancellationToken ct = default) { Rec($"sleep:{ms}"); return Task.CompletedTask; }
}
