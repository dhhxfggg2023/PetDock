// 原规格 11 §5 的指针交互接口的 C# 绑定。
// 接口名 = IPointerSurface（对应 原规格 11 哪一个接口、为什么换名，见 交接档案）。
// 三条语义（原规格 11:188-190）逐条落在注释上：
// - 这一层不做「点击」，只做「会话」：任何点击都必须表达为 session 开始/结束，
//   好让宿主在「拖动 / 长按 / 双击」之间仲裁；
// - 多个 surface 叠加时 priority 高的先收到，同优先级按注册顺序（先注册先问）；
// - 命中测试总是先过 hitmap / alpha，再过 surface —— 任何 surface 都不能让已经穿透的
//   区域变成可点（所以宿主侧的顺序是：穿透判定 → 仲裁 surface → 会话）。
namespace PetDock.Core.PetRuntime;

/// <summary>原规格 11 §5 的指针按键三值。</summary>
public enum PointerButton
{
    /// <summary>left。</summary>
    Left,

    /// <summary>right。</summary>
    Right,

    /// <summary>middle。</summary>
    Middle,
}

/// <summary>
/// 原规格 11 §5 的一次交互会话（本 namespace 里叫 IPointerSession）。
/// 宿主在 up / cancel 之后即弃用它。
/// </summary>
public interface IPointerSession
{
    /// <summary>指针移动（会话内）。</summary>
    void OnPointerMove(PetPoint local);

    /// <summary>指针抬起 —— 会话结束。</summary>
    void OnPointerUp(PetPoint local);

    /// <summary>宿主取消（ALT、关机、锁屏，原规格 11:183）。</summary>
    void OnCancel();

    /// <summary>光标 id（原规格 11:184）：arrow / hand / size-nwse / 自定义 id；null = 不改。</summary>
    string? Cursor { get; }
}

/// <summary>
/// 原规格 11 §5 的交互表面（本 namespace 里叫 IPointerSurface）。
/// 每实例 0..n 个，可叠加（原规格 11:17：交互层是四者里唯一数量不定的）。
/// </summary>
public interface IPointerSurface
{
    /// <summary>原规格 11 §5 `readonly id`。</summary>
    string Id { get; }

    /// <summary>原规格 11 §5 `readonly priority`：覆盖顺序，默认 0（越大越先收到）。</summary>
    int Priority { get; }

    /// <summary>
    /// 询问宿主：某点是否命中我的区域（原规格 11:174）。默认用整个窗口 ——
    /// 但判定权必须留在 surface 手里，只有它知道自己的热区（原规格 01 P-01：内核不认识插件的区域语义）。
    /// </summary>
    bool HitTest(PetPoint local);

    /// <summary>命中之后的语义（原规格 11:176）。返回 null = 拒绝，宿主转给下一个 surface。</summary>
    IPointerSession? OnPointerDown(PetPoint local, PointerButton button);
}
