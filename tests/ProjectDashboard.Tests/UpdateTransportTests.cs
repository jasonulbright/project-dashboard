using System.Net;
using System.Net.Http;
using System.Text;
using ProjectDashboard.Services.Update;

namespace ProjectDashboard.Tests;

public class UpdateTransportTests
{
    [Fact]
    public async Task AStalledBody_UsesTheWholeRequestBudget()
    {
        using var client = new HttpClient(new ResponseHandler(new StreamContent(new StalledStream())));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var result = await UpdateCheckService.FetchLatestAsync(client, TimeSpan.FromMilliseconds(100), stop.Token);

        Assert.False(result.Reached);
        Assert.Contains("timed out", result.TransportError);
        Assert.False(stop.IsCancellationRequested);
    }

    [Fact]
    public async Task CallerCancellation_RemainsCancellation()
    {
        using var client = new HttpClient(new ResponseHandler(new StreamContent(new StalledStream())));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateCheckService.FetchLatestAsync(client, TimeSpan.FromSeconds(3), stop.Token));
    }

    [Fact]
    public async Task AnOversizedBody_CannotPassAsAValidPrefix()
    {
        var body = "{}".PadRight(256 * 1024) + "invalid trailing content";
        using var client = new HttpClient(new ResponseHandler(new StringContent(body, Encoding.UTF8)));

        var result = await UpdateCheckService.FetchLatestAsync(client, TimeSpan.FromSeconds(3), default);

        Assert.False(result.Reached);
        Assert.Contains("too large", result.TransportError);
    }

    [Fact]
    public async Task ABodyExactlyAtTheLimit_IsReadCompletely()
    {
        var body = "{}".PadRight(256 * 1024);
        using var client = new HttpClient(new ResponseHandler(new StringContent(body, Encoding.UTF8)));

        var result = await UpdateCheckService.FetchLatestAsync(client, TimeSpan.FromSeconds(3), default);

        Assert.True(result.Reached);
        Assert.Equal(body, result.Body);
    }

    private sealed class ResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
    }
}
