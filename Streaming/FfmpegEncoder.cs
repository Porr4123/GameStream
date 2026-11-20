using System.Diagnostics;
using GameStream.Server.Config;

namespace GameStream.Server.Streaming;

/// <summary>
/// MVP: o ffmpeg captura a janela (gdigrab), codifica em H.264 de baixa latência e despeja Annex B no stdout.
/// Depois dá para trocar por Windows Graphics Capture + encoder de hardware sem mudar o resto.
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _windowTitle;
    private readonly GameProfile _profile;
    private readonly ulong _affinityMask;
    private Process? _process;

    /// <param name="affinityMask">Núcleos permitidos ao ffmpeg (os que o jogo não usa). 0 = sem restrição.</param>
    public FfmpegEncoder(string ffmpegPath, string windowTitle, GameProfile profile, ulong affinityMask)
    {
        _ffmpegPath = ffmpegPath;
        _windowTitle = windowTitle;
        _profile = profile;
        _affinityMask = affinityMask;
    }

    public async Task RunAsync(Func<byte[], Task> onAccessUnit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in BuildArgs()) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        _process = process;

        if (_affinityMask != 0)
        {
            try { process.ProcessorAffinity = (IntPtr)(long)_affinityMask; }
            catch { /* sem permissão: segue sem restrição */ }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                    Console.WriteLine($"[ffmpeg] {line}");
            }
            catch { /* processo encerrado */ }
        });

        var splitter = new AnnexBSplitter();
        var buffer = new byte[64 * 1024];
        var stdout = process.StandardOutput.BaseStream;

        while (!ct.IsCancellationRequested)
        {
            int n = await stdout.ReadAsync(buffer, ct);
            if (n == 0) break; // ffmpeg terminou

            foreach (var au in splitter.Push(buffer, n))
                await onAccessUnit(au);
        }
    }

    private List<string> BuildArgs()
    {
        int fps = Math.Max(1, _profile.Fps);
        int kbps = Math.Max(200, _profile.BitrateKbps);

        return new List<string>
        {
            "-hide_banner", "-loglevel", "warning",

            // Entrada: janela do jogo, sem cursor.
            "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "0",
            "-i", $"title={_windowTitle}",

            // 'neighbor' mantém pixel art nítida.
            "-vf", $"scale={_profile.StreamWidth}:{_profile.StreamHeight}:flags=neighbor,format=yuv420p",

            // Saída: H.264 baseline, sem B-frames, AUD em todo quadro, SPS/PPS em todo keyframe.
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-level:v", "3.1",
            "-bf", "0", "-g", (fps * 2).ToString(),
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps / 2}k",
            "-x264-params", "aud=1:repeat-headers=1",
            "-f", "h264", "pipe:1"
        };
    }

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* ignora */ }
        _process?.Dispose();
    }
}
