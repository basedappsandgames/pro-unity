// CREDIT TO MISCHA W FROM BLOBTOWN FOR THIS SCRIPT

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Audio chunker that uses a simple VAD to keep only voiced audio and
/// emits fixed-size chunks of voiced samples.
/// </summary>
public class VADAudioChunkerBehaviour : AudioChunkerBehaviour
{
    [Header("Chunking")]
    [SerializeField, Tooltip("Chunk length in seconds (set by PROManager)")]
    private int _configuredChunkDurationSeconds = 15;

    [Header("VAD Settings (defaults tuned for ~48kHz)")]
    [SerializeField, Tooltip("Energy threshold for voice detection")]
    private float _energyThreshold = 0.0001f;

    [SerializeField, Tooltip("Zero crossing rate threshold")]
    private float _zcrThreshold = 0.2f;

    [
        SerializeField,
        Tooltip("Hangover time in milliseconds to keep voice active after speech ends")
    ]
    private int _hangoverMs = 500;

    [SerializeField, Tooltip("Smoothing factor for running energy (0..1), higher = slower")]
    private float _energyAlpha = 0.95f;

    [Header("Encoding")]
    [
        SerializeField,
        Tooltip(
            "Minimum amplitude to consider non-silent when encoding (used for final quiet check)"
        )
    ]
    private float _encodeSilenceThreshold = 0.0f;

    private float[] _voicedBuffer; // holds only voiced samples
    private int _sampleIndex; // Current position in buffer
    private int _samplesPerChunk;
    private int _sampleRate = 16000;
    private int _channelCount = 1;
    private int _downsampleFactor = 1;
    private const int TargetSampleRate = 16000; // 16kHz is optimal for the PRO Moderation API
    private readonly MemoryStream _reusableStream = new MemoryStream();

    // VAD instance
    private VoiceActivityDetector _vad;

    public override void Configure(
        int chunkDurationSeconds,
        int? sampleRate = null,
        int? channelCount = null
    )
    {
        _configuredChunkDurationSeconds = chunkDurationSeconds;
        _sampleRate = sampleRate ?? _sampleRate;
        _channelCount = channelCount ?? _channelCount;
        _samplesPerChunk = _sampleRate * _channelCount * _configuredChunkDurationSeconds;
        _downsampleFactor = Mathf.Max(1, _sampleRate / TargetSampleRate);

        _voicedBuffer = new float[_samplesPerChunk];
        _sampleIndex = 0;

        // Initialize VAD with current sample rate and thresholds
        _vad = new VoiceActivityDetector(
            _sampleRate,
            _energyThreshold,
            _zcrThreshold,
            _hangoverMs,
            _energyAlpha
        );
        _vad.Reset();
    }

    public override bool TryAppend(float[] frame, out float[] completeChunk)
    {
        completeChunk = null;
        if (_voicedBuffer == null)
        {
            Debug.LogError(
                "[PRO] Chunk buffer is not set, did you call Configure on your AudioChunkerBehaviour?"
            );
            return false;
        }

        // Run VAD on the incoming frame and collect voiced samples
        float[] voiced = _vad.ProcessAudioData(frame);
        if (voiced == null || voiced.Length == 0)
        {
            return false;
        }

        int offset = 0;
        int remaining = voiced.Length;
        while (remaining > 0)
        {
            int spaceLeft = _samplesPerChunk - _sampleIndex;
            int toCopy = Mathf.Min(spaceLeft, remaining);
            Array.Copy(voiced, offset, _voicedBuffer, _sampleIndex, toCopy);
            _sampleIndex += toCopy;
            offset += toCopy;
            remaining -= toCopy;

            if (_sampleIndex >= _samplesPerChunk)
            {
                completeChunk = new float[_samplesPerChunk];
                Array.Copy(_voicedBuffer, 0, completeChunk, 0, _samplesPerChunk);
                _sampleIndex = 0;
                return true; // emit one chunk per call; leftover will be appended on next calls
            }
        }

        return false;
    }

    public override byte[] Encode(float[] samples)
    {
        var (wavBytes, tooQuiet) = PROAudioUtil.ConvertDownsampleAndWav(
            samples,
            _sampleRate,
            _downsampleFactor,
            _encodeSilenceThreshold,
            _channelCount,
            TargetSampleRate,
            _reusableStream
        );
        return tooQuiet ? null : wavBytes;
    }

