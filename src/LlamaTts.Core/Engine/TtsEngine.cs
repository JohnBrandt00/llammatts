using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LlamaTts.Core.Prompting;
using LlamaTts.Core.Speakers;
using LlamaTts.Core.Text;

namespace LlamaTts.Core.Engine;

public sealed class SamplerSettings
{
    public float Temperature { get; set; } = 0.4f;
    public float RepetitionPenalty { get; set; } = 1.1f;
    /// <summary>OuteTTS 1.0 requires the repetition penalty window to be 64 recent tokens.</summary>
    public int RepetitionRange { get; set; } = 64;
    public int TopK { get; set; } = 40;
    public float TopP { get; set; } = 0.9f;
    public float MinP { get; set; } = 0.05f;
    public uint Seed { get; set; } = 0; // 0 -> random
}

public sealed class GenerationProgress
{
    public int GeneratedTokens { get; init; }
    public int MaxTokens { get; init; }
    public int ChunkIndex { get; init; }
    public int ChunkCount { get; init; }
    /// <summary>True for the final (exact) report of a chunk.</summary>
    public bool ChunkDone { get; init; }
}

/// <summary>
/// Runs the Llama-OuteTTS-1.0 GGUF model with llama.cpp (via LLamaSharp) and produces the two
/// DAC codebook streams for a given prompt. Thread-safe via an internal lock; one generation at a time.
/// </summary>
public sealed class TtsEngine : IDisposable
{
    private readonly LLamaWeights _model;
    private readonly LLamaContext _context;
    private readonly Dictionary<int, int> _c1Tokens = new(); // token id -> code value
    private readonly Dictionary<int, int> _c2Tokens = new();
    private readonly LLamaToken _audioEndToken;
    private readonly object _lock = new();

    public int ContextSize { get; }
    public string ModelPath { get; }

    private TtsEngine(LLamaWeights model, LLamaContext context, string modelPath)
    {
        _model = model;
        _context = context;
        ModelPath = modelPath;
        ContextSize = (int)context.ContextSize;

        // Map the <|c1_N|>/<|c2_N|> special tokens. Codebook size is 1024 but the vocab
        // defines 1025 of each; build the map from actual tokenizer output.
        for (int i = 0; i <= 1024; i++)
        {
            _c1Tokens[(int)SingleToken(SpecialTokens.C1(i))] = i;
            _c2Tokens[(int)SingleToken(SpecialTokens.C2(i))] = i;
        }
        _audioEndToken = SingleToken(SpecialTokens.AudioEnd);
    }

    private LLamaToken SingleToken(string text)
    {
        var tokens = _context.Tokenize(text, addBos: false, special: true);
        if (tokens.Length != 1)
            throw new InvalidOperationException($"Expected '{text}' to be a single token, got {tokens.Length}. Wrong model file?");
        return tokens[0];
    }

    public static TtsEngine Load(string modelPath, int contextSize = 8192, int gpuLayerCount = 0, int? threads = null)
    {
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = gpuLayerCount,
            BatchSize = 1024,
        };
        if (threads.HasValue) parameters.Threads = threads;

        var model = LLamaWeights.LoadFromFile(parameters);
        var context = model.CreateContext(parameters);
        return new TtsEngine(model, context, modelPath);
    }

    /// <summary>
    /// Generate DAC codes for the given text. Long text is split into chunks (10-30 words), each
    /// generated independently with the same speaker prefix, mirroring OuteTTS chunked generation.
    /// </summary>
    public (List<int> C1, List<int> C2) GenerateCodes(
        string text,
        Speaker? speaker,
        SamplerSettings? settings = null,
        Action<GenerationProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= new SamplerSettings();
        var chunks = TextChunker.Chunk(text);
        if (chunks.Count == 0) throw new ArgumentException("No speakable text after normalization.", nameof(text));

        var c1 = new List<int>();
        var c2 = new List<int>();

        lock (_lock)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prompt = PromptBuilder.BuildCompletionPrompt(chunks[i], speaker);
                GenerateChunk(prompt, settings, c1, c2, i, chunks.Count, onProgress, cancellationToken);
            }
        }

        int n = Math.Min(c1.Count, c2.Count);
        return (c1.Take(n).ToList(), c2.Take(n).ToList());
    }

    private void GenerateChunk(
        string prompt,
        SamplerSettings settings,
        List<int> c1,
        List<int> c2,
        int chunkIndex,
        int chunkCount,
        Action<GenerationProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var promptTokens = _context.Tokenize(prompt, addBos: false, special: true);
        if (promptTokens.Length >= ContextSize - 8)
            throw new InvalidOperationException($"Prompt too long ({promptTokens.Length} tokens) for context {ContextSize}. Use a shorter speaker reference clip.");

        // Fresh KV cache per chunk
        _context.NativeHandle.MemoryClear(true);

        using var sampler = new DefaultSamplingPipeline
        {
            Temperature = settings.Temperature,
            RepeatPenalty = settings.RepetitionPenalty,
            PenaltyCount = settings.RepetitionRange,
            TopK = settings.TopK,
            TopP = settings.TopP,
            MinP = settings.MinP,
            Seed = settings.Seed == 0 ? (uint)Random.Shared.Next(1, int.MaxValue) : settings.Seed,
        };

        // Feed prompt in batch-sized pieces; request logits only for the final token.
        var batch = new LLamaBatch();
        int batchSize = (int)_context.BatchSize;
        int pos = 0;
        for (int start = 0; start < promptTokens.Length; start += batchSize)
        {
            int len = Math.Min(batchSize, promptTokens.Length - start);
            batch.Clear();
            for (int i = 0; i < len; i++)
            {
                bool isLast = start + i == promptTokens.Length - 1;
                batch.Add(promptTokens[start + i], pos++, LLamaSeqId.Zero, isLast);
            }
            Decode(batch);
        }

        // Seed the repetition-penalty window with the prompt tail, like llama.cpp's last_n ring buffer.
        foreach (var t in promptTokens.TakeLast(settings.RepetitionRange))
            sampler.Accept(t);

        int maxNewTokens = ContextSize - promptTokens.Length - 8;
        int generated = 0;

        while (generated < maxNewTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var token = sampler.Sample(_context.NativeHandle, batch.TokenCount - 1);
            if (token == _audioEndToken || token.IsEndOfGeneration(_model.Vocab))
                break;

            int id = (int)token;
            if (_c1Tokens.TryGetValue(id, out var v1)) c1.Add(v1);
            else if (_c2Tokens.TryGetValue(id, out var v2)) c2.Add(v2);

            batch.Clear();
            batch.Add(token, pos++, LLamaSeqId.Zero, true);
            Decode(batch);

            generated++;
            if (generated % 32 == 0)
                onProgress?.Invoke(new GenerationProgress
                {
                    GeneratedTokens = generated,
                    MaxTokens = maxNewTokens,
                    ChunkIndex = chunkIndex,
                    ChunkCount = chunkCount,
                });
        }

        onProgress?.Invoke(new GenerationProgress
        {
            GeneratedTokens = generated,
            MaxTokens = maxNewTokens,
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
            ChunkDone = true,
        });
    }

    private void Decode(LLamaBatch batch)
    {
        var result = _context.Decode(batch);
        if (result != DecodeResult.Ok)
            throw new InvalidOperationException($"llama.cpp decode failed: {result}");
    }

    public void Dispose()
    {
        _context.Dispose();
        _model.Dispose();
    }
}
