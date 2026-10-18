using System.Net;
using System.Net.Http.Headers;
using Cursor.Agent;

namespace Cursor.Agent.Tests;

public sealed class PollinationsImageGeneratorTests
{
    [Fact]
    public async Task EmptyBody_DeletesDestination()
    {
        using var http = new HttpClient(new StubHandler(new byte[8], "image/png"));
        var generator = new PollinationsImageGenerator(http);
        var cwd = Directory.CreateTempSubdirectory("pollinations").FullName;
        var dest = Path.Combine(cwd, "broken.png");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => generator.GenerateAsync("logo", dest, [], CancellationToken.None));
            Assert.Contains("empty file", error.Message);
            Assert.False(File.Exists(dest));
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly string _media;

        public StubHandler(byte[] body, string media)
        {
            _body = body;
            _media = media;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_media);
            return Task.FromResult(response);
        }
    }
}
