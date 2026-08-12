using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using PptPoc.Core.Interfaces;
using Serilog;

namespace PptPoc.Matching;

public class SemanticEmbeddingService : ISemanticEmbeddingService, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SemanticEmbeddingService>();
    private const string SharedModelRootFolder = "Intel_Smart_Presenter_Assistant";
    private const string HfBaseUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/";
    private const string ModelFile = "model_quantized.onnx"; 
    private const int MaxSequenceLength = 512;
    
    // MiniLM uses standard bert vocab
    private const string VocabUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    public bool IsReady => _session != null && _tokenizer != null;

    public async Task InitializeAsync(string modelDir)
    {
        modelDir = ResolveStableModelPath(modelDir, "models/minilm");
        Directory.CreateDirectory(modelDir);
        var modelPath = Path.Combine(modelDir, ModelFile);
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        // Verify or download model
        await DownloadFileIfMissingAsync(HfBaseUrl + ModelFile, modelPath);
        
        // Verify or download tokenizer vocab
        await DownloadFileIfMissingAsync(VocabUrl, vocabPath);

        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            throw new FileNotFoundException($"Model or vocab not found in {modelDir}. Download might have failed.");
        }

        // Load Tokenizer using BertTokenizer
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true });

        // Initialize ONNX inference session
        var options = new SessionOptions();
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(modelPath, options);

        Log.Information("Semantic Embedding Service initialized successfully.");
    }

    private static string ResolveStableModelPath(string configuredPath, string defaultRelativePath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? defaultRelativePath : configuredPath.Trim();
        path = path.Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var sharedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SharedModelRootFolder);

        return Path.GetFullPath(Path.Combine(sharedRoot, path));
    }

    private async Task DownloadFileIfMissingAsync(string url, string destPath)
    {
        if (File.Exists(destPath))
        {
            var size = new FileInfo(destPath).Length;
            if (size > 10 * 1024) // sanity check
            {
                Log.Debug($"File {Path.GetFileName(destPath)} already exists (size: {size}). Skipping download.");
                return;
            }
            File.Delete(destPath);
        }

        Log.Information($"Downloading {Path.GetFileName(destPath)} from {url} ...");
        
        var handler = new HttpClientHandler();
        var proxyUrl = Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
        if (!string.IsNullOrEmpty(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var fs = File.Create(destPath);
            await response.Content.CopyToAsync(fs);
            Log.Information($"Successfully downloaded {Path.GetFileName(destPath)}.");
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, $"Network or proxy error while downloading {Path.GetFileName(destPath)} from {url}");
            if (File.Exists(destPath)) File.Delete(destPath);
            throw new InvalidOperationException($"Failed to download {Path.GetFileName(destPath)}. Check your network or proxy settings.", ex);
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, $"Download timed out for {Path.GetFileName(destPath)}");
            if (File.Exists(destPath)) File.Delete(destPath);
            throw new InvalidOperationException($"Download timed out for {Path.GetFileName(destPath)}.", ex);
        }
    }

    public float[] GenerateEmbedding(string text)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();

        // Tokenize
        var tokenIds = _tokenizer!.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true);
        if (tokenIds.Count > MaxSequenceLength)
        {
            tokenIds = tokenIds.Take(MaxSequenceLength).ToList();
        }

        var attentionMask = Enumerable.Repeat(1L, tokenIds.Count).ToArray();
        var typeIds = Enumerable.Repeat(0L, tokenIds.Count).ToArray();

        var shape = new int[] { 1, tokenIds.Count };

        // Explicitly create tensors and populate them
        var inputIdsTensor = new DenseTensor<long>(shape);
        var attentionMaskTensor = new DenseTensor<long>(shape);
        var typeIdsTensor = new DenseTensor<long>(shape);

        for (int i = 0; i < tokenIds.Count; i++)
        {
            inputIdsTensor[0, i] = tokenIds[i];
            attentionMaskTensor[0, i] = attentionMask[i];
            typeIdsTensor[0, i] = typeIds[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", typeIdsTensor)
        };

        using var results = _session!.Run(inputs);
        var lastHiddenState = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();

        // Mean Pooling based on attention mask
        int seqLength = tokenIds.Count;
        int hiddenSize = lastHiddenState.Dimensions[2];
        float[] pooled = new float[hiddenSize];
        int validTokens = 0;

        for (int i = 0; i < seqLength; i++)
        {
            if (attentionMask[i] == 1)
            {
                validTokens++;
                for (int j = 0; j < hiddenSize; j++)
                {
                    pooled[j] += lastHiddenState[0, i, j];
                }
            }
        }

        // Average and normalize (L2)
        float sumSquares = 0;
        for (int j = 0; j < hiddenSize; j++)
        {
            if (validTokens > 0)
            {
                pooled[j] /= validTokens;
            }
            sumSquares += pooled[j] * pooled[j];
        }

        if (sumSquares > 0)
        {
            float norm = (float)Math.Sqrt(sumSquares);
            for (int j = 0; j < hiddenSize; j++)
            {
                pooled[j] /= norm;
            }
        }

        return pooled;
    }

    public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA == null || vectorB == null || vectorA.Length != vectorB.Length || vectorA.Length == 0)
            return 0.0;

        double dotProduct = 0.0;
        for (int i = 0; i < vectorA.Length; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
        }

        return dotProduct;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}