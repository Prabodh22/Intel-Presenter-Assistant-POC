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
using PptPoc.Core.Interfaces;

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
        _mutex = new Mutex(true, "PptPocEngine_Unique_Mutex", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("The Engine is already running in your System Tray!", "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);

        // Inject Proxy globally for the current process
        Environment.SetEnvironmentVariable("HTTP_PROXY", "http://proxy-iind.intel.com:911", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://proxy-iind.intel.com:911", EnvironmentVariableTarget.Process);

        var config = AppConfigLoader.Load();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(config.LogFilePath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Log.Information("System Tray POC starting");

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

    private void EnsureTokenExists()
    {
        var token = Environment.GetEnvironmentVariable("GNAI_TOKEN") ?? Environment.GetEnvironmentVariable("GNAI_TOKEN", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(token))
        {
            var dialog = new TokenInputDialog();
            dialog.ShowDialog();
        }
        else
        {
            Environment.SetEnvironmentVariable("GNAI_TOKEN", token, EnvironmentVariableTarget.Process);
        }
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
        
        contextMenu.Items.Add("Update GNAI Token", null, (s, e) => 
        {
            var dialog = new TokenInputDialog();
            dialog.ShowDialog();
        });

        contextMenu.Items.Add(new ToolStripSeparator());
        
        _startMenuItem = new ToolStripMenuItem("Start Engine", null, async (s, e) => 
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
                        string detail = string.IsNullOrWhiteSpace(currentToken)
                            ? "GNAI_TOKEN is not set.\n\nUse 'Update GNAI Token' in the tray menu to enter your token."
                            : "The API endpoint is unreachable or returned an auth/server error.\n\n" +
                              "• Check that you are on the Intel network or VPN\n" +
                              "• Verify your GNAI_TOKEN is correct (use 'Update GNAI Token')\n" +
                              "• Check the log file for the exact HTTP status code";

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
        
        _stopMenuItem = new ToolStripMenuItem("Stop Engine", null, async (s, e) => 
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
        _refreshMenuItem = new ToolStripMenuItem("Refresh Knowledge Base", null, async (s, e) =>
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
        contextMenu.Items.Add("Exit", null, (s, e) => 
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
        });

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

    private Icon CreatePocIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 7, System.Drawing.FontStyle.Bold))
        using (var brush = new SolidBrush(Color.White))
        using (var bgBrush = new SolidBrush(Color.DarkBlue))
        {
            graphics.FillRectangle(bgBrush, 0, 0, 16, 16);
            graphics.DrawString("POC", font, brush, -2, 2);
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
