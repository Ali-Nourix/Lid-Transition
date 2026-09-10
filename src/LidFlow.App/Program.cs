using System;
using System.Globalization;
using System.Runtime;
using System.Threading;
using System.Windows.Forms;
using LidFlow.App.Tray;
using LidFlow.Core.Configuration;
using LidFlow.Core.Diagnostics;

namespace LidFlow.App;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\LidFlow.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        CommandLine options = CommandLine.Parse(args);

        if (options.ShowHelp)
        {
            CommandLine.WriteHelp();
            return 0;
        }

        if (options.ShowVersion)
        {
            MessageBox.Show(
                $"LidFlow {typeof(Program).Assembly.GetName().Version}",
                "LidFlow",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        // Single instance. Two copies would each try to own the overlay and the
        // preview hotkeys, and both would capture each other's output.
        using Mutex mutex = new(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "LidFlow is already running. Look for it in the notification area.",
                "LidFlow",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        AppPaths.EnsureDataDirectory();

        ConfigLoadResult loaded = ConfigStore.Load(AppPaths.EffectiveConfigFile);
        LidFlowConfig config = loaded.Config;

        using AppLog log = CreateLog(config);

        log.Info($"LidFlow {typeof(Program).Assembly.GetName().Version} starting on {Environment.OSVersion}.");
        log.Info($"Configuration: {loaded.Status} ({AppPaths.EffectiveConfigFile}).");

        if (loaded.Status == ConfigLoadStatus.Invalid)
        {
            log.Warn($"Configuration could not be parsed and defaults are in use: {loaded.Message}");
        }

        try
        {
            ApplicationConfiguration.Initialize();

            // The animation is short and must not be interrupted by a blocking
            // collection. Sustained low latency keeps the GC in background mode for
            // the life of the process, which costs a little throughput we do not
            // need and buys predictable frame times.
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => log.Error("Unhandled UI exception.", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                log.Error("Unhandled exception.", e.ExceptionObject as Exception);

            using TrayApplicationContext context = new(config, log, options.ForceDebugHud);

            if (options.PreviewClose || options.PreviewOpen)
            {
                // Run the requested preview once the message loop is pumping, so it
                // goes through exactly the same path a real lid event would.
                SynchronizationContext.Current?.Post(_ => context.RunPreview(options.PreviewClose), null);
            }

            Application.Run(context);
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("LidFlow terminated with an unhandled exception.", ex);
            return 1;
        }
    }

    private static AppLog CreateLog(LidFlowConfig config)
    {
        if (!config.Diagnostics.EnableFileLogging)
        {
            return new AppLog(NullLog.Instance);
        }

        LogLevel minimum = config.Diagnostics.VerboseFrameLogging ? LogLevel.Debug : LogLevel.Info;
        return new AppLog(new RollingFileLog(AppPaths.LogFile, minimum));
    }

    /// <summary>
    /// Wraps whichever sink was chosen so the entry point can own it with a single
    /// <c>using</c>, whether or not the underlying sink holds resources.
    /// </summary>
    private sealed class AppLog : ILidFlowLog, IDisposable
    {
        private readonly ILidFlowLog _inner;

        public AppLog(ILidFlowLog inner)
        {
            _inner = inner;
        }

        public void Log(LogLevel level, string message, Exception? exception = null) =>
            _inner.Log(level, message, exception);

        public void Dispose() => (_inner as IDisposable)?.Dispose();
    }

    /// <summary>Parsed command line.</summary>
    private readonly struct CommandLine
    {
        public bool PreviewClose { get; init; }

        public bool PreviewOpen { get; init; }

        public bool ForceDebugHud { get; init; }

        public bool ShowHelp { get; init; }

        public bool ShowVersion { get; init; }

        public static CommandLine Parse(string[] args)
        {
            bool previewClose = false;
            bool previewOpen = false;
            bool debug = false;
            bool help = false;
            bool version = false;

            foreach (string argument in args ?? Array.Empty<string>())
            {
                switch (argument.Trim().ToLowerInvariant())
                {
                    case "--preview-close":
                    case "-c":
                        previewClose = true;
                        break;

                    case "--preview-open":
                    case "-o":
                        previewOpen = true;
                        break;

                    case "--debug":
                    case "-d":
                        debug = true;
                        break;

                    case "--help":
                    case "-h":
                    case "/?":
                        help = true;
                        break;

                    case "--version":
                    case "-v":
                        version = true;
                        break;
                }
            }

            return new CommandLine
            {
                // Close wins if both are given: previewing a close is the more
                // common case and running both at once is meaningless.
                PreviewClose = previewClose,
                PreviewOpen = previewOpen && !previewClose,
                ForceDebugHud = debug,
                ShowHelp = help,
                ShowVersion = version,
            };
        }

        public static void WriteHelp()
        {
            string text = string.Join(
                Environment.NewLine,
                "LidFlow - MacBook-style lid transition for Windows",
                string.Empty,
                "Usage: LidFlow.exe [options]",
                string.Empty,
                "  --preview-close, -c   Run the closing transition once at start-up.",
                "  --preview-open,  -o   Run the opening transition once at start-up.",
                "  --debug,         -d   Show the developer overlay.",
                "  --version,       -v   Show the version.",
                "  --help,          -h   Show this message.",
                string.Empty,
                "With no options LidFlow runs in the notification area and reacts to the",
                "physical lid switch.",
                string.Empty,
                "Preview hotkeys while running:",
                "  Ctrl+Alt+Shift+C      Preview the closing transition.",
                "  Ctrl+Alt+Shift+O      Preview the opening transition.",
                string.Empty,
                $"Configuration: {AppPaths.ConfigFile}",
                $"Logs:          {AppPaths.LogDirectory}");

            MessageBox.Show(text, "LidFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
