public interface IAudioChunker
{
    void Configure(int chunkDurationSeconds, int? sampleRate = null, int? channelCount = null);
    // Returns true and a complete chunk when ready; otherwise false
    bool TryAppend(float[] frame, out float[] completeChunk);
    byte[] Encode(float[] samples);
}
