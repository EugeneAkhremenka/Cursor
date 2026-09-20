namespace Cursor.Agent;

public sealed record PromptResult(
    bool Success,
    string Text,
    string? Error = null,
    bool Cancelled = false,
    bool Busy = false)
{
    public static PromptResult Ok(string text) => new(true, text);

    public static PromptResult Fail(string error) => new(false, "", error);

    public static PromptResult WasCancelled(string text = "") =>
        new(false, text, "Cancelled", Cancelled: true);

    public static PromptResult WasBusy() =>
        new(false, "", "Agent is busy with another prompt.", Busy: true);
}
