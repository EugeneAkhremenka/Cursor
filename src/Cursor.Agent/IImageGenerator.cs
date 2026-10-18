namespace Cursor.Agent;

internal interface IImageGenerator
{
    Task<string> GenerateAsync(
        string description,
        string destinationPath,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken);
}
