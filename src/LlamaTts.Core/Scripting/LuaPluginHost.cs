using System.Text;
using LlamaTts.Core.Audio;
using LlamaTts.Core.Dac;
using LlamaTts.Core.Engine;
using LlamaTts.Core.Speakers;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Serialization.Json;

namespace LlamaTts.Core.Scripting;

public sealed class LuaPlugin
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public Script? Script { get; set; }
    public string? LoadError { get; set; }
    public DateTime LoadedAt { get; set; }
    public readonly object OutputLock = new();
    public List<string> Output { get; } = new();

    public void AppendOutput(string line)
    {
        lock (OutputLock)
        {
            Output.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (Output.Count > 500) Output.RemoveRange(0, Output.Count - 500);
        }
    }
}

public sealed record LuaRunResult(bool Ok, string? Error, string[] Output);

/// <summary>
/// Hosts Lua plugins (MoonSharp, soft sandbox: no io/os.execute). Each *.lua file in the plugins
/// directory becomes a plugin. The file body runs at load time; an optional global run() is the
/// entry point for manual runs; optional on_generate_start/on_generate_complete globals receive
/// generation events.
/// </summary>
public sealed class LuaPluginHost
{
    private readonly ILuaAppContext _app;
    private readonly Dictionary<string, LuaPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public LuaPluginHost(ILuaAppContext app)
    {
        _app = app;
        Directory.CreateDirectory(app.PluginsDir);
    }

    public IReadOnlyList<LuaPlugin> Plugins
    {
        get { lock (_lock) return _plugins.Values.OrderBy(p => p.Name).ToList(); }
    }

    public LuaPlugin? Get(string name)
    {
        lock (_lock) return _plugins.GetValueOrDefault(name);
    }

    public void ReloadAll()
    {
        lock (_lock)
        {
            _plugins.Clear();
            foreach (var file in Directory.EnumerateFiles(_app.PluginsDir, "*.lua").OrderBy(f => f))
                LoadPlugin(file);
        }
    }

    public LuaPlugin Reload(string name)
    {
        lock (_lock)
        {
            var path = Path.Combine(_app.PluginsDir, name + ".lua");
            if (!File.Exists(path)) throw new FileNotFoundException($"Plugin not found: {name}");
            return LoadPlugin(path);
        }
    }

    /// <summary>Forget a plugin (does not delete its file).</summary>
    public bool Remove(string name)
    {
        lock (_lock) return _plugins.Remove(name);
    }

    private LuaPlugin LoadPlugin(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var plugin = new LuaPlugin { Name = name, Path = path };
        _plugins[name] = plugin;

        try
        {
            var script = CreateScript(plugin);
            script.DoString(File.ReadAllText(path), codeFriendlyName: name);
            plugin.Script = script;
            plugin.LoadedAt = DateTime.UtcNow;

            CallIfDefined(plugin, "on_load");
            _app.Log("info", $"Loaded plugin '{name}'");
        }
        catch (Exception ex)
        {
            plugin.LoadError = Describe(ex);
            plugin.AppendOutput("LOAD ERROR: " + plugin.LoadError);
            _app.Log("error", $"Plugin '{name}' failed to load: {plugin.LoadError}");
        }

        return plugin;
    }

    /// <summary>Run a plugin's run() function (or re-run its body when no run() is defined).</summary>
    public LuaRunResult Run(string name, CancellationToken cancellationToken = default)
    {
        LuaPlugin? plugin;
        lock (_lock) plugin = _plugins.GetValueOrDefault(name);
        if (plugin is null) return new LuaRunResult(false, $"Plugin not found: {name}", Array.Empty<string>());

        lock (plugin.OutputLock) plugin.Output.Clear();

        try
        {
            if (plugin.Script is null || plugin.LoadError is not null)
            {
                // Try a fresh load before running
                lock (_lock) plugin = LoadPlugin(plugin.Path);
                if (plugin.LoadError is not null)
                    return new LuaRunResult(false, plugin.LoadError, SnapshotOutput(plugin));
            }

            var run = plugin.Script!.Globals.Get("run");
            if (run.Type == DataType.Function)
                plugin.Script.Call(run);
            else
                plugin.Script.DoString(File.ReadAllText(plugin.Path), codeFriendlyName: plugin.Name);

            return new LuaRunResult(true, null, SnapshotOutput(plugin));
        }
        catch (Exception ex)
        {
            var error = Describe(ex);
            plugin.AppendOutput("ERROR: " + error);
            return new LuaRunResult(false, error, SnapshotOutput(plugin));
        }
    }

