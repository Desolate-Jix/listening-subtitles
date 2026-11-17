using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ListeningSubtitles.Audio;
using ListeningSubtitles.Speech;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Session uses official endpoint and GA PCM transcription schema", Schema),
    ("VAD suppresses silence and short noise, preserving onset", VoiceGate),
    ("VAD commits silence, callback cessation and maximum duration", VoiceBoundaries),
    ("Subtitle cue timing uses captured audio even when transcript arrives late", CueTiming),
    ("Transport aggregates deltas and finalizes in committed order once", Transcripts),
    ("Audio is copied, queue is bounded and overflow stops sending", Overflow),
    ("Stop cancels pending work and refuses future audio", Stop),
    ("Server errors are redacted without reconnect", Errors),
    ("Readiness times out and startup can be cancelled", Readiness),
    ("Readiness rejects the wrong server configuration", WrongSession),
    ("Playback callback cessation commits through the wall timer", TimerCommit),
    ("Expired queued audio is never replayed", StaleQueue),
    ("Invalid PCM is rejected before upload", InvalidPcm),
    ("Fragmented messages are parsed and oversize messages rejected", Fragments),
    ("Device boundary discards VAD candidate preroll and partial frames", BoundaryCandidate),
    ("Device boundary serializes clear after in-flight audio and drops queued old PCM", BoundaryInFlight),
    ("Device boundary preserves committed turns and binds late events only by acknowledged IDs", BoundaryTranscriptOrder),
    ("Stop during clear refuses fresh audio without reconnect", BoundaryStop),
    ("Unacknowledged transcript buffering has item and character bounds and clears on stop", BoundaryBufferBounds)
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {test.Name}: {e}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

static async Task Schema()
{
    var socket = new FakeTransport();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "zh,en,zh", socket);
    Check(!client.TryAppend(Pcm(200, true)), "audio accepted before readiness");
    await client.StartAsync("sk-fake", default);
    Check(socket.Endpoint!.AbsoluteUri == "wss://api.openai.com/v1/realtime?intent=transcription", "wrong official endpoint");
    Check(socket.Key == "sk-fake", "auth key missing");
    using var doc = JsonDocument.Parse(socket.Sent.Single());
    var root = doc.RootElement;
    Check(root.GetProperty("type").GetString() == "session.update", "legacy event schema");
    var session = root.GetProperty("session");
    Check(session.GetProperty("type").GetString() == "transcription", "not a transcription-only session");
    var input = session.GetProperty("audio").GetProperty("input");
    Check(input.GetProperty("format").GetProperty("type").GetString() == "audio/pcm" && input.GetProperty("format").GetProperty("rate").GetInt32() == 24000, "wrong PCM format");
    Check(input.GetProperty("turn_detection").ValueKind == JsonValueKind.Null, "server VAD must be disabled");
    var transcription = input.GetProperty("transcription");
    Check(transcription.GetProperty("model").GetString() == "gpt-live-transcribe", "wrong model");
    Check(transcription.GetProperty("delay").GetString() == "low", "wrong low latency hint");
    Check(transcription.GetProperty("languages").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "zh", "en" }), "comma languages not parsed/deduplicated");
    Check(!transcription.TryGetProperty("language", out _), "singular language sent");
    var automatic = new FakeTransport();
    await using var other = new RealtimeTranscriber(AudioSourceKind.Microphone, "gpt-transcribe", "", automatic);
    await other.StartAsync("fake", default);
    using var second = JsonDocument.Parse(automatic.Sent.Single());
    Check(!second.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription").TryGetProperty("languages", out _), "blank should auto-detect");
    Throws<ArgumentException>(() => new RealtimeTranscriber(AudioSourceKind.System, "unsupported-model"));
    Throws<ArgumentException>(() => new RealtimeTranscriber(AudioSourceKind.System, language: "https://example.test"));
}

