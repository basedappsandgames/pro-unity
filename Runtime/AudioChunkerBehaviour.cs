using UnityEngine;

public abstract class AudioChunkerBehaviour : MonoBehaviour, IAudioChunker
{
    // init the chunker with your parameters based on input device
    public abstract void Configure(
        int chunkDurationSeconds,
        int? sampleRate = null,
        int? channelCount = null
    );

    // append a frame to the chunker, return true and a complete chunk when ready; otherwise false
    public abstract bool TryAppend(float[] frame, out float[] completeChunk);

    // encode the chunk into a byte array, prefer 16kHz wav
    public abstract byte[] Encode(float[] samples);
}
