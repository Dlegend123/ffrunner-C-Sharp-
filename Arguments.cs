using ffrunner;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using static ffrunner.Structs;

namespace ffrunner
{
    public static class Defaults
    {
        public const string FALLBACK_SRC_URL =
            "http://cdn.dexlabs.systems/ff/big/beta-20100104/main.unity3d";

        public const string FALLBACK_ASSET_URL =
            "http://cdn.dexlabs.systems/ff/big/beta-20100104/";

        public const string FALLBACK_SERVER_ADDRESS =
            "127.0.0.1:23000";

        public const string LOG_FILE_PATH = "ffrunner.log";

        public const int DEFAULT_WIDTH = 1280;
        public const int DEFAULT_HEIGHT = 720;
    }

    public class Arguments
    {
        public bool VerboseLogging { get; set; } = false;
        public string MainPathOrAddress { get; set; }
        public string LogPath { get; set; }
        public string ServerAddress { get; set; }
        public string AssetUrl { get; set; }
        public string EndpointHost { get; set; }
        public string TegId { get; set; }
        public string AuthId { get; set; }
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 720;
        public bool Fullscreen { get; set; } = false;
        public bool UseEndpointLoadingScreen { get; set; } = false;
        public bool ForceVulkan { get; set; } = false;
        public bool ForceOpenGl { get; set; } = false;
    }
}
