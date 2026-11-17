# Listening Subtitles

A Windows desktop app dedicated to **speech recognition, live subtitle transcription and caption export**. Capture audio playing through your computer, display floating subtitles, optionally translate English speech into Simplified Chinese, and save SRT, WebVTT (CC) or TXT.

[中文说明](README.md) · [Download for Windows](https://github.com/Desolate-Jix/listening-subtitles/releases/latest) · [MIT License](LICENSE)

## Start

1. Download and fully extract the Windows x64 ZIP. Run `ListeningSubtitles.exe`.
2. Open Settings (设置) and enter your own OpenAI API key. Credentials are encrypted locally with Windows DPAPI.
3. Play your video/audio, then press Start (开始). Stop (停止) ends capture and pending network work. Opening the app alone never starts recording.
4. Open History (历史) to review, fill missing Chinese translations, or export captions.

The UI is currently in Simplified Chinese. The app captures **system output audio only**, not the microphone. Play existing audio/video files in a media player to transcribe them; there is no batch file-import interface. Windows 10/11 x64 is required.

Default transcription: `gpt-live-transcribe`, language `en`. Use comma-separated language codes or leave blank for automatic recognition. Optional Chinese translation uses `gpt-6-luna` by default; change the model in Settings. Access depends on your API account. Requests incur OpenAI API charges; no shared key or API credits are included.

## Captions and timing

- **The first caption starts at 00:00:00.000 by default.** All later gaps and durations are preserved. History, TXT, SRT and VTT use the same relative timeline.
- Uncheck “首条字幕从 0 开始” to use capture start as the origin, retaining leading silence.
- “整体偏移（秒）” applies a signed offset in seconds to the history view and exports. Positive delays the captions; negative advances them. Time before zero is clipped.
- SRT and VTT support bilingual, original-only and Chinese-only output. TXT includes original text and available Chinese.
- Dates in the recording selector and filenames identify recordings, not cue positions.
- Timing is approximate, based on captured audio segments, not translation arrival. It is not word alignment and does not track media seeking, pauses or playback-speed changes.
- An exported VTT file can be loaded as a caption track by a supporting website/player; it does not automatically add CC support to arbitrary websites.

## History and data

Originals and translations are encrypted under `%LOCALAPPDATA%\ListeningSubtitles\history`, saved approximately every second when changed and during normal stop/exit. The latest recording reopens on startup; older recordings are selectable in History. Late translations update their own rows. Completed captions are retained beyond 500 entries. A recording has an approximately 64 MB storage limit.

API credentials are independently encrypted in the same application data directory. No other app is required. Only the original Windows account can decrypt these files; export text/captions for portability. Exported files are ordinary readable UTF-8 files.

Audio is sent to OpenAI only while capture is started. Translation sends the relevant transcript text using stateless Responses API calls with `store: false`. The app saves no raw audio and has no telemetry or automatic startup. System capture includes other sounds routed to the selected playback device.

Stopping cancels pending translations and uncommitted speech. Use “补齐中文（API）” to translate missing completed English segments explicitly; this incurs API usage. Automatic translation mainly targets English/Latin-heavy text. No automatic retries/reconnects are performed. Recognition, translation and timing may be imperfect.

## Build and test

Requires Windows, .NET 8 SDK and NAudio 2.4.0 (NuGet). Published ZIPs include the .NET/WindowsDesktop runtime.

```powershell
dotnet run --project ListeningSubtitles.csproj -c Release
dotnet run --project tests/ListeningSubtitles.Tests.csproj -c Release
dotnet run --project tests/Speech/SpeechTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

The output is `artifacts/win-x64`. Keep all published files together. Tests use synthetic audio and fake HTTP/WebSocket transports and require no API key. `--preview <temporary-directory>` renders synthetic captions without recording or accessing an API.

MIT licensed; NAudio and derived capture code retain their upstream license in `licenses`. See the Chinese guide for protocol references.
