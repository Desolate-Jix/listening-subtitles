using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ListeningSubtitles.Tests")]

namespace ListeningSubtitles.Audio;

public enum AudioSourceKind { System, Microphone }
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Consumer-owned mono signed PCM16, little-endian, 24000 Hz. Level is normalized RMS.</summary>
public sealed record PcmChunk(AudioSourceKind Source, byte[] Data, float Level);
