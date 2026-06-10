using System.Collections.Concurrent;

namespace LlamaTts.Core.Download;

public sealed record ModelFileSpec(string Name, string Url, string LocalFileName);

public sealed class DownloadProgress
{
    public string Name { get; init; } = "";
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public string Status { get; set; } = "pending"; // pending | downloading | done | error
    public string? Error { get; set; }
    public double Percent => TotalBytes > 0 ? Math.Round(BytesReceived * 100.0 / TotalBytes, 1) : 0;
}

/// <summary>Downloads model files from Hugging Face with progress reporting and resume-by-restart.</summary>
public sealed class ModelDownloader
{
    public const string GgufRepo = "aoiandroid/Llama-OuteTTS-1.0-1B-GGUF";
    public const string DacRepo = "OuteAI/DAC-speech-v1.0-ONNX";

    public static ModelFileSpec Gguf(string quant = "Q4_K_M") => new(
        $"Llama-OuteTTS-1.0-1B ({quant})",
        $"https://huggingface.co/{GgufRepo}/resolve/main/Llama-OuteTTS-1.0-1B-{quant}.gguf",
        $"Llama-OuteTTS-1.0-1B-{quant}.gguf");

    public static readonly ModelFileSpec DacDecoder = new(
        "DAC decoder (ONNX)",
        $"https://huggingface.co/{DacRepo}/resolve/main/onnx/decoder_model.onnx",
        "dac_decoder.onnx");

    public static readonly ModelFileSpec DacEncoder = new(
        "DAC encoder (ONNX)",
        $"https://huggingface.co/{DacRepo}/resolve/main/onnx/encoder_model.onnx",
        "dac_encoder.onnx");

    private readonly string _modelsDir;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, DownloadProgress> _progress = new();

    public ModelDownloader(string modelsDir, HttpClient? http = null)
    {
        _modelsDir = modelsDir;
        Directory.CreateDirectory(modelsDir);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromHours(2) };
    }

    public string PathFor(ModelFileSpec spec) => Path.Combine(_modelsDir, spec.LocalFileName);
    public bool Exists(ModelFileSpec spec) => File.Exists(PathFor(spec));
    public IReadOnlyDictionary<string, DownloadProgress> Progress => _progress;

    public async Task<string> EnsureAsync(ModelFileSpec spec, CancellationToken cancellationToken = default)
    {
        var path = PathFor(spec);
        if (File.Exists(path))
        {
            _progress[spec.LocalFileName] = new DownloadProgress
            {
                Name = spec.Name,
                Status = "done",
                BytesReceived = new FileInfo(path).Length,
                TotalBytes = new FileInfo(path).Length,
            };
            return path;
        }

        var progress = _progress.GetOrAdd(spec.LocalFileName, _ => new DownloadProgress { Name = spec.Name });
        progress.Status = "downloading";
        progress.Error = null;

        var tempPath = path + ".part";
        try
        {
            using var response = await _http.GetAsync(spec.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            progress.TotalBytes = response.Content.Headers.ContentLength ?? 0;

            await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var dst = File.Create(tempPath))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await src.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress.BytesReceived += read;
                }
            }

            File.Move(tempPath, path, overwrite: true);
            progress.Status = "done";
            return path;
        }
        catch (Exception ex)
        {
            progress.Status = "error";
            progress.Error = ex.Message;
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
