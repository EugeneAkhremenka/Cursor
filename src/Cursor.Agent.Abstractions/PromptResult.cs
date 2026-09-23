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

    /// <summary>
    /// Watchdog stop: unlike <see cref="WasCancelled"/> this was not asked for by the user,
    /// so it is reported as a failure and keeps whatever text arrived before the silence.
    /// </summary>
    public static PromptResult TimedOut(string text, string error) => new(false, text, error);
}
