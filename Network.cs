using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        public IntPtr UnmanagedBuffer;
        public IntPtr MimeTypePtr;
        public IntPtr HeadersPtr;
        public uint Current; // tracks bytes already processed
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

    public static void HandleIoProgress(Request req, int bytesRead = 0)
    {
        try
        {
            if (req == null || req.Done) return;

            // Increment processed bytes
            req.Current += (uint)bytesRead;

            // Call ProgressRequestAsync with a token (use None for C-style synchronous flow)
            ProgressRequestAsync(req, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

            // If we have reached the end
            if (req.Current >= req.End && !req.Completed)
            {
                req.Done = true;
                req.DoneReason = NPRES_DONE;
                req.Completed = true;
                CompleteRequest();

                if (req.DoNotify)
                    Plugin_UrlNotify!(nppUnmanagedPtr, req.UrlPtr, req.DoneReason, req.NotifyData);

                Logger.Log($"[Network] Completed request: {req.Url}");
            }
        }
        catch (Exception ex)
        {
            FailRequest(req, nameof(HandleIoProgress), ex);
        }
    }

    public static void InitializeWindow(IntPtr hwnd)
    {
        Logger.Log($"Network.InitializeWindow hwnd=0x{hwnd:x}");
        _sHwnd = hwnd;
        if (SIoMsg == 0)
            SIoMsg = RegisterWindowMessage(IO_MSG_NAME);
        Logger.Log($"Network.InitializeWindow ioMsg=0x{SIoMsg:x}");
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

        var main = App.Args.MainPathOrAddress ?? string.Empty;
        if (!string.IsNullOrEmpty(main))
        {
            _sMainRequested = 1;
            RegisterGetRequest(main, true, IntPtr.Zero);
        }
    }

    public static void Init() => EnsureWorker();

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
        EnsureWorker();
        var cts = _sCts ?? throw new InvalidOperationException("Network request processing is not initialized.");
        Task.Run(() => ProcessRequestAsync(req, cts.Token), cts.Token);
    }

    private static void EnsureWorker()
    {
        if (_sCts != null) return;
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
                Logger.Log($"Network.PostRequestAsync timeout requestId={req.Id}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Network] PostRequestAsync failed for requestId={req.Id}: {ex}");
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

        if (req.HeadersPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(req.HeadersPtr);
            req.HeadersPtr = IntPtr.Zero;
        }

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

    // -------------------
    // ALL THE HELPER METHODS
    // -------------------

    private static async Task ProgressRequestAsync(Request req, CancellationToken ct)
    {
        // Implementation adapted to C# async
        while (!req.Done && !req.Failed)
        {
            if (req.InMemoryData != null)
            {
                int remaining = req.InMemoryData.Length - req.InMemoryOffset;
                if (remaining > 0)
                {
                    int toCopy = Math.Min(req.Buffer.Length, remaining);
                    Buffer.BlockCopy(req.InMemoryData, req.InMemoryOffset, req.Buffer, 0, toCopy);
                    req.InMemoryOffset += toCopy;
                    req.WriteSize = toCopy;
                }
                else
                {
                    req.Done = true;
                    req.DoneReason = NPRES_DONE;
                }
            }
            else if (req.Source?.Stream != null)
            {
                int toRead = Math.Min(REQUEST_BUFFER_SIZE, req.Buffer.Length);
                req.WriteSize = await req.Source.Stream.ReadAsync(req.Buffer.AsMemory(0, toRead), ct);
                if (req.WriteSize == 0)
                {
                    req.Done = true;
                    req.DoneReason = NPRES_DONE;
                }
            }

            int writePtr = 0;
            while (writePtr < req.WriteSize)
            {
                int ready = Plugin_WriteReady!(nppUnmanagedPtr, req.StreamPtr);
                if (ready <= 0) { await Task.Yield(); continue; }

                int chunk = Math.Min(ready, req.WriteSize - writePtr);
                if (req.UnmanagedBuffer == IntPtr.Zero)
                    req.UnmanagedBuffer = Marshal.AllocHGlobal(REQUEST_BUFFER_SIZE);

                Marshal.Copy(req.Buffer, writePtr, req.UnmanagedBuffer, chunk);
                int written = Plugin_Write!(nppUnmanagedPtr, req.StreamPtr, req.BytesWritten, chunk, req.UnmanagedBuffer);

                if (written <= 0)
                {
                    req.Failed = true;
                    req.Done = true;
                    break;
                }

                writePtr += written;
                req.BytesWritten += written;
                req.WritePtr = writePtr;
            }

            if (req.Done || req.Failed)
            {
                Plugin_DestroyStream!(nppUnmanagedPtr, req.StreamPtr, req.DoneReason);
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
    }

    private static async Task InitRequestAsync(Request req, CancellationToken ct)
    {
        // Adapted flow for C# async, keeping C flow
        req.EffectiveUrl = GetRedirectedUrl(req.Url);

        try
        {
            if (TryGetInMemoryContent(req.EffectiveUrl, out var inMemory))
            {
                req.InMemoryData = inMemory;
                req.InMemoryOffset = 0;
                req.End = (uint)Math.Min(inMemory.Length, uint.MaxValue);

                req.UrlPtr = StringToUtf8(req.Url);
                req.MimeTypePtr = StringToUtf8(GetMimeType(req.Url));
                var streamEmu = new NPStream { url = req.UrlPtr, end = req.End, notifyData = req.NotifyData };
                req.StreamPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NPStream>());
                Marshal.StructureToPtr(streamEmu, req.StreamPtr, false);

                int res = Plugin_NewStream!(nppUnmanagedPtr, req.MimeTypePtr, req.StreamPtr, 0, out var stype);
                req.StreamType = stype;

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
            }
            else if (!req.IsPost && TryInitFromCache(req))
            {
                // source set
            }
            else if (!req.IsPost)
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
            else if (req.IsPost)
            {
                await ProcessPostAsync(req, ct);
            }

            if (req.Source != null)
            {
                req.UrlPtr = StringToUtf8(req.Url);
                req.MimeTypePtr = StringToUtf8(GetMimeType(req.Url));
                req.HeadersPtr = !string.IsNullOrEmpty(req.Source.Headers) ? StringToUtf8(req.Source.Headers) : IntPtr.Zero;

                var streamEmu = new NPStream
                {
                    url = req.UrlPtr,
                    end = req.End,
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
                    return;
                }
            }
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
    // --------------------------
    // HELPER METHODS
    // --------------------------

    private static Uri ResolveUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute;

        if (s_baseUri != null)
            return new Uri(s_baseUri, url);

        throw new InvalidOperationException($"Cannot resolve relative URL without base: {url}");
    }

    private static byte[][] SplitPostBuffer(byte[] postData, int chunkSize = 8192)
    {
        int total = postData.Length;
        int chunks = (total + chunkSize - 1) / chunkSize;
        var result = new byte[chunks][];
        for (int i = 0; i < chunks; i++)
        {
            int size = Math.Min(chunkSize, total - (i * chunkSize));
            result[i] = new byte[size];
            Buffer.BlockCopy(postData, i * chunkSize, result[i], 0, size);
        }
        return result;
    }

    private static HttpRequestMessage ApplyPostHeaders(HttpRequestMessage req, Request r)
    {
        if (!string.IsNullOrEmpty(r.ContentType))
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(r.ContentType);
        return req;
    }

    private static HttpClient newHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static string GetMimeType(string url)
    {
        string ext = Path.GetExtension(url).ToLowerInvariant();
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

    private static string GetRedirectedUrl(string url)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Head, url);
            var resp = SHttp.Send(req);
            if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400 && resp.Headers.Location != null)
                return resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location.ToString() : new Uri(new Uri(url), resp.Headers.Location).ToString();
        }
        catch { /* ignore */ }

        return url;
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return Path.GetFileName(uri.LocalPath);
        }
        catch
        {
            return "unnamed";
        }
    }

    private static bool TryGetInMemoryContent(string url, out byte[] content)
    {
        content = Array.Empty<byte>();
        return false; // implement memory caching if needed
    }

    private static string EnsureTrailingSlash(string url)
    {
        if (url.EndsWith("/")) return url;
        return url + "/";
    }

    private static int NextRequestId()
    {
        return Interlocked.Increment(ref _sNextRequestId);
    }

    private static void BeginRequest()
    {
        Interlocked.Increment(ref _sActiveRequests);
    }

    private static void CompleteRequest()
    {
        Interlocked.Decrement(ref _sActiveRequests);
    }

    private static bool TryInitFromCache(Request req)
    {
        return false; // implement caching logic if needed
    }

    private static async Task ProcessPostAsync(Request req, CancellationToken ct)
    {
        if (req.PostData.Length == 0) return;

        var target = req.TargetUri ?? throw new InvalidOperationException("Post request without TargetUri");
        var httpReq = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new ByteArrayContent(req.PostData)
        };

        ApplyPostHeaders(httpReq, req);

        var resp = await SendRequestAsync(httpReq, ct);

        req.Source = new StreamSource(await resp.Content.ReadAsStreamAsync(ct), resp.Content.Headers.ContentLength, resp, null);
        req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
    }

    private static HttpRequestMessage BuildHttpRequest(Request req, Uri target)
    {
        return new HttpRequestMessage(req.IsPost ? HttpMethod.Post : HttpMethod.Get, target);
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

        byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        SRetainedPluginAllocations.Add(ptr);
        return ptr;
    }

}