using System;
using System.Windows;
using PptPoc.Core.Configuration;
using PptPoc.Orchestration;
using PptPoc.PowerPoint;
using PptPoc.Audio;
using PptPoc.Asr;
using PptPoc.Matching;
using PptPoc.Vision;
using Serilog;
using System.Windows.Forms;
using Application = System.Windows.Application;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Text;
using PptPoc.Core.Interfaces;
using System.IO;

using Microsoft.Extensions.Configuration;
using System.Threading;

namespace PptPoc.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private NotifyIcon? _notifyIcon;
    private Orchestrator? _orchestrator;
    private KnowledgeBasePreprocessor? _kbPreprocessor;
    private IPowerPointService? _pptService;

    // ── API Preflight ─────────────────────────────────────────────────────────
    // Stored so the "Start Engine" tray handler can call PingAsync() before
    // PreprocessAsync(). If the ping fails, the start is aborted with a clear
    // user-facing message rather than a silent KB degradation (all slides fail
    // → zero matching for the entire session).
    private IOpenAIVisionService? _visionService;

    private ToolStripMenuItem? _startMenuItem;
    private ToolStripMenuItem? _stopMenuItem;

    // ── Refresh KB menu item ──────────────────────────────────────────────────
    // Allows the user to force a KB rebuild at any time without restarting the
    // app. This is the manual override for the case where IsYamlStale() could
    // not detect staleness automatically (e.g. SharePoint / COM-title-only path).
    // It also acts as the one-click fix if a presenter edits slides right before
    // the talk and wants the KB to reflect the final version.
    private ToolStripMenuItem? _refreshMenuItem;

    private StatusIndicatorWindow? _statusIndicator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            SetupGlobalExceptionHandlers();

            _mutex = new Mutex(true, "PptPocEngine_Unique_Mutex", out bool createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show("The Engine is already running in your System Tray!", "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);

            if (!ValidateRuntimePackagingPreconditions())
            {
                Current.Shutdown();
                return;
            }

            // Inject Proxy globally for the current process
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://proxy-iind.intel.com:911", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://proxy-iind.intel.com:911", EnvironmentVariableTarget.Process);

            var config = AppConfigLoader.Load();

            ConfigureLogging(config);

            Log.Information("System Tray POC starting from {BaseDirectory}", AppContext.BaseDirectory);

            InitializeNotifyIcon();
            
            // Ensure GNAI Token exists or prompt now.
            EnsureTokenExists();

            var splash = new SplashWindow();
            splash.Show();

            _statusIndicator = new StatusIndicatorWindow(config.LaserToggleHotkey);
            _statusIndicator.Show();
            _statusIndicator.UpdateStatus("Paused");

            await InitializeEngineAndStart(config, splash);
            
            splash.Close();
            
            // Wait for the UI layout to settle after the splash window closes
            await Task.Delay(500);
            _notifyIcon?.ShowBalloonTip(3000, "PPT Helper", "Running silently in background. Right-click the tray icon for options.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            TryLogFatal(ex, "Startup failed before engine initialization completed.");
            System.Windows.MessageBox.Show(
                "Application failed during startup.\n\n" + ex.Message,
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    private static void ConfigureLogging(AppConfig config)
    {
        var primaryLogPath = ResolveLogFilePath(config.LogFilePath);

        try
        {
            var primaryDir = Path.GetDirectoryName(primaryLogPath);
            if (!string.IsNullOrWhiteSpace(primaryDir))
                Directory.CreateDirectory(primaryDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(primaryLogPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();
            return;
        }
        catch
        {
            // Fall through to local app data fallback.
        }

        var fallbackDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PptPoc",
            "logs");
        Directory.CreateDirectory(fallbackDir);
        var fallbackLogPath = Path.Combine(fallbackDir, "pptpoc-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(fallbackLogPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    private static string ResolveLogFilePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Path.Combine(AppContext.BaseDirectory, "logs", "pptpoc-.log");

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    private static bool ValidateRuntimePackagingPreconditions()
    {
        try
        {
            var probeFile = Path.Combine(Path.GetTempPath(), $"pptpoc_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Failed to access the TEMP directory required by the packaged app.\n\n" +
                "Please ensure TEMP/TMP is writable and try again.\n\n" +
                ex.Message,
                "Packaging Runtime Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private static void SetupGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                TryLogFatal(ex, "Unhandled AppDomain exception.");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryLogFatal(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private static void TryLogFatal(Exception ex, string message)
    {
        try
        {
            Log.Fatal(ex, message);
            Log.CloseAndFlush();
        }
        catch
        {
            // Last-resort path: avoid crashing inside exception logging.
        }
    }

    private void EnsureTokenExists()
    {
        var token = Environment.GetEnvironmentVariable("GNAI_TOKEN") ?? Environment.GetEnvironmentVariable("GNAI_TOKEN", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(token))
        {
            PromptForToken("Set GNAI API Key");
        }
        else
        {
            Environment.SetEnvironmentVariable("GNAI_TOKEN", token, EnvironmentVariableTarget.Process);
        }
    }

    private bool PromptForToken(string title)
    {
        var dialog = new TokenInputDialog();
        dialog.Title = title;

        var owner = Current?.MainWindow;
        if (CanAssignDialogOwner(owner, dialog))
        {
            dialog.Owner = owner!;
        }

        dialog.ShowDialog();

        if (dialog.DialogResult == true && !string.IsNullOrWhiteSpace(dialog.ApiKey))
        {
            ApplyToken(dialog.ApiKey);
            return true;
        }

        return false;
    }

    private static bool CanAssignDialogOwner(Window? owner, Window dialog)
    {
        return owner != null && !ReferenceEquals(owner, dialog);
    }

    private async Task<bool> PromptForTokenAndRetryAsync(Func<Task<bool>> pingCheck, string title)
    {
        if (!PromptForToken(title))
            return false;

        return await pingCheck();
    }

    private static void ApplyToken(string token)
    {
        Environment.SetEnvironmentVariable("GNAI_TOKEN", token, EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable("GNAI_TOKEN", token, EnvironmentVariableTarget.User);
        }
        catch
        {
            // Ignore permission issues when writing the user environment store.
        }
    }

    private static void ClearProcessToken()
    {
        Environment.SetEnvironmentVariable("GNAI_TOKEN", null, EnvironmentVariableTarget.Process);
    }

    private void InitializeNotifyIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = CreatePocIcon(), 
            Visible = true,
            Text = "PPT Highlighting Engine (Starting)"
        };
        
        var contextMenu = new ContextMenuStrip();
        
        var updateTokenItem = CreateMenuItem("Update GNAI Token", (s, e) =>
        {
            PromptForToken("Update GNAI API Key");
        });
        contextMenu.Items.Add(updateTokenItem);

        contextMenu.Items.Add(new ToolStripSeparator());
        
        _startMenuItem = CreateMenuItem("Start Engine", async (s, e) => 
        { 
            try 
            {
                if (_pptService != null && _kbPreprocessor != null && _visionService != null)
                {
                    // ── Step 1: API Preflight Ping ────────────────────────────────────
                    // Before spending time preprocessing all slides (which calls the Vision
                    // API once per slide), verify the endpoint is reachable and the token
                    // is valid. A 1-token text call costs ~0 and completes in <2s.
                    // If it fails we surface a specific error immediately instead of
                    // letting the user wait through preprocessing only to get zero results.
                    _notifyIcon.Text = "PPT Highlighting Engine (Checking API...)";
                    _statusIndicator?.UpdateStatus("Checking API...");
                    Log.Information("API preflight ping starting...");

                    bool apiOk = await _visionService.PingAsync();
                    if (!apiOk)
                    {
                        var currentToken = Environment.GetEnvironmentVariable("GNAI_TOKEN") ?? string.Empty;
                        ClearProcessToken();
                        Log.Warning("API preflight failed. Cleared process token before prompting for a replacement.");

                        string detail = string.IsNullOrWhiteSpace(currentToken)
                            ? "GNAI_TOKEN is not set.\n\nEnter a new key when prompted."
                            : "The stored token appears invalid or expired.\n\nEnter a new key when prompted.";

                        if (await PromptForTokenAndRetryAsync(() => _visionService.PingAsync(), "Update GNAI API Key"))
                        {
                            apiOk = true;
                        }
                        else
                        {
                            Log.Error("API preflight ping failed — aborting engine start.");
                            System.Windows.MessageBox.Show(
                                $"API connectivity check failed — engine not started.\n\n{detail}",
                                "API Not Reachable",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);

                            UpdateMenuState(false);
                            _statusIndicator?.UpdateStatus("Paused");
                            _notifyIcon.Text = "PPT Highlighting Engine (Stopped)";
                            return;
                        }
                    }

                    Log.Information("API preflight ping passed — proceeding to knowledge base build.");

                    // ── Step 2: Attach to PowerPoint ──────────────────────────────────
                    _notifyIcon.Text = "PPT Highlighting Engine (Analyzing Slides...)";
                    _statusIndicator?.UpdateStatus("Building KB");
                    Log.Information("Manual start triggered. Analyzing slides into YAML...");

                    if (!_pptService.TryAttach())
                    {
                        throw new Exception("Could not attach to a running PowerPoint instance.");
                    }

                    // ── Step 3: Preprocess slides into KB ─────────────────────────────
                    // KnowledgeBasePreprocessor.PreprocessAsync now performs a staleness
                    // check automatically:
                    //   • YAML missing             → build from scratch
                    //   • YAML up to date          → return instantly (no API calls)
                    //   • YAML stale (PPT edited)  → delete old YAML and rebuild
                    await _kbPreprocessor.PreprocessAsync(_pptService, "knowledge_base.yaml");
                }
                
                // ── Step 4: Start the processing loop ─────────────────────────────────
                if (_orchestrator != null) 
                {
                    await _orchestrator.StartAsync(); 
                    UpdateMenuState(true);
                    _statusIndicator?.Dispatcher.Invoke(() => _statusIndicator.Show());
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start or analyze knowledge base.");
                System.Windows.MessageBox.Show("Make sure PowerPoint is open before starting.\n\nError: " + ex.Message, "Start Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateMenuState(false);
            }
        });
        
        _stopMenuItem = CreateMenuItem("Stop Engine", async (s, e) => 
        { 
            if (_orchestrator != null) 
            {
                await _orchestrator.StopAsync(); 
                UpdateMenuState(false);
                _statusIndicator?.Dispatcher.Invoke(() => _statusIndicator.Hide());
            }
        });

        // ── Refresh Knowledge Base ────────────────────────────────────────────
        // Deletes the cached YAML for the currently open deck and rebuilds it
        // from scratch. Use this when:
        //   • Slides were edited and the auto-staleness check couldn't detect it
        //     (SharePoint / COM-title-only path where file-time comparison is
        //     unavailable)
        //   • You want to force a clean rebuild regardless of file times
        //
        // If the engine is currently running, it is stopped first, the KB is
        // rebuilt, and then the engine is automatically restarted — so the user
        // gets a seamless "refresh and keep going" experience.
        _refreshMenuItem = CreateMenuItem("Refresh Knowledge Base", async (s, e) =>
        {
            try
            {
                if (_pptService == null || _kbPreprocessor == null || _visionService == null || _orchestrator == null)
                    return;

                bool wasRunning = _orchestrator.IsRunning;

                // ── Step 1: Stop engine if it is currently running ─────────────
                if (wasRunning)
                {
                    Log.Information("Refresh KB: stopping engine before rebuild...");
                    await _orchestrator.StopAsync();
                    UpdateMenuState(false);
                }

                // ── Step 2: Attach to PowerPoint (needed to read the PPT path) ─
                if (!_pptService.TryAttach())
                {
                    System.Windows.MessageBox.Show(
                        "Could not attach to a running PowerPoint instance.\n\nMake sure PowerPoint is open before refreshing.",
                        "Refresh Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    if (wasRunning) UpdateMenuState(false);
                    return;
                }

                // ── Step 3: Delete the existing YAML so PreprocessAsync rebuilds ─
                // GetActivePresentationPath gives the real on-disk path;
                // GetYamlPath normalises it to the canonical YAML filename.
                string? pptPath = _pptService.GetActivePresentationPath();
                if (pptPath != null)
                {
                    string yamlPath = KbPathHelper.GetYamlPath(pptPath);
                    if (System.IO.File.Exists(yamlPath))
                    {
                        System.IO.File.Delete(yamlPath);
                        Log.Information("Refresh KB: deleted stale YAML at {Path}", yamlPath);
                    }
                }

                // ── Step 4: API preflight ping ─────────────────────────────────
                _notifyIcon!.Text = "PPT Highlighting Engine (Checking API...)";
                _statusIndicator?.UpdateStatus("Checking API...");

                bool apiOk = await _visionService.PingAsync();
                if (!apiOk)
                {
                    ClearProcessToken();
                    Log.Warning("Refresh KB preflight failed. Cleared process token before prompting for a replacement.");

                    if (!await PromptForTokenAndRetryAsync(() => _visionService.PingAsync(), "Update GNAI API Key"))
                    {
                        Log.Error("Refresh KB: API preflight ping failed.");
                        System.Windows.MessageBox.Show(
                            "API connectivity check failed — knowledge base not rebuilt.\n\n" +
                            "Check that you are on the Intel network or VPN and that your GNAI_TOKEN is valid.",
                            "API Not Reachable",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        _notifyIcon.Text = "PPT Highlighting Engine (Stopped)";
                        _statusIndicator?.UpdateStatus("Paused");
                        return;
                    }

                    apiOk = true;
                }

                // ── Step 5: Rebuild KB ─────────────────────────────────────────
                _notifyIcon.Text = "PPT Highlighting Engine (Rebuilding KB...)";
                _statusIndicator?.UpdateStatus("Rebuilding KB...");
                Log.Information("Refresh KB: rebuilding knowledge base from scratch...");

                await _kbPreprocessor.PreprocessAsync(_pptService, "knowledge_base.yaml");

                Log.Information("Refresh KB: rebuild complete.");

                // ── Step 6: Restart engine if it was running before ────────────
                if (wasRunning)
                {
                    Log.Information("Refresh KB: restarting engine...");
                    await _orchestrator.StartAsync();
                    UpdateMenuState(true);
                    _statusIndicator?.Dispatcher.Invoke(() => _statusIndicator.Show());
                    _notifyIcon.Text = "PPT Highlighting Engine (Running)";
                    _notifyIcon.ShowBalloonTip(3000, "Knowledge Base Refreshed",
                        "KB rebuilt successfully. Engine is running with the updated slides.", ToolTipIcon.Info);
                }
                else
                {
                    UpdateMenuState(false);
                    _notifyIcon.Text = "PPT Highlighting Engine (Stopped)";
                    _notifyIcon.ShowBalloonTip(3000, "Knowledge Base Refreshed",
                        "KB rebuilt successfully. Click 'Start Engine' when ready.", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Refresh KB: failed.");
                System.Windows.MessageBox.Show(
                    "Knowledge base refresh failed.\n\nError: " + ex.Message,
                    "Refresh Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                UpdateMenuState(false);
            }
        });

        _startMenuItem.Enabled = false;
        _stopMenuItem.Enabled = false;
        _refreshMenuItem.Enabled = false;

        contextMenu.Items.Add(_startMenuItem);
        contextMenu.Items.Add(_stopMenuItem);
        contextMenu.Items.Add(_refreshMenuItem);
        
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(CreateMenuItem("Exit", (s, e) => 
        {
            Task.Run(async () => 
            {
                if (_orchestrator != null)
                {
                    await _orchestrator.StopAsync();
                }
                Dispatcher.Invoke(() => 
                {
                    _statusIndicator?.Close();
                    Current.Shutdown();
                });
            });
        }));

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void UpdateMenuState(bool isRunning)
    {
        if (_startMenuItem != null)   _startMenuItem.Enabled   = !isRunning;
        if (_stopMenuItem != null)    _stopMenuItem.Enabled    = isRunning;
        // Refresh is enabled whenever the engine is NOT mid-start and the services
        // are initialised — regardless of running state.
        if (_refreshMenuItem != null) _refreshMenuItem.Enabled = true;
        if (_notifyIcon != null)      _notifyIcon.Text         = isRunning
            ? "PPT Highlighting Engine (Running)"
            : "PPT Highlighting Engine (Stopped)";
    }

    private static ToolStripMenuItem CreateMenuItem(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text, null, handler);
        item.DisplayStyle = ToolStripItemDisplayStyle.Text;
        item.TextImageRelation = TextImageRelation.Overlay;
        return item;
    }

    private static ToolStripMenuItem CreateMenuItem(string text, Func<object?, EventArgs, Task> handler)
    {
        var item = new ToolStripMenuItem(text);
        item.DisplayStyle = ToolStripItemDisplayStyle.Text;
        item.TextImageRelation = TextImageRelation.Overlay;
        item.Click += async (s, e) => await handler(s, e);
        return item;
    }

    private Icon CreatePocIcon()
    {
        var bitmap = new Bitmap(16, 16);
        bitmap.MakeTransparent(Color.Black);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var bgBrush = new SolidBrush(Color.FromArgb(0, 90, 180));
            using var borderPen = new Pen(Color.White);
            using var font = new System.Drawing.Font("Segoe UI", 7, System.Drawing.FontStyle.Bold);
            using var brush = new SolidBrush(Color.White);

            graphics.FillRectangle(bgBrush, 1, 1, 14, 14);
            graphics.DrawRectangle(borderPen, 1, 1, 14, 14);
            graphics.DrawString("P", font, brush, 3, 1);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private async Task InitializeEngineAndStart(AppConfig config, SplashWindow splash)
    {
        try 
        {
            var pptService = new PowerPointService();
            var ocrService = new WindowsOcrService();
            var gptVision = new OpenAIVisionService(config);
            var slideReader = new SlideReader(ocrService, gptVision);
            var audioCapture = new MicrophoneCaptureService(config);
            var asrService = new ParakeetAsrService(config);
            
            // Connect ASR download progress to splash screen
            asrService.DownloadProgressChanged += (progress, message) => 
            {
                splash.UpdateProgress(progress, message);
            };

            // Force ASR model download and initialization during Splash Screen
            splash.UpdateProgress(0, "Checking ASR speech models...");
            await asrService.InitializeAsync(config.ParakeetModelPath, config.OpenVinoDevice);

            var semanticService = new SemanticEmbeddingService();

            // Validate Semantic models
            splash.UpdateProgress(100, "Checking Semantic matching models...");
            await semanticService.InitializeAsync(config.SemanticModelPath);

            // Store references for the start button and refresh button
            _pptService = pptService;
            _visionService = gptVision;   // stored for PingAsync() in Start Engine + Refresh handlers
            _kbPreprocessor = new KnowledgeBasePreprocessor(config, slideReader, semanticService, gptVision);

            var transcriptProcessor = new TranscriptProcessor(config);
            var ragAgent = new RAGAgent(config);
            var matcherEngine = new MatcherEngine(config, semanticService, ragAgent);
            var renderer = new SlideshowLaserRenderer(config);
            var debounce = new DebounceManager(config);
            var kbLoader = new KnowledgeBaseLoader();

            renderer.EnsureOverlay();

            _orchestrator = new Orchestrator(
                config, pptService, slideReader, audioCapture, asrService, 
                transcriptProcessor, matcherEngine, renderer, debounce, kbLoader, 
                ragAgent, semanticService);

            if (_statusIndicator != null)
            {
                _statusIndicator.ToggleLaserRequested += () => 
                {
                    if (_orchestrator != null && _orchestrator.IsRunning)
                    {
                        var newState = !_orchestrator.IsLaserEnabled;
                        _orchestrator.IsLaserEnabled = newState;
                        _statusIndicator.UpdateStatus(newState ? "Laser Enabled" : "Laser Disabled");
                        Log.Information("HotKey triggered Laser State Change: {State}", newState);
                    }
                };
                
                _orchestrator.LaserStateChanged += (enabled) => 
                {
                    _statusIndicator.UpdateStatus(enabled ? "Laser Enabled" : "Laser Disabled");
                };
                
                _orchestrator.StatusChanged += (msg) => 
                {
                    if (msg == "Microphone active") _statusIndicator.UpdateStatus("Listening");
                };
            }

            Log.Information("Engine successfully initialized via Tray. Waiting for manual start.");
            
            // Enable the tray buttons now that services are fully initialised.
            // Do NOT auto-start to prevent PowerPoint errors on boot.
            UpdateMenuState(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to prep orchestrator.");
            UpdateMenuState(false);
            System.Windows.MessageBox.Show("Failed to initialize engine. Check logs.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _orchestrator?.StopAsync().Wait();

        Log.Information("Exiting Tray App.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
