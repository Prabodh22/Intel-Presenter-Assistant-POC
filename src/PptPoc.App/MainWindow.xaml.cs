using System.IO;
using System.Linq;
using System.Windows;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Orchestration;
using PptPoc.PowerPoint;
using PptPoc.Audio;
using PptPoc.Asr;
using PptPoc.Matching;
using PptPoc.Vision;
using Serilog;

namespace PptPoc.App;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly IOrchestrator _orchestrator;
    private readonly IAsrService _asrService;
    private readonly IOcrService _ocrService;
    private readonly ISemanticEmbeddingService _semanticService;
    private readonly IPowerPointService _pptService;
    private readonly ISlideReader _slideReader;
    private readonly IOpenAIVisionService _gptVision;
    private readonly KnowledgeBaseLoader _kbLoader = new();
    private FileSystemWatcher? _pptWatcher;
    private string? _currentPptPath;
    private Task? _warmupTask;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfigLoader.Load();

        var pptService = new PowerPointService();
        _pptService = pptService;
        _ocrService = new WindowsOcrService();
        var gptVision = new OpenAIVisionService(_config);
        _gptVision = gptVision;
        var slideReader = new SlideReader(_ocrService, gptVision);
        _slideReader = slideReader;
        var audioCapture = new MicrophoneCaptureService(_config);
        _asrService = new ParakeetAsrService(_config);
        _semanticService = new SemanticEmbeddingService();

        _asrService.DownloadProgressChanged += (progress, message) => Dispatcher.Invoke(() =>
        {
            DownloadProgressBar.Value = progress;
            StatusText.Text = message;
        });

        var transcriptProcessor = new TranscriptProcessor(_config);
        var matcherEngine = new MatcherEngine(_config, _semanticService);
        var renderer = new SlideshowLaserRenderer(_config);
        renderer.EnsureOverlay();
        var debounce = new DebounceManager(_config);

        _orchestrator = new Orchestrator(
            _config,
            pptService,
            slideReader,
            audioCapture,
            _asrService,
            transcriptProcessor,
            matcherEngine,
            renderer,
            debounce,
            _kbLoader);

        _orchestrator.StatusChanged += msg => Dispatcher.Invoke(() => StatusText.Text = msg);
        _orchestrator.TranscriptUpdated += text => Dispatcher.Invoke(() =>
        {
            TranscriptText.Text = text;
            TranscriptScroller.ScrollToEnd();
        });
        _orchestrator.HighlightApplied += msg => Dispatcher.Invoke(() =>
        {
            HighlightLog.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (HighlightLog.Items.Count > 50)
                HighlightLog.Items.RemoveAt(HighlightLog.Items.Count - 1);
        });

        Closing += MainWindow_Closing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        _warmupTask = WarmUpAsrAsync();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        try
        {
            if (_warmupTask != null)
                await _warmupTask;

            // Auto-KB: check/preprocess before starting
            await EnsureKnowledgeBaseAsync();

            await _orchestrator.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start orchestrator");
            StatusText.Text = $"Error: {ex.Message}";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;

        try
        {
            await _orchestrator.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping orchestrator");
            StatusText.Text = $"Error stopping: {ex.Message}";
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;

        // Stop watching for PPT saves
        _pptWatcher?.Dispose();
        _pptWatcher = null;
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prevent the app from closing while a critical background task is running.
        if (_warmupTask != null && !_warmupTask.IsCompleted)
        {
            e.Cancel = true; // Cancel the closing event.
            StatusText.Text = "Please wait, ASR model download in progress...";
            
            // Disable closing actions while we wait.
            this.IsEnabled = false; 
            
            try
            {
                await _warmupTask; // Wait for the download/init to finish.
                StatusText.Text = "ASR ready. You can now close the application.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during ASR warmup on closing.");
                StatusText.Text = "ASR failed. Safe to close.";
            }
            finally
            {
                this.IsEnabled = true;
                // After the task is complete, the user can close the window again.
                // If they do, this event will fire again, but the task will be complete.
            }
            return; // Return here to avoid running the cleanup logic below yet.
        }

        if (_orchestrator.IsRunning)
        {
            try
            {
                await _orchestrator.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during shutdown");
            }
        }

        _orchestrator.Dispose();
        _ocrService.Dispose();
        _pptWatcher?.Dispose();
        
        if (_semanticService is IDisposable disposableSemantic)
        {
            disposableSemantic.Dispose();
        }
    }

    private async Task WarmUpAsrAsync()
    {
        if (_asrService.IsReady && _semanticService.IsReady)
        {
            StatusText.Text = "Ready — open PowerPoint and click Start";
            StartButton.IsEnabled = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            StatusText.Text = "Warming up ASR & Embeddings... Downloading models...";
            StartButton.IsEnabled = false;
            DownloadProgressBar.Visibility = Visibility.Visible;

            // Initialize OCR, ASR and Semantic matching concurrently
            var ocrTask = _ocrService.InitializeAsync();
            var asrTask = _asrService.InitializeAsync(_config.ParakeetModelPath ?? "models/parakeet", _config.OpenVinoDevice);
            var semanticTask = _semanticService.InitializeAsync(_config.SemanticModelPath ?? "models/minilm");
            
            await Task.WhenAll(ocrTask, asrTask, semanticTask);

            StatusText.Text = "Ready — open PowerPoint and click Start";
            StartButton.IsEnabled = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ASR warmup failed");
            StatusText.Text = "ASR warmup failed. Please check logs and restart.";
            StartButton.IsEnabled = false;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void PreprocessButton_Click(object sender, RoutedEventArgs e)
    {
        PreprocessButton.IsEnabled = false;
        StatusText.Text = "Pre-processing presentation...";

        try
        {
            if (_warmupTask != null) await _warmupTask;

            if (!_pptService.TryAttach())
            {
                StatusText.Text = "ERROR: PowerPoint not found. Open a presentation first.";
                PreprocessButton.IsEnabled = true;
                return;
            }

            var preprocessor = new KnowledgeBasePreprocessor(_config, _slideReader, _semanticService, _gptVision);
            preprocessor.SlideProgress += (current, total) => Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"Pre-processing slide {current}/{total}...";
                DownloadProgressBar.Visibility = Visibility.Visible;
                DownloadProgressBar.Value = (double)current / total * 100;
            });

            var outputPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "knowledge_base.yaml");

            await preprocessor.PreprocessAsync(_pptService, outputPath);

            DownloadProgressBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"KB saved: {outputPath}";
            Log.Information("Knowledge base saved to {Path}", outputPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Preprocessing failed");
            StatusText.Text = $"Preprocess error: {ex.Message}";
            DownloadProgressBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            PreprocessButton.IsEnabled = true;
        }
    }

    private void LoadKbButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "YAML files|*.yaml;*.yml|All files|*.*",
            Title = "Select Knowledge Base YAML"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                _kbLoader.Load(dlg.FileName);
                StatusText.Text = $"KB loaded: {_kbLoader.PresentationName} ({_kbLoader.SlideCount} slides)";
                Log.Information("Loaded KB from {Path}", dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load KB");
                StatusText.Text = $"KB load error: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Derives the KB YAML path from the PPT file path.
    /// For local files: stores alongside the PPT (e.g. "deck.pptx" → "deck.pptx.kb.yaml").
    /// For SharePoint/URL paths: stores in a local "kb" folder next to the app.
    /// </summary>
    private static string? GetKbPath(string? pptPath)
    {
        if (string.IsNullOrWhiteSpace(pptPath)) return null;

        // Detect URL-based paths (SharePoint, OneDrive, http/https)
        if (pptPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || pptPath.Contains("sharepoint.com", StringComparison.OrdinalIgnoreCase))
        {
            // Extract just the filename from the URL/path
            var fileName = pptPath.Split('/', '\\').Last(s => !string.IsNullOrEmpty(s));
            var kbDir = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "kb");
            Directory.CreateDirectory(kbDir);
            return Path.Combine(kbDir, fileName + ".kb.yaml");
        }

        return pptPath + ".kb.yaml";
    }

    /// <summary>
    /// Auto-checks if a KB exists and is fresh for the current PPT. If stale or missing, preprocesses.
    /// </summary>
    private async Task EnsureKnowledgeBaseAsync()
    {
        // Skip if a KB was already loaded manually via the Load KB button
        if (_kbLoader.IsLoaded)
        {
            Log.Information("KB already loaded ({Name}, {Count} slides), skipping auto-preprocess",
                _kbLoader.PresentationName, _kbLoader.SlideCount);
            return;
        }

        if (!_pptService.TryAttach()) return;

        var presObj = _pptService.GetActivePresentationComObject();
        if (presObj == null) return;

        var presentation = (Microsoft.Office.Interop.PowerPoint.Presentation)presObj;
        _currentPptPath = presentation.FullName;
        var kbPath = GetKbPath(_currentPptPath);
        if (kbPath == null) return;

        bool needsPreprocess = true;
        bool isRemotePpt = !File.Exists(_currentPptPath);

        if (File.Exists(kbPath))
        {
            if (isRemotePpt)
            {
                // Remote/SharePoint PPT — can't check timestamps, just load existing KB
                StatusText.Text = "Loading existing knowledge base...";
                _kbLoader.Load(kbPath);
                StatusText.Text = $"KB loaded: {_kbLoader.PresentationName} ({_kbLoader.SlideCount} slides)";
                Log.Information("Auto-loaded KB for remote PPT from {Path}", kbPath);
                needsPreprocess = false;
            }
            else
            {
                var pptLastWrite = File.GetLastWriteTimeUtc(_currentPptPath);
                var kbLastWrite = File.GetLastWriteTimeUtc(kbPath);

                if (kbLastWrite >= pptLastWrite)
                {
                    // KB is fresh — just load it
                    StatusText.Text = "Loading existing knowledge base...";
                    _kbLoader.Load(kbPath);
                    StatusText.Text = $"KB loaded: {_kbLoader.PresentationName} ({_kbLoader.SlideCount} slides)";
                    Log.Information("Auto-loaded fresh KB from {Path}", kbPath);
                    needsPreprocess = false;
                }
                else
                {
                    Log.Information("KB is stale (PPT modified {PptTime}, KB built {KbTime}), re-preprocessing",
                        pptLastWrite, kbLastWrite);
                }
            }
        }

        if (needsPreprocess)
        {
            StatusText.Text = "Auto-preprocessing presentation...";
            DownloadProgressBar.Visibility = Visibility.Visible;

            var preprocessor = new KnowledgeBasePreprocessor(_config, _slideReader, _semanticService, _gptVision);
            preprocessor.SlideProgress += (current, total) => Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"Auto-preprocessing slide {current}/{total}...";
                DownloadProgressBar.Value = (double)current / total * 100;
            });

            await preprocessor.PreprocessAsync(_pptService, kbPath);

            _kbLoader.Load(kbPath);
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"KB ready: {_kbLoader.PresentationName} ({_kbLoader.SlideCount} slides)";
            Log.Information("Auto-preprocessed and loaded KB: {Path}", kbPath);
        }

        // Watch for PPT saves to re-preprocess automatically
        StartPptWatcher(_currentPptPath, kbPath);
    }

    /// <summary>
    /// Watches the PPT file for saves and triggers re-preprocessing.
    /// </summary>
    private void StartPptWatcher(string pptPath, string kbPath)
    {
        _pptWatcher?.Dispose();

        // Can't watch remote/SharePoint files
        if (!File.Exists(pptPath))
        {
            Log.Information("PPT is remote ({Path}), skipping file watcher", pptPath);
            return;
        }

        var dir = Path.GetDirectoryName(pptPath);
        var fileName = Path.GetFileName(pptPath);
        if (dir == null || fileName == null) return;

        _pptWatcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        // Debounce: PPT saves can trigger multiple events
        DateTime lastTriggered = DateTime.MinValue;

        _pptWatcher.Changed += async (_, _) =>
        {
            var now = DateTime.UtcNow;
            if ((now - lastTriggered).TotalSeconds < 5) return; // skip rapid-fire events
            lastTriggered = now;

            Log.Information("PPT file saved, re-preprocessing KB...");

            await Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    StatusText.Text = "PPT saved — re-preprocessing KB...";

                    var preprocessor = new KnowledgeBasePreprocessor(_config, _slideReader, _semanticService, _gptVision);
                    preprocessor.SlideProgress += (current, total) => Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Re-preprocessing slide {current}/{total}...";
                    });

                    await preprocessor.PreprocessAsync(_pptService, kbPath);
                    _kbLoader.Load(kbPath);
                    StatusText.Text = $"KB updated: {_kbLoader.PresentationName} ({_kbLoader.SlideCount} slides)";
                    Log.Information("Auto re-preprocessed KB after PPT save");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Auto re-preprocess failed after PPT save");
                    StatusText.Text = "KB re-preprocess failed (will retry on next save)";
                }
            });
        };

        Log.Information("Watching {PptPath} for saves", pptPath);
    }
}
