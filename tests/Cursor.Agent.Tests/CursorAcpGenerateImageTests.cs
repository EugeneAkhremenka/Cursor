using System.Text.Json.Nodes;
using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class CursorAcpGenerateImageTests
{
    [Fact]
    public async Task GenerateImage_WritesFileAndReturnsGenerated()
    {
        var cwd = Directory.CreateTempSubdirectory("acp-gen").FullName;
        try
        {
            var generator = new RecordingImageGenerator();
            var hooks = new AcpClientHooks { Images = generator, WorkingDirectory = cwd };
            var events = new List<AgentActivityEvent>();
            hooks.Progress = new SyncProgress(events.Add);

            var result = await CursorAcpProtocol.HandleIncomingRequestAsync(
                "cursor/generate_image",
                JsonNode.Parse("""
                    {
                      "toolCallId":"img-1",
                      "description":"blue circle logo",
                      "filePath":"assets/logo.png"
                    }
                    """),
                "allow-always",
                hooks,
                logger: null,
                CancellationToken.None);

            var path = Path.Combine(cwd, "assets", "logo.png");
            Assert.Equal("generated", result?["outcome"]?["outcome"]?.GetValue<string>());
            Assert.Equal(path, result?["outcome"]?["filePath"]?.GetValue<string>());
            Assert.True(File.Exists(path));
            Assert.Equal(["blue circle logo"], generator.Descriptions);
            Assert.Contains(events, e => e.Kind == AgentActivityKind.Image && e.Status == "in_progress");
            Assert.Contains(events, e => e.Kind == AgentActivityKind.Image && e.Status == "completed" && e.Text == path);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateImage_ReusesExistingFileWithoutCallingGenerator()
    {
        var cwd = Directory.CreateTempSubdirectory("acp-reuse").FullName;
        try
        {
            var existing = Path.Combine(cwd, "assets", "ready.png");
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            await File.WriteAllBytesAsync(existing, RecordingImageGenerator.MinimalPng);

            var generator = new RecordingImageGenerator();
            var hooks = new AcpClientHooks { Images = generator, WorkingDirectory = cwd };

            var result = await CursorAcpProtocol.HandleIncomingRequestAsync(
                "cursor/generate_image",
                JsonNode.Parse("""
                    {"description":"already there","filePath":"assets/ready.png"}
                    """),
                "allow-always",
                hooks,
                logger: null,
                CancellationToken.None);

            Assert.Equal("generated", result?["outcome"]?["outcome"]?.GetValue<string>());
            Assert.Empty(generator.Descriptions);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateImage_RegeneratesTinyExistingFile()
    {
        var cwd = Directory.CreateTempSubdirectory("acp-tiny").FullName;
        try
        {
            var existing = Path.Combine(cwd, "assets", "ready.png");
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            await File.WriteAllBytesAsync(existing, new byte[8]);

            var generator = new RecordingImageGenerator();
            var hooks = new AcpClientHooks { Images = generator, WorkingDirectory = cwd };

            var result = await CursorAcpProtocol.HandleIncomingRequestAsync(
                "cursor/generate_image",
                JsonNode.Parse("""
                    {"description":"replace junk","filePath":"assets/ready.png"}
                    """),
                "allow-always",
                hooks,
                logger: null,
                CancellationToken.None);

            Assert.Equal("generated", result?["outcome"]?["outcome"]?.GetValue<string>());
            Assert.Equal(["replace junk"], generator.Descriptions);
            Assert.True(new FileInfo(existing).Length >= GeneratedImagePath.MinBytes);
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateImage_DeletesTinyFileWhenGeneratorFails()
    {
        var cwd = Directory.CreateTempSubdirectory("acp-junk").FullName;
        try
        {
            var dest = Path.Combine(cwd, "assets", "nope.png");
            var generator = new JunkThenFailGenerator();
            var hooks = new AcpClientHooks { Images = generator, WorkingDirectory = cwd };

            var result = await CursorAcpProtocol.HandleIncomingRequestAsync(
                "cursor/generate_image",
                JsonNode.Parse("""{"description":"nope","filePath":"assets/nope.png"}"""),
                "allow-always",
                hooks,
                logger: null,
                CancellationToken.None);

            Assert.Equal("rejected", result?["outcome"]?["outcome"]?.GetValue<string>());
            Assert.False(File.Exists(dest));
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateImage_RejectsWhenGeneratorFails()
    {
        var cwd = Directory.CreateTempSubdirectory("acp-fail").FullName;
        try
        {
            var generator = new RecordingImageGenerator { FailWith = new InvalidOperationException("boom") };
            var hooks = new AcpClientHooks { Images = generator, WorkingDirectory = cwd };

            var result = await CursorAcpProtocol.HandleIncomingRequestAsync(
                "cursor/generate_image",
                JsonNode.Parse("""{"description":"nope"}"""),
                "allow-always",
                hooks,
                logger: null,
                CancellationToken.None);

            Assert.Equal("rejected", result?["outcome"]?["outcome"]?.GetValue<string>());
            Assert.Equal("boom", result?["outcome"]?["reason"]?.GetValue<string>());
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Resolve_KeepsRelativePathUnderRepo()
    {
        var cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo-root"));
        var dest = GeneratedImagePath.Resolve(cwd, "icons/app.png", "unused");
        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "icons", "app.png")), dest);
    }

    [Fact]
    public void Resolve_RelocatesPathOutsideRepo()
    {
        var cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "safe-repo"));
        var dest = GeneratedImagePath.Resolve(cwd, Path.Combine(Path.GetTempPath(), "escape.png"), "logo");
        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "assets", "escape.png")), dest);
    }

    [Fact]
    public void Resolve_DefaultsMissingPathToAssetsSlug()
    {
        var cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "slug-repo"));
        var dest = GeneratedImagePath.Resolve(cwd, null, "Red Square Icon!");
        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "assets", "red-square-icon.png")), dest);
    }

    private sealed class SyncProgress : IProgress<AgentActivityEvent>
    {
        private readonly Action<AgentActivityEvent> _onNext;

        public SyncProgress(Action<AgentActivityEvent> onNext) => _onNext = onNext;

        public void Report(AgentActivityEvent value) => _onNext(value);
    }

    private sealed class JunkThenFailGenerator : IImageGenerator
    {
        public async Task<string> GenerateAsync(
            string description,
            string destinationPath,
            IReadOnlyList<string> references,
            CancellationToken cancellationToken)
        {
            _ = description;
            _ = references;
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(destinationPath, new byte[8], cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("boom");
        }
    }
}
