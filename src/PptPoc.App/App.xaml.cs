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

namespace PptPoc.App;

public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private Orchestrator? _orchestrator;
    private KnowledgeBasePreprocessor? _kbPreprocessor;
    private IPowerPointService? _pptService;
    private ToolStripMenuItem? _startMenuItem;
    private ToolStripMenuItem? _stopMenuItem;

    protected override async void OnStartup(StartupEventArgs e)
    {
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

        await InitializeEngineAndStart(config, splash);
        
        splash.Close();
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
                if (_pptService != null && _kbPreprocessor != null)
                {
                    _notifyIcon.Text = "PPT Highlighting Engine (Analyzing Slides...)";
                    Log.Information("Manual start triggered. Analzying slides into YAML...");

                    if (!_pptService.TryAttach())
                    {
                        throw new Exception("Could not attach to a running PowerPoint instance.");
                    }

                    await _kbPreprocessor.PreprocessAsync(_pptService, "knowledge_base.yaml");
                }
                
                if (_orchestrator != null) 
                {
                    await _orchestrator.StartAsync(); 
                    UpdateMenuState(true);
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
            }
        });

        _startMenuItem.Enabled = false;
        _stopMenuItem.Enabled = false;

        contextMenu.Items.Add(_startMenuItem);
        contextMenu.Items.Add(_stopMenuItem);
        
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (s, e) => Current.Shutdown());

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void UpdateMenuState(bool isRunning)
    {
        if (_startMenuItem != null) _startMenuItem.Enabled = !isRunning;
        if (_stopMenuItem != null) _stopMenuItem.Enabled = isRunning;
        if (_notifyIcon != null) _notifyIcon.Text = isRunning ? "PPT Highlighting Engine (Running)" : "PPT Highlighting Engine (Stopped)";
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

            // Store references for the start button
            _pptService = pptService;
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

            Log.Information("Engine successfully initialized via Tray. Waiting for manual start.");
            
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

