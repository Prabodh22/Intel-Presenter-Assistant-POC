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
    private Task? _warmupTask;

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfigLoader.Load();

        var pptService = new PowerPointService();
        _ocrService = new WindowsOcrService();
        var slideReader = new SlideReader(_ocrService);
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
        var renderer = new EditModeRenderer(_config);
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
            debounce);

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
}
