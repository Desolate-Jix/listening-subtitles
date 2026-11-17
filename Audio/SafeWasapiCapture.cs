/*
Derived from NAudio 2.4.0 WasapiCapture.cs, fixed upstream commit:
https://github.com/naudio/NAudio/blob/7c855e6737435f781dcbac782930a0de7a13cb2b/NAudio.Wasapi/WasapiCapture.cs
Local changes: one internal capture type for microphone/loopback, guarded worker
shutdown and notification, owned background callbacks, buffer cleanup. No NuGet cache changes.

Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#nullable disable
﻿using System;
using System.Threading;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace ListeningSubtitles.Audio
{
    /// <summary>
    /// Audio Capture using Wasapi
    /// See http://msdn.microsoft.com/en-us/library/dd370800%28VS.85%29.aspx
    /// </summary>
    internal sealed class SafeWasapiCapture : IWaveIn
    {
        private const long ReftimesPerSec = 10000000;
        private const long ReftimesPerMillisec = 10000;
        private volatile CaptureState captureState;
        private byte[] recordBuffer;
        private Thread captureThread;
        private AudioClient audioClient;
        private int bytesPerFrame;
        private WaveFormat waveFormat;
        private bool initialized;
        private readonly bool isUsingEventSync;
        private EventWaitHandle frameEventWaitHandle;
        private readonly int audioBufferMillisecondsLength;
        private readonly bool loopback;

        /// <summary>
        /// Indicates recorded data is available
        /// </summary>
        public event EventHandler<WaveInEventArgs> DataAvailable;

        /// <summary>
        /// Indicates that all recorded data has now been received.
        /// </summary>
        public event EventHandler<StoppedEventArgs> RecordingStopped;

        public SafeWasapiCapture(MMDevice captureDevice, bool loopback = false)
        {
            this.loopback = loopback;
            audioClient = captureDevice.AudioClient;
            ShareMode = AudioClientShareMode.Shared;
            isUsingEventSync = false;
            audioBufferMillisecondsLength = loopback ? 100 : 50;
            waveFormat = audioClient.MixFormat;
        }

        /// <summary>
        /// Share Mode - set before calling StartRecording
        /// </summary>
        public AudioClientShareMode ShareMode { get; set; }

        /// <summary>
        /// Current Capturing State
        /// </summary>
        public CaptureState CaptureState {  get { return captureState; } }

        /// <summary>
        /// Capturing wave format
        /// </summary>
        public WaveFormat WaveFormat
        {
            get
            {
                // for convenience, return a WAVEFORMATEX, instead of the real
                // WAVEFORMATEXTENSIBLE being used
                return waveFormat.AsStandardWaveFormat();
            }
            set { waveFormat = value; }
        }

        private void InitializeCaptureDevice()
        {
            if (initialized)
                return;

            long requestedDuration = ReftimesPerMillisec * audioBufferMillisecondsLength;

            if ((ShareMode == AudioClientShareMode.Exclusive) && !audioClient.IsFormatSupported(ShareMode, waveFormat))
            {
                throw new ArgumentException("Unsupported Wave Format");
            }

            var streamFlags = GetAudioClientStreamFlags();

            // If using EventSync, setup is specific with shareMode
            if (isUsingEventSync)
            {
                // Init Shared or Exclusive
                if (ShareMode == AudioClientShareMode.Shared)
                {
                    // With EventCallBack and Shared, both latencies must be set to 0
                    audioClient.Initialize(ShareMode, AudioClientStreamFlags.EventCallback | streamFlags, requestedDuration, 0,
                        waveFormat, Guid.Empty);
                }
                else
                {
                    // With EventCallBack and Exclusive, both latencies must equals
                    audioClient.Initialize(ShareMode, AudioClientStreamFlags.EventCallback | streamFlags, requestedDuration, requestedDuration,
                                        waveFormat, Guid.Empty);
                }

                // Create the Wait Event Handle
                frameEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
                audioClient.SetEventHandle(frameEventWaitHandle.SafeWaitHandle.DangerousGetHandle());
            }
            else
            {
                // Normal setup for both sharedMode
                audioClient.Initialize(ShareMode,
                streamFlags,
                requestedDuration,
                0,
                waveFormat,
                Guid.Empty);
            }

            int bufferFrameCount = audioClient.BufferSize;
            bytesPerFrame = waveFormat.Channels * waveFormat.BitsPerSample / 8;
            recordBuffer = new byte[bufferFrameCount * bytesPerFrame];

            //Debug.WriteLine(string.Format("record buffer size = {0}", this.recordBuffer.Length));

            initialized = true;
        }

        /// <summary>
        /// To allow overrides to specify different flags (e.g. loopback)
        /// </summary>
        private AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            if (ShareMode == AudioClientShareMode.Shared)
            {
                // enable auto-convert PCM
                return AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality
                    | (loopback ? AudioClientStreamFlags.Loopback : 0);
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Start Capturing
        /// </summary>
        public void StartRecording()
        {
            if (captureState != CaptureState.Stopped)
            {
                throw new InvalidOperationException("Previous recording still in progress");
            }
            captureState = CaptureState.Starting;
            InitializeCaptureDevice();
            captureThread = new Thread(() => CaptureThread(audioClient))
            {
                IsBackground = true,
            };
            captureThread.Start();
        }

        /// <summary>
        /// Stop Capturing (requests a stop, wait for RecordingStopped event to know it has finished)
        /// </summary>
        public void StopRecording()
        {
            if (captureState != CaptureState.Stopped)
                captureState = CaptureState.Stopping;
        }

        private void CaptureThread(AudioClient client)
        {
            RunCaptureWorker(() => DoRecording(client), () => client.Stop(), exception =>
            {
                captureState = CaptureState.Stopped;
                RaiseRecordingStopped(exception);
            });
        }

        // Factored from the exact upstream worker for device-free failure regression tests.
        internal static void RunCaptureWorker(Action capture, Action stop, Action<Exception> stopped)
        {
            Exception failure = null;
            try { capture(); }
            catch (Exception error) { failure = error; }
            finally
            {
                // IAudioClient.Stop translates a device-invalidated HRESULT into an exception.
                // It must not escape this background thread or hide the original capture failure.
                try { stop(); }
                catch (Exception error) { failure ??= error; }
            }
            try { stopped(failure); }
            catch { /* A subscriber cannot turn a driver failure into an unhandled thread exception. */ }
        }

        private void DoRecording(AudioClient client)
        {
            //Debug.WriteLine(String.Format("Client buffer frame count: {0}", client.BufferSize));
            int bufferFrameCount = client.BufferSize;

            // Calculate the actual duration of the allocated buffer.
            long actualDuration = (long)((double)ReftimesPerSec *
                             bufferFrameCount / waveFormat.SampleRate);
            int sleepMilliseconds = (int)(actualDuration / ReftimesPerMillisec / 2);
            int waitMilliseconds = (int)(3 * actualDuration / ReftimesPerMillisec);

            var capture = client.AudioCaptureClient;
            client.Start();
            // avoid race condition where we stop immediately after starting
            if (captureState == CaptureState.Starting)
            {
                captureState = CaptureState.Capturing;
            }
            while (captureState == CaptureState.Capturing)
            {
                if (isUsingEventSync)
                {
                    frameEventWaitHandle.WaitOne(waitMilliseconds, false);
                }
                else
                {
                    Thread.Sleep(sleepMilliseconds);
                }
                if (captureState != CaptureState.Capturing)
                    break;

                // If still recording
                ReadNextPacket(capture);
            }
        }

        private void RaiseRecordingStopped(Exception error)
        {
            // SystemAudioCapture owns this internal type and dispatches subscribers itself.
            // Do not post this driver notification onto a potentially blocked UI thread.
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }

        private void ReadNextPacket(AudioCaptureClient capture)
        {
            int packetSize = capture.GetNextPacketSize();
            int recordBufferOffset = 0;
            //Debug.WriteLine(string.Format("packet size: {0} samples", packetSize / 4));

            while (packetSize != 0)
            {
                IntPtr buffer = capture.GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags);

                int bytesAvailable = framesAvailable * bytesPerFrame;

                // apparently it is sometimes possible to read more frames than we were expecting?
                // fix suggested by Michael Feld:
                int spaceRemaining = Math.Max(0, recordBuffer.Length - recordBufferOffset);
                if (spaceRemaining < bytesAvailable && recordBufferOffset > 0)
                {
                    DataAvailable?.Invoke(this, new WaveInEventArgs(recordBuffer, recordBufferOffset));
                    recordBufferOffset = 0;
                }

                // if not silence...
                if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent)
                {
                    Marshal.Copy(buffer, recordBuffer, recordBufferOffset, bytesAvailable);
                }
                else
                {
                    Array.Clear(recordBuffer, recordBufferOffset, bytesAvailable);
                }
                recordBufferOffset += bytesAvailable;
                capture.ReleaseBuffer(framesAvailable);
                packetSize = capture.GetNextPacketSize();
            }
            DataAvailable?.Invoke(this, new WaveInEventArgs(recordBuffer, recordBufferOffset));
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            StopRecording();
            if (captureThread != null)
            {
                if (captureThread != Thread.CurrentThread) captureThread.Join();
                captureThread = null;
            }
            if (recordBuffer != null) Array.Clear(recordBuffer);
            if (audioClient != null)
            {
                audioClient.Dispose();
                audioClient = null;
            }
        }
    }
}
