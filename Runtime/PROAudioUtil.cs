/// LICENSE WILD WEST LABS, INC: APACHE 2.0 [https://opensource.org/license/apache-2-0]
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

public static class PROAudioUtil
{
    // burst job
    public static (byte[] bytes, bool tooQuiet) ConvertDownsampleAndWav(
        float[] samples,
        int originalRate,
        int factor,
        float silenceThreshold,
        int channelCount,
        int targetSampleRate,
        MemoryStream reusableStream
    )
    {
        // Use ceiling division to ensure the output buffer is large enough
        int outputSampleCount = (samples.Length + factor - 1) / factor;
        var inputNative = new NativeArray<float>(samples, Allocator.TempJob);
        var outputBytes = new NativeArray<byte>(outputSampleCount * 2, Allocator.TempJob);
        var tooQuietFlag = new NativeArray<byte>(1, Allocator.TempJob); // 1=silent, 0=non-silent
        tooQuietFlag[0] = 1;

        var job = new DownsamplePackJob
        {
            input = inputNative,
            output = outputBytes,
            downFactor = factor,
            rescale = 32767f,
            silenceThreshold = silenceThreshold,
            silentByte = tooQuietFlag,
        };

        JobHandle h = job.Schedule();
        h.Complete();

        // Write wav to stream
        int dataLength = outputBytes.Length;
        reusableStream.SetLength(0);
        reusableStream.Position = 0;
        reusableStream.Write(new byte[44], 0, 44);
        reusableStream.Write(outputBytes.AsReadOnlySpan());

        // header
        reusableStream.Position = 0;
        using (BinaryWriter bw = new BinaryWriter(reusableStream, System.Text.Encoding.UTF8, true))
        {
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLength);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)channelCount);
            // Actual sample rate after integer decimation
            int actualSampleRate = math.max(1, originalRate / math.max(1, factor));
            bw.Write(actualSampleRate);
            bw.Write(actualSampleRate * channelCount * sizeof(short));
            bw.Write((short)(channelCount * sizeof(short)));
            bw.Write((short)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataLength);
        }

        byte[] result = reusableStream.ToArray();
        bool tooQuiet = tooQuietFlag[0] == 1;

        tooQuietFlag.Dispose();
        inputNative.Dispose();
        outputBytes.Dispose();

        return (result, tooQuiet);
    }

    [BurstCompile]
    private struct DownsamplePackJob : IJob
    {
        [ReadOnly]
        public NativeArray<float> input;
        public NativeArray<byte> output;
        public int downFactor;
        public float rescale;
        public float silenceThreshold;

        [NativeDisableContainerSafetyRestriction]
        public NativeArray<byte> silentByte; // length 1

        public void Execute()
        {
            int outIndex = 0;
            int len = input.Length;
            for (int i = 0; i < len; i += downFactor)
            {
                float sample = input[i];
                if (silentByte[0] == 1 && math.abs(sample) > silenceThreshold)
                    silentByte[0] = 0;
                short s = (short)(math.clamp(sample, -1f, 1f) * rescale);
                int byteIdx = outIndex * 2;
                output[byteIdx] = (byte)(s & 0xFF);
                output[byteIdx + 1] = (byte)((s >> 8) & 0xFF);
                outIndex++;
            }
        }
    }
}
