using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static ffrunner.NPAPIStubs;
using static ffrunner.PluginBootstrap;

namespace ffrunner;

public static class Network
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const string IO_MSG_NAME = "FFRunnerIoReady";
    private const int REQUEST_BUFFER_SIZE = 0x10000;

    private const short NPRES_DONE = 0;
    private const short NPRES_NETWORK_ERR = 1;

    private static readonly ConcurrentQueue<Request> _requestQueue = new();
    private static readonly AutoResetEvent _queueSignal = new(false);

    private static readonly HttpClient SHttp = newHttpClient();
    private static readonly ConcurrentBag<IntPtr> SRetainedPluginAllocations = new();

    private static CancellationTokenSource? _sCts;
    private static IntPtr _sHwnd;
    public static int SIoMsg;

    private static int _sActiveRequests;
    private static int _sNextRequestId;

    // -------------------
    // REQUEST CLASS
    // -------------------
    public class Request
    {
        public int Id;
        public string Url = string.Empty;
        public bool DoNotify;
        public IntPtr NotifyData;
        public bool IsPost;
        public byte[] PostData = Array.Empty<byte>();
        public string ContentType = string.Empty;

        public bool Initialized;
        public Uri? TargetUri;
        public StreamSource? Source;
        public byte[]? InMemoryData;
        public int InMemoryOffset;

        public ushort StreamType;
        public bool Done;
        public bool Failed;
        public bool Completed;
        public short DoneReason = NPRES_DONE;
        public int WriteSize;
        public int WritePtr;
        public int BytesWritten;
        public uint End;
        public byte[] Buffer = new byte[REQUEST_BUFFER_SIZE];
        public readonly AutoResetEvent ReadyEvent = new(false);
        public IntPtr StreamPtr;
        public IntPtr UrlPtr;
        public IntPtr UnmanagedBuffer;
        public IntPtr MimeTypePtr;
        public IntPtr HeadersPtr;

        // ✅ Added field to track processed bytes
        public uint Current;

        public GCHandle? Handle;
        public uint PostDataLength { get; set; }
    }

    public sealed class StreamSource : IAsyncDisposable
    {
        public StreamSource(Stream stream, long? length, HttpResponseMessage? response, string? headers)
        {
            Stream = stream;
            Length = length;
            Headers = headers;
            _response = response;
        }

        public Stream Stream { get; }
        public long? Length { get; }
        public string? Headers { get; }
        private readonly HttpResponseMessage? _response;

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            _response?.Dispose();
        }
    }

    // -------------------
    // PUBLIC METHODS
    // -------------------
    public static void InitializeWindow(IntPtr hwnd)
    {
        _sHwnd = hwnd;
        if (SIoMsg == 0)
            SIoMsg = RegisterWindowMessage(IO_MSG_NAME);
    }

    public static void RegisterGetRequest(
        [MarshalAs(UnmanagedType.LPStr)] string url,
        [MarshalAs(UnmanagedType.I1)] bool doNotify,
        IntPtr notifyData)
    {
        var req = new Request
        {
            Id = NextRequestId(),
            Url = url ?? string.Empty,
            DoNotify = doNotify,
            NotifyData = notifyData,
            IsPost = false,
            PostDataLength = 0
        };
        BeginRequest();
        Enqueue(req);
    }

    public static void InitNetwork(string mainSrcUrl)
    {
        Logger.Log($"Network.InitNetwork main: {mainSrcUrl}");

        Init();
        InMemorySources.Init();

        var main = App.Args.MainPathOrAddress ?? string.Empty;
        if (!string.IsNullOrEmpty(main))
        {
            // Register the main request on the UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                RegisterGetRequest(main, true, IntPtr.Zero);
            });
        }
    }


    public static void RegisterPostRequest(
        [MarshalAs(UnmanagedType.LPStr)] string url,
        [MarshalAs(UnmanagedType.I1)] bool doNotify,
        IntPtr notifyData,
        [MarshalAs(UnmanagedType.LPStr)] string postData,
        uint postDataLen)
    {
        // Convert string payload to raw bytes (ANSI/UTF8 depending on NPAPI expectations)
        var payloadBytes = string.IsNullOrEmpty(postData)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(postData);

        if (postDataLen > payloadBytes.Length)
            throw new ArgumentException("postDataLen exceeds actual payload length");

        var req = new Request
        {
            Id = NextRequestId(),
            Url = url ?? string.Empty,
            DoNotify = doNotify,
            NotifyData = notifyData,
            IsPost = true,
            PostData = payloadBytes,
            PostDataLength = postDataLen
        };

        BeginRequest();
        Enqueue(req);
    }


    public static void Init() => EnsureWorker();

    public static void Shutdown()
    {
        var cts = Interlocked.Exchange(ref _sCts, null);
        cts?.Cancel();
        _queueSignal.Set(); // wake worker so it exits

        while (_requestQueue.TryDequeue(out _)) { }

        while (SRetainedPluginAllocations.TryTake(out var ptr))
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
    }

    // -------------------
    // QUEUE + WORKER
    // -------------------
    public static void Enqueue(Request req)
    {
        EnsureWorker();
        _requestQueue.Enqueue(req);
        _queueSignal.Set();
    }

    private static void EnsureWorker()
    {
        if (_sCts != null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _sCts, cts, null) != null)
        {
            cts.Dispose();
            return;
        }
        Task.Run(() => WorkerLoop(cts.Token), cts.Token);
    }

    private static async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_requestQueue.TryDequeue(out var req))
                await ProcessRequestAsync(req, ct);
            else
                _queueSignal.WaitOne(500);
        }
    }

    // -------------------
    // REQUEST LIFECYCLE
    // -------------------
    private static async Task ProcessRequestAsync(Request req, CancellationToken ct)
    {
        try
        {
            while (!req.Done && !req.Completed)
            {
                if (!req.Initialized)
                    await InitRequestAsync(req, ct);

                if (!req.Failed)
                {
                    if (req.IsPost)
                        await ProcessPostAsync(req, ct);
                    else
                        await ProgressRequestAsync(req, ct);
                }

                if (!req.Completed)
                    await PostRequestAsync(req, ct);
            }
        }
        finally
        {
            await CleanupRequestAsync(req);
        }
    }

    private static async Task ProcessPostAsync(Request req, CancellationToken ct)
    {
        if (req.PostData.Length == 0) return;

        var target = req.TargetUri ?? throw new InvalidOperationException("Post request without TargetUri");
        var httpReq = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new ByteArrayContent(req.PostData, 0, (int)req.PostDataLength)
        };

        if (!string.IsNullOrEmpty(req.ContentType))
            httpReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(req.ContentType);

        var resp = await SendRequestAsync(httpReq, ct);

        req.Source = new StreamSource(
            await resp.Content.ReadAsStreamAsync(ct),
            resp.Content.Headers.ContentLength,
            resp,
            null
        );

        // Mirror native: End = postDataLen
        req.End = req.PostDataLength;
    }

    public static void HandleIoProgress(Request req, int bytesRead = 0)
    {
        try
        {
            if (req == null || req.Done) return;

            // Increment processed bytes
            req.Current += (uint)bytesRead;

            App.RunOnUI(() =>
            {
                // Progress the request synchronously on the UI thread
                ProgressRequestAsync(req, CancellationToken.None)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                // If we’ve reached the end and not yet marked completed
                if (req.Current >= req.End && !req.Completed)
                {
                    req.Done = true;
                    req.DoneReason = NPRES_DONE;
                    req.Completed = true;
                    CompleteRequest();

                    if (req.DoNotify)
                    {
                        Plugin_UrlNotify!(nppUnmanagedPtr, req.Url, req.DoneReason, req.NotifyData);
                    }

                    Logger.Log($"[Network] Completed request: {req.Url}");
                }
            });
        }
        catch (Exception ex)
        {
            FailRequest(req, nameof(HandleIoProgress), ex);
        }
    }


    private static async Task InitRequestAsync(Request req, CancellationToken ct)
    {
        try
        {
            req.Url = GetRedirected(req.Url);

            var inMem = InMemorySources.Get(req.Url);
            if (inMem != null)
            {
                var bytes = Encoding.ASCII.GetBytes(inMem);
                req.Source = new StreamSource(new MemoryStream(bytes), bytes.Length, null, null);
                req.End = (uint)bytes.Length;
            }
            else
            {
                var target = new Uri(req.Url, UriKind.RelativeOrAbsolute);
                if (target.IsFile && File.Exists(target.LocalPath))
                {
                    var fi = new FileInfo(target.LocalPath);
                    req.Source = new StreamSource(
                        new FileStream(target.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                        fi.Length, null, null);
                    req.End = (uint)Math.Min(fi.Length, uint.MaxValue);
                }
                else
                {
                    var httpReq = new HttpRequestMessage(req.IsPost ? HttpMethod.Post : HttpMethod.Get, target);
                    var httpResp = await SendRequestAsync(httpReq, ct);
                    req.Source = new StreamSource(
                        await httpResp.Content.ReadAsStreamAsync(ct),
                        httpResp.Content.Headers.ContentLength,
                        httpResp,
                        httpResp.Headers.ToString());
                    req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
                }
            }

            if (req.Source != null)
            {
                req.UrlPtr = StringToUtf8(req.Url);
                req.MimeTypePtr = StringToUtf8(GetMimeType(req.Url));
                req.HeadersPtr = StringToUtf8(req.Source.Headers ?? string.Empty);

                var streamEmu = new Structs.NPStream
                {
                    pdata = IntPtr.Zero,
                    ndata = IntPtr.Zero,
                    url = req.UrlPtr,
                    end = req.End,
                    notifyData = req.DoNotify ? req.NotifyData : IntPtr.Zero,
                    headers = req.HeadersPtr
                };

                req.StreamPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Structs.NPStream>());
                Marshal.StructureToPtr(streamEmu, req.StreamPtr, false);

                App.RunOnUI(() =>
                {
                    int res = Plugin_NewStream!(nppUnmanagedPtr, req.Url, req.StreamPtr, false, out var stype);
                    req.StreamType = stype;
                    if (res != 0)
                    {
                        // ✅ Fail-fast cancel
                        req.Failed = true;
                        req.Done = true;
                        req.DoneReason = NPRES_NETWORK_ERR;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            FailRequest(req, nameof(InitRequestAsync), ex);
        }
        finally
        {
            req.Initialized = true;
        }
    }

    private static async Task ProgressRequestAsync(Request req, CancellationToken ct)
    {
        while (req is { Done: false, Failed: false })
        {
            if (req.Source?.Stream != null)
            {
                req.WriteSize = await req.Source.Stream.ReadAsync(req.Buffer.AsMemory(0, REQUEST_BUFFER_SIZE), ct);
                if (req.WriteSize == 0)
                {
                    req.Done = true;
                    req.DoneReason = NPRES_DONE;
                }
            }

            int writePtr = 0;
            while (writePtr < req.WriteSize)
            {
                int ready = 0;
                App.RunOnUI(() => ready = Plugin_WriteReady!(nppUnmanagedPtr, req.StreamPtr));
                if (ready <= 0) { await Task.Yield(); continue; }

                int chunk = Math.Min(ready, req.WriteSize - writePtr);
                if (req.UnmanagedBuffer == IntPtr.Zero)
                    req.UnmanagedBuffer = Marshal.AllocHGlobal(REQUEST_BUFFER_SIZE);

                Marshal.Copy(req.Buffer, writePtr, req.UnmanagedBuffer, chunk);

                int written = 0;
                App.RunOnUI(() =>
                {
                    written = Plugin_Write!(nppUnmanagedPtr, req.StreamPtr, req.BytesWritten, chunk, req.UnmanagedBuffer);
                });

                // ✅ Fail-fast cancellation
                if (written < 0)
                {
                    Logger.Log($"Write error {written}");
                    req.Failed = true;
                    req.Done = true;
                    req.DoneReason = NPRES_NETWORK_ERR;
                    break;
                }
                else if (written < chunk)
                {
                    Logger.Log($"Not enough bytes consumed {written} < {chunk}");
                    req.Failed = true;
                    req.Done = true;
                    req.DoneReason = NPRES_NETWORK_ERR;
                    break;
                }

                writePtr += written;
                req.BytesWritten += written;
                req.WritePtr = writePtr;
            }

            // ✅ Destroy stream + notify on completion/failure
            if (req.Failed || (req.Done && req.WriteSize == 0))
            {
                App.RunOnUI(() =>
                {
                    Plugin_DestroyStream!(nppUnmanagedPtr, req.StreamPtr, req.DoneReason);
                    if (req.DoNotify)
                        Plugin_UrlNotify!(nppUnmanagedPtr, req.Url, req.DoneReason, req.NotifyData);
                });

                if (req.Source != null)
                {
                    await req.Source.DisposeAsync();
                    req.Source = null;
                }

                req.Completed = true;
                CompleteRequest(); // ✅ decrement active requests
            }
        }
    }


    private static async Task PostRequestAsync(Request req, CancellationToken ct)
    {
        try
        {
            if (_sHwnd == IntPtr.Zero || SIoMsg == 0)
                throw new InvalidOperationException("Network window message plumbing is not initialized.");

            // Allocate a GCHandle so the request can be passed through PostMessage
            if (req.Handle is not { IsAllocated: true })
                req.Handle = GCHandle.Alloc(req, GCHandleType.Normal);

            bool posted = false;
            App.RunOnUI(() =>
            {
                posted = PostMessage(_sHwnd, SIoMsg, IntPtr.Zero, GCHandle.ToIntPtr(req.Handle.Value));
            });

            if (!posted)
                throw new InvalidOperationException($"Failed to post IO message: {Marshal.GetLastWin32Error()}");

            // Wait for the worker to signal readiness
            if (!await Task.Run(() => req.ReadyEvent.WaitOne(20000), ct))
                Logger.Log($"Network.PostRequestAsync timeout requestId={req.Id}");
        }
        catch (Exception ex)
        {
            FailRequest(req, nameof(PostRequestAsync), ex);
            req.Failed = true;
            req.Done = true;
            req.DoneReason = NPRES_NETWORK_ERR;
        }
    }


    private static async Task CleanupRequestAsync(Request req)
    {
        try
        {
            if (req.Source != null)
            {
                await req.Source.DisposeAsync();
                req.Source = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Network] CleanupRequestAsync failed: {ex}");
        }

        if (req.HeadersPtr != IntPtr.Zero) { Marshal.FreeHGlobal(req.HeadersPtr); req.HeadersPtr = IntPtr.Zero; }
        if (req.UrlPtr != IntPtr.Zero) { Marshal.FreeHGlobal(req.UrlPtr); req.UrlPtr = IntPtr.Zero; }
        if (req.MimeTypePtr != IntPtr.Zero) { Marshal.FreeHGlobal(req.MimeTypePtr); req.MimeTypePtr = IntPtr.Zero; }

        // ✅ Free unmanaged buffer only after destroy/notify
        if (req.UnmanagedBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(req.UnmanagedBuffer);
            req.UnmanagedBuffer = IntPtr.Zero;
        }

        if (req.Handle?.IsAllocated == true)
            req.Handle.Value.Free();

        req.StreamPtr = IntPtr.Zero;
        req.ReadyEvent.Dispose();
    }

    // --------------------------
    // HELPER METHODS
    // --------------------------
    private static HttpClient newHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        return new HttpClient(handler, true);
    }

    private static string GetMimeType(string url)
    {
        var ext = Path.GetExtension(url).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html",
            ".htm" => "text/html",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream"
        };
    }

    private static string GetRedirected(string url)
    {
        // Redirect loading images if endpoint loading screen is enabled
        if (App.Args.UseEndpointLoadingScreen && url.StartsWith("assets/img", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url.Substring("assets/img".Length);
            return $"https://{App.Args.EndpointHost}/launcher/loading{rest}";
        }

        return url;
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        return await SHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static void FailRequest(Request req, string where, Exception ex)
    {
        Logger.Log($"[Network] FailRequest in {where} for URL {req.Url}: {ex}");
        req.Failed = true;
        req.Done = true;
        req.DoneReason = NPRES_NETWORK_ERR;
    }

    private static IntPtr StringToUtf8(string s)
    {
        if (string.IsNullOrEmpty(s)) return IntPtr.Zero;
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        SRetainedPluginAllocations.Add(ptr);
        return ptr;
    }

    private static int NextRequestId() => Interlocked.Increment(ref _sNextRequestId);
    private static void BeginRequest() => Interlocked.Increment(ref _sActiveRequests);
    private static void CompleteRequest() => Interlocked.Decrement(ref _sActiveRequests);
}
