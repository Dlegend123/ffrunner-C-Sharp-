using System.IO;
using System.Text;

namespace ffrunner
{
    public static class Logger
    {
        private static readonly object logLock = new();
        private static StreamWriter? _logFileWriter;
        private static bool _verbose = false;
        private static bool _writeConsole = true;
        private static bool _initialized = false;

        public static void Init(string logPath, bool verboseLogging)
        {
            lock (logLock)
            {
                _verbose = verboseLogging;
                // Console output is extremely slow under the debugger.
                // Keep it only for verbose sessions; always log to file when configured.
                _writeConsole = verboseLogging;
                if (!string.IsNullOrEmpty(logPath))
                {
                    _logFileWriter?.Close(); // make sure old handle is released
                    _logFileWriter = new StreamWriter(
                        new FileStream(logPath,
                                       FileMode.Append,        // append to avoid truncating
                                       FileAccess.Write,
                                       FileShare.ReadWrite),   // allow others to read/write
                        Encoding.UTF8)
                    {
                        AutoFlush = true,
                        NewLine = "\n"
                    };
                }
                _initialized = true;
            }
        }

        public static void Verbose(string fmt, params object[] args)
        {
            if (!_initialized) throw new InvalidOperationException("Logger not initialized");
            if (!_verbose) return;

            var msg = $"{DateTime.Now:HH:mm:ss.fff} [VERBOSE] {string.Format(fmt, args)}\n";
            lock (logLock)
            {
                if (_writeConsole) Console.Write(msg);
                _logFileWriter?.Write(msg);
            }
        }

        public static void Log(string fmt, params object[] args)
        {
            if (!_initialized) throw new InvalidOperationException("Logger not initialized");

            var msg = $"{DateTime.Now:HH:mm:ss.fff} [LOG] {string.Format(fmt, args)}\n";
            lock (logLock)
            {
                if (_writeConsole) Console.Write(msg);
                _logFileWriter?.Write(msg);
            }
        }

        public static void Close()
        {
            lock (logLock)
            {
                _logFileWriter?.Flush();
                _logFileWriter?.Close();
                _logFileWriter = null;
                _initialized = false;
            }
        }
    }
}