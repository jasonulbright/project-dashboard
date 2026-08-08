using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// A README image is fetched from a host the reader does not control. HttpClient.Timeout
/// under ResponseHeadersRead ends at the response headers, so a server that answers 200
/// and then stops sending body bytes leaves the read pending forever: the socket, the
/// buffer and the empty image block are held for the life of the process, and the reader
/// sees a gap that never resolves. The fetch must therefore be bounded end to end, and
/// whatever ends it must still leave the alt-text placeholder on screen.
/// </summary>
[Collection(MarkdownImageCollection.Name)]
public class MarkdownImageFetchTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>Generous multiple of the budget: proves the bound exists, not its precision.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task StalledBody_IsAbandonedAtTheBudget_NotLeftPending()
    {
        using var server = new StubImageServer(StubImageServer.Mode.HeadersThenStall);

        var clock = Stopwatch.StartNew();
        var abandon = ProjectDetailPage.FetchBoundedAsync(server.Url("/stalled.png"), Budget);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandon);
        clock.Stop();

        Assert.True(clock.Elapsed < Slack, $"body read ran {clock.Elapsed} past a {Budget} budget");
    }

    [Fact]
    public async Task DribbledBody_IsAbandonedAtTheBudget_EvenThoughItStaysUnderTheByteCap()
    {
        using var server = new StubImageServer(StubImageServer.Mode.HeadersThenDribble);

        var clock = Stopwatch.StartNew();
        var abandon = ProjectDetailPage.FetchBoundedAsync(server.Url("/dribble.png"), Budget);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandon);
        clock.Stop();

        Assert.True(clock.Elapsed < Slack, $"dribbled body ran {clock.Elapsed} past a {Budget} budget");
    }

    [Fact]
    public async Task NormalFetch_StillReadsTheWholeBody()
    {
        var png = SmallPng();
        using var server = new StubImageServer(StubImageServer.Mode.Serve, png);

        using var fetched = await ProjectDetailPage.FetchBoundedAsync(server.Url("/badge.png"), Budget);

        Assert.NotNull(fetched);
        Assert.Equal(png.Length, fetched.Length);
        Assert.NotNull(ProjectDetailPage.DecodeBounded(fetched));
    }

    /// <summary>
    /// The abandoned fetch has to reach the same place a refused one does. An image block
    /// left holding a null Source renders as blank space, so the reader cannot tell a
    /// stalled host from a README with a gap in it.
    /// </summary>
    [Fact]
    public void AbandonedFetch_LeavesTheAltTextPlaceholder_NotAnEmptyBlock()
    {
        using var server = new StubImageServer(StubImageServer.Mode.HeadersThenStall);
        var url = server.Url("/never-arrives.png");
        string rendered = "";

        RunOnDispatcher(async () =>
        {
            var doc = new FlowDocument();
            var block = new BlockUIContainer(new System.Windows.Controls.Image());
            doc.Blocks.Add(block);

            await ProjectDetailPage.FillRemoteImageAsync(doc, block, url, "coverage badge", Budget);

            rendered = await doc.Dispatcher.InvokeAsync(
                () => new TextRange(doc.ContentStart, doc.ContentEnd).Text);
        });

        Assert.Contains("[image not loaded: coverage badge]", rendered);
    }

    /// <summary>
    /// A badge row repeats one URL, and the decoded-image cache is written only once a
    /// fetch completes — so on first render every occurrence would open its own
    /// connection to the same host for the same bytes.
    /// </summary>
    [Fact]
    public void ARepeatedImageUrl_IsFetchedOnce_NotOncePerOccurrence()
    {
        using var server = new StubImageServer(StubImageServer.Mode.Serve, SmallPng());
        var url = server.Url("/repeated-badge.png");

        RunOnDispatcher(async () =>
        {
            var doc = new FlowDocument();
            var blocks = new List<BlockUIContainer>();
            for (var occurrence = 0; occurrence < 5; occurrence++)
            {
                var block = new BlockUIContainer(new System.Windows.Controls.Image());
                doc.Blocks.Add(block);
                blocks.Add(block);
            }

            await Task.WhenAll(blocks.Select(
                block => ProjectDetailPage.FillRemoteImageAsync(doc, block, url, "badge", Budget)));
        });

        Assert.Equal(1, server.Requests);
    }

    /// <summary>
    /// Runs an async body on an STA thread with a pumping dispatcher. FillRemoteImageAsync
    /// applies its result through <c>doc.Dispatcher.InvokeAsync</c>, which never runs on a
    /// thread whose dispatcher is idle.
    /// </summary>
    private static void RunOnDispatcher(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                body().ContinueWith(finished =>
                {
                    error ??= finished.Exception;
                    dispatcher.InvokeShutdown();
                }, TaskScheduler.Default);
                if (!dispatcher.HasShutdownStarted) Dispatcher.Run();
            }
            catch (Exception ex) { error ??= ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("dispatcher test body did not complete");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static byte[] SmallPng()
    {
        var source = BitmapSource.Create(8, 8, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[8 * 4 * 8], 8 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// A loopback HTTP origin that can answer honestly, answer and then go quiet, or
    /// answer one byte at a time. Raw sockets rather than HttpListener: the failure under
    /// test is a well-formed response whose body never ends.
    /// </summary>
    private sealed class StubImageServer : IDisposable
    {
        internal enum Mode { Serve, HeadersThenStall, HeadersThenDribble }

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Mode _mode;
        private readonly byte[] _body;
        private int _requests;

        public StubImageServer(Mode mode, byte[]? body = null)
        {
            _mode = mode;
            _body = body ?? new byte[64];
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptAsync();
        }

        /// <summary>Requests the origin has accepted, for asserting a fetch was not duplicated.</summary>
        public int Requests => Volatile.Read(ref _requests);

        public string Url(string path) =>
            $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _requests);
                    _ = RespondAsync(client);
                }
            }
            catch { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    await ReadRequestAsync(stream);

                    var declared = _mode == Mode.Serve ? _body.Length : _body.Length + 4096;
                    var headers = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\n" +
                        $"Content-Length: {declared}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.FlushAsync(_stop.Token);

                    if (_mode == Mode.Serve)
                    {
                        await stream.WriteAsync(_body, _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        return;
                    }

                    if (_mode == Mode.HeadersThenDribble)
                    {
                        while (!_stop.IsCancellationRequested)
                        {
                            await stream.WriteAsync(new byte[] { 0x00 }, _stop.Token);
                            await stream.FlushAsync(_stop.Token);
                            await Task.Delay(TimeSpan.FromMilliseconds(250), _stop.Token);
                        }
                        return;
                    }

                    // Headers sent, body never: the socket stays open until the test ends.
                    await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
                }
            }
            catch { }
        }

        private async Task ReadRequestAsync(NetworkStream stream)
        {
            var seen = new List<byte>();
            var one = new byte[1];
            while (seen.Count < 8192)
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                seen.Add(one[0]);
                if (seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n'
                    && seen[^2] == '\r' && seen[^1] == '\n') return;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
