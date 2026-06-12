namespace LlamaTts.Core.Audio;

/// <summary>Minimal RIFF/WAVE reader and writer. Reads PCM 8/16/24/32-bit and IEEE float; writes 16-bit PCM.</summary>
public static class WavFile
{
    public sealed record WavData(float[] Samples, int SampleRate, int Channels)
    {
        public float[] ToMono24k()
        {
            var mono = Dsp.ToMono(Samples, Channels);
            return Dsp.Resample(mono, SampleRate, 24000);
        }
    }

    public static WavData Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    // Chunk ids are 4 raw ASCII bytes; BinaryReader.ReadChars would run them through the
    // UTF-8 decoder, which throws on binary metadata some encoders embed (seen with
    // ElevenLabs exports). Read bytes instead.
    private static string ChunkId(BinaryReader br) =>
        System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));

    public static WavData Read(Stream stream)
    {
        using var br = new BinaryReader(stream);
        if (ChunkId(br) != "RIFF") throw new InvalidDataException("Not a RIFF file");
        br.ReadInt32(); // riff size
        if (ChunkId(br) != "WAVE") throw new InvalidDataException("Not a WAVE file");

        short format = 0, channels = 0, bitsPerSample = 0;
        int sampleRate = 0;
        float[]? samples = null;

        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var chunkId = ChunkId(br);
            int chunkSize = br.ReadInt32();
            long next = br.BaseStream.Position + chunkSize + (chunkSize % 2);

            if (chunkId == "fmt ")
            {
                format = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bitsPerSample = br.ReadInt16();
                if (format == unchecked((short)0xFFFE) && chunkSize >= 40)
                {
                    br.ReadInt16(); // cbSize
                    br.ReadInt16(); // valid bits
                    br.ReadInt32(); // channel mask
                    format = br.ReadInt16(); // first two bytes of the subformat GUID
                }
            }
            else if (chunkId == "data")
            {
                if (sampleRate == 0) throw new InvalidDataException("data chunk before fmt chunk");
                var raw = br.ReadBytes(chunkSize);
                samples = DecodeSamples(raw, format, bitsPerSample);
            }

            if (next > br.BaseStream.Length) break;
            br.BaseStream.Position = next;
        }

        if (samples is null) throw new InvalidDataException("WAV file has no data chunk");
        return new WavData(samples, sampleRate, channels);
    }

    private static float[] DecodeSamples(byte[] raw, short format, short bits)
    {
        switch (format, bits)
        {
            case (1, 8):
            {
                var output = new float[raw.Length];
                for (int i = 0; i < raw.Length; i++) output[i] = (raw[i] - 128) / 128f;
                return output;
            }
            case (1, 16):
            {
                var output = new float[raw.Length / 2];
                for (int i = 0; i < output.Length; i++)
                    output[i] = BitConverter.ToInt16(raw, i * 2) / 32768f;
                return output;
            }
            case (1, 24):
            {
                var output = new float[raw.Length / 3];
                for (int i = 0; i < output.Length; i++)
                {
                    int v = raw[i * 3] | (raw[i * 3 + 1] << 8) | (raw[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    output[i] = v / 8388608f;
                }
                return output;
            }
            case (1, 32):
            {
                var output = new float[raw.Length / 4];
                for (int i = 0; i < output.Length; i++)
                    output[i] = BitConverter.ToInt32(raw, i * 4) / 2147483648f;
                return output;
            }
            case (3, 32):
            {
                var output = new float[raw.Length / 4];
                Buffer.BlockCopy(raw, 0, output, 0, raw.Length);
                return output;
            }
            case (3, 64):
            {
                var output = new float[raw.Length / 8];
                for (int i = 0; i < output.Length; i++)
                    output[i] = (float)BitConverter.ToDouble(raw, i * 8);
                return output;
            }
            default:
                throw new NotSupportedException($"Unsupported WAV format: tag={format}, bits={bits}");
        }
    }

    public sealed record WavInfo(int SampleRate, int Channels, short BitsPerSample, double DurationSeconds);

    /// <summary>Read format and duration from the header without loading sample data.</summary>
    public static WavInfo? TryReadInfo(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (ChunkId(br) != "RIFF") return null;
            br.ReadInt32();
            if (ChunkId(br) != "WAVE") return null;

            short channels = 0, bits = 0;
            int rate = 0;
            long dataSize = -1;

            while (fs.Position + 8 <= fs.Length)
            {
                var id = ChunkId(br);
                int size = br.ReadInt32();
                long next = fs.Position + size + (size % 2);
                if (id == "fmt ")
                {
                    br.ReadInt16(); // format tag
                    channels = br.ReadInt16();
                    rate = br.ReadInt32();
                    br.ReadInt32();
                    br.ReadInt16();
                    bits = br.ReadInt16();
                }
                else if (id == "data")
                {
                    dataSize = size;
                }
                if (next > fs.Length) break;
                fs.Position = next;
            }

            if (rate <= 0 || dataSize < 0 || channels <= 0 || bits <= 0) return null;
            int blockAlign = Math.Max(1, channels * bits / 8);
            return new WavInfo(rate, channels, bits, (double)dataSize / (rate * blockAlign));
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int dataSize = samples.Length * 2;
        bw.Write("RIFF"u8); bw.Write(36 + dataSize); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16);
        bw.Write((short)1); bw.Write((short)channels); bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataSize);
        foreach (var s in samples)
        {
            var v = Math.Clamp(s, -1f, 1f);
            bw.Write((short)Math.Round(v * 32767f));
        }
    }
}