    /// <summary>
    /// Lightweight VAD implementation modeled after user's snippet, adapted for arbitrary sample rates.
    /// Emits the delayed voiced frames to allow a small pre-roll via hangover buffering.
    /// </summary>
    private class VoiceActivityDetector
    {
        private readonly int _sampleRate;
        private readonly float _energyThreshold;
        private readonly float _zcrThreshold;
        private readonly int _hangoverFramesTarget;
        private readonly float _alpha;

        private readonly int _frameSize; // ~10ms frames
        private readonly float[] _frameBuffer;
        private int _frameBufferIndex;

        private int _hangoverFrames;
        private float _runningEnergy;

        private readonly Queue<float[]> _delayQueue;

        public VoiceActivityDetector(
            int sampleRate,
            float energyThreshold,
            float zcrThreshold,
            int hangoverMs,
            float alpha
        )
        {
            _sampleRate = Mathf.Max(1, sampleRate);
            _energyThreshold = Mathf.Max(0f, energyThreshold);
            _zcrThreshold = Mathf.Clamp01(zcrThreshold);
            _alpha = Mathf.Clamp01(alpha);

            // 10ms frame size derived from sample rate
            _frameSize = Mathf.Max(1, _sampleRate / 100);
            _frameBuffer = new float[_frameSize];
            _frameBufferIndex = 0;

            // Convert hangover milliseconds to frames
            int framesPerSecond = Mathf.Max(1, _sampleRate / _frameSize);
            _hangoverFramesTarget = Mathf.Max(
                0,
                Mathf.RoundToInt((hangoverMs / 1000f) * framesPerSecond)
            );

            _hangoverFrames = 0;
            _runningEnergy = 0f;
            _delayQueue = new Queue<float[]>();
        }

        public void Reset()
        {
            _frameBufferIndex = 0;
            _hangoverFrames = 0;
            _runningEnergy = 0f;
            _delayQueue.Clear();
            Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
        }

        public float[] ProcessAudioData(float[] inputData)
        {
            if (inputData == null || inputData.Length == 0)
                return Array.Empty<float>();

            List<float> outputList = new List<float>(inputData.Length);

            foreach (float sample in inputData)
            {
                _frameBuffer[_frameBufferIndex++] = sample;

                if (_frameBufferIndex >= _frameSize)
                {
                    ProcessFrame(outputList);
                    _frameBufferIndex = 0;
                }
            }

            return outputList.Count == 0 ? Array.Empty<float>() : outputList.ToArray();
        }

        private void ProcessFrame(List<float> outputList)
        {
            float energy = 0f;
            int zeroCrossings = 0;

            for (int i = 0; i < _frameSize; i++)
            {
                float s = _frameBuffer[i];
                energy += s * s;
                if (i > 0)
                {
                    float prev = _frameBuffer[i - 1];
                    if ((s >= 0 && prev < 0) || (s < 0 && prev >= 0))
                    {
                        zeroCrossings++;
                    }
                }
            }

            energy /= _frameSize;
            float zcr = (float)zeroCrossings / _frameSize;

            // Smoothed energy baseline
            _runningEnergy = _alpha * _runningEnergy + (1f - _alpha) * energy;

            bool isVoice = false;
            if (energy > _energyThreshold && zcr < _zcrThreshold)
            {
                isVoice = true;
                _hangoverFrames = _hangoverFramesTarget;
            }
            else if (_hangoverFrames > 0)
            {
                isVoice = true;
                _hangoverFrames--;
            }

            // store current frame (copy) into delay queue
            float[] frameCopy = new float[_frameSize];
            Array.Copy(_frameBuffer, frameCopy, _frameSize);
            _delayQueue.Enqueue(frameCopy);

            // once queue exceeds hangover, emit delayed frame if we are in voice
            if (_delayQueue.Count > _hangoverFramesTarget)
            {
                float[] delayedFrame = _delayQueue.Dequeue();
                if (isVoice)
                {
                    outputList.AddRange(delayedFrame);
                }
            }
        }
    }
}