static Task VoiceGate()
{
    using var gate = new ClientVoiceGate();
    var now = TimeSpan.Zero;
    for (var i = 0; i < 10; i++) Check(gate.Push(Pcm(200, false), now += TimeSpan.FromMilliseconds(200)).Count == 0, "idle silence uploaded");
    var first = gate.Push(Pcm(100, true), now += TimeSpan.FromMilliseconds(100));
    Check(first.Count == 0, "speech uploaded before minimum duration");
    var second = gate.Push(Pcm(100, true), now += TimeSpan.FromMilliseconds(100));
    var audio = second.Where(x => x.Kind == VoiceActionKind.Append).SelectMany(x => x.Pcm!).ToArray();
    Check(audio.Length == 400 * 48, "200ms preroll or speech onset lost");
    Check(audio.Take(200 * 48).All(x => x == 0), "preroll should preserve earlier silence");
    Check(audio.Skip(200 * 48).Any(x => x != 0), "speech lost");
    Clear(second);
    using var click = new ClientVoiceGate();
    Check(click.Push(Pcm(100, true), TimeSpan.Zero).Count == 0, "short noise uploaded");
    Check(click.Push(Pcm(200, false), TimeSpan.FromMilliseconds(200)).Count == 0, "short noise was committed");
    Check(click.Tick(TimeSpan.FromSeconds(2)).Count == 0, "short noise timer committed");
    return Task.CompletedTask;
}

static Task VoiceBoundaries()
{
    using var gate = new ClientVoiceGate();
    var first = gate.Push(Pcm(200, true), TimeSpan.Zero);
    Check(first.Any(x => x.Kind == VoiceActionKind.Append), "speech not uploaded");
    Clear(first);
    var commits = 0;
    for (var i = 1; i <= 4; i++)
    {
        var actions = gate.Push(Pcm(200, false), TimeSpan.FromMilliseconds(i * 200));
        commits += actions.Count(x => x.Kind == VoiceActionKind.Commit);
        Clear(actions);
    }
    Check(commits == 1, "800ms silence should commit once");
    Check(gate.Tick(TimeSpan.FromSeconds(3)).Count == 0, "silence committed an empty turn");
    var active = gate.Push(Pcm(200, true), TimeSpan.FromSeconds(4));
    Clear(active);
    Check(gate.Tick(TimeSpan.FromMilliseconds(4799)).Count == 0, "wall timer committed early");
    Check(gate.Tick(TimeSpan.FromMilliseconds(4800)).Single().Kind == VoiceActionKind.Commit, "callback cessation did not commit");
    using var continuous = new ClientVoiceGate();
    commits = 0;
    for (var i = 0; i < 25; i++)
    {
        var actions = continuous.Push(Pcm(200, true), TimeSpan.FromMilliseconds(i * 200));
        commits += actions.Count(x => x.Kind == VoiceActionKind.Commit);
        Clear(actions);
    }
    Check(commits == 1, "5-second turn not split");
    return Task.CompletedTask;
}

static async Task Transcripts()
{
    var socket = new FakeTransport();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "en", socket);
    var updates = new ConcurrentQueue<TranscriptUpdate>();
    client.Transcript += updates.Enqueue;
    await client.StartAsync("fake", default);
    await Turn(client, socket, 1);
    socket.Push("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_1\",\"delta\":\"Hello\"}", 13);
    socket.Push("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_1\",\"delta\":\" world\"}");
    await Until(() => updates.Any(x => x.Text == "Hello world"), "aggregated partial missing");
    await Turn(client, socket, 2);
    socket.Push(Final("item_2", "Second"));
    await Task.Delay(40);
    Check(!updates.Any(x => x.IsFinal), "out-of-order final emitted too early");
    socket.Push(Final("item_1", "Hello world."));
    await Until(() => updates.Count(x => x.IsFinal) == 2, "finals not emitted");
    socket.Push(Final("item_1", "duplicate"));
    socket.Push(Final("item_2", "duplicate"));
    await Task.Delay(40);
    var finals = updates.Where(x => x.IsFinal).ToArray();
    Check(finals.Length == 2 && finals[0].ItemId == "item_1" && finals[1].ItemId == "item_2", "final order/dedup broken");
    Check(finals[0].Sequence < finals[1].Sequence, "stable sequence not retained");
    Check(finals[0].Text == "Hello world." && finals[0].Source == AudioSourceKind.System, "wrong final content/source");
    Check(updates.Where(x => x.ItemId == "item_1").Select(x => x.Timestamp).Distinct().Count() == 1, "turn timestamp changed across updates");
}

