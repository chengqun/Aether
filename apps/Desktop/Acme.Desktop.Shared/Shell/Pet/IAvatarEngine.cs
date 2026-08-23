namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌宠「表现引擎」抽象（Avatar Runtime 层）。Agent 只经此接口发送语义状态与说话流，不关心底层
/// 怎么渲染——替换 Lottie / Spine / Live2D / VRM 只需新写一个实现类，Agent 与 <see cref="PetWindow"/>
/// 的信号链零改动。核心原则：Agent 发状态与意图，Avatar Runtime 把智能转成可见的生命表现。
/// 现阶段唯一实现 <see cref="LottiePetControl"/>；未来 Spine/Live2D 各自实现本接口即可替换。
/// </summary>
public interface IAvatarEngine
{
    /// <summary>当前语义状态。</summary>
    PetMood Mood { get; }

    /// <summary>
    /// 切换语义状态（思考/工作/说话/开心/出错/待机）。实现方换当前动画/节奏，零重新加载。
    /// 说话（<see cref="PetMood.Speaking"/>）是持续流，应由 <see cref="AppendToken"/> 驱动口型节奏。
    /// </summary>
    void SetMood(PetMood mood);

    /// <summary>追加一片说话 token——驱动「说话」口型节奏（节流由实现方决定）。</summary>
    void AppendSpeakToken(string token);

    /// <summary>本轮说话结束（实现方 flush 口型残留 + 回落状态）。</summary>
    void EndSpeak();

    /// <summary>停止并释放全部动画/定时器/位图等资源（窗口关闭或换引擎时调）。</summary>
    void StopAndDispose();
}