    private static string[] SnapshotOutput(LuaPlugin plugin)
    {
        lock (plugin.OutputLock) return plugin.Output.ToArray();
    }

    /// <summary>Fire an event (e.g. on_generate_complete) on every loaded plugin that defines it.</summary>
    public void FireEvent(string functionName, Dictionary<string, object?> payload)
    {
        List<LuaPlugin> plugins;
        lock (_lock) plugins = _plugins.Values.Where(p => p.Script is not null).ToList();

        foreach (var plugin in plugins)
        {
            try
            {
                var fn = plugin.Script!.Globals.Get(functionName);
                if (fn.Type != DataType.Function) continue;
                var table = new Table(plugin.Script);
                foreach (var (k, v) in payload) table[k] = v;
                plugin.Script.Call(fn, table);
            }
            catch (Exception ex)
            {
                plugin.AppendOutput($"{functionName} ERROR: {Describe(ex)}");
                _app.Log("warn", $"Plugin '{plugin.Name}' {functionName} failed: {Describe(ex)}");
            }
        }
    }

    private void CallIfDefined(LuaPlugin plugin, string functionName)
    {
        var fn = plugin.Script!.Globals.Get(functionName);
        if (fn.Type == DataType.Function) plugin.Script.Call(fn);
    }

    private static string Describe(Exception ex) =>
        ex is InterpreterException ie ? ie.DecoratedMessage ?? ie.Message : ex.Message;

    // ------------------------------------------------------------------ API surface

    private Script CreateScript(LuaPlugin plugin)
    {
        var script = new Script(CoreModules.Preset_SoftSandbox);
        script.Options.DebugPrint = s =>
        {
            plugin.AppendOutput(s);
            _app.Log("info", $"[{plugin.Name}] {s}");
        };

        script.Globals["app"] = BuildAppTable(script);
        script.Globals["log"] = BuildLogTable(script, plugin);
        script.Globals["tts"] = BuildTtsTable(script, plugin);
        script.Globals["speakers"] = BuildSpeakersTable(script, plugin);
        script.Globals["audio"] = BuildAudioTable(script);
        script.Globals["json"] = BuildJsonTable(script);
        return script;
    }

    private Table BuildAppTable(Script script)
    {
        var t = new Table(script)
        {
            ["output_dir"] = _app.OutputDir,
            ["speakers_dir"] = _app.SpeakersDir,
            ["plugins_dir"] = _app.PluginsDir,
        };
        return t;
    }

    private Table BuildLogTable(Script script, LuaPlugin plugin)
    {
        void Write(string level, string msg)
        {
            plugin.AppendOutput($"{level.ToUpperInvariant()}: {msg}");
            _app.Log(level, $"[{plugin.Name}] {msg}");
        }

        var t = new Table(script)
        {
            ["info"] = (Action<string>)(m => Write("info", m)),
            ["warn"] = (Action<string>)(m => Write("warn", m)),
            ["error"] = (Action<string>)(m => Write("error", m)),
        };
        return t;
    }

