using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

    private const ushort NP_NORMAL = 1;
    private const ushort NP_ASFILE = 3;
    private const ushort NP_ASFILEONLY = 4;
    private const short NPRES_DONE = 0;
    private const short NPRES_NETWORK_ERR = 1;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct AssetInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string Url;

        public int Size;
        public int Flags;
    }
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

        public IntPtr HeadersPtr; // track unmanaged headers

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
            await Stream.DisposeAsync();

            _response?.Dispose();
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
        try
        {
            if (_sHwnd == IntPtr.Zero || SIoMsg == 0)
                throw new InvalidOperationException("Network window message plumbing is not initialized.");

            if (req.Handle is not { IsAllocated: true })
                req.Handle = GCHandle.Alloc(req, GCHandleType.Normal);

            Logger.Verbose(
                $"Network.PostRequestAsync requestId={req.Id}, url='{req.Url}', done={req.Done}");

            if (!PostMessage(_sHwnd, SIoMsg, IntPtr.Zero, GCHandle.ToIntPtr(req.Handle.Value)))
                throw new InvalidOperationException($"Failed to post IO message: {Marshal.GetLastWin32Error()}");
           
            if (!await Task.Run(() => req.ReadyEvent.WaitOne(20000), ct))
            {
                Logger.Log($"Network.PostRequestAsync timeout requestId={req.Id}");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Network] PostRequestAsync failed for requestId={req.Id}: {ex}");
            req.Failed = true;
            req.Done = true;
            req.DoneReason = NPRES_NETWORK_ERR;
        }
    }

    public static void HandleIoProgress(Request req)
    {
        if (req.Failed || req.Completed) return;

        if (req.StreamPtr == IntPtr.Zero)
        {
            req.UrlPtr = StringToUtf8(req.Url);
            req.MimeTypePtr = StringToUtf8(GetMimeType(req.Url));
            req.HeadersPtr = !string.IsNullOrEmpty(req.Source?.Headers) ? StringToUtf8(req.Source.Headers) : IntPtr.Zero;

            var streamEmu = new NPStream
            {
                pdata = IntPtr.Zero,
                ndata = IntPtr.Zero,
                url = req.UrlPtr,
                end = req.End,
                lastmodified = 0,
                notifyData = req.NotifyData,
                headers = req.HeadersPtr
            };

            req.StreamPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPStream>());
            Marshal.StructureToPtr(streamEmu, req.StreamPtr, false);

            int res = Plugin_NewStream!(nppUnmanagedPtr, req.MimeTypePtr, req.StreamPtr, 0, out var stype);
            req.StreamType = stype;

            if (res != 0)
            {
                req.Failed = true;
                req.Done = true;
                req.DoneReason = NPRES_NETWORK_ERR;
                return;
            }
        }

        // Feed plugin loop (simplified)
        int writePtr = req.WritePtr;
        while (writePtr < req.WriteSize)
        {
            int ready = Plugin_WriteReady!(nppUnmanagedPtr, req.StreamPtr);
            if (ready <= 0) { Task.Yield(); continue; }

            int chunk = Math.Min(ready, req.WriteSize - writePtr);
            if (req.UnmanagedBuffer == IntPtr.Zero)
                req.UnmanagedBuffer = Marshal.AllocHGlobal(REQUEST_BUFFER_SIZE);

            Marshal.Copy(req.Buffer, writePtr, req.UnmanagedBuffer, chunk);
            int written = Plugin_Write!(nppUnmanagedPtr, req.StreamPtr, req.BytesWritten, chunk, req.UnmanagedBuffer);

            if (written <= 0)
            {
                req.Failed = true;
                req.Done = true;
                req.DoneReason = NPRES_NETWORK_ERR;
                break;
            }

            writePtr += written;
            req.BytesWritten += written;
            req.WritePtr = writePtr;
        }

        if (req.Done || req.Failed)
        {
            Plugin_DestroyStream!(nppUnmanagedPtr, req.StreamPtr, req.DoneReason);
            req.StreamPtr = IntPtr.Zero;

            if (req.DoNotify)
                Plugin_UrlNotify!(nppUnmanagedPtr, req.UrlPtr, req.DoneReason, req.NotifyData);

            if (req.Source != null)
            {
                _ = req.Source.DisposeAsync(); // fire-and-forget async disposal
                req.Source = null;
            }

            req.Completed = true;
            CompleteRequest();
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

        if (req.HeadersPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(req.HeadersPtr);
            req.HeadersPtr = IntPtr.Zero;
        }

        // ⚠️ Do NOT free StreamPtr here — Plugin_DestroyStream already handled it
        req.StreamPtr = IntPtr.Zero;

        if (req.UrlPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(req.UrlPtr);
            req.UrlPtr = IntPtr.Zero;
        }

        if (req.MimeTypePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(req.MimeTypePtr);
            req.MimeTypePtr = IntPtr.Zero;
        }

        if (req.UnmanagedBuffer != IntPtr.Zero)
        {
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

        BeginRequest(); // ensure request count increment
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

        BeginRequest(); // ensure request count increment
        Enqueue(req);
    }

    public static void InitNetwork(string mainSrcUrl)
    {
        s_baseUri = null;

        CleanupGeneratedTransientFiles();

        Logger.Log($"Network.InitNetwork main: {mainSrcUrl}");
        Logger.Log($"Network baseUri: {(s_baseUri != null ? s_baseUri.ToString() : "(none)")}");

        Init();

        // Immediately queue main loading request
        var main = App.Args.MainPathOrAddress ?? string.Empty;
        if (!string.IsNullOrEmpty(main))
        {
            _sMainRequested = 1; // mark main requested
            RegisterGetRequest(main, true, IntPtr.Zero);
        }
    }

    public static void Init()
    {
        EnsureWorker();
        // ❌ DO NOTHING ELSE
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
            Logger.Log($"[Network] Shutdown failed: {ex}");
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
        // Logger.Verbose($"Network.Enqueue {DescribeRequest(req)}");
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Log($"Network.ProcessRequestAsync canceled requestId={req.Id}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Network] request worker crashed for requestId={req.Id}, url='{req.Url}': {ex}");
        }
        finally
        {
            await CleanupRequestAsync(req);
            Logger.Log($"Network.ProcessRequestAsync end requestId={req.Id}, url='{req.Url}'");
        }
    }

    private static async Task ProgressRequestAsync(Request req, CancellationToken ct)
    {
        try
        {
            while (!req.Done && !req.Failed)
            {
                int toRead = REQUEST_BUFFER_SIZE;

                if (req.InMemoryData != null)
                {
                    toRead = Math.Min(toRead, req.InMemoryData.Length - req.InMemoryOffset);
                    toRead = Math.Min(toRead, req.Buffer.Length);
                    Buffer.BlockCopy(req.InMemoryData, req.InMemoryOffset, req.Buffer, 0, toRead);
                    req.InMemoryOffset += toRead;
                }
                else if (req.Source?.Stream != null)
                {
                    toRead = Math.Min(toRead, req.Buffer.Length);
                    toRead = await req.Source.Stream.ReadAsync(req.Buffer.AsMemory(0, toRead), ct);
                }
                else
                {
                    toRead = 0;
                }

                req.WriteSize = toRead;
                if (req.WriteSize == 0) { req.Done = true; req.DoneReason = NPRES_DONE; break; }

                int writePtr = 0;
                while (writePtr < req.WriteSize)
                {
                    if (req.StreamPtr == IntPtr.Zero) { req.Failed = true; req.Done = true; req.DoneReason = NPRES_NETWORK_ERR; break; }

                    int ready = Plugin_WriteReady!(nppUnmanagedPtr, req.StreamPtr);
                    if (ready <= 0) { await Task.Yield(); continue; }

                    int chunk = Math.Min(ready, req.WriteSize - writePtr);
                    chunk = Math.Min(chunk, req.Buffer.Length - writePtr);

                    if (req.UnmanagedBuffer == IntPtr.Zero)
                        req.UnmanagedBuffer = Marshal.AllocHGlobal(REQUEST_BUFFER_SIZE);

                    Marshal.Copy(req.Buffer, writePtr, req.UnmanagedBuffer, chunk);
                    int written = Plugin_Write!(nppUnmanagedPtr, req.StreamPtr, req.BytesWritten, chunk, req.UnmanagedBuffer);

                    if (written <= 0) { req.Failed = true; req.Done = true; req.DoneReason = NPRES_NETWORK_ERR; break; }

                    writePtr += written;
                    req.BytesWritten += written;
                    req.WritePtr = writePtr;
                }
            }

            if (req.Done || req.Failed)
            {
                if (req.StreamPtr != IntPtr.Zero)
                {
                    Plugin_DestroyStream!(nppUnmanagedPtr, req.StreamPtr, req.DoneReason);
                    req.StreamPtr = IntPtr.Zero;
                }

                if (req.DoNotify)
                    Plugin_UrlNotify!(nppUnmanagedPtr, req.UrlPtr, req.DoneReason, req.NotifyData);

                if (req.Source != null)
                {
                    await req.Source.DisposeAsync();
                    req.Source = null;
                }

                req.Completed = true;
                CompleteRequest();
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
            if (TryGetInMemoryContent(req.EffectiveUrl, out var inMemory))
            {
                req.InMemoryData = inMemory;
                req.InMemoryOffset = 0;
                req.End = (uint)Math.Min(inMemory.Length, uint.MaxValue);
                req.Initialized = true;
                return;
            }

            var target = ResolveUri(req.EffectiveUrl);
            req.TargetUri = target;

            if (target.IsFile && File.Exists(target.LocalPath))
            {
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

            if (!req.IsPost && TryInitFromCache(req))
            {
                req.Initialized = true;
                return;
            }

            if (!req.IsPost)
            {
                var httpRequest = BuildHttpRequest(req, target);
                var httpResp = await SendRequestAsync(httpRequest, ct);

                req.Source = new StreamSource(
                    await httpResp.Content.ReadAsStreamAsync(ct),
                    httpResp.Content.Headers.ContentLength,
                    httpResp,
                    null
                );
                req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
            }

            if (req.IsPost)
                await ProcessPostAsync(req, ct);
        }
        catch (Exception ex)
        {
            FailRequest(req, "InitRequestAsync", ex);
            req.Done = true;
            req.DoneReason = NPRES_NETWORK_ERR;
            req.Completed = true;
        }
        finally
        {
            req.Initialized = true;
        }
    }

    private static Uri ResolveUri(string url)
    {
        var requestUrl = url ?? string.Empty;
        try
        {
            Logger.Log($"Network.ResolveUri input='{url}', currentDir='{Environment.CurrentDirectory}'");

            if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var abs) &&
                (abs.Scheme == Uri.UriSchemeFile ||
                 abs.Scheme == Uri.UriSchemeHttp ||
                 abs.Scheme == Uri.UriSchemeHttps))
            {
                Logger.Log($"Network.ResolveUri absolute -> '{abs}'");
                return abs;
            }

            if (Path.IsPathRooted(requestUrl))
            {
                var fullPath = Path.GetFullPath(requestUrl);
                var fileUri = new Uri(fullPath);
                Logger.Log($"Network.ResolveUri Windows path -> '{fileUri}'");
                return fileUri;
            }

            var combined = Path.GetFullPath(requestUrl);
            var fallbackUri = new Uri(combined);
            Logger.Log($"Network.ResolveUri fallback -> '{fallbackUri}'");
            return fallbackUri;
        }
        catch (Exception ex)
        {
            Logger.Log($"Network.ResolveUri failed for '{url}': {ex}");
            return new Uri("file:///" + Path.GetFullPath(url));
        }
    }

    private static (byte[] Headers, byte[] Payload) SplitPostBuffer(byte[] postData)
    {
        if (postData.Length == 0)
            return (Array.Empty<byte>(), Array.Empty<byte>());

        for (var i = 0; i + 3 < postData.Length; i++)
            if (postData[i] == '\r' && postData[i + 1] == '\n' &&
                postData[i + 2] == '\r' && postData[i + 3] == '\n')
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
        if (headerBytes.Length == 0) return;

        var headerText = Encoding.Latin1.GetString(headerBytes);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) continue;

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
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
        try
        {
            Logger.Log($"Network.GetRedirectedUrl input='{url}'");
            var requestUrl = url ?? string.Empty;

            if (!App.Args.UseEndpointLoadingScreen)
            {
                Logger.Log("Network.GetRedirectedUrl endpoint loading screen disabled, no redirection applied");
                return requestUrl;
            }


            var endpointHost = App.Args.EndpointHost ?? string.Empty;
            if (string.IsNullOrEmpty(endpointHost))
            {
                Logger.Log("Network.GetRedirectedUrl no endpoint host configured, no redirection applied");
                return requestUrl;
            }

            const string prefix = "assets/img";
            if (requestUrl.StartsWith(prefix, StringComparison.Ordinal))
            {
                Logger.Log(
                    $"Network.GetRedirectedUrl applying redirection for '{requestUrl}' with endpoint '{endpointHost}'");
                var rest = requestUrl.Substring(prefix.Length);
                var redirected = $"https://{endpointHost}/launcher/loading{rest}";
                Logger.Log($"Network.GetRedirectedUrl redirected '{url}' -> '{redirected}'");
                return redirected;
            }

            Logger.Log(
                $"Network.GetRedirectedUrl no matching redirection rule for '{requestUrl}', no redirection applied");
            return requestUrl;
        }
        catch (Exception ex)
        {
            Logger.Log($"Network.GetRedirectedUrl failed for '{url}': {ex}");
            return url ?? string.Empty;
        }
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
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
        catch (Exception ex)
        {
            Logger.Log($"GetFileNameFromUrl failed for '{url}': {ex}");
            return string.Empty;
        }
    }

    public static bool TryGetInMemoryContent(string url, out byte[] content)
    {
        try
        {
            var fileName = Path.GetFileName(url).ToLowerInvariant();

            if (fileName == "logininfo.php")
            {
                var addr = App.Args.ServerAddress ?? string.Empty;
                Logger.Log($"[Network] loginInfo.php -> '{addr}'");
                content = Encoding.ASCII.GetBytes(addr + "\n");
                return true;
            }

            if (fileName == "assetinfo.php")
            {
                // Build the AssetInfo exactly like the native requests.c does
                AssetInfo info = new AssetInfo
                {
                    Name = "",                          // typically empty
                    Url = EnsureTrailingSlash(App.Args.AssetUrl ?? ""),     // loader asset base URL
                    Size = 0,
                    Flags = 0
                };

                int structSize = Marshal.SizeOf<AssetInfo>();
                content = new byte[structSize];

                IntPtr ptr = Marshal.AllocHGlobal(structSize);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    Marshal.Copy(ptr, content, 0, structSize);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                Logger.Log($"[Network] assetInfo.php served as AssetInfo struct: Url='{info.Url}'");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Network] TryGetInMemoryContent failed for url='{url}': {ex}");
        }

        content = Array.Empty<byte>();
        return false;
    }

    private static string EnsureTrailingSlash(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        return url.EndsWith("/") ? url : url + "/";
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

        // ✅ DO NOT reset mainRequested here
        // ✅ DO NOT requeue main automatically
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
        try
        {
            var target = ResolveUri(req.Url);
            (var headers, var payload) = SplitPostBuffer(req.PostData ?? Array.Empty<byte>());

            using var content = new ByteArrayContent(payload);
            ApplyPostHeaders(content, headers);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = content
            };

            var httpResp = await SendRequestAsync(httpRequest, ct);

            req.Source = new StreamSource(
                await httpResp.Content.ReadAsStreamAsync(ct),
                httpResp.Content.Headers.ContentLength,
                httpResp,
                null
            );

            req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
            req.Initialized = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Log($"Network.ProcessPostAsync canceled requestId={req.Id}");
            req.Failed = true;
            req.Done = true;
            req.DoneReason = NPRES_NETWORK_ERR;
        }
        catch (Exception ex)
        {
            FailRequest(req, "ProcessPostAsync", ex);
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
        var resp = await SHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    private static void FailRequest(Request req, string context, Exception? ex = null)
    {
        try
        {
            req.Failed = true;
            req.Done = true;
            req.DoneReason = NPRES_NETWORK_ERR;

            // Only call NewStream if a valid StreamPtr already exists
            if (req.StreamPtr != IntPtr.Zero)
            {
                Plugin_DestroyStream!(nppUnmanagedPtr, req.StreamPtr, NPRES_NETWORK_ERR);
            }

            if (req.DoNotify)
            {
                Plugin_UrlNotify!(nppUnmanagedPtr, req.UrlPtr, NPRES_NETWORK_ERR, req.NotifyData);
            }

            req.Completed = true;
            CompleteRequest();

            // Free memory
            if (req.StreamPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(req.StreamPtr);
                req.StreamPtr = IntPtr.Zero;
            }
        }
        catch (Exception logEx)
        {
            Logger.Log(
                $"[Network] FailRequest logging failed for requestId={req.Id}, url='{req.Url}', context={context}, ex={logEx}");
        }
    }
}