static async Task CueTiming()
{
    var socket = new FakeTransport();
    var clock = new ManualClock();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "en", socket, clock);
    var updates = new ConcurrentQueue<TranscriptUpdate>();
    client.Transcript += updates.Enqueue;
    await client.StartAsync("fake", default);
    clock.Advance(TimeSpan.FromSeconds(10));
    client.TryAppend(Pcm(200, true));
    await Until(() => socket.AudioMessages == 1, "audio was not sent");
    clock.Advance(TimeSpan.FromMilliseconds(800));
    await Until(() => socket.Commits == 1, "turn was not committed");
    clock.Advance(TimeSpan.FromSeconds(30));
    socket.Push(Final("item_1", "This arrived much later."));
    await Until(() => updates.Any(x => x.IsFinal), "final missing");
    var final = updates.Single(x => x.IsFinal);
    Check(final.Timestamp == DateTimeOffset.UnixEpoch.AddSeconds(9.8), "cue start included network delay or lost onset");
    Check(final.EndTime == DateTimeOffset.UnixEpoch.AddSeconds(10), "cue end included timer or response delay");
}

static async Task Overflow()
{
    var socket = new FakeTransport { BlockAudio = true };
    await using var client = new RealtimeTranscriber(AudioSourceKind.Microphone, "gpt-live-transcribe", "", socket);
    var errors = new ConcurrentQueue<string>();
    client.Failed += errors.Enqueue;
    await client.StartAsync("fake", default);
    var original = Pcm(200, true);
    var expected = original.ToArray();
    Check(client.TryAppend(original), "first audio rejected");
    Array.Clear(original);
    await socket.AudioEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    using var message = JsonDocument.Parse(socket.Sent.Last());
    Check(Convert.FromBase64String(message.RootElement.GetProperty("audio").GetString()!).SequenceEqual(expected), "TryAppend did not copy caller bytes");
    var rejected = false;
    for (var i = 0; i < 20; i++) if (!client.TryAppend(Pcm(200, true))) { rejected = true; break; }
    Check(rejected, "three-second queue was not bounded");
    await Until(() => errors.Count == 1, "overflow was silent");
    Check(errors.Single().Contains("积压"), "overflow message unclear");
    Check(!client.TryAppend(Pcm(200, true)), "overflow still accepts audio");
    await client.StopAsync();
    Check(socket.ConnectCalls == 1 && socket.AudioMessages == 1, "overflow replayed or reconnected");
}

static async Task Stop()
{
    var socket = new FakeTransport { BlockAudio = true };
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
    await client.StartAsync("fake", default);
    Check(client.TryAppend(Pcm(200, true)), "speech rejected");
    await socket.AudioEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(client.TryAppend(Pcm(200, true)), "queued speech rejected");
    await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Check(!client.TryAppend(Pcm(200, true)), "stopped instance accepts audio");
    Check(socket.AudioMessages == 1 && socket.Commits == 0, "stop sent queued data or committed audio");
    await client.StopAsync();
}

static async Task Errors()
{
    var socket = new FakeTransport();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
    var errors = new ConcurrentQueue<string>();
    client.Failed += errors.Enqueue;
    await client.StartAsync("sk-secret", default);
    socket.Push("{\"type\":\"error\",\"error\":{\"message\":\"sk-secret PRIVATE AUDIO\"}}");
    await Until(() => errors.Count == 1, "error not reported");
    Check(!errors.Single().Contains("sk-secret") && !errors.Single().Contains("PRIVATE AUDIO"), "server details leaked");
    Check(!client.TryAppend(Pcm(200, true)), "failed instance accepts audio");
    await client.StopAsync();
    Check(socket.ConnectCalls == 1, "automatically reconnected");
}