    private Table BuildTtsTable(Script script, LuaPlugin plugin)
    {
        var t = new Table(script)
        {
            // tts.generate{ text=..., speaker=..., file=..., temperature=..., top_k=..., top_p=...,
            //               min_p=..., repetition_penalty=..., seed=... } -> { file, seconds, text }
            ["generate"] = (Func<Table, Table>)(args => Generate(script, plugin, args)),

            // tts.speak(text [, speakerName]) -> { file, seconds, text }
            ["speak"] = (Func<string, string?, Table>)((text, speakerName) =>
            {
                var args = new Table(script) { ["text"] = text };
                if (speakerName is not null) args["speaker"] = speakerName;
                return Generate(script, plugin, args);
            }),
        };
        return t;
    }

    private Table Generate(Script script, LuaPlugin plugin, Table args)
    {
        var text = args.Get("text").CastToString()
            ?? throw new ScriptRuntimeException("tts.generate: 'text' is required");

        var speakerArg = args.Get("speaker");
        Speaker? speaker = speakerArg.Type switch
        {
            DataType.Nil or DataType.Void => _app.ResolveSpeaker(null),
            DataType.String => _app.ResolveSpeaker(speakerArg.String),
            DataType.Table => _app.ResolveSpeaker(speakerArg.Table.Get("name").CastToString()),
            _ => throw new ScriptRuntimeException("tts.generate: 'speaker' must be a string or speaker table"),
        };

        var settings = new SamplerSettings();
        if (TryNumber(args, "temperature", out var temp)) settings.Temperature = (float)temp;
        if (TryNumber(args, "top_k", out var topK)) settings.TopK = (int)topK;
        if (TryNumber(args, "top_p", out var topP)) settings.TopP = (float)topP;
        if (TryNumber(args, "min_p", out var minP)) settings.MinP = (float)minP;
        if (TryNumber(args, "repetition_penalty", out var rp)) settings.RepetitionPenalty = (float)rp;
        if (TryNumber(args, "seed", out var seed)) settings.Seed = (uint)seed;

        var file = args.Get("file").CastToString();
        file = file is null
            ? _app.NewOutputPath($"plugin-{plugin.Name}")
            : Path.GetFullPath(Path.Combine(_app.OutputDir, file));
        if (!file.StartsWith(Path.GetFullPath(_app.OutputDir), StringComparison.OrdinalIgnoreCase))
            throw new ScriptRuntimeException("tts.generate: 'file' must stay inside the output directory");

        plugin.AppendOutput($"generating: \"{Truncate(text, 80)}\"");
        var pipeline = _app.GetPipeline();
        var seconds = pipeline.SynthesizeToFile(file, text, speaker, settings);
        plugin.AppendOutput($"done: {Path.GetFileName(file)} ({seconds:F1}s audio)");

        _app.NotifyGenerated(new GenerationRecord(file, text, seconds, $"plugin:{plugin.Name}"));

        return new Table(script)
        {
            ["file"] = file,
            ["name"] = Path.GetFileName(file),
            ["seconds"] = seconds,
            ["text"] = text,
        };
    }

