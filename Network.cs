using SharpGen.Runtime.Win32;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static ffrunner.NPAPIStubs;
using static ffrunner.PluginBootstrap;
using static ffrunner.Structs;

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
        public int WriteSize;
        public int WritePtr;
        public int BytesWritten;
        public uint End;
        public byte[] Buffer = new byte[REQUEST_BUFFER_SIZE];
        public readonly AutoResetEvent ReadyEvent = new(false);
        public IntPtr StreamPtr;
        public IntPtr UrlPtr;
        public IntPtr MimeTypePtr;
        public IntPtr UnmanagedBuffer;

        public ushort StreamType;
        // Track processed bytes
        public uint Current;

        public GCHandle? Handle;
        public uint PostDataLength { get; set; }
        public bool Done;
        public bool Failed;
        public bool Completed;
        public short DoneReason = NPRES_DONE;
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

    public static void RegisterPostRequest(
        [MarshalAs(UnmanagedType.LPStr)] string url,
        [MarshalAs(UnmanagedType.I1)] bool doNotify,
        IntPtr notifyData,
        [MarshalAs(UnmanagedType.LPStr)] string postData,
        uint postDataLen)
    {
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

    public static void InitNetwork(string mainSrcUrl)
    {
        Logger.Log($"Network.InitNetwork main: {mainSrcUrl}");
        Init();
        InMemorySources.Init();

        var main = App.Args.MainPathOrAddress ?? string.Empty;
        if (!string.IsNullOrEmpty(main))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                RegisterGetRequest(main, true, IntPtr.Zero);
            });
        }
    }

    public static void Init() => EnsureWorker();

    public static void Shutdown()
    {
        var cts = Interlocked.Exchange(ref _sCts, null);
        cts?.Cancel();
        _queueSignal.Set();

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
                ProcessRequestAsync(req, ct);
            else
                _queueSignal.WaitOne(500);
        }
    }


    private static void ProcessPostAsync(Request req, CancellationToken ct)
    {
        if (req.PostData.Length == 0) return;

        var target = req.TargetUri ?? new Uri(req.Url, UriKind.RelativeOrAbsolute);

        var httpReq = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new ByteArrayContent(req.PostData, 0, (int)req.PostDataLength)
        };

        if (!string.IsNullOrEmpty(req.ContentType))
            httpReq.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(req.ContentType);

        var resp = SendRequestAsync(httpReq, ct).WaitAsync(TimeSpan.FromMilliseconds(20000),ct).Result;

        req.Source = new StreamSource(
            resp.Content.ReadAsStreamAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(20000), ct).Result,
            resp.Content.Headers.ContentLength,
            resp,
            null
        );

        // ✅ FIX: use RESPONSE length, not request length
        req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
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

    private static void ProgressRequestAsync(Request req, CancellationToken ct)
    {
        if (req.WritePtr < req.WriteSize) return; // wait until previous chunk is written

        req.WritePtr = 0;
        req.WriteSize = 0;

        try
        {
            if (req.Source != null && !req.Done)
            {
                int read = req.Source.Stream
                    .ReadAsync(req.Buffer.AsMemory(0, REQUEST_BUFFER_SIZE), ct)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();


                if (read == 0)
                {
                    req.Done = true;
                    req.DoneReason = NPRES_DONE;
                    return;
                }

                req.WriteSize = read;
                req.BytesWritten = read;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ProgressRequestAsync failed for {req.Url}: {ex}");
            req.Failed = true;
            req.Done = true;
            req.DoneReason = NPRES_NETWORK_ERR;
        }
    }

    private static void CleanupRequestAsync(Request req)
    {
        if (req.UnmanagedBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(req.UnmanagedBuffer);
            req.UnmanagedBuffer = IntPtr.Zero;
        }

        if (req.UrlPtr != IntPtr.Zero) { Marshal.FreeHGlobal(req.UrlPtr); req.UrlPtr = IntPtr.Zero; }
        if (req.Handle?.IsAllocated == true) req.Handle.Value.Free();

        if (req.MimeTypePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(req.MimeTypePtr);
            req.MimeTypePtr = IntPtr.Zero;
        }

        req.StreamPtr = IntPtr.Zero;
        req.ReadyEvent.Dispose();
    }

    // --------------------------
    // HELPERS
    // --------------------------
    private static HttpClient newHttpClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, true);

    private static string GetRedirected(string url)
    {
        if (App.Args.UseEndpointLoadingScreen && url.StartsWith("assets/img", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url.Substring("assets/img".Length);
            return $"https://{App.Args.EndpointHost}/launcher/loading{rest}";
        }
        return url;
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await SHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex)
        {
            Logger.Log($"[Network] SendAsync failed: {ex}");
            throw;
        }
    }

    private static void FailRequest(Request req, string where, Exception ex)
    {
        Logger.Log($"[Network] FailRequest in {where} for URL {req.Url}: {ex}");
        req.Failed = true;
        req.Done = true;
        req.DoneReason = NPRES_NETWORK_ERR;
    }

    private static void SetupSpecialFile(Request req, string fileName, string data)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ffrunner");
            Directory.CreateDirectory(tempDir);

            string tempFile = Path.Combine(tempDir, fileName);
            File.WriteAllText(tempFile, data, Encoding.UTF8);

            var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            req.Source = new StreamSource(fs, fs.Length, null, null);
            req.End = (uint)Math.Min(fs.Length, uint.MaxValue);
            req.Current = 0;

            req.UrlPtr = StringToUtf8(req.Url);
            req.MimeTypePtr = StringToUtf8("application/x-www-form-urlencoded");
            if (req.Handle is not { IsAllocated: true })
                req.Handle = GCHandle.Alloc(req, GCHandleType.Normal);

            IntPtr requestPtr = GCHandle.ToIntPtr(req.Handle.Value);

            var streamEmu = new NPStream
            {
                pdata = requestPtr,
                ndata = IntPtr.Zero,
                url = req.UrlPtr,
                end = req.End,
                lastmodified = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                notifyData = req.DoNotify ? req.NotifyData : IntPtr.Zero,
                headers = IntPtr.Zero
            };

            int size = Marshal.SizeOf<NPStream>();
            req.StreamPtr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(streamEmu, req.StreamPtr, false);

            App.RunOnUI(() =>
            {
                short res = Plugin_NewStream!(
                    nppUnmanagedPtr,
                    req.MimeTypePtr,
                    req.StreamPtr,
                    true,
                    out ushort streamType
                );

                if (res != 0)
                {
                    req.Failed = true;
                    req.Done = true;
                    req.DoneReason = NPRES_NETWORK_ERR;
                }
                else
                {
                    req.StreamType = streamType;
                }

                req.ReadyEvent.Set();
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"SetupSpecialFile failed for {fileName}: {ex}");
            req.Failed = true;
            req.Done = true;
            req.DoneReason = NPRES_NETWORK_ERR;
        }
    }


    [DllImport("wininet.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool RetrieveUrlCacheEntryFile(
        string lpszUrlName,
        IntPtr lpCacheEntryInfo,
        ref int lpdwCacheEntryInfoBufferSize,
        int dwReserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool UnlockUrlCacheEntryFile(string lpszUrlName, int dwReserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct INTERNET_CACHE_ENTRY_INFOA
    {
        public uint dwStructSize;
        public IntPtr lpszSourceUrlName;
        public IntPtr lpszLocalFileName;
        public uint CacheEntryType;
        public uint dwUseCount;
        public uint dwHitRate;
        public uint dwSizeLow;
        public uint dwSizeHigh;
        public FILETIME LastModifiedTime;
        // … add other fields if needed
    }

    public static bool TryInitFromCache(Network.Request req)
    {
        int bufferSize = Marshal.SizeOf<INTERNET_CACHE_ENTRY_INFOA>() + 260 + 260;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            if (!RetrieveUrlCacheEntryFile(req.Url, buffer, ref bufferSize, 0))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 0x7A) // ERROR_INSUFFICIENT_BUFFER
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal(bufferSize);
                    if (!RetrieveUrlCacheEntryFile(req.Url, buffer, ref bufferSize, 0))
                        return false;
                }
                else if (err == 2) // ERROR_FILE_NOT_FOUND
                {
                    return false;
                }
                else
                {
                    Logger.Log($"RetrieveUrlCacheEntryFile returned unexpected err {err}");
                    return false;
                }
            }

            // Marshal the struct
            var cacheData = Marshal.PtrToStructure<INTERNET_CACHE_ENTRY_INFOA>(buffer);
            string localFile = Marshal.PtrToStringAnsi(cacheData.lpszLocalFileName) ?? string.Empty;

            if (string.IsNullOrEmpty(localFile) || !File.Exists(localFile))
                return false;

            // Open the cached file
            var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            req.Source = new Network.StreamSource(fs, fs.Length, null, null);
            req.End = (uint)Math.Min(fs.Length, uint.MaxValue);
            req.Current = 0;

            Logger.Log($"Serving {req.Url} from cache: {localFile}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"TryInitFromCache failed for {req.Url}: {ex}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            UnlockUrlCacheEntryFile(req.Url, 0);
        }
    }

    private static void InitRequestAsync(Request req, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(req.Url))
            {
                FailRequest(req, nameof(InitRequestAsync), new Exception("Missing URL"));
                return;
            }

            req.Url = GetRedirected(req.Url);
            Uri target = ResolveUri(req.Url);
            req.TargetUri = target;

            // Special-case loginInfo.php and assetInfo.php
            if (req.Url.EndsWith("loginInfo.php", StringComparison.OrdinalIgnoreCase))
            {
                string data = App.Args.ServerAddress ?? string.Empty;
                SetupSpecialFile(req, "loginInfo.php", data);
                return;
            }
            if (req.Url.EndsWith("assetInfo.php", StringComparison.OrdinalIgnoreCase))
            {
                string data = App.Args.AssetUrl ?? string.Empty;
                SetupSpecialFile(req, "assetInfo.php", data);
                return;
            }

            // Try cache before file/network
            if (TryInitFromCache(req))
            {
                BuildNpStream(req, GetMimeType(req.Url), isFile: true);
                return;
            }

            // If local file requested but not found → cancel
            if (target.IsFile && !File.Exists(target.LocalPath))
            {
                req.Failed = true;
                req.Done = true;
                req.DoneReason = NPRES_NETWORK_ERR;
                if (_sHwnd != IntPtr.Zero && SIoMsg != 0)
                    PostMessage(_sHwnd, SIoMsg, IntPtr.Zero, (IntPtr)req.Id);
                return;
            }

            // File or network path
            if (target.IsFile)
            {
                var fs = new FileStream(target.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                req.Source = new StreamSource(fs, fs.Length, null, null);
                req.End = (uint)Math.Min(fs.Length, uint.MaxValue);
            }
            else
            {
                var httpReq = new HttpRequestMessage(req.IsPost ? HttpMethod.Post : HttpMethod.Get, target);
                var httpResp = SendRequestAsync(httpReq, ct).WaitAsync(TimeSpan.FromMilliseconds(20000), ct).Result;

                // Build header block from response
                string headerBlock = $"HTTP/{httpResp.Version.Major}.{httpResp.Version.Minor} {(int)httpResp.StatusCode} {httpResp.ReasonPhrase}\n";
                foreach (var h in httpResp.Headers)
                    headerBlock += $"{h.Key}: {string.Join(",", h.Value)}\n";
                foreach (var h in httpResp.Content.Headers)
                    headerBlock += $"{h.Key}: {string.Join(",", h.Value)}\n";
                headerBlock += "\n";

                req.Source = new StreamSource(
                    httpResp.Content.ReadAsStreamAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(20000), ct).Result,
                    httpResp.Content.Headers.ContentLength,
                    httpResp,
                    headerBlock
                );
                req.End = (uint)Math.Min(req.Source.Length ?? 0, uint.MaxValue);
            }

            if (req.Source != null)
                BuildNpStream(req, GetMimeType(req.Url), target.IsFile);
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

    private static void BuildNpStream(Request req, string mimeType, bool isFile)
    {

            req.UrlPtr = StringToUtf8(req.Url);
            req.MimeTypePtr = StringToUtf8(mimeType);
            if (req.Handle is not { IsAllocated: true })
                req.Handle = GCHandle.Alloc(req, GCHandleType.Normal);

            IntPtr requestPtr = GCHandle.ToIntPtr(req.Handle.Value);

            // Use headers from response if available
            string headerBlock = req.Source?.Headers ??
                                 "HTTP/1.1 200 OK\nContent-Type: " + mimeType + "\n\n";

            var streamEmu = new NPStream
            {
                pdata = requestPtr,
                ndata = IntPtr.Zero,
                url = req.UrlPtr,
                end = req.End,
                lastmodified = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                notifyData = req.DoNotify ? req.NotifyData : IntPtr.Zero,
                headers = StringToUtf8(headerBlock)
            };

            int size = Marshal.SizeOf<NPStream>();
            req.StreamPtr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(streamEmu, req.StreamPtr, false);


            short res = Plugin_NewStream!(
                nppUnmanagedPtr,
                req.MimeTypePtr,
                req.StreamPtr,
                isFile,
                out ushort streamType
            );

            if (res != 0)
            {
                req.Failed = true;
                req.Done = true;
                req.DoneReason = NPRES_NETWORK_ERR;
            }
            else
            {
                req.StreamType = streamType;
            }
    }


    private static Uri ResolveUri(string url)
    {
        Logger.Log($"Network.ResolveUri input='{url}', currentDir='{Environment.CurrentDirectory}'");

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            Logger.Log($"Network.ResolveUri absolute -> '{abs}'");
            return abs;
        }

        // Convert local file path to file:/// URI
        string fullPath = Path.GetFullPath(url, Environment.CurrentDirectory);
        var uri = new UriBuilder
        {
            Scheme = Uri.UriSchemeFile,
            Host = "",
            Path = fullPath.Replace('\\', '/')
        }.Uri;

        Logger.Log($"Network.ResolveUri local -> '{uri}'");
        return uri;
    }

    public static void HandleIOProgress(Request req)
    {
        App.RunOnUI(() =>
        {
            if (Plugin_WriteReady == null || Plugin_Write == null || req.StreamPtr == IntPtr.Zero)
                return;

            int ready = Plugin_WriteReady!(nppUnmanagedPtr, req.StreamPtr);
            if (ready <= 0) return;

            int chunk = Math.Min(ready, req.WriteSize - req.WritePtr);
            if (chunk <= 0) return;

            if (req.UnmanagedBuffer == IntPtr.Zero)
                req.UnmanagedBuffer = Marshal.AllocHGlobal(REQUEST_BUFFER_SIZE);

            Marshal.Copy(req.Buffer, req.WritePtr, req.UnmanagedBuffer, chunk);

            Plugin_Write!(
                nppUnmanagedPtr,
                req.StreamPtr,
                (int)req.Current,
                chunk,
                req.UnmanagedBuffer
            );

            req.WritePtr += chunk;
            req.Current += (uint)chunk;

            if (req.WritePtr >= req.WriteSize && req.Current >= req.End)
                req.Done = true;

            req.ReadyEvent.Set();
        });
    }

    private static void ProcessRequestAsync(Request req, CancellationToken ct)
    {
        try
        {
            if (!req.Initialized)
                InitRequestAsync(req, ct);

            while (!req.Done && !req.Failed && !ct.IsCancellationRequested)
            {
                if (req.IsPost)
                    ProcessPostAsync(req, ct);
                else
                    ProgressRequestAsync(req, ct);

                if (_sHwnd != IntPtr.Zero && SIoMsg != 0 && req.Handle is { IsAllocated: true })
                {
                    IntPtr handlePtr = GCHandle.ToIntPtr(req.Handle.Value);
                    PostMessage(_sHwnd, SIoMsg, IntPtr.Zero, handlePtr);
                }

                req.ReadyEvent.WaitOne(20000);
            }

            if (req.StreamPtr != IntPtr.Zero)
            {
                App.RunOnUI(() =>
                {
                    Plugin_DestroyStream!(
                        nppUnmanagedPtr,
                        req.StreamPtr,
                        req.DoneReason
                    );
                });
            }

            CleanupRequestAsync(req);
            CompleteRequest();
        }
        catch (Exception ex)
        {
            FailRequest(req, nameof(ProcessRequestAsync), ex);

            if (req.StreamPtr != IntPtr.Zero)
            {
                App.RunOnUI(() =>
                {
                    Plugin_DestroyStream!(
                        nppUnmanagedPtr,
                        req.StreamPtr,
                        NPRES_NETWORK_ERR
                    );
                });
            }

            CleanupRequestAsync(req);
            CompleteRequest();
        }
    }

    private static IntPtr StringToUtf8(string s)
    {
        if (string.IsNullOrEmpty(s)) return IntPtr.Zero;

        byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);

        // Keep alive until Shutdown
        lock (SRetainedPluginAllocations)
        {
            SRetainedPluginAllocations.Add(ptr);
        }

        return ptr;
    }


    private static int NextRequestId() => Interlocked.Increment(ref _sNextRequestId);
    private static void BeginRequest() => Interlocked.Increment(ref _sActiveRequests);
    private static void CompleteRequest() => Interlocked.Decrement(ref _sActiveRequests);
}