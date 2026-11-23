using System.Net;
using System.Net.Http.Headers;
using NebulaServer.Helpers;
using NebulaServer.Models.ImageEngine;
using NebulaServer.Services.ImageEngine;
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

var imageBytes = new byte[64];
new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(imageBytes, 0);
for (var i = 8; i < imageBytes.Length; i++) imageBytes[i] = (byte)i;
var resumedFactory = new ResumableFactory(imageBytes);
var imageManager = new ImageDownloadManager(resumedFactory);
var imageDirectory = Path.Combine(Path.GetTempPath(), "nebula-image-check-" + Guid.NewGuid().ToString("N"));
try
{
    var downloaded = await imageManager.DownloadAndValidateAsync(
        new ImageCandidate { Url = "https://example.com/image", MimeType = "image/png" },
        imageDirectory);
    Check(File.ReadAllBytes(downloaded).SequenceEqual(imageBytes), "Interrupted image download resumes with HTTP Range");
    Check(resumedFactory.Ranges.SequenceEqual(new long?[] { null, 16 }), "Resume request starts at the saved byte offset");
}
finally
{
    if (Directory.Exists(imageDirectory)) Directory.Delete(imageDirectory, recursive: true);
}

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

sealed class ResumableFactory : HttpMessageHandler, IHttpClientFactory
    {
        private readonly byte[] _content;
        private int _calls;
        public List<long?> Ranges { get; } = new();

        public ResumableFactory(byte[] content) => _content = content;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var range = request.Headers.Range?.Ranges.SingleOrDefault()?.From;
            Ranges.Add(range);
            _calls++;
            if (_calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FailingStream(_content, 16))
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                response.Content.Headers.ContentLength = _content.Length;
                return Task.FromResult(response);
            }

            var offset = checked((int)(range ?? 0));
            var response206 = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_content[offset..])
            };
            response206.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            response206.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, _content.Length - 1, _content.Length);
            return Task.FromResult(response206);
        }
}

sealed class FailingStream : Stream
    {
        private readonly byte[] _content;
        private readonly int _failureOffset;
        private int _position;

        public FailingStream(byte[] content, int failureOffset)
        {
            _content = content;
            _failureOffset = failureOffset;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_position >= _failureOffset) throw new IOException("Simulated connection interruption.");
            var count = Math.Min(buffer.Length, _failureOffset - _position);
            _content.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return Read(buffer.Span);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
