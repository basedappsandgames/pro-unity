using System.IO;
using UnityEngine;

/// <summary>
/// Sample audio chunker that strips out dead air.
/// Good for saving money by lowering the number of clips sent to server.
/// NOTE: This can cause delays if people do not talk that often
/// (eg they can say a quick slur, then not talk for a while,
/// and then their clip won't get moderated until much later)
/// </summary>
public class SilenceFilterChunkerBehaviour : AudioChunkerBehaviour
{
    [SerializeField, Tooltip("Only include samples that are above this threshold in the chunk")]
    private float silenceThreshold = 0.02f;

    private float[] _chunkBuffer; // holds the audio data
    private int _sampleIndex; // Current position in buffer
    private int _samplesPerChunk;
    private int _sampleRate = 16000;
    private int _channelCount = 1;
    private int _downsampleFactor = 1;
    private const int TargetSampleRate = 16000; // 16kHz is optimal for the PRO Moderation API
    private readonly MemoryStream _reusableStream = new MemoryStream();

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
        // Pre-allocate buffer - worst case all samples are active
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
        // Only add non-silent samples to pre-allocated buffer
        foreach (float sample in frame)
        {
            if (Mathf.Abs(sample) > silenceThreshold)
            {
                _chunkBuffer[_sampleIndex++] = sample;

                // Return chunk when buffer is full
                if (_sampleIndex >= _samplesPerChunk)
                {
                    // Create properly-sized result array
                    completeChunk = new float[_sampleIndex];
                    System.Array.Copy(_chunkBuffer, 0, completeChunk, 0, _sampleIndex);
                    _sampleIndex = 0; // Reset for next chunk
                    return true;
                }
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
            silenceThreshold,
            _channelCount,
            TargetSampleRate,
            _reusableStream
        );
        return tooQuiet ? null : wavBytes;
    }
}