static async Task Readiness()
{
    var socket = new FakeTransport { Created = false };
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket, readinessTimeout: TimeSpan.FromMilliseconds(80));
    var errors = new ConcurrentQueue<string>();
    client.Failed += errors.Enqueue;
    var failure = await ThrowsAsync<InvalidOperationException>(() => client.StartAsync("sk-fake", default));
    Check(failure.Message.Contains("超时") && !failure.ToString().Contains("sk-fake"), "readiness timeout unclear or leaked key");
    Check(socket.Sent.Count == 0 && errors.Count == 1, "audio/config sent before created readiness");
    var noUpdate = new FakeTransport { Updated = false };
    await using var second = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", noUpdate, readinessTimeout: TimeSpan.FromMilliseconds(80));
    await ThrowsAsync<InvalidOperationException>(() => second.StartAsync("fake", default));
    Check(noUpdate.Sent.Count == 1 && !second.TryAppend(Pcm(200, true)), "audio accepted before session.updated");
    var pending = new FakeTransport { Created = false };
    await using var third = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", pending);
    using var cancel = new CancellationTokenSource();
    var startup = third.StartAsync("fake", cancel.Token);
    cancel.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => startup);
    Check(pending.ConnectCalls == 1 && pending.Sent.Count == 0, "cancelled startup sent data");
}

static async Task Fragments()
{
    var socket = new FakeTransport();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
    var errors = new ConcurrentQueue<string>();
    client.Failed += errors.Enqueue;
    await client.StartAsync("fake", default);
    socket.Push(new string('x', 1024 * 1024 + 1), 4096);
    await Until(() => errors.Count == 1, "oversize message not rejected");
    Check(!errors.Single().Contains(new string('x', 20)), "raw invalid event leaked");
}

static async Task WrongSession()
{
    var socket = new FakeTransport { UpdatedEventOverride = "{\"type\":\"session.updated\",\"session\":{\"type\":\"realtime\"}}" };
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
    await ThrowsAsync<InvalidOperationException>(() => client.StartAsync("fake", default));
    Check(!client.TryAppend(Pcm(200, true)) && socket.AudioMessages == 0, "wrong session still uploaded audio");
}

static async Task TimerCommit()
{
    var socket = new FakeTransport();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
    await client.StartAsync("fake", default);
    Check(client.TryAppend(Pcm(200, true)), "timer speech rejected");
    await Until(() => socket.AudioMessages == 1, "speech wasn't sent");
    await Until(() => socket.Commits == 1, "wall timer did not commit when callbacks ceased");
    await Task.Delay(80);
    Check(socket.Commits == 1, "wall timer emitted duplicate commit");
}

static async Task StaleQueue()
{
    var socket = new FakeTransport { BlockAudio = true };
    var clock = new ManualClock();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket, clock);
    var errors = new ConcurrentQueue<string>();
    client.Failed += errors.Enqueue;
    await client.StartAsync("fake", default);
    Check(client.TryAppend(Pcm(200, true)), "first speech rejected");
    await socket.AudioEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(client.TryAppend(Pcm(200, true)), "queued speech rejected");
    clock.Advance(TimeSpan.FromSeconds(4));
    socket.ReleaseAudio.TrySetResult();
    await Until(() => errors.Count == 1, "stale queue not reported");
    Check(socket.AudioMessages == 1 && errors.Single().Contains("过期"), "stale audio was replayed");
}

static async Task InvalidPcm()
{
    var socket = new FakeTransport();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
    var errors = new ConcurrentQueue<string>();
    client.Failed += errors.Enqueue;
    await client.StartAsync("fake", default);
    Check(!client.TryAppend([1, 2, 3]), "unaligned PCM accepted");
    Check(errors.Count == 1 && socket.AudioMessages == 0, "invalid PCM uploaded or failed silently");
    var keySocket = new FakeTransport();
    await using var second = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", keySocket);
    await ThrowsAsync<ArgumentException>(() => second.StartAsync("sk-\r\ninvalid", default));
    Check(keySocket.ConnectCalls == 0, "invalid key attempted connection");
}

