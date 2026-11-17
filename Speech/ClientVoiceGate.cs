using System.Security.Cryptography;

namespace ListeningSubtitles.Speech;

internal enum VoiceActionKind { Append, Commit }
internal sealed record VoiceAction(VoiceActionKind Kind, byte[]? Pcm = null, TimeSpan Start = default, TimeSpan End = default);

/// <summary>Bounded energy VAD for 24kHz mono PCM16; it never uploads idle silence.</summary>
internal sealed class ClientVoiceGate : IDisposable
{
    private const int BytesPerMillisecond = 48;
    private const int FrameBytes = 20 * BytesPerMillisecond;
    private const int PreRollBytes = 200 * BytesPerMillisecond;
    private const int MinimumSpeechBytes = 200 * BytesPerMillisecond;
    private const int EndSilenceBytes = 800 * BytesPerMillisecond;
    private const int MaximumTurnBytes = 5000 * BytesPerMillisecond;
    private readonly Queue<byte[]> _preRoll = new();
    private readonly List<byte[]> _candidate = [];
    private readonly byte[] _partial = new byte[FrameBytes];
    private int _partialCount, _preRollLength, _candidateSpeech, _candidateGap, _turnBytes, _silenceBytes;
    private bool _active;
    private TimeSpan _lastVoice, _lastInput;

    public List<VoiceAction> Push(byte[] pcm, TimeSpan now)
    {
        var actions = new List<VoiceAction>();
        _lastInput = now;
        for (var offset = 0; offset < pcm.Length;)
        {
            var count = Math.Min(FrameBytes - _partialCount, pcm.Length - offset);
            Buffer.BlockCopy(pcm, offset, _partial, _partialCount, count);
            _partialCount += count;
            offset += count;
            if (_partialCount != FrameBytes) continue;
            var frame = _partial.ToArray();
            CryptographicOperations.ZeroMemory(_partial);
            _partialCount = 0;
            ProcessFrame(frame, now - TimeSpan.FromMilliseconds((pcm.Length - offset) / (double)BytesPerMillisecond), actions);
        }
        return actions;
    }

    public List<VoiceAction> Tick(TimeSpan now)
    {
        var actions = new List<VoiceAction>();
        if (_active && now - _lastVoice >= TimeSpan.FromMilliseconds(800)) Commit(actions);
        else if (!_active && now - _lastInput >= TimeSpan.FromMilliseconds(800)) ClearBuffered();
        return actions;
    }

    private void ProcessFrame(byte[] frame, TimeSpan now, List<VoiceAction> actions)
    {
        var voiced = IsVoice(frame);
        if (_active)
        {
            actions.Add(new(VoiceActionKind.Append, frame, now - TimeSpan.FromMilliseconds(20), now));
            _turnBytes += frame.Length;
            if (voiced) { _silenceBytes = 0; _lastVoice = now; }
            else _silenceBytes += frame.Length;
            if (_silenceBytes >= EndSilenceBytes || _turnBytes >= MaximumTurnBytes) Commit(actions);
            return;
        }
        if (_candidate.Count == 0)
        {
            if (!voiced) { Remember(frame); return; }
            while (_preRoll.TryDequeue(out var earlier)) _candidate.Add(earlier);
            _preRollLength = 0;
        }
        _candidate.Add(frame);
        if (voiced)
        {
            _candidateSpeech += frame.Length;
            _candidateGap = 0;
            _lastVoice = now;
        }
        else _candidateGap += frame.Length;
        if (_candidateSpeech >= MinimumSpeechBytes)
        {
            var audio = new byte[_candidate.Sum(x => x.Length)];
            var offset = 0;
            foreach (var part in _candidate)
            {
                Buffer.BlockCopy(part, 0, audio, offset, part.Length);
                offset += part.Length;
                CryptographicOperations.ZeroMemory(part);
            }
            _candidate.Clear();
            _candidateSpeech = _candidateGap = 0;
            _turnBytes = audio.Length;
            _silenceBytes = 0;
            _active = true;
            actions.Add(new(VoiceActionKind.Append, audio, now - TimeSpan.FromMilliseconds(audio.Length / (double)BytesPerMillisecond), now));
        }
        else if (_candidateGap >= PreRollBytes)
        {
            foreach (var part in _candidate) Remember(part);
            _candidate.Clear();
            _candidateSpeech = _candidateGap = 0;
        }
    }

    private void Remember(byte[] frame)
    {
        _preRoll.Enqueue(frame);
        _preRollLength += frame.Length;
        while (_preRollLength > PreRollBytes && _preRoll.TryDequeue(out var old))
        {
            _preRollLength -= old.Length;
            CryptographicOperations.ZeroMemory(old);
        }
    }

    private void Commit(List<VoiceAction> actions)
    {
        // Activation requires >=200ms of speech, exceeding the API's 100ms minimum buffer.
        if (_active) actions.Add(new(VoiceActionKind.Commit));
        _active = false;
        _turnBytes = _silenceBytes = 0;
        ClearBuffered();
    }

    private static bool IsVoice(byte[] pcm)
    {
        long energy = 0;
        var peak = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var value = (short)(pcm[i] | pcm[i + 1] << 8);
            energy += (long)value * value;
            peak = Math.Max(peak, Math.Abs((int)value));
        }
        // Conservative fixed energy gate; real microphones/noise still need human validation.
        return energy / (pcm.Length / 2) >= 400L * 400 && peak >= 800;
    }

    private void ClearBuffered()
    {
        while (_preRoll.TryDequeue(out var part)) CryptographicOperations.ZeroMemory(part);
        foreach (var part in _candidate) CryptographicOperations.ZeroMemory(part);
        _candidate.Clear();
        CryptographicOperations.ZeroMemory(_partial);
        _partialCount = _preRollLength = _candidateSpeech = _candidateGap = 0;
    }

    public void Reset()
    {
        ClearBuffered();
        _active = false;
        _turnBytes = _silenceBytes = 0;
        _lastVoice = _lastInput = default;
    }

    public void Dispose() => Reset();
}
