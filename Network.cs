using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
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
    private const int REQUEST_BUFFER_SIZE = 0x8000;

    private const ushort NP_NORMAL = 1;
    private const ushort NP_ASFILE = 3;
    private const ushort NP_ASFILEONLY = 4;
    private const short NPRES_DONE = 0;
    private const short NPRES_NETWORK_ERR = 1;

    public sealed class Request
    {
        public int Id;
        public string Url = string.Empty;
        public bool DoNotify;
        public IntPtr NotifyData;
        public bool IsPost;
        public byte[] PostData = Array.Empty<byte>();
        public string ContentType = string.Empty;

        public bool Initialized;
        public string EffectiveUrl = string.Empty;
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
        public IntPtr UnmanagedBuffer; // persistent unmanaged buffer for Plugin_Write

		public IntPtr MimeTypePtr;

        // Track the GCHandle created in NPN_NewStream
        public GCHandle? Handle;
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
            _response?.Dispose();
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct NPStream
    {
        public IntPtr pdata;
        public IntPtr ndata;
        public IntPtr url;
        public uint end;
        public uint lastmodified;
        public IntPtr notifyData;
        public IntPtr headers;
    }

    private static readonly HttpClient SHttp = newHttpClient();
    private static readonly ConcurrentBag<IntPtr> SRetainedPluginAllocations = new();

    private static CancellationTokenSource? _sCts;
    private static IntPtr _sHwnd;
    public static int SIoMsg;

    private static Uri? s_baseUri;

    private static int _sActiveRequests;
    private static int _sMainRequested;
    private static int _sNextRequestId;

    public static void InitializeWindow(IntPtr hwnd)
    {
        Logger.Log($"Network.InitializeWindow hwnd=0x{hwnd:x}");
        _sHwnd = hwnd;
        if (SIoMsg == 0)
            SIoMsg = RegisterWindowMessage(IO_MSG_NAME);
        Logger.Log($"Network.InitializeWindow ioMsg=0x{SIoMsg:x}");
    }

    private static async Task PostRequestAsync(Request req, CancellationToken ct)
    {
        if (_sHwnd == IntPtr.Zero || SIoMsg == 0)
            throw new InvalidOperationException("Network window message plumbing is not initialized.");

        if (req.Handle is not { IsAllocated: true })
            req.Handle = GCHandle.Alloc(req, GCHandleType.Normal);

        Logger.Verbose(
            $"Network.PostRequestAsync requestId={req.Id}, url='{req.Url}', done={req.Done}, failed={req.Failed}, writeSize={req.WriteSize}, writePtr={req.WritePtr}, bytesWritten={req.BytesWritten}");

        if (!PostMessage(_sHwnd, SIoMsg, IntPtr.Zero, GCHandle.ToIntPtr(req.Handle.Value)))
            throw new InvalidOperationException($"Failed to post IO message: {Marshal.GetLastWin32Error()}");

        // Wait asynchronously for the plugin to consume data
        await Task.Run(() => req.ReadyEvent.WaitOne(20000), ct).ConfigureAwait(false);
    }

    public static void HandleIoProgress(Request req)
    {
        Logger.Verbose(
            $"Network.HandleIoProgress enter requestId={req.Id}, url='{req.Url}', completed={req.Completed}, done={req.Done}, failed={req.Failed}, reason={req.DoneReason}, writeSize={req.WriteSize}, writePtr={req.WritePtr}, bytesWritten={req.BytesWritten}");

        // --- Ensure NPStream created ---
        if (req.StreamPtr == IntPtr.Zero && !req.Failed)
        {
            req.UrlPtr = StringToUtf8(req.Url);
            req.MimeTypePtr = StringToUtf8(GetMimeType(req.Url));

            var streamEmu = new NPStream
            {
                pdata = IntPtr.Zero,
                ndata = IntPtr.Zero,
                url = req.UrlPtr,
                end = req.End,
                lastmodified = 0,
                notifyData = req.NotifyData,
                headers = IntPtr.Zero
            };

            req.StreamPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPStream>());
            Marshal.StructureToPtr(streamEmu, req.StreamPtr, false);

            int newStreamRet = Plugin_NewStream!(nppUnmanagedPtr, req.MimeTypePtr, req.StreamPtr, 0, out var stype);
            req.StreamType = stype;

            if (newStreamRet != 0)
            {
                req.Failed = true;
                req.Done = true;
                req.DoneReason = NPRES_NETWORK_ERR;
            }
        }

        // --- Write loop ---
        var bytesAvailable = req.WriteSize - req.WritePtr;
        if (!req.Failed && req.StreamPtr != IntPtr.Zero && bytesAvailable > 0)
        {
            var ready = Plugin_WriteReady!(nppUnmanagedPtr, req.StreamPtr);

            if (ready >= 0)
            {
                var toSend = Math.Min(Math.Max(ready, 0), bytesAvailable);
                if (toSend > 0)
                {
                    if (req.UnmanagedBuffer == IntPtr.Zero)
                        req.UnmanagedBuffer = Marshal.AllocHGlobal(REQUEST_BUFFER_SIZE);

                    // ✅ Always copy into start of unmanaged buffer
                    Marshal.Copy(req.Buffer, req.WritePtr, req.UnmanagedBuffer, toSend);

                    // ✅ Pass unmanaged buffer directly, not offset
                    var written = Plugin_Write!(nppUnmanagedPtr, req.StreamPtr,
                                                req.BytesWritten, toSend,
                                                req.UnmanagedBuffer);

                    req.WritePtr += written;
                    req.BytesWritten += written;
                }
            }
            else
            {
                req.Failed = true;
                req.Done = true;
                req.DoneReason = NPRES_NETWORK_ERR;
            }
        }

        bytesAvailable = req.WriteSize - req.WritePtr;
        var forceManifestClose = req.Url.Contains("Manifest.resourceFile") && req.Done;

        if (req.Failed || (req.Done && bytesAvailable == 0) || forceManifestClose)
        {
            Plugin_DestroyStream!(nppUnmanagedPtr, req.StreamPtr, req.DoneReason);

            if (req.DoNotify || forceManifestClose)
                Plugin_UrlNotifyPtr!(nppUnmanagedPtr, req.UrlPtr, req.DoneReason, req.NotifyData);

            req.Source?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            req.Source = null;

            req.Completed = true;
            CompleteRequest();
        }

        Logger.Log($"Network.HandleIoProgress exit requestId={req.Id}, completed={req.Completed}, done={req.Done}, failed={req.Failed}, reason={req.DoneReason}, writeSize={req.WriteSize}, writePtr={req.WritePtr}, bytesWritten={req.BytesWritten}");
    }

    private static void CleanupRequest(Request req)
    {
        try
        {
            req.Source?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            req.Source = null;
        }
        catch (Exception ex)
        {
            //Logger.Log($"CleanupRequest source dispose failed requestId={req.Id}: {ex}");
        }

        // FIX: Only free unmanaged memory once
        if (req.StreamPtr != IntPtr.Zero)
        {
            Logger.Log($"Freeing StreamPtr for requestId={req.Id}");
            Marshal.FreeHGlobal(req.StreamPtr);
            req.StreamPtr = IntPtr.Zero;
        }

        if (req.UrlPtr != IntPtr.Zero)
        {
            Logger.Log($"Freeing UrlPtr for requestId={req.Id}");
            Marshal.FreeHGlobal(req.UrlPtr);
            req.UrlPtr = IntPtr.Zero;
        }

        if (req.MimeTypePtr != IntPtr.Zero)
        {
            Logger.Log($"Freeing MimeTypePtr for requestId={req.Id}");
            Marshal.FreeHGlobal(req.MimeTypePtr);
            req.MimeTypePtr = IntPtr.Zero;
        }

        // Free persistent unmanaged buffer
        if (req.UnmanagedBuffer != IntPtr.Zero)
        {
            Logger.Log($"Freeing UnmanagedBuffer for requestId={req.Id}");
            Marshal.FreeHGlobal(req.UnmanagedBuffer);
            req.UnmanagedBuffer = IntPtr.Zero;
        }

		if (req.Handle?.IsAllocated == true)
            req.Handle.Value.Free();

        req.ReadyEvent.Dispose();
    }


    public static void RegisterGetRequest(string url, bool doNotify, IntPtr notifyData)
    {
        Request req = new()
        {
            Id = NextRequestId(),
            Url = url ?? string.Empty,
            DoNotify = doNotify,
            NotifyData = notifyData,
            IsPost = false
        };

        Logger.Log($"Network.RegisterGetRequest {DescribeRequest(req)}");
        BeginRequest();
        Enqueue(req);
    }

    public static void RegisterPostRequest(string url, bool doNotify, IntPtr notifyData, uint postLen, byte[] postData)
    {
        Request req = new()
        {
            Id = NextRequestId(),
            Url = url ?? string.Empty,
            DoNotify = doNotify,
            NotifyData = notifyData,
            IsPost = true,
            PostData = postData ?? Array.Empty<byte>()
        };

        Logger.Log($"Network.RegisterPostRequest {DescribeRequest(req)}, postLen={postLen}");
        BeginRequest();
        Enqueue(req);
    }

    public static void InitNetwork(string mainSrcUrl)
    {
        s_baseUri = null;

        CleanupGeneratedTransientFiles();

        Logger.Log($"Network.InitNetwork main: {mainSrcUrl}");
        Logger.Log($"Network baseUri: {(s_baseUri != null ? s_baseUri.ToString() : "(none)")}");
        Init();
    }

    public static void Init()
    {
        EnsureWorker();
    }

    public static void Shutdown()
    {
        try
        {
            var cts = Interlocked.Exchange(ref _sCts, null);
            cts?.Cancel();

            while (SRetainedPluginAllocations.TryTake(out var ptr))
                if (ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptr);
        }
        catch (Exception ex)
        {
            //Logger.Log($"[Network] Shutdown failed: {ex}");
        }
    }

    private static void CleanupGeneratedTransientFiles()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            string[] transientFiles =
            {
                Path.Combine(baseDir, "loginInfo.php"),
                Path.Combine(baseDir, "assetInfo.php")
            };

            foreach (var path in transientFiles)
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logger.Log($"[Network] Deleted stale generated file '{path}'");
                }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Network] CleanupGeneratedTransientFiles failed: {ex.Message}");
        }
    }

    public static void Enqueue(Request req)
    {
        Logger.Verbose($"Network.Enqueue {DescribeRequest(req)}");
        EnsureWorker();

        var cts = _sCts;
        if (cts == null)
            throw new InvalidOperationException("Network request processing is not initialized.");

        Task.Run(() => ProcessRequestAsync(req, cts.Token), cts.Token);
    }

    private static void EnsureWorker()
    {
        if (_sCts != null)
            return;

        var cts = new CancellationTokenSource();
        var existing = Interlocked.CompareExchange(ref _sCts, cts, null);
        if (existing != null)
        {
            cts.Dispose();
            return;
        }

        Logger.Log("Network.EnsureWorker initialized request processing");
    }

    private static async Task ProcessRequestAsync(Request req, CancellationToken ct)
    {
        Logger.Log($"Network.ProcessRequestAsync start {DescribeRequest(req)}");
        try
        {
            while (!req.Done)
            {
                if (!req.Initialized)
                    await InitRequestAsync(req, ct).ConfigureAwait(false);

                if (!req.Failed)
                {
                    if (req.IsPost)
                        await ProcessPostAsync(req, ct).ConfigureAwait(false);
                    else
                        await ProgressRequestAsync(req, ct).ConfigureAwait(false);
                }

                if (!req.Completed)
                    await PostRequestAsync(req, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Log($"Network.ProcessRequestAsync canceled requestId={req.Id}");
        }
        catch (Exception ex)
        {
            //Logger.Log($"[Network] request worker crashed for requestId={req.Id}, url='{req.Url}': {ex}");
        }
        finally
        {
            CleanupRequest(req);
            Logger.Log($"Network.ProcessRequestAsync end requestId={req.Id}, url='{req.Url}'");
        }
    }


    private static async Task ProgressRequestAsync(Request req, CancellationToken ct)
    {
        // Only refill if plugin has consumed previous buffer
        if (req.WritePtr != req.WriteSize)
            return;

        req.WritePtr = 0;
        req.WriteSize = 0;

        try
        {
            if (req.InMemoryData != null)
            {
                var remaining = req.InMemoryData.Length - req.InMemoryOffset;
                if (remaining > 0)
                {
                    var toCopy = Math.Min(req.Buffer.Length, remaining);
                    Buffer.BlockCopy(req.InMemoryData, req.InMemoryOffset, req.Buffer, 0, toCopy);
                    req.InMemoryOffset += toCopy;
                    req.WriteSize = toCopy;
                }
            }
            else if (req.Source != null)
            {
                var read = await req.Source.Stream.ReadAsync(req.Buffer.AsMemory(0, req.Buffer.Length), ct)
                    .ConfigureAwait(false);
                req.WriteSize = read;
            }

            if (req.WriteSize == 0)
            {
                req.Done = true;
                req.DoneReason = NPRES_DONE;
            }
        }
        catch (Exception ex)
        {
            FailRequest(req, "ProgressRequestAsync", ex);
        }
    }

    private static async Task InitRequestAsync(Request req, CancellationToken ct)
    {
        req.EffectiveUrl = GetRedirectedUrl(req.Url);

        try
        {
            // 1. In-memory source
            if (TryGetInMemoryContent(req.EffectiveUrl, out var inMemory))
            {
                req.InMemoryData = inMemory;
                req.InMemoryOffset = 0;
                req.End = (uint)Math.Min(inMemory.Length, uint.MaxValue);
                req.Initialized = true;
                return;
            }

            // 2. Resolve URI
            var target = ResolveUri(req.EffectiveUrl);
            req.TargetUri = target;

            // 3. Local file
            if (target.IsFile)
            {
                if (!File.Exists(target.LocalPath))
                {
                    if (!string.IsNullOrEmpty(App.Args.EndpointHost))
                    {
                        var endpointUrl = $"https://{App.Args.EndpointHost}/{Path.GetFileName(req.Url)}";
                        req.TargetUri = new Uri(endpointUrl);

                        var request = BuildHttpRequest(req, req.TargetUri);
                        var resp = await SendRequestAsync(request, ct).ConfigureAwait(false);

                        req.Source = new StreamSource(
                            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                            resp.Content.Headers.ContentLength,
                            resp,
                            null
                        );
                        req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
                        req.Initialized = true;
                        return;
                    }
                    else
                    {
                        FailRequest(req, "InitRequestAsync", new FileNotFoundException(req.Url));
                        return;
                    }
                }

                req.Source = new StreamSource(
                    new FileStream(target.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                    new FileInfo(target.LocalPath).Length,
                    null,
                    null
                );
                req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
                req.Initialized = true;
                return;
            }

            // 4. Cache source (GET only)
            if (!req.IsPost && TryInitFromCache(req))
            {
                req.Initialized = true;
                return;
            }

            // 5. HTTP/HTTPS source
            var httpRequest = BuildHttpRequest(req, target);
            var httpResp = await SendRequestAsync(httpRequest, ct).ConfigureAwait(false);

            req.Source = new StreamSource(
                await httpResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                httpResp.Content.Headers.ContentLength,
                httpResp,
                null
            );
            req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
        }
        catch (Exception ex)
        {
            FailRequest(req, "InitRequestAsync", ex);
            return;
        }
        finally
        {
            req.Initialized = true;
        }
    }

    private static Uri ResolveUri(string url)
    {
        var requestUrl = url ?? string.Empty;

        Logger.Log($"Network.ResolveUri input='{url}', currentDir='{Environment.CurrentDirectory}'");

        // ✅ Already a valid absolute URI
        if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeFile ||
             abs.Scheme == Uri.UriSchemeHttp ||
             abs.Scheme == Uri.UriSchemeHttps))
        {
            Logger.Log($"Network.ResolveUri absolute -> '{abs}'");
            return abs;
        }

        // 🔥 FIX: Handle Windows paths explicitly
        if (Path.IsPathRooted(requestUrl))
        {
            var fullPath = Path.GetFullPath(requestUrl);

            // Normalize to proper file URI
            var fileUri = new Uri(fullPath);

            Logger.Log($"Network.ResolveUri Windows path -> '{fileUri}'");
            return fileUri;
        }

        // fallback (relative path)
        var combined = Path.GetFullPath(requestUrl);
        var fallbackUri = new Uri(combined);

        Logger.Log($"Network.ResolveUri fallback -> '{fallbackUri}'");
        return fallbackUri;
    }

    private static (byte[] Headers, byte[] Payload) SplitPostBuffer(byte[] postData)
    {
        if (postData.Length == 0)
            return (Array.Empty<byte>(), Array.Empty<byte>());

        for (var i = 0; i + 3 < postData.Length; i++)
            if (postData[i] == (byte)'\r' && postData[i + 1] == (byte)'\n' &&
                postData[i + 2] == (byte)'\r' && postData[i + 3] == (byte)'\n')
            {
                var headerLen = i + 4;
                var headers = new byte[headerLen];
                Buffer.BlockCopy(postData, 0, headers, 0, headerLen);

                var payloadLen = postData.Length - headerLen;
                var payload = new byte[payloadLen];
                if (payloadLen > 0)
                    Buffer.BlockCopy(postData, headerLen, payload, 0, payloadLen);

                return (headers, payload);
            }

        return (Array.Empty<byte>(), postData);
    }


    private static void ApplyPostHeaders(HttpContent content, byte[] headerBytes)
    {
        if (headerBytes.Length == 0)
            return;

        var headerText = Encoding.Latin1.GetString(headerBytes);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (name.Length == 0 || value.Length == 0)
                continue;

            // Add headers safely
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                content.Headers.TryAddWithoutValidation(name, value);
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                // Skip Content-Length: HttpClient sets this automatically
                continue;
            else
                content.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static HttpClient newHttpClient()
    {
        var hc = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        }, true);

        hc.Timeout = TimeSpan.FromSeconds(30);
        hc.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ffrunner");
        return hc;
    }

    private static string GetMimeType(string url)
    {
        var fileName = GetFileNameFromUrl(url);

        if (fileName.Contains("unity-dexlabs.png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        if (fileName.Contains("unity-loadingbar.png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        if (fileName.Contains("unity-loadingframe.png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        if (fileName.Contains("main.unity3d", StringComparison.OrdinalIgnoreCase))
            return "application/octet-stream";

        if (fileName.Contains(".php", StringComparison.OrdinalIgnoreCase))
            return "text/plain";

        if (fileName.Contains(".txt", StringComparison.OrdinalIgnoreCase))
            return "text/plain";

        return fileName.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "application/octet-stream";
    }

    private static string GetRedirectedUrl(string url)
    {
        var requestUrl = url ?? string.Empty;

        if (!App.Args.UseEndpointLoadingScreen)
            return requestUrl;

        var endpointHost = App.Args.EndpointHost ?? string.Empty;
        if (string.IsNullOrEmpty(endpointHost))
            return requestUrl;

        const string prefix = "assets/img";
        if (requestUrl.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = requestUrl.Substring(prefix.Length);
            var redirected = $"https://{endpointHost}/launcher/loading{rest}";
            Logger.Log($"Network.GetRedirectedUrl redirected '{url}' -> '{redirected}'");
            return redirected;
        }

        return requestUrl;
    }

    private static string GetFileNameFromUrl(string url)
    {
        var requestUrl = url ?? string.Empty;

        if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var abs))
            return Path.GetFileName(abs.LocalPath);

        var trimmed = requestUrl;
        var q = trimmed.IndexOf('?');
        if (q >= 0)
            trimmed = trimmed.Substring(0, q);

        var hash = trimmed.IndexOf('#');
        if (hash >= 0)
            trimmed = trimmed.Substring(0, hash);

        return Path.GetFileName(trimmed);
    }

    private static bool TryGetInMemoryContent(string url, out byte[] content)
    {
        var fileName = Path.GetFileName(url).ToLowerInvariant();

        if (fileName == "logininfo.php")
        {
            var addr = App.Args.ServerAddress ?? string.Empty;
            Logger.Log($"[Network] loginInfo.php -> '{addr}'");
            content = Encoding.ASCII.GetBytes(addr);
            return true;
        }

        if (fileName == "assetinfo.php")
        {
            var assetUrl = App.Args.AssetUrl ?? string.Empty;
            Logger.Log($"[Network] assetInfo.php -> '{assetUrl}'");
            content = Encoding.ASCII.GetBytes(assetUrl);
            return true;
        }

        content = Array.Empty<byte>();
        return false;
    }

    public static int NextRequestId()
    {
        return Interlocked.Increment(ref _sNextRequestId);
    }


    public static void BeginRequest()
    {
        var active = Interlocked.Increment(ref _sActiveRequests);
        Logger.Log($"Network.BeginRequest activeRequests={active}, mainRequested={_sMainRequested}");
    }

    public static void CompleteRequest()
    {
        var remaining = Interlocked.Decrement(ref _sActiveRequests);
        Logger.Log($"Network.CompleteRequest remaining={remaining}, mainRequested={_sMainRequested}");
        if (remaining == 0)
            TryQueueMainRequest();
    }

    private static void TryQueueMainRequest()
    {
        Logger.Log($"Network.TryQueueMainRequest mainRequested={_sMainRequested}, activeRequests={_sActiveRequests}");
        if (Interlocked.CompareExchange(ref _sMainRequested, 1, 0) != 0)
            return;

        var main = App.Args.MainPathOrAddress ?? string.Empty;
        if (string.IsNullOrEmpty(main))
            return;

        Logger.Log($"[Network] Queuing main request: {main}");
        RegisterGetRequest(main, true, IntPtr.Zero);
    }

    private static string DescribeRequest(in Request req)
    {
        return
            $"requestId={req.Id}, url='{req.Url}', doNotify={req.DoNotify}, notifyData=0x{req.NotifyData.ToString("x")}, isPost={req.IsPost}";
    }

    private static bool TryInitFromCache(Request req)
    {
        try
        {
            var cacheDir = Path.Combine(AppContext.BaseDirectory, "cache");
            var cachePath = Path.Combine(cacheDir, Path.GetFileName(req.Url));

            if (File.Exists(cachePath))
            {
                Logger.Log($"[Network] Cache hit for {req.Url} -> {cachePath}");
                req.Source = new StreamSource(
                    new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                    new FileInfo(cachePath).Length,
                    null,
                    null
                );
                req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Network] TryInitFromCache failed for {req.Url}: {ex.Message}");
        }

        return false;
    }

    private static async Task ProcessPostAsync(Request req, CancellationToken ct)
    {
        Logger.Log(
            $"[Network] POST requestId={req.Id} url='{req.Url}' len={req.PostData?.Length ?? 0} notify={req.DoNotify}");

        try
        {
            var target = ResolveUri(req.Url);
            if (!target.IsAbsoluteUri)
                throw new InvalidOperationException("Resolved URI is not absolute: " + req.Url);

            (var headers, var payload) = SplitPostBuffer(req.PostData ?? Array.Empty<byte>());
            using var content = new ByteArrayContent(payload);
            ApplyPostHeaders(content, headers);

            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Version = new Version(1, 1),
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Content = content
            };

            var resp = await SHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // FIX: Only throw for non-success status code
            if (!resp.IsSuccessStatusCode)
            {
                FailRequest(req, $"HTTP {resp.StatusCode}", null);
                return;
            }

            await using var source = new StreamSource(
                await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                resp.Content.Headers.ContentLength,
                resp,
                null);

            req.Source = source;
            req.End = (uint)Math.Min(source.Length ?? 0, uint.MaxValue);
        }
        catch (Exception ex)
        {
            FailRequest(req, "ProcessPostAsync", ex);
            return;
        }
    }

    private static HttpRequestMessage BuildHttpRequest(Request req, Uri target)
    {
        var method = req.IsPost ? HttpMethod.Post : HttpMethod.Get;
        var request = new HttpRequestMessage(method, target)
        {
            Version = new Version(1, 1),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        if (!req.IsPost || req.PostData.Length <= 0) return request;
        var (headers, payload) = SplitPostBuffer(req.PostData);
        var content = new ByteArrayContent(payload);
        ApplyPostHeaders(content, headers);
        request.Content = content;

        return request;
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var resp = await SHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    private static void FailRequest(Request req, string context, Exception? ex = null)
    {
        Logger.Log($"[Network] FAIL requestId={req.Id}, url='{req.Url}', context={context}, ex={ex}");

        req.Failed = true;
        req.Done = true;
        req.DoneReason = NPRES_NETWORK_ERR;

        if (req.StreamPtr == IntPtr.Zero)
        {
            req.UrlPtr = StringToUtf8(req.Url);
            req.MimeTypePtr = StringToUtf8(GetMimeType(req.Url));

            var streamEmu = new NPStream
            {
                pdata = IntPtr.Zero,
                ndata = IntPtr.Zero,
                url = req.UrlPtr,
                end = 0,
                lastmodified = 0,
                notifyData = req.NotifyData,
                headers = IntPtr.Zero
            };

            req.StreamPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPStream>());
            Marshal.StructureToPtr(streamEmu, req.StreamPtr, false);

            Plugin_NewStream!(nppUnmanagedPtr, req.MimeTypePtr, req.StreamPtr, 0, out _);
        }

        Plugin_DestroyStream!(nppUnmanagedPtr, req.StreamPtr, NPRES_NETWORK_ERR);

        if (req.DoNotify)
            Plugin_UrlNotifyPtr!(nppUnmanagedPtr, req.UrlPtr, NPRES_NETWORK_ERR, req.NotifyData);

        req.Completed = true;
        CompleteRequest();
    }

}