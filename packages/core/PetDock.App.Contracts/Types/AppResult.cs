namespace PetDock.App;

/// <summary>
/// 一次宿主操作的结局：成功/失败 + 一句给人看的话。
/// 与旧层的 UiCommandResult 差在**没有 Cancelled**：取消属于界面层的状态，
/// 服务只回答「做了没做成」；需要三态的调用方自己包一层。
/// </summary>
public readonly record struct AppResult(bool Ok, string Message)
{
    public static AppResult Success(string message = "") => new(true, message);

    public static AppResult Failure(string message) => new(false, message);
}