static bool Boundary(RealtimeTranscriber client) => client.ResetAudioBoundary();
static int QueuedBytes(RealtimeTranscriber client) => (int)typeof(RealtimeTranscriber).GetField("_queuedBytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
static byte[] Tone(int milliseconds, short sample)
{
    var bytes = new byte[milliseconds * 48];
    for (var i = 0; i < bytes.Length; i += 2) { bytes[i] = (byte)sample; bytes[i + 1] = (byte)(sample >> 8); }
    return bytes;
}
static async Task BoundaryCandidate()
{
    var socket = new FakeTransport(); var clock = new ManualClock();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket, clock);
    Check(!Boundary(client), "boundary accepted before readiness");
    await client.StartAsync("fake", default);
    Check(client.TryAppend(Pcm(200, false)) && client.TryAppend(Tone(110, 1000)), "old candidate rejected");
    await Until(() => QueuedBytes(client) == 0, "old candidate wasn't processed");
    Check(Boundary(client), "device boundary was rejected");
    await Until(() => socket.Clears == 1, "clear missing");
    Check(client.TryAppend(Tone(100, 2500)), "fresh half candidate rejected");
    await Until(() => QueuedBytes(client) == 0, "fresh candidate wasn't processed");
    Check(socket.AudioMessages == 0, "old candidate/partial frame activated fresh speech early");
    Check(client.TryAppend(Tone(100, 2500)), "fresh second candidate rejected");
    await Until(() => socket.AudioMessages == 1, "fresh speech wasn't sent");
    using var append = JsonDocument.Parse(socket.Sent.Last(x => x.Contains("input_audio_buffer.append")));
    Check(Convert.FromBase64String(append.RootElement.GetProperty("audio").GetString()!).SequenceEqual(Tone(200, 2500)), "old candidate/preroll/partial PCM leaked into new source");
    Check(socket.ConnectCalls == 1 && socket.Sent.Count(x => x.Contains("session.update")) == 1, "boundary reconnected or reconfigured session");
}
static async Task BoundaryInFlight()
{
    var socket = new FakeTransport { BlockAudioAt = 2 }; var clock = new ManualClock();
    await using var client = new RealtimeTranscriber(AudioSourceKind.Microphone, "gpt-live-transcribe", "", socket, clock);
    var updates = new ConcurrentQueue<TranscriptUpdate>(); client.Transcript += updates.Enqueue;
    await client.StartAsync("fake", default);
    Check(client.TryAppend(Tone(200, 1000)), "old active speech rejected");
    await Until(() => socket.AudioMessages == 1 && QueuedBytes(client) == 0, "old activation wasn't sent");
    socket.Push("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"item_abandoned\",\"delta\":\"old half\"}");
    await Until(() => updates.Count == 1, "old partial wasn't assigned");
    // Active VAD creates ten 20ms append actions for this chunk. Freeze its first
    // write so reset must discard the remaining actions as well as queued chunks.
    Check(client.TryAppend(Tone(200, 1200)), "old active continuation rejected");
    await Until(() => socket.AudioMessages == 2, "old continuation wasn't in flight");
    Check(client.TryAppend(Tone(200, 1500)) && Boundary(client) && client.TryAppend(Tone(200, 2500)), "boundary handoff rejected");
    Check(socket.Clears == 0, "clear raced concurrently with an in-flight append");
    socket.ReleaseAudio.TrySetResult();
    await Until(() => socket.Clears == 1 && socket.AudioMessages == 3 && QueuedBytes(client) == 0, "clear/new audio handoff stalled");
    var types = socket.Sent.Select(x => JsonDocument.Parse(x)).Select(x => { using (x) return x.RootElement.GetProperty("type").GetString(); }).ToArray();
    Check(types.SequenceEqual(new[] { "session.update", "input_audio_buffer.append", "input_audio_buffer.append", "input_audio_buffer.clear", "input_audio_buffer.append" }), "old queued PCM or VAD actions crossed clear boundary");
    using var fresh = JsonDocument.Parse(socket.Sent.Last());
    Check(Convert.FromBase64String(fresh.RootElement.GetProperty("audio").GetString()!).SequenceEqual(Tone(200, 2500)), "new source inherited old active audio");
    socket.Push(Final("item_abandoned", "must not finalize the abandoned half"));
    await Task.Delay(30);
    Check(!updates.Any(x => x.IsFinal) && socket.Commits == 0, "boundary committed or finalized the abandoned turn");
}
static async Task BoundaryTranscriptOrder()
{
    var socket = new FakeTransport { AcknowledgeCommit = false }; var clock = new ManualClock();
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket, clock);
    var updates = new ConcurrentQueue<TranscriptUpdate>(); client.Transcript += updates.Enqueue;
    await client.StartAsync("fake", default);
    await Turn(client, socket, 1);
    Check(client.TryAppend(Tone(200, 1000)), "uncommitted old speech rejected");
    await Until(() => QueuedBytes(client) == 0, "old speech wasn't processed");
    Check(Boundary(client), "device boundary was rejected");
    await Until(() => socket.Clears == 1, "clear missing");
    clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(80);
    Check(socket.Commits == 1, "old active VAD timer committed after reset");
    Check(client.TryAppend(Tone(200, 2500)), "fresh turn rejected");
    await Until(() => QueuedBytes(client) == 0, "fresh turn wasn't processed");
    socket.Push(Final("item_uncommitted_old", "old late final"));
    socket.Push(Final("item_2", "fresh final before commit acknowledgement"));
    await Task.Delay(30);
    Check(!updates.Any(x => x.IsFinal), "unacknowledged/old final was guessed onto fresh turn");
    for (var i = 0; i < 4; i++) Check(client.TryAppend(Pcm(200, false)), "fresh silence rejected");
    await Until(() => socket.Commits == 2, "fresh commit missing");
    // Both old and fresh commit acknowledgements arrive after the reset; neither
    // committed turn may be discarded or matched to an unknown orphan item ID.
    socket.Push("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"item_1\",\"previous_item_id\":null}");
    socket.Push("{\"type\":\"input_audio_buffer.committed\",\"item_id\":\"item_2\",\"previous_item_id\":\"item_1\"}");
    socket.Push(Final("item_1", "previous committed turn"));
    await Until(() => updates.Count(x => x.IsFinal) == 2, "committed old or fresh result was lost");
    var finals = updates.Where(x => x.IsFinal).ToArray();
    Check(finals[0].ItemId == "item_1" && finals[0].Text == "previous committed turn" && finals[1].ItemId == "item_2" && finals[1].Text == "fresh final before commit acknowledgement", "committed order or exact ID binding changed");
    Check(finals[0].Sequence < finals[1].Sequence && socket.ConnectCalls == 1, "sequence reset or socket reconnected");
}
static async Task BoundaryStop()
{
    var socket = new FakeTransport { AcknowledgeClear = false };
    await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
    await client.StartAsync("fake", default);
    Check(Boundary(client), "device boundary was rejected");
    await Until(() => socket.Clears == 1, "clear missing");
    Check(client.TryAppend(Pcm(200, true)), "fresh handoff audio rejected");
    await Task.Delay(40);
    Check(socket.AudioMessages == 0, "fresh audio sent before clear acknowledgement");
    await client.StopAsync();
    Check(!Boundary(client) && !client.TryAppend(Pcm(200, true)) && socket.ConnectCalls == 1 && socket.Commits == 0, "stop reset/reconnected/committed audio");
}
static async Task BoundaryBufferBounds()
{
    foreach (var countBound in new[] { true, false })
    {
        var socket = new FakeTransport();
        await using var client = new RealtimeTranscriber(AudioSourceKind.System, "gpt-live-transcribe", "", socket);
        var errors = new ConcurrentQueue<string>(); client.Failed += errors.Enqueue;
        await client.StartAsync("fake", default);
        Check(Boundary(client), "device boundary was rejected");
        await Until(() => socket.Clears == 1, "clear missing");
        var text = countBound ? "orphan" : new string('x', 64000);
        for (var i = 0; i < (countBound ? 65 : 3); i++) socket.Push(Final("item_unbound_" + i, text));
        await Until(() => errors.Count == 1, "unacknowledged item/character cap wasn't enforced");
        Check(!errors.Single().Contains("orphan") && !errors.Single().Contains(new string('x', 30)), "buffer error leaked transcript");
        await client.StopAsync();
        var buffered = (System.Collections.IDictionary)typeof(RealtimeTranscriber).GetField("_unboundTranscripts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client)!;
        Check(buffered.Count == 0 && !client.TryAppend(Pcm(200, true)), "stop retained unacknowledged transcript content");
    }
}

static async Task Turn(RealtimeTranscriber client, FakeTransport socket, int number)
{
    Check(client.TryAppend(Pcm(200, true)), "turn voice rejected");
    for (var i = 0; i < 4; i++) Check(client.TryAppend(Pcm(200, false)), "turn silence rejected");
    await Until(() => socket.Commits >= number, "commit missing");
}
static string Final(string id, string text) => JsonSerializer.Serialize(new { type = "conversation.item.input_audio_transcription.completed", item_id = id, transcript = text });
static byte[] Pcm(int milliseconds, bool voice)
{
    var bytes = new byte[milliseconds * 48];
    if (voice) for (var i = 0; i < bytes.Length; i += 2) { var sample = (short)(Math.Sin(i / 2.0 * 2 * Math.PI * 440 / 24000) * 6000); bytes[i] = (byte)sample; bytes[i + 1] = (byte)(sample >> 8); }
    return bytes;
}
static void Clear(IEnumerable<VoiceAction> actions) { foreach (var action in actions) if (action.Pcm is { } bytes) Array.Clear(bytes); }
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T e) { return e; } throw new Exception($"Expected {typeof(T).Name}"); }
static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T e) { return e; } throw new Exception($"Expected {typeof(T).Name}"); }
static async Task Until(Func<bool> condition, string message)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try { while (!condition()) await Task.Delay(5, timeout.Token); }
    catch (OperationCanceledException) { throw new Exception(message); }
}

