using System.Text;

namespace GeminiMcp.Gemini;

/// <summary>생성된 오디오의 WAV 변환.</summary>
public static class OutputFiles
{
    /// <summary>Gemini TTS 의 raw PCM(audio/L16;rate=24000) 을 WAV 컨테이너로 감쌉니다.</summary>
    public static byte[] PcmToWav(byte[] pcm, string mimeType)
    {
        var sampleRate = 24000;
        foreach (var segment in mimeType.Split(';'))
        {
            var kv = segment.Trim().Split('=');
            if (kv.Length == 2 && kv[0].Equals("rate", StringComparison.OrdinalIgnoreCase) && int.TryParse(kv[1], out var rate))
                sampleRate = rate;
        }

        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;

        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bitsPerSample / 8));
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
