using System.Buffers.Binary;
using NAudio.Wave;

namespace ListeningSubtitles.Audio;

/// <summary>
/// A streaming, channel-averaging PCM converter. A windowed-sinc low-pass filter prevents aliasing
/// when downsampling; integer sample positions preserve phase across arbitrary input boundaries.
/// Each instance belongs to one source and is called serially. No audio device is opened here.
/// </summary>
public sealed class PcmStreamConverter
{
    public const int OutputSampleRate = 24_000;
    public const int ChunkSamples = 1_200; // 50 ms, always less than 200 ms.
    private const int FilterRadius = 32;
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly AudioSourceKind source;
    private readonly int sampleRate;
    private readonly int channels;
    private readonly int bytesPerSample;
    private readonly int blockAlign;
    private readonly bool floatingPoint;
    private readonly byte[] pendingFrame;
    private readonly List<float> samples = new();
    private readonly Dictionary<int, float[]> kernels = new();
    private int pendingCount;
    private long totalFrames;
    private long baseFrame;
    private long nextOutput;
    private byte[] chunk = new byte[ChunkSamples * 2];
    private int chunkCount;
    private double chunkSquares;
    private bool completed;

    public PcmStreamConverter(WaveFormat format, AudioSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(format);
        this.source = source;
        sampleRate = format.SampleRate;
        channels = format.Channels;
        bytesPerSample = format.BitsPerSample / 8;
        blockAlign = format.BlockAlign;
        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
            encoding = extensible.SubFormat == FloatSubtype ? WaveFormatEncoding.IeeeFloat
                : extensible.SubFormat == PcmSubtype ? WaveFormatEncoding.Pcm : encoding;
        floatingPoint = encoding == WaveFormatEncoding.IeeeFloat;
        if (sampleRate is < 8_000 or > 192_000 || channels is < 1 or > 32
            || (floatingPoint ? format.BitsPerSample is not (32 or 64)
                : encoding != WaveFormatEncoding.Pcm || format.BitsPerSample is not (8 or 16 or 24 or 32))
            || blockAlign != channels * bytesPerSample)
            throw new ArgumentException("音频设备格式不受支持；需要 8–192kHz、最多 32 声道的 PCM 或浮点音频。", nameof(format));
        pendingFrame = new byte[blockAlign];
    }

    public IReadOnlyList<PcmChunk> Consume(ReadOnlySpan<byte> input)
    {
        if (completed)
            throw new InvalidOperationException("音频转换已经结束。");
        var output = new List<PcmChunk>();
        if (pendingCount != 0)
        {
            var copied = Math.Min(blockAlign - pendingCount, input.Length);
            input[..copied].CopyTo(pendingFrame.AsSpan(pendingCount));
            pendingCount += copied;
            input = input[copied..];
            if (pendingCount == blockAlign)
            {
                AddFrame(pendingFrame, output);
                pendingCount = 0;
            }
        }
        while (input.Length >= blockAlign)
        {
            AddFrame(input[..blockAlign], output);
            input = input[blockAlign..];
        }
        if (!input.IsEmpty)
        {
            input.CopyTo(pendingFrame);
            pendingCount = input.Length;
        }
        if (sampleRate != OutputSampleRate)
        {
            Resample(output, finishing: false);
            TrimHistory();
        }
        return output;
    }

    /// <summary>Flush a finite synthetic stream or a completed recording; never call to pad a temporary pause.</summary>
    public IReadOnlyList<PcmChunk> Complete()
    {
        if (completed)
            return Array.Empty<PcmChunk>();
        if (pendingCount != 0)
            throw new FormatException("音频数据在不完整的采样帧处结束。");
        completed = true;
        var output = new List<PcmChunk>();
        if (sampleRate != OutputSampleRate)
            Resample(output, finishing: true);
        if (chunkCount != 0)
        {
            var tail = chunk.AsSpan(0, chunkCount * 2).ToArray();
            output.Add(new PcmChunk(source, tail, (float)Math.Sqrt(chunkSquares / chunkCount)));
        }
        samples.Clear();
        Array.Clear(chunk);
        Array.Clear(pendingFrame);
        chunkCount = 0;
        chunkSquares = 0;
        return output;
    }