sealed class FakeTransport : IRealtimeTransport
{
    private readonly Channel<(byte[] Data, bool End)> _incoming = Channel.CreateUnbounded<(byte[], bool)>();
    public ConcurrentQueue<string> Sent { get; } = new();
    public Uri? Endpoint { get; private set; }
    public string? Key { get; private set; }
    public int ConnectCalls { get; private set; }
    public bool Created { get; init; } = true;
    public bool Updated { get; init; } = true;
    public bool BlockAudio { get; init; }
    public int BlockAudioAt { get; init; }
    public bool AcknowledgeClear { get; init; } = true;
    public bool AcknowledgeCommit { get; init; } = true;
    public string? UpdatedEventOverride { get; init; }
    public int AudioMessages;
    public int Commits;
    public int Clears;
    public TaskCompletionSource AudioEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseAudio { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ConnectCalls++; Endpoint = endpoint; Key = apiKey;
        if (Created) Push("{\"type\":\"session.created\",\"session\":{\"type\":\"transcription\"}}");
        return Task.CompletedTask;
    }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = Encoding.UTF8.GetString(message.Span); Sent.Enqueue(json);
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();
        if (type == "session.update" && Updated) Push(UpdatedEventOverride ?? JsonSerializer.Serialize(new { type = "session.updated", session = doc.RootElement.GetProperty("session") }));
        if (type == "input_audio_buffer.append")
        {
            var number = Interlocked.Increment(ref AudioMessages); AudioEntered.TrySetResult();
            if (BlockAudio || number == BlockAudioAt) await ReleaseAudio.Task.WaitAsync(cancellationToken);
        }
        if (type == "input_audio_buffer.commit")
        {
            var n = Interlocked.Increment(ref Commits);
            if (AcknowledgeCommit) Push(JsonSerializer.Serialize(new { type = "input_audio_buffer.committed", item_id = $"item_{n}", previous_item_id = n == 1 ? null : $"item_{n - 1}" }));
        }
        if (type == "input_audio_buffer.clear")
        {
            Interlocked.Increment(ref Clears);
            if (AcknowledgeClear) Push("{\"type\":\"input_audio_buffer.cleared\",\"event_id\":\"event_fake_clear\"}");
        }
    }
    public async ValueTask<RealtimeReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var frame = await _incoming.Reader.ReadAsync(cancellationToken);
        frame.Data.CopyTo(buffer); return new(frame.Data.Length, frame.End, true, false);
    }
    public void Push(string json, int fragmentSize = 8192)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var offset = 0; offset < bytes.Length; offset += fragmentSize)
        {
            var count = Math.Min(fragmentSize, bytes.Length - offset);
            _incoming.Writer.TryWrite((bytes.AsSpan(offset, count).ToArray(), offset + count == bytes.Length));
        }
    }
    public void Abort() { }
    public ValueTask DisposeAsync() { Key = null; return ValueTask.CompletedTask; }
}

sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}