    private Table BuildSpeakersTable(Script script, LuaPlugin plugin)
    {
        var t = new Table(script)
        {
            ["list"] = (Func<Table>)(() =>
            {
                var result = new Table(script);
                int i = 1;
                result[i++] = "default";
                foreach (var f in Directory.EnumerateFiles(_app.SpeakersDir, "*.json").OrderBy(f => f))
                    result[i++] = Path.GetFileNameWithoutExtension(f);
                return result;
            }),

            ["load"] = (Func<string, Table>)(name =>
            {
                var speaker = _app.ResolveSpeaker(name);
                return SpeakerToTable(script, name, speaker);
            }),

            ["default"] = (Func<Table>)(() => SpeakerToTable(script, "default", _app.ResolveSpeaker(null))),

            // speakers.clone{ audio="path/to.wav", transcript="...", name="my-voice" } -> speaker table
            ["clone"] = (Func<Table, Table>)(args =>
            {
                var audioPath = args.Get("audio").CastToString()
                    ?? throw new ScriptRuntimeException("speakers.clone: 'audio' (wav path) is required");
                var transcript = args.Get("transcript").CastToString()
                    ?? throw new ScriptRuntimeException("speakers.clone: 'transcript' is required");
                var name = args.Get("name").CastToString()
                    ?? Path.GetFileNameWithoutExtension(audioPath);

                if (!Path.IsPathRooted(audioPath))
                    audioPath = Path.Combine(_app.OutputDir, audioPath);
                if (!File.Exists(audioPath))
                    throw new ScriptRuntimeException($"speakers.clone: audio file not found: {audioPath}");

                plugin.AppendOutput($"cloning voice from {Path.GetFileName(audioPath)}...");
                var codec = _app.GetPipeline().Codec;
                var speaker = SpeakerFactory.CreateFromWavFile(audioPath, transcript, codec);
                var savePath = Path.Combine(_app.SpeakersDir, SanitizeName(name) + ".json");
                speaker.Save(savePath);
                plugin.AppendOutput($"saved speaker '{name}'");
                return SpeakerToTable(script, SanitizeName(name), speaker);
            }),

            // speakers.preview(name [, file]) -> { file, seconds } (decodes the stored voice sample)
            ["preview"] = (Func<string, string?, Table>)((name, file) =>
            {
                var speaker = _app.ResolveSpeaker(name);
                var path = file is null
                    ? _app.NewOutputPath($"speaker-{SanitizeName(name)}")
                    : Path.GetFullPath(Path.Combine(_app.OutputDir, file));
                var seconds = _app.GetPipeline().DecodeSpeakerToFile(speaker, path);
                _app.NotifyGenerated(new GenerationRecord(path, $"(speaker preview: {name})", seconds, $"plugin:{plugin.Name}"));
                return new Table(script) { ["file"] = path, ["seconds"] = seconds };
            }),
        };
        return t;
    }

    private Table BuildAudioTable(Script script)
    {
        var t = new Table(script)
        {
            // audio.concat({ "a.wav", "b.wav" } [, "out.wav"]) -> output path
            ["concat"] = (Func<Table, string?, string>)((files, outName) =>
            {
                var samples = new List<float>();
                foreach (var v in files.Values)
                {
                    var p = v.CastToString() ?? throw new ScriptRuntimeException("audio.concat: file list must contain paths");
                    if (!Path.IsPathRooted(p)) p = Path.Combine(_app.OutputDir, p);
                    var wav = WavFile.Read(p);
                    samples.AddRange(wav.ToMono24k());
                }
                var output = outName is null
                    ? _app.NewOutputPath("concat")
                    : Path.GetFullPath(Path.Combine(_app.OutputDir, outName));
                WavFile.Write(output, samples.ToArray(), DacCodec.SampleRate);
                return output;
            }),

            // audio.silence(seconds [, "out.wav"]) -> output path
            ["silence"] = (Func<double, string?, string>)((seconds, outName) =>
            {
                var output = outName is null
                    ? _app.NewOutputPath("silence")
                    : Path.GetFullPath(Path.Combine(_app.OutputDir, outName));
                WavFile.Write(output, new float[(int)(seconds * DacCodec.SampleRate)], DacCodec.SampleRate);
                return output;
            }),
        };
        return t;
    }

    private static Table BuildJsonTable(Script script)
    {
        var t = new Table(script)
        {
            ["encode"] = (Func<Table, string>)(table => JsonTableConverter.TableToJson(table)),
            ["decode"] = (Func<string, Table>)(s => JsonTableConverter.JsonToTable(s, script)),
        };
        return t;
    }

    private Table SpeakerToTable(Script script, string name, Speaker speaker)
    {
        return new Table(script)
        {
            ["name"] = name,
            ["text"] = speaker.Text,
            ["words"] = speaker.Words.Count,
            ["duration"] = Math.Round(speaker.Words.Sum(w => w.Duration), 2),
        };
    }

    private static bool TryNumber(Table args, string key, out double value)
    {
        var v = args.Get(key);
        if (v.Type == DataType.Number) { value = v.Number; return true; }
        value = 0;
        return false;
    }

    public static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return sb.Length == 0 ? "speaker" : sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
