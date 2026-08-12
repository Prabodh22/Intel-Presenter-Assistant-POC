using System;
using System.Windows;
using PptPoc.Core.Configuration;
using PptPoc.Orchestration;
using PptPoc.PowerPoint;
using PptPoc.App.Views;
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
using PptPoc.Core.Utilities;
using System.IO;
using System.Threading;

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

    // -- API Preflight --------------------------------------------------------
    // Stored so the "Start Engine" tray handler can call PingAsync() before
    // PreprocessAsync(). If the ping fails, the start is aborted with a clear
    // user-facing message rather than a silent KB degradation (all slides fail
    // ? zero matching for the entire session).
    private IOpenAIVisionService? _visionService;

    // -- Refresh KB menu item -------------------------------------------------
    // Allows the user to force a KB rebuild at any time without restarting the
    // app. This is the manual override for the case where IsYamlStale() could
    // not detect staleness automatically (e.g. SharePoint / COM-title-only path).
    // It also acts as the one-click fix if a presenter edits slides right before
    // the talk and wants the KB to reflect the final version.
    private ToolStripMenuItem? _refreshMenuItem;

    private ToolStripMenuItem? _settingsMenuItem;
    private StatusIndicatorWindow? _statusIndicator;
    private AppConfig? _currentConfig;

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            SetupGlobalExceptionHandlers();

            _mutex = new Mutex(true, "PptPocEngine_Unique_Mutex", out bool createdNew);
            if (!createdNew)
            {
                _notifyIcon?.ShowBalloonTip(3000, "Already Running", "The Engine is already running in your System Tray!", ToolTipIcon.Info);
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);

            _currentConfig = AppConfigLoader.Load();
            ConfigureLogging(_currentConfig);

            Log.Information("System Tray POC starting from {BaseDir}", AppDomain.CurrentDomain.BaseDirectory);

            if (!VerifyWpfTempPackagingAccess())
            {
                Current.Shutdown();
                return;
            }

            InitializeNotifyIcon();
            _statusIndicator = new StatusIndicatorWindow(_currentConfig?.LaserToggleHotkey ?? "Ctrl+Shift+L");
            EnsureTokenExists(silent: true);

            // Give the UI thread a heartbeat to paint the context menu + status dot before blocking
            await Task.Delay(500);
            _notifyIcon?.ShowBalloonTip(5000, "PPT Helper", "Loading background AI models... Hover over the System Tray icon to view live download progress!", ToolTipIcon.Info);

            InitializeEngineAndStart(_currentConfig);
        }
        catch (Exception ex)
        {
            TryLogFatal(ex, "Startup failed before engine initialization completed.");
            _notifyIcon?.ShowBalloonTip(5000, "Startup Error", "Application failed during startup. Check Logs.", ToolTipIcon.Error);
            Current.Shutdown();
        }
    }

    private static void ConfigureLogging(AppConfig config)
    {
        var primaryLogPath = ResolveLogFilePath(config.LogFilePath);

        // Ensure the shared artifact directory exists and is used for all logs.
        var primaryDir = Path.GetDirectoryName(primaryLogPath);
        if (!string.IsNullOrWhiteSpace(primaryDir))
            Directory.CreateDirectory(primaryDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(primaryLogPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    // Resolves a log path supporting "%APPDATA%" expansion.
    private static string ResolveLogFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = "pptpoc-.log";

        return Environment.ExpandEnvironmentVariables(path);
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

    private bool VerifyWpfTempPackagingAccess()
    {
        try
        {
            string tempDir = Path.GetTempPath();
            string probeFile = Path.Combine(tempDir, $"pptpoc_probe_{Guid.NewGuid()}.tmp");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            return true;
        }
        catch (Exception ex)
        {
            var balloonMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            if (balloonMsg.Length > 80) balloonMsg = balloonMsg.Substring(0, 77) + "...";
            _notifyIcon?.ShowBalloonTip(5000, "Packaging Error", $"Failed to access TEMP dir: {balloonMsg}", ToolTipIcon.Error);
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

        Current.DispatcherUnhandledException += (_, args) =>
        {
            TryLogFatal(args.Exception, "Dispatcher unhandled exception.");
            args.Handled = true; // Prevent immediate crash if possible
        };
    }

    private void EnsureTokenExists(bool silent)
    {
        var token = Environment.GetEnvironmentVariable("GNAI_TOKEN") ?? Environment.GetEnvironmentVariable("GNAI_TOKEN", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(token))
        {
            if (!silent)
                ShowSettingsDialog();
        }
        else
        {
            Environment.SetEnvironmentVariable("GNAI_TOKEN", token, EnvironmentVariableTarget.Process);
        }
    }

    private void ShowSettingsDialog()
    {
        if (Current == null || Current.Dispatcher == null) return;

        Current.Dispatcher.Invoke(() =>
        {
            var token = Environment.GetEnvironmentVariable("GNAI_TOKEN");
            var dialog = new SettingsWindow(token ?? "", _currentConfig?.LaserToggleHotkey ?? "Ctrl+Shift+L", _currentConfig?.ParakeetModelPath ?? "models/parakeet");
            
            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                if (!string.IsNullOrWhiteSpace(dialog.GnaiToken))
                {
                    ApplyToken(dialog.GnaiToken);
                }

                // Actually persist config to appsettings.json so it sticks between boots
                UpdateAppConfigValues(dialog.GlobalHotkey, dialog.ModelPath);

                if (_currentConfig != null)
                {
                    _currentConfig.LaserToggleHotkey = dialog.GlobalHotkey;
                    _currentConfig.ParakeetModelPath = dialog.ModelPath;
                }

                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(3000, "PPT Helper", "Settings saved successfully.\nModel path changes require a restart.", ToolTipIcon.Info);
                }
            }
        });
    }

    private void UpdateAppConfigValues(string hotkey, string modelPath)
    {
        try
        {
            string configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (System.IO.File.Exists(configPath))
            {
                var json = System.IO.File.ReadAllText(configPath);
                var jObject = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                if (jObject != null && jObject.ContainsKey("AppConfig"))
                {
                    var appConfig = jObject["AppConfig"] as System.Text.Json.Nodes.JsonObject;
                    if (appConfig != null)
                    {
                        appConfig["LaserToggleHotkey"] = hotkey;
                        appConfig["ParakeetModelPath"] = modelPath;
                    }
                    
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    System.IO.File.WriteAllText(configPath, jObject.ToJsonString(options));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to appsettings.json");
        }
    }

    private static void ApplyToken(string token)
    {
        Environment.SetEnvironmentVariable("GNAI_TOKEN", token, EnvironmentVariableTarget.Process);
        // Persisting to the user environment can involve registry I/O which
        // may block on some systems. Do this off the UI thread to avoid a
        // transient freeze of the tray/status UI when the user updates the key.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("GNAI_TOKEN", token, EnvironmentVariableTarget.User);
            }
            catch
            {
                // Ignore permission issues when writing the user environment store.
            }
        });
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
        
        // Settings Menu
        _settingsMenuItem = CreateMenuItem("Settings...", (s, e) =>
        {
            ShowSettingsDialog();
        });
        contextMenu.Items.Add(_settingsMenuItem);

        contextMenu.Items.Add(new ToolStripSeparator());
        
        // Start/Stop engine options removed. Managed via hotkey.
        // -- Refresh Knowledge Base -------------------------------------------
        // Deletes the cached YAML for the currently open deck and rebuilds it
        // from scratch. Use this when:
        //   � Slides were edited and the auto-staleness check couldn't detect it
        //     (SharePoint / COM-title-only path where file-time comparison is
        //     unavailable)
        //   � You want to force a clean rebuild regardless of file times
        //
        // If the engine is currently running, it is stopped first, the KB is
        // rebuilt, and then the engine is automatically restarted � so the user
        // gets a seamless "refresh and keep going" experience.
        _refreshMenuItem = CreateMenuItem("Refresh Knowledge Base", async (s, e) =>
        {
            try
            {
                if (_pptService == null || _kbPreprocessor == null || _visionService == null || _orchestrator == null)
                    return;

                bool wasRunning = _orchestrator.IsRunning;

                // -- Step 1: Stop engine if it is currently running -----------
                if (wasRunning)
                {
                    Log.Information("Refresh KB: stopping engine before rebuild...");
                    await _orchestrator.StopAsync();
                    UpdateMenuState(false);
                }

                // -- Step 2: Attach to PowerPoint (needed to read the PPT path) -
                if (!_pptService.TryAttach())
                {
                    _notifyIcon?.ShowBalloonTip(5000, "Refresh Failed", "Could not attach to a running PowerPoint instance. Make sure PowerPoint is open before refreshing.", ToolTipIcon.Warning);
                    if (wasRunning) UpdateMenuState(false);
                    return;
                }

                // -- Step 3: Delete the existing YAML so PreprocessAsync rebuilds -
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

                // -- Step 4: API preflight ping ---------------------------------
                _notifyIcon!.Text = "PPT Highlighting Engine (Checking API...)";
                _statusIndicator?.UpdateStatus("Checking API...");

                bool apiOk = await _visionService.PingAsync();
                if (!apiOk)
                {
                    Log.Error("Refresh KB: API preflight ping failed.");
                    _notifyIcon?.ShowBalloonTip(5000, "API Not Reachable", "API connectivity check failed � knowledge base not rebuilt. Check your GNAI_TOKEN in Settings.", ToolTipIcon.Warning);
                    _notifyIcon.Text = "PPT Highlighting Engine (Stopped)";
                    _statusIndicator?.UpdateStatus("Paused");
                    return;
                }

                // -- Step 5: Rebuild KB -----------------------------------------
                _notifyIcon.Text = "PPT Highlighting Engine (Rebuilding KB...)";
                _statusIndicator?.UpdateStatus("Rebuilding KB...");
                Log.Information("Refresh KB: rebuilding knowledge base from scratch...");

                await RunPreprocessOnStaThread(_kbPreprocessor, _pptService, "knowledge_base.yaml");

                Log.Information("Refresh KB: rebuild complete.");

                // -- Step 6: Restart engine if it was running before -------------
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
                        "KB rebuilt successfully. Hit your hotkey to enable Laser.", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Refresh KB: failed.");
                var balloonMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                if (balloonMsg.Length > 80) balloonMsg = balloonMsg.Substring(0, 77) + "...";
                _notifyIcon?.ShowBalloonTip(5000, "Refresh Error", $"Knowledge base refresh failed. {balloonMsg}", ToolTipIcon.Error);
                UpdateMenuState(false);
            }
        });


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
        // Refresh is enabled whenever the engine is NOT mid-start and the services
        // are initialised � regardless of running state.
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

    private static Task<string> RunPreprocessOnStaThread(KnowledgeBasePreprocessor preprocessor, IPowerPointService pptService, string path)
    {
        var tcs = new TaskCompletionSource<string>();
        var thread = new Thread(() =>
        {
            try
            {
                var result = preprocessor.PreprocessAsync(pptService, path).ConfigureAwait(false).GetAwaiter().GetResult();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static Icon CreatePocIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.FillEllipse(Brushes.DodgerBlue, 2, 2, 12, 12);
        
        using var pen = new Pen(Color.White, 2);
        g.DrawEllipse(pen, 2, 2, 12, 12);
        
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void InitializeEngineAndStart(AppConfig config)
    {
        try
        {
            var pptService = new PowerPointService();

            var semanticService = new SemanticEmbeddingService();
            var gptVision = new OpenAIVisionService(config);
            var ocrService = new WindowsOcrService(); var slideReader = new SlideReader(ocrService, gptVision);
            var audioCapture = new MicrophoneCaptureService(config);
            var asrService = new ParakeetAsrService(config);

            var transcriptProcessor = new TranscriptProcessor(config);
            var ragAgent = new RAGAgent(config);
            var matcherEngine = new MatcherEngine(config, semanticService, ragAgent);
            var kbLoader = new KnowledgeBaseLoader();
            var debounce = new DebounceManager(config);
            var renderer = new SlideshowLaserRenderer(config);

            Dispatcher.Invoke(() => renderer.EnsureOverlay());

            _orchestrator = new Orchestrator(
                config, pptService, slideReader, audioCapture, asrService, 
                transcriptProcessor, matcherEngine, renderer, debounce, kbLoader, 
                ragAgent, semanticService);

            // Store references for the refresh button
            _pptService = pptService;
            _visionService = gptVision;
            _kbPreprocessor = new KnowledgeBasePreprocessor(config, slideReader, semanticService, gptVision);

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
                    if (!enabled && !_orchestrator.IsRunning)
                        return;

                    _statusIndicator.UpdateStatus(enabled ? "Laser Enabled" : "Laser Disabled");
                };
                
                _orchestrator.StatusChanged += (msg) => 
                {
                    if (_notifyIcon != null) 
                    { 
                        var safeMsg = msg ?? ""; 
                        if (safeMsg.Length > 63) safeMsg = safeMsg.Substring(0, 60) + "..."; 
                        _notifyIcon.Text = safeMsg; 
                    }
                    _statusIndicator.UpdateStatus(msg);
                };

            }

            Log.Information("Engine successfully initialized via Tray. Auto-starting PPT Orchestrator...");
            
            // Auto start the engine here so it idles grey and waits for 'Laser ON' command
            if (_orchestrator != null)
            {
                // We use Task.Run so startup doesn't block the UI thread waiting on StartAsync
                _ = Task.Run(async () => 
                {
                    try
                    {
                        await _orchestrator.StartAsync();
                        Dispatcher.Invoke(() => 
                        {
                            UpdateMenuState(true);
                            // Hide until PPT slideshow launches
                            _statusIndicator?.Hide(); 
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to auto-start orchestrator.");
                        var balloonMsg = ex.Message;
                        if (balloonMsg.Length > 80) balloonMsg = balloonMsg.Substring(0, 77) + "...";
                        _notifyIcon?.ShowBalloonTip(5000, "Background Engine Failed", $"Check Logs: {balloonMsg}", ToolTipIcon.Error);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to prep orchestrator.");
            UpdateMenuState(false);
            
            var balloonMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            if (balloonMsg.Length > 80) balloonMsg = balloonMsg.Substring(0, 77) + "...";
            _notifyIcon?.ShowBalloonTip(5000, "Initialization Failed", $"Check Settings/Logs: {balloonMsg}", ToolTipIcon.Error);
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
