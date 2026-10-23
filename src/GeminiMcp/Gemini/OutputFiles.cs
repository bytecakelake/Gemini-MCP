using System.Text;

namespace GeminiMcp.Gemini;

/// <summary>생성된 이미지/오디오 파일의 저장 경로 결정 및 WAV 변환.</summary>
public static class OutputFiles
{
    /// <param name="requested">사용자가 지정한 경로(없으면 기본 출력 폴더에 자동 이름). 상대 경로는 출력 폴더 기준.</param>
    /// <param name="index">여러 파일을 저장할 때 0 이 아닌 번호를 붙입니다.</param>
    public static string Resolve(GeminiOptions options, string? requested, string prefix, string extension, int index = 0)
    {
        var outputDirectory = options.OutputDirectory;
        string path;
        if (string.IsNullOrWhiteSpace(requested))
        {
            path = Path.Combine(outputDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}");
        }
        else
        {
            var expanded = options.Paths.ToLocal(Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"')));
            path = Path.IsPathRooted(expanded) ? expanded : Path.Combine(outputDirectory, expanded);
            if (Directory.Exists(path) || expanded.EndsWith('/') || expanded.EndsWith('\\'))
                path = Path.Combine(path, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}");
            else if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                // 실제 데이터 형식과 확장자를 일치시킵니다 (예: 모델이 JPEG 를 반환했는데 .png 를 요청한 경우).
                path = Path.ChangeExtension(path, extension);
        }

        if (index > 0)
            path = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{index + 1}{Path.GetExtension(path)}");

        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

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
