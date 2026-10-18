using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cursor.Agent;

internal sealed class PollinationsImageGenerator : IImageGenerator
{
    public const string DefaultUrl =
        "https://image.pollinations.ai/prompt/{prompt}?model=flux&width=1024&height=1024&nologo=true";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _urlTemplate;

    public PollinationsImageGenerator(
        HttpClient http,
        ILogger? logger = null,
        string? urlTemplate = null)
    {
        _http = http;
        _logger = logger ?? NullLogger.Instance;
        _urlTemplate = string.IsNullOrWhiteSpace(urlTemplate) ? DefaultUrl : urlTemplate.Trim();
    }

    public async Task<string> GenerateAsync(
        string description,
        string destinationPath,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        _ = references;
        var prompt = string.IsNullOrWhiteSpace(description) ? "image" : description.Trim();
        if (prompt.Length > 1500)
        {
            prompt = prompt[..1500];
        }

        var url = _urlTemplate.Replace("{prompt}", Uri.EscapeDataString(prompt), StringComparison.Ordinal);
        _logger.LogInformation("Generating image for {Path}", destinationPath);

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || media.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Image API returned " + media + ": " + Truncate(body, 200));
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                if (output.Length < GeneratedImagePath.MinBytes)
                {
                    throw new InvalidOperationException("Image API returned an empty file.");
                }
            }
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }

        return destinationPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
