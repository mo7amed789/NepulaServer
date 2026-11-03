using System.Net;
using System.Net.Http.Headers;
using NebulaServer.Helpers;
using NebulaServer.Services.ImageEngine.Extractors;

static void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
static HttpResponseMessage Response(HttpStatusCode status, string type = "image/jpeg")
{
    var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(new byte[32]) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
    return response;
}
var url = new Uri("https://yt3.ggpht.com/opaque=s288-c-fcrop64=1,000020b9ffffe0b5-rw-nd-v1");
Check(YouTubeImageUrl.OriginalSize(url).AbsoluteUri == "https://yt3.ggpht.com/opaque=s0", "Replace size/crop transformations with original size");
Check(YouTubeImageUrl.OriginalSize(new Uri("https://yt3.googleusercontent.com/id=w500-h200?x=1")).Query == "?x=1", "Preserve query and support alternate YouTube CDN");
foreach (var invalid in new[] { "https://yt3.ggpht.com.evil.test/id=s288", "https://notggpht.com/id=s288", "ftp://yt3.ggpht.com/id=s288", "https://user@yt3.ggpht.com/id=s288" })
    Check(!YouTubeImageUrl.IsSupported(new Uri(invalid)), "Reject unsupported/spoofed host or scheme");
using var factory = new FakeFactory((req, ct) => Task.FromResult(Response(HttpStatusCode.OK)));
var extractor = new DirectImageExtractor(factory);
Check(extractor.CanHandle(url), "Recognize extensionless YouTube image");
var images = await extractor.ExtractAsync(url);
Check(images.Count == 1 && images[0].Url.EndsWith("=s0") && factory.Calls.Count == 1, "Original image uses one HEAD request");
using var fallback = new FakeFactory((req, ct) => Task.FromResult(Response(req.RequestUri!.AbsoluteUri.EndsWith("=s0") ? HttpStatusCode.NotFound : HttpStatusCode.OK)));
images = await new DirectImageExtractor(fallback).ExtractAsync(url);
Check(images.Single().Url == url.AbsoluteUri, "Unavailable original falls back to supplied preview");
using var getOnly = new FakeFactory((req, ct) => Task.FromResult(Response(req.Method == HttpMethod.Head ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK)));
images = await new DirectImageExtractor(getOnly).ExtractAsync(url);
Check(images.Count == 1 && getOnly.Calls.SequenceEqual(new[] { "HEAD", "GET" }), "HEAD rejection falls back to GET headers");
using var html = new FakeFactory((req, ct) => Task.FromResult(Response(HttpStatusCode.OK, "text/html")));
Check((await new DirectImageExtractor(html).ExtractAsync(url)).Count == 0, "Reject non-image content");
using var canceled = new FakeFactory((req, ct) => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true)));
try { await new DirectImageExtractor(canceled).ExtractAsync(url, new CancellationToken(true)); throw new Exception("Cancellation swallowed"); }
catch (OperationCanceledException) { Console.WriteLine("PASS Caller cancellation propagates"); }
Check(extractor.CanHandle(new Uri("https://example.com/photo.JPG?token=1")), "Existing direct images remain supported");

sealed class FakeFactory : HttpMessageHandler, IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    public List<string> Calls { get; } = new();
    public FakeFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls.Add(request.Method.Method);
        return _send(request, token);
    }
}
