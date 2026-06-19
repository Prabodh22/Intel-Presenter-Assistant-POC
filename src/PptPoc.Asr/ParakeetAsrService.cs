using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Sdcb.OpenVINO;
using Sdcb.OpenVINO.Natives;
using Serilog;

namespace PptPoc.Asr;

/// <summary>
/// Parakeet-TDT ASR service backed by OpenVINO.
/// Model source: FluidInference/parakeet-tdt-0.6b-v2-ov.
/// </summary>
public sealed class ParakeetAsrService : IAsrService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ParakeetAsrService>();
    private const string SharedModelRootFolder = "PptPoc.App";

    private static readonly string[] ModelFiles =
    {
        "parakeet_melspectogram.xml", "parakeet_melspectogram.bin",
        "parakeet_encoder.xml", "parakeet_encoder.bin",
        "parakeet_decoder.xml", "parakeet_decoder.bin",
        "parakeet_joint.xml", "parakeet_joint.bin",
        "parakeet_vocab.json"
    };

    private static readonly Dictionary<string, long> ExpectedFileSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "parakeet_melspectogram.xml", 34778 },
        { "parakeet_melspectogram.bin", 66700 },
        { "parakeet_encoder.xml", 2087694 },
        { "parakeet_encoder.bin", 1185869036 },
        { "parakeet_decoder.xml", 35456 },
        { "parakeet_decoder.bin", 14429520 },
        { "parakeet_joint.xml", 13742 },
        { "parakeet_joint.bin", 3452956 },
        { "parakeet_vocab.json", 18762 }
    };

    private const string HfBaseUrl = "https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v2-ov/resolve/main/";
    private const int BlankTokenId = 1024;
    private const int MelBins = 128;
    private const int SampleRate = 16000;
    private static readonly int[] DurationBins = [1, 2, 3, 4];

    private readonly AppConfig _config;
    private readonly object _inferLock = new();

    private OVCore? _core;
    private CompiledModel? _preprocModel;
    private CompiledModel? _encoderModel;
    private CompiledModel? _decoderModel;
    private CompiledModel? _jointModel;

    private int _preprocAudioInputIdx;
    private int _preprocLengthInputIdx;
    private int _preprocMelOutputIdx;

    private int _encoderMelInputIdx;
    private int _encoderLenInputIdx;
    private int _encoderOutIdx;
    private int _encoderOutLenIdx;

    private int _decoderTargetsInputIdx;
    private int _decoderHInIdx;
    private int _decoderCInIdx;
    private int _decoderOutIdx;
    private int _decoderHOutIdx;
    private int _decoderCOutIdx;

    private int _jointEncInputIdx;
    private int _jointDecInputIdx;
    private int _jointLogitsOutputIdx;

    private int _encoderExpectedFrames = 1250;
    private int _encoderHiddenSize;
    private int _decoderHiddenSize;

    private string[] _vocab = [];
    private float[]? _hState;
    private float[]? _cState;
    private int _lastToken = BlankTokenId;

    private bool _disposed;

    public event Action<double, string>? DownloadProgressChanged;

    public bool IsReady { get; private set; }

    public ParakeetAsrService(AppConfig config)
    {
        _config = config;
    }

    public void SetVocabularyHints(IReadOnlyList<string> keywords)
    {
        // Parakeet-TDT runtime does not currently support prompt injection.
    }

    public async Task InitializeAsync(string modelPath, string openVinoDevice)
    {
        ThrowIfDisposed();

        var configuredPath = string.IsNullOrWhiteSpace(modelPath) ? "models/parakeet" : modelPath;
        var resolvedPath = ResolveStableModelPath(configuredPath);
        var modelDir = Path.GetFileName(resolvedPath).Equals("parakeet", StringComparison.OrdinalIgnoreCase)
            ? resolvedPath
            : Path.Combine(resolvedPath, "parakeet");
        Directory.CreateDirectory(modelDir);

        Log.Information("Initializing Parakeet ASR. ModelDir={ModelDir}, Device={Device}", modelDir, openVinoDevice);

        var filesToDownload = GetInvalidOrMissingFiles(modelDir, forceDownload: false);
        await DownloadModelFilesAsync(modelDir, filesToDownload);

        var deviceName = string.IsNullOrWhiteSpace(openVinoDevice) ? "AUTO" : openVinoDevice.ToUpperInvariant();
        try
        {
            await Task.Run(() => InitializeOpenVinoModels(modelDir, deviceName));
        }
        catch (OpenVINOException ex) when (ex.Message.Contains("Incorrect weights in bin file", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning(ex, "Parakeet model files appear corrupted despite size checks. Re-downloading all model files and retrying once.");
            DisposeOpenVinoHandles();
            await Task.Delay(1000);

            var retryBaseDir = Path.GetDirectoryName(modelDir) ?? modelDir;
            var retryDir = Path.Combine(retryBaseDir, $"parakeet_repair_{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(retryDir);

            var retryFiles = GetInvalidOrMissingFiles(retryDir, forceDownload: true);
            await DownloadModelFilesAsync(retryDir, retryFiles);
            await Task.Run(() => InitializeOpenVinoModels(retryDir, deviceName));
        }

        IsReady = true;
        Log.Information("Parakeet ASR initialized. EncFrames={Frames}, EncHidden={EncHidden}, DecHidden={DecHidden}",
            _encoderExpectedFrames, _encoderHiddenSize, _decoderHiddenSize);
    }

    private static string ResolveStableModelPath(string configuredPath)
    {
        var path = configuredPath.Trim().Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var sharedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SharedModelRootFolder);

        return Path.GetFullPath(Path.Combine(sharedRoot, path));
    }

    public async Task<List<TranscriptChunk>> TranscribeAsync(float[] audioSamples)
    {
        ThrowIfDisposed();

        if (!IsReady || audioSamples.Length == 0)
            return new List<TranscriptChunk>();

        return await Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── Gold Mine #6: Non-blocking inference lock ───────────────────
            // If a previous inference is still running, return immediately instead
            // of blocking the processing loop for 60-120ms. The next loop iteration
            // will try again with a slightly larger audio window — no speech is lost.
            if (!Monitor.TryEnter(_inferLock))
            {
                Log.Debug("ASR: Skipping — previous inference still running");
                return new List<TranscriptChunk>();
            }

            try
            {
                // Each call receives an overlapping audio window — reset RNNT state.
                ResetState();

                var mel = RunPreprocessor(audioSamples);
                var (enc, validFrames) = RunEncoder(mel);
                var tokenIds = DecodeGreedy(enc, validFrames);
                var text = NormalizeDecodedText(DecodeTokens(tokenIds));

                sw.Stop();

                if (string.IsNullOrWhiteSpace(text))
                    return new List<TranscriptChunk>();

                Log.Debug("Parakeet transcribed in {Ms}ms: {Text}", sw.ElapsedMilliseconds, text);

                var now = DateTime.UtcNow;
                return new List<TranscriptChunk>
                {
                    new()
                    {
                        Text = text,
                        Start = TimeSpan.Zero,
                        End = TimeSpan.FromSeconds(audioSamples.Length / (double)SampleRate),
                        ReceivedAt = now,
                        OriginalSpeechAt = now  // Gold Mine #4: initial value; Orchestrator may override
                    }
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Parakeet transcription failed");
                return new List<TranscriptChunk>();
            }
            finally
            {
                Monitor.Exit(_inferLock);
            }
        }).ConfigureAwait(false);
    }

    private static string NormalizeDecodedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!normalized.Any(char.IsLetterOrDigit))
        {
            return string.Empty;
        }

        normalized = normalized.Trim(' ', '.', ',', ';', ':', '!', '?', '\'', '"', '-', '_', '(', ')', '[', ']');
        return normalized.Any(char.IsLetterOrDigit) ? normalized : string.Empty;
    }

    private void ResolvePortsAndShapes()
    {
        _preprocAudioInputIdx = GetPortIndex(_preprocModel!.Inputs, 0, "audio", "input", "audio_signal");
        _preprocLengthInputIdx = GetPortIndex(_preprocModel.Inputs, 1, "length", "audio_length");
        _preprocMelOutputIdx = GetPortIndex(_preprocModel.Outputs, 0, "mel", "spectrogram");

        _encoderMelInputIdx = GetPortIndex(_encoderModel!.Inputs, 0, "melspectogram");
        _encoderLenInputIdx = GetPortIndex(_encoderModel.Inputs, 1, "melspectogram_length");
        _encoderOutIdx = GetPortIndex(_encoderModel.Outputs, 0, "encoder_output");
        _encoderOutLenIdx = GetPortIndex(_encoderModel.Outputs, 1, "encoder_output_length");

        _decoderTargetsInputIdx = GetPortIndex(_decoderModel!.Inputs, 0, "targets");
        _decoderHInIdx = GetPortIndex(_decoderModel.Inputs, 1, "h_in");
        _decoderCInIdx = GetPortIndex(_decoderModel.Inputs, 2, "c_in");
        _decoderOutIdx = 0;
        _decoderHOutIdx = 1;
        _decoderCOutIdx = 2;

        _jointEncInputIdx = GetPortIndex(_jointModel!.Inputs, 0, "encoder_outputs");
        _jointDecInputIdx = GetPortIndex(_jointModel.Inputs, 1, "decoder_outputs");
        _jointLogitsOutputIdx = GetPortIndex(_jointModel.Outputs, 0, "logits");

        var encInShape = _encoderModel.Inputs[_encoderMelInputIdx].Shape;
        if (encInShape.Rank >= 3 && encInShape[2] > 0)
        {
            _encoderExpectedFrames = encInShape[2];
        }

        var encOutShape = _encoderModel.Outputs[_encoderOutIdx].Shape;
        _encoderHiddenSize = encOutShape.Rank >= 2 ? encOutShape[1] : 0;

        var decHShape = _decoderModel.Inputs[_decoderHInIdx].Shape;
        _decoderHiddenSize = decHShape.Rank >= 3 ? decHShape[2] : 0;

        if (_encoderHiddenSize <= 0 || _decoderHiddenSize <= 0)
        {
            throw new InvalidOperationException("Failed to resolve model hidden sizes.");
        }
    }

    private static int GetPortIndex(PortIndexer ports, int fallback, params string[] names)
    {
        if (ports.Count == 0)
        {
            throw new InvalidOperationException("Model has no ports to resolve.");
        }

        int safeFallback = Math.Clamp(fallback, 0, ports.Count - 1);

        if (names.Length == 0)
        {
            return safeFallback;
        }

        for (int i = 0; i < ports.Count; i++)
        {
            var name = TryGetPortName(ports, i);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (names.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        for (int i = 0; i < ports.Count; i++)
        {
            var name = TryGetPortName(ports, i);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return safeFallback;
    }

    private static string? TryGetPortName(PortIndexer ports, int index)
    {
        try
        {
            return ports[index].Name;
        }
        catch (OpenVINOException)
        {
            return null;
        }
    }

    private void LoadVocab(string vocabPath)
    {
        var json = File.ReadAllText(vocabPath);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            _vocab = doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
            return;
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<int, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (int.TryParse(prop.Name, out var id))
                {
                    map[id] = prop.Value.GetString() ?? string.Empty;
                }
            }

            var max = map.Count == 0 ? 0 : map.Keys.Max();
            _vocab = Enumerable.Range(0, max + 1).Select(i => map.TryGetValue(i, out var v) ? v : string.Empty).ToArray();
        }
    }

    private void ResetState()
    {
        _hState = new float[2 * _decoderHiddenSize];
        _cState = new float[2 * _decoderHiddenSize];
        _lastToken = BlankTokenId;
    }

    private float[,] RunPreprocessor(float[] pcm)
    {
        using var req = _preprocModel!.CreateInferRequest();

        int paddedLength = ((pcm.Length + 999) / 1000) * 1000;
        var padded = new float[paddedLength];
        Array.Copy(pcm, padded, pcm.Length);

        using var audioTensor = Tensor.FromArray(padded, new Shape([1, paddedLength]));
        req.Inputs[_preprocAudioInputIdx] = audioTensor;

        var lenType = _preprocModel.Inputs[_preprocLengthInputIdx].ElementType;
        using var lenTensor = lenType == ov_element_type_e.I64
            ? Tensor.FromArray(new long[] { pcm.Length }, new Shape([1]))
            : Tensor.FromArray(new int[] { pcm.Length }, new Shape([1]));
        req.Inputs[_preprocLengthInputIdx] = lenTensor;

        req.Run();

        using var melTensor = req.Outputs[_preprocMelOutputIdx];
        var melShape = melTensor.Shape;
        int timeFrames = melShape.Rank >= 3 ? melShape[2] : 0;

        var melData = melTensor.GetData<float>().ToArray();
        var mel = new float[MelBins, timeFrames];
        for (int b = 0; b < MelBins; b++)
        {
            for (int t = 0; t < timeFrames; t++)
            {
                mel[b, t] = melData[b * timeFrames + t];
            }
        }

        return mel;
    }

    private (float[,] Encoded, int ValidFrames) RunEncoder(float[,] mel)
    {
        using var req = _encoderModel!.CreateInferRequest();

        int actualFrames = mel.GetLength(1);
        int expectedFrames = _encoderExpectedFrames;
        int framesToCopy = Math.Min(actualFrames, expectedFrames);

        var packed = new float[MelBins * expectedFrames];
        for (int b = 0; b < MelBins; b++)
        {
            for (int t = 0; t < framesToCopy; t++)
            {
                packed[b * expectedFrames + t] = mel[b, t];
            }
        }

        using var melTensor = Tensor.FromArray(packed, new Shape([1, MelBins, expectedFrames]));
        req.Inputs[_encoderMelInputIdx] = melTensor;

        var lenType = _encoderModel.Inputs[_encoderLenInputIdx].ElementType;
        using var lenTensor = lenType == ov_element_type_e.I64
            ? Tensor.FromArray(new long[] { framesToCopy }, new Shape([1]))
            : Tensor.FromArray(new int[] { framesToCopy }, new Shape([1]));
        req.Inputs[_encoderLenInputIdx] = lenTensor;

        req.Run();

        using var encTensor = req.Outputs[_encoderOutIdx];
        var encShape = encTensor.Shape;
        int hidden = encShape[1];
        int time = encShape[2];
        var encRaw = encTensor.GetData<float>().ToArray();

        var encoded = new float[hidden, time];
        for (int h = 0; h < hidden; h++)
        {
            for (int t = 0; t < time; t++)
            {
                encoded[h, t] = encRaw[h * time + t];
            }
        }

        using var lenOut = req.Outputs[_encoderOutLenIdx];
        int validFrames = lenOut.ElementType == ov_element_type_e.I64
            ? (int)lenOut.GetData<long>()[0]
            : lenOut.GetData<int>()[0];
        validFrames = Math.Clamp(validFrames, 0, time);

        return (encoded, validFrames);
    }

    private List<int> DecodeGreedy(float[,] enc, int validFrames)
    {
        var tokens = new List<int>();
        if (validFrames == 0)
        {
            return tokens;
        }

        var h = (float[])_hState!.Clone();
        var c = (float[])_cState!.Clone();
        int lastToken = _lastToken;
        float[]? cachedDecoderOutput = null;

        int frame = 0;
        int maxTokens = 512;

        while (frame < validFrames && tokens.Count < maxTokens)
        {
            float[] decoderOut;
            if (cachedDecoderOutput != null)
            {
                decoderOut = cachedDecoderOutput;
            }
            else
            {
                var d = RunDecoder(lastToken, h, c);
                decoderOut = d.Output;
                h = d.HState;
                c = d.CState;
                cachedDecoderOutput = decoderOut;
            }

            var encFrame = new float[_encoderHiddenSize];
            for (int i = 0; i < _encoderHiddenSize; i++)
            {
                encFrame[i] = enc[i, frame];
            }

            var logits = RunJoint(encFrame, decoderOut);
            int tokenVocabSize = Math.Min(BlankTokenId + 1, logits.Length);

            int bestToken = 0;
            float bestScore = logits[0];
            for (int i = 1; i < tokenVocabSize; i++)
            {
                if (logits[i] > bestScore)
                {
                    bestScore = logits[i];
                    bestToken = i;
                }
            }

            int duration = 1;
            if (logits.Length > tokenVocabSize)
            {
                int bins = Math.Min(DurationBins.Length, logits.Length - tokenVocabSize);
                int bestDur = 0;
                float bestDurScore = logits[tokenVocabSize];
                for (int i = 1; i < bins; i++)
                {
                    if (logits[tokenVocabSize + i] > bestDurScore)
                    {
                        bestDurScore = logits[tokenVocabSize + i];
                        bestDur = i;
                    }
                }

                duration = DurationBins[bestDur];
            }

            if (bestToken != BlankTokenId)
            {
                tokens.Add(bestToken);
                lastToken = bestToken;
                cachedDecoderOutput = null;
            }

            frame = Math.Min(validFrames, frame + Math.Max(1, duration));
        }

        _hState = h;
        _cState = c;
        _lastToken = lastToken;

        return tokens;
    }

    private (float[] Output, float[] HState, float[] CState) RunDecoder(int token, float[] hState, float[] cState)
    {
        using var req = _decoderModel!.CreateInferRequest();

        var targetType = _decoderModel.Inputs[_decoderTargetsInputIdx].ElementType;
        using var tokenTensor = targetType == ov_element_type_e.I64
            ? Tensor.FromArray(new long[] { token }, new Shape([1, 1]))
            : Tensor.FromArray(new int[] { token }, new Shape([1, 1]));

        using var hIn = Tensor.FromArray(hState, new Shape([2, 1, _decoderHiddenSize]));
        using var cIn = Tensor.FromArray(cState, new Shape([2, 1, _decoderHiddenSize]));

        req.Inputs[_decoderTargetsInputIdx] = tokenTensor;
        req.Inputs[_decoderHInIdx] = hIn;
        req.Inputs[_decoderCInIdx] = cIn;

        req.Run();

        using var dOut = req.Outputs[_decoderOutIdx];
        using var hOut = req.Outputs[_decoderHOutIdx];
        using var cOut = req.Outputs[_decoderCOutIdx];

        return (dOut.GetData<float>().ToArray(), hOut.GetData<float>().ToArray(), cOut.GetData<float>().ToArray());
    }

    private float[] RunJoint(float[] encFrame, float[] decoderOut)
    {
        using var req = _jointModel!.CreateInferRequest();

        using var encTensor = Tensor.FromArray(encFrame, new Shape([1, 1, _encoderHiddenSize]));
        using var decTensor = Tensor.FromArray(decoderOut, new Shape([1, 1, _decoderHiddenSize]));

        req.Inputs[_jointEncInputIdx] = encTensor;
        req.Inputs[_jointDecInputIdx] = decTensor;

        req.Run();
        using var logits = req.Outputs[_jointLogitsOutputIdx];
        return logits.GetData<float>().ToArray();
    }

    private string DecodeTokens(IReadOnlyList<int> tokenIds)
    {
        if (tokenIds.Count == 0 || _vocab.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var id in tokenIds)
        {
            if (id < 0 || id >= _vocab.Length)
            {
                continue;
            }

            var piece = _vocab[id];
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            if (piece[0] == '\u2581')
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                if (piece.Length > 1)
                {
                    sb.Append(piece, 1, piece.Length - 1);
                }
            }
            else
            {
                sb.Append(piece);
            }
        }

        return sb.ToString();
    }

    private List<string> GetInvalidOrMissingFiles(string modelDir, bool forceDownload)
    {
        if (forceDownload) return ModelFiles.ToList();

        var missingOrCorrupt = new List<string>();
        foreach (var file in ModelFiles)
        {
            var path = Path.Combine(modelDir, file);
            if (!File.Exists(path))
            {
                missingOrCorrupt.Add(file);
                continue;
            }

            if (ExpectedFileSizes.TryGetValue(file, out var expectedSize))
            {
                var actualSize = new FileInfo(path).Length;
                if (actualSize != expectedSize)
                {
                    Log.Warning("File {File} size mismatch. Expected: {Expected}, Actual: {Actual}", file, expectedSize, actualSize);
                    missingOrCorrupt.Add(file);
                }
            }
        }
        return missingOrCorrupt;
    }

    private async Task DownloadModelFilesAsync(string modelDir, List<string> targets)
    {
        if (targets.Count == 0)
        {
            DownloadProgressChanged?.Invoke(100, "Models already downloaded and verified.");
            return;
        }

        var handler = new HttpClientHandler();

        var proxy = Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
        if (proxy != null)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(60)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "PptPoc/1.0");

        for (int i = 0; i < targets.Count; i++)
        {
            var file = targets[i];
            var url = HfBaseUrl + file;
            var dst = Path.Combine(modelDir, file);
            
            Log.Information("Downloading Parakeet asset {File} from {Url}", file, url);

            try
            {
                if (File.Exists(dst))
                {
                    File.Delete(dst);
                }

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var totalBytesRead = 0L;
                var buffer = new byte[8192];
                int bytesRead;

                await using var net = await response.Content.ReadAsStreamAsync();
                await using var fs = File.Create(dst);

                while ((bytesRead = await net.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if (totalBytes != -1)
                    {
                        var fileProgress = (totalBytesRead / (double)totalBytes) * 100;
                        var overallProgress = ((i + (totalBytesRead / (double)totalBytes)) / targets.Count) * 100;
                        DownloadProgressChanged?.Invoke(overallProgress, $"Downloading {file} ({fileProgress:F1}%)");
                    }
                    else
                    {
                        var overallProgress = (i / (double)targets.Count) * 100;
                        DownloadProgressChanged?.Invoke(overallProgress, $"Downloading {file} ({totalBytesRead / 1024 / 1024:F1} MB)");
                    }
                }

                Log.Information("Finished downloading {File}", file);
            }
            catch (HttpRequestException ex)
            {
                Log.Error(ex, "Network or proxy error downloading {File}", file);
                DownloadProgressChanged?.Invoke(0, $"Network/Proxy Error downloading {file}");
                throw new InvalidOperationException($"Network or proxy error while trying to download {file}. Check your proxy settings.", ex);
            }
            catch (TaskCanceledException ex)
            {
                Log.Error(ex, "Timeout downloading {File}", file);
                DownloadProgressChanged?.Invoke(0, $"Timeout downloading {file}");
                throw new InvalidOperationException($"Download timed out for {file}.", ex);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download Parakeet asset {File}", file);
                DownloadProgressChanged?.Invoke(0, $"Error: {ex.Message}");
                throw;
            }
        }
        DownloadProgressChanged?.Invoke(100, "Download complete.");
    }

    private void InitializeOpenVinoModels(string modelDir, string device)
    {
        DisposeOpenVinoHandles();

        _core = new OVCore();
        
        var cacheDir = Path.Combine(modelDir, "cache");
        Directory.CreateDirectory(cacheDir);
        _core.SetDeviceProperty(device, "CACHE_DIR", cacheDir);
        if (device != "CPU") 
        {
            _core.SetDeviceProperty("CPU", "CACHE_DIR", cacheDir);
        }

        _preprocModel = _core.CompileModel(
            Path.Combine(modelDir, "parakeet_melspectogram.xml"),
            new DeviceOptions("CPU"));

        string actualDevice = device;
        try
        {
            _encoderModel = _core.CompileModel(Path.Combine(modelDir, "parakeet_encoder.xml"), new DeviceOptions(device));
            _decoderModel = _core.CompileModel(Path.Combine(modelDir, "parakeet_decoder.xml"), new DeviceOptions(device));
            _jointModel = _core.CompileModel(Path.Combine(modelDir, "parakeet_joint.xml"), new DeviceOptions(device));
            Log.Information("Parakeet models compiled on device: {Device}", device);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize on device {Device}, falling back to CPU", device);
            actualDevice = "CPU";
            _encoderModel = _core.CompileModel(Path.Combine(modelDir, "parakeet_encoder.xml"), new DeviceOptions("CPU"));
            _decoderModel = _core.CompileModel(Path.Combine(modelDir, "parakeet_decoder.xml"), new DeviceOptions("CPU"));
            _jointModel = _core.CompileModel(Path.Combine(modelDir, "parakeet_joint.xml"), new DeviceOptions("CPU"));
            Log.Information("Parakeet models compiled on fallback device: CPU");
        }

        ResolvePortsAndShapes();
        LoadVocab(Path.Combine(modelDir, "parakeet_vocab.json"));
        ResetState();
    }

    private void DisposeOpenVinoHandles()
    {
        _preprocModel?.Dispose();
        _preprocModel = null;

        _encoderModel?.Dispose();
        _encoderModel = null;

        _decoderModel?.Dispose();
        _decoderModel = null;

        _jointModel?.Dispose();
        _jointModel = null;

        _core?.Dispose();
        _core = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ParakeetAsrService));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        IsReady = false;
        DisposeOpenVinoHandles();
    }
}