    /// <summary>Discard a retired device's partial frame/filter/chunk without flushing into another device.</summary>
    internal void Discard()
    {
        completed = true; Array.Clear(pendingFrame); Array.Clear(chunk);
        for (var index = 0; index < samples.Count; index++) samples[index] = 0;
        samples.Clear(); pendingCount = 0; chunkCount = 0; chunkSquares = 0;
    }

    private void AddFrame(ReadOnlySpan<byte> frame, List<PcmChunk> output)
    {
        double sum = 0;
        for (var channel = 0; channel < channels; channel++)
        {
            var value = ReadSample(frame.Slice(channel * bytesPerSample, bytesPerSample));
            sum += double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        }
        var mono = (float)(sum / channels);
        totalFrames++;
        if (sampleRate == OutputSampleRate)
            AppendPcm(mono, output);
        else
            samples.Add(mono);
    }

    private double ReadSample(ReadOnlySpan<byte> sample)
    {
        if (floatingPoint)
            return bytesPerSample == 4
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample));
        return bytesPerSample switch
        {
            1 => (sample[0] - 128) / 128.0,
            2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
            3 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608.0,
            4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0,
            _ => throw new InvalidOperationException("音频采样格式无效。")
        };
    }

    private void Resample(List<PcmChunk> output, bool finishing)
    {
        while (true)
        {
            var position = nextOutput * sampleRate;
            var center = position / OutputSampleRate;
            if (finishing ? position >= totalFrames * OutputSampleRate : center + FilterRadius >= totalFrames)
                return;
            var phase = (int)(position % OutputSampleRate);
            if (!kernels.TryGetValue(phase, out var weights))
            {
                weights = MakeKernel(phase / (double)OutputSampleRate);
                kernels.Add(phase, weights);
            }
            double value = 0;
            var first = center - FilterRadius + 1;
            for (var tap = 0; tap < weights.Length; tap++)
            {
                var index = first + tap;
                if (index >= 0 && index < totalFrames)
                    value += samples[checked((int)(index - baseFrame))] * weights[tap];
            }
            AppendPcm((float)value, output);
            nextOutput++;
        }
    }

    private float[] MakeKernel(double fraction)
    {
        var weights = new float[FilterRadius * 2];
        var cutoff = Math.Min(1, OutputSampleRate / (double)sampleRate) * 0.94;
        double sum = 0;
        for (var tap = 0; tap < weights.Length; tap++)
        {
            var distance = fraction + FilterRadius - 1 - tap;
            var value = Math.Abs(distance) >= FilterRadius ? 0
                : cutoff * Sinc(cutoff * distance) * Sinc(distance / FilterRadius);
            weights[tap] = (float)value;
            sum += value;
        }
        for (var tap = 0; tap < weights.Length; tap++)
            weights[tap] = (float)(weights[tap] / sum);
        return weights;
    }

    private static double Sinc(double value) => Math.Abs(value) < 1e-12 ? 1 : Math.Sin(Math.PI * value) / (Math.PI * value);

    private void TrimHistory()
    {
        var neededFrom = Math.Max(0, nextOutput * sampleRate / OutputSampleRate - FilterRadius + 1);
        var remove = (int)Math.Min(samples.Count, neededFrom - baseFrame);
        if (remove > 0)
        {
            samples.RemoveRange(0, remove);
            baseFrame += remove;
        }
    }

    private void AppendPcm(float value, List<PcmChunk> output)
    {
        var integer = (short)Math.Clamp(Math.Round(Math.Clamp(value, -1, 1) * 32768.0), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(chunkCount * 2), integer);
        var normalized = integer / 32768.0;
        chunkSquares += normalized * normalized;
        chunkCount++;
        if (chunkCount != ChunkSamples)
            return;
        output.Add(new PcmChunk(source, chunk, (float)Math.Sqrt(chunkSquares / ChunkSamples)));
        chunk = new byte[ChunkSamples * 2];
        chunkCount = 0;
        chunkSquares = 0;
    }
}
