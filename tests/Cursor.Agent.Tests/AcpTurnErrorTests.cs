using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class AcpTurnErrorTests
{
    [Fact]
    public void ReadsErrorGluedToTheEndOfTheAnswer()
    {
        const string text = "Проверю режимы.\n\nError: RetriableError: [aborted] read ECONNRESET";

        Assert.True(AcpTurnError.TryReadTrailing(text, out var error));
        Assert.Equal("RetriableError: [aborted] read ECONNRESET", error);
        Assert.Equal("Проверю режимы.", AcpTurnError.StripTrailing(text));
    }

    [Theory]
    [InlineData("Всё готово.")]
    [InlineData("Лог вернул Error: read ECONNRESET, это ожидаемо.")]
    [InlineData("Error: read ECONNRESET\n\nПродолжаю разбор.")]
    [InlineData("")]
    public void IgnoresAnswersThatOnlyMentionAnError(string text)
    {
        Assert.False(AcpTurnError.TryReadTrailing(text, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void IgnoresLongTrailingLineThatMerelyStartsWithError()
    {
        var text = "итог\n\nError: " + new string('x', 500);

        Assert.False(AcpTurnError.TryReadTrailing(text, out _));
    }
}
