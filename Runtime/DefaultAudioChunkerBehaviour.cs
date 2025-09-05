using System.IO;
using UnityEngine;

/// <summary>
/// Sample default audio chunker that copies input audio frames into a
/// rolling buffer of complete chunks
/// </summary>
public class DefaultAudioChunkerBehaviour : AudioChunkerBehaviour
{
    [SerializeField, Tooltip("Skip chunk if all samples are below this threshold")]
    private float _silenceThreshold = 0.021f;
    private float[] _chunkBuffer; // holds the audio data
    private int _sampleIndex; // Current position in buffer
    private int _samplesPerChunk; // once we hit this number of samples, complete the chunk
    private int _sampleRate = 16000;
    private int _channelCount = 1;
    private readonly MemoryStream _reusableStream = new MemoryStream();
    private int _downsampleFactor = 1;
    private const int TargetSampleRate = 16000; // 16kHz is optimal for the PRO Moderation API

    public override void Configure(
        int chunkDurationSeconds,
        int? sampleRate = null,
        int? channelCount = null
    )
    {
        _sampleRate = sampleRate ?? _sampleRate;
        _channelCount = channelCount ?? _channelCount;
        _samplesPerChunk = _sampleRate * _channelCount * chunkDurationSeconds;
        _downsampleFactor = Mathf.Max(1, _sampleRate / TargetSampleRate);
        if (_chunkBuffer != null)
        {
            // destroy and free memory old buffer
            _chunkBuffer = null;
        }
        _chunkBuffer = new float[_samplesPerChunk];
        _sampleIndex = 0;
    }

    public override bool TryAppend(float[] frame, out float[] completeChunk)
    {
        completeChunk = null;
        if (_chunkBuffer == null)
        {
            Debug.LogError(
                "[PRO] Chunk buffer is not set, did you call Configure on your AudioChunkerBehaviour?"
            );
            return false;
        }

        int copyLen = Mathf.Min(frame.Length, _chunkBuffer.Length - _sampleIndex);
        System.Array.Copy(frame, 0, _chunkBuffer, _sampleIndex, copyLen);
        _sampleIndex += copyLen;

        if (_sampleIndex >= _samplesPerChunk)
        {
            completeChunk = _chunkBuffer;
            _sampleIndex = 0;
            return true;
        }
        return false;
    }

    public override byte[] Encode(float[] samples)
    {
        var (wavBytes, tooQuiet) = PROAudioUtil.ConvertDownsampleAndWav(
            samples,
            _sampleRate,
            _downsampleFactor,
            _silenceThreshold,
            _channelCount,
            TargetSampleRate,
            _reusableStream
        );
        return tooQuiet ? null : wavBytes;
    }
}
