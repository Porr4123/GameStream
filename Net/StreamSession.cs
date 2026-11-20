using System.Net.WebSockets;
using System.Text.Json;
using GameStream.Server.Config;
using GameStream.Server.Input;
using GameStream.Server.Interop;
using GameStream.Server.Sandbox;
using GameStream.Server.Streaming;

namespace GameStream.Server.Net;

/// <summary>
/// Uma sessão = um cliente jogando um jogo.
/// Servidor -> cliente:  texto {"t":"hello",...} | binário [1][flags][quadro H.264 Annex B]
/// Cliente -> servidor:  texto {"t":"key","k":"Z","d":1}   (d: 1 = apertou, 0 = soltou)
/// </summary>
public sealed class StreamSession
{
    private readonly GameProfile _profile;
    private readonly ServerOptions _options;
    private readonly string _ffmpegPath;
    private readonly ILogger _log;

    public StreamSession(GameProfile profile, ServerOptions options, string ffmpegPath, ILogger log)
    {
        _profile = profile;
        _options = options;
        _ffmpegPath = ffmpegPath;
        _log = log;
    }

    public async Task RunAsync(WebSocket ws, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = cts.Token;

        LaunchedGame? game = null;
        FfmpegEncoder? encoder = null;
        InputInjector? input = null;
        Task encoderTask = Task.CompletedTask, receiveTask = Task.CompletedTask, watchTask = Task.CompletedTask;

        try
        {
            _log.LogInformation("Iniciando jogo {Game}", _profile.Id);
            game = await GameLauncher.LaunchAsync(_profile, _options, ct);
            _log.LogInformation("Jogo {Game}: {Cores} núcleo(s), limite de {Mem} MB", _profile.Id, game.Limits.Cores, game.Limits.MemoryMb);
            input = new InputInjector(_profile.AllowedKeys, game.WindowHandle);

            await SendJsonAsync(ws, new
            {
                t = "hello",
                game = _profile.Id,
                w = _profile.StreamWidth,
                h = _profile.StreamHeight,
                fps = _profile.Fps
            }, ct);

            encoder = new FfmpegEncoder(_ffmpegPath, game.WindowTitle, _profile, game.Limits.ServerMask);
            encoderTask = encoder.RunAsync(au => SendVideoAsync(ws, au, ct), ct);
            receiveTask = ReceiveLoopAsync(ws, input, ct);
            watchTask = WatchWindowAsync(game.WindowHandle, ct);

            // Termina quando o cliente sai, o ffmpeg para ou a janela do jogo fecha.
            var finished = await Task.WhenAny(encoderTask, receiveTask, watchTask);
            await finished; // repassa erro, se houver
        }
        catch (OperationCanceledException)
        {
            // sessão encerrada
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro na sessão de {Game}", _profile.Id);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await SendJsonAsync(ws, new { t = "error", msg = ex.Message }, CancellationToken.None);
            }
            catch { /* cliente já saiu */ }
        }
        finally
        {
            cts.Cancel();
            input?.ReleaseAll();
            encoder?.Dispose();
            game?.Dispose();

            await SwallowAsync(encoderTask);
            await SwallowAsync(receiveTask);
            await SwallowAsync(watchTask);

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None); }
                catch { /* ignora */ }
            }
            _log.LogInformation("Sessão de {Game} encerrada", _profile.Id);
        }
    }

    // TODO: se o cliente for lento, o envio bloqueia e o atraso cresce. Descartar quadros / reduzir bitrate.
    private static Task SendVideoAsync(WebSocket ws, byte[] accessUnit, CancellationToken ct)
    {
        var message = new byte[accessUnit.Length + 2];
        message[0] = 1;                                                   // tipo: vídeo
        message[1] = (byte)(AnnexBSplitter.IsKeyFrame(accessUnit) ? 1 : 0); // bit0: keyframe
        Buffer.BlockCopy(accessUnit, 0, message, 2, accessUnit.Length);
        return ws.SendAsync(message, WebSocketMessageType.Binary, true, ct);
    }

    private static Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, ct);

    private async Task ReceiveLoopAsync(WebSocket ws, InputInjector input, CancellationToken ct)
    {
        var buffer = new byte[1024];
        using var message = new MemoryStream();
        bool discard = false;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;

            if (!discard)
            {
                if (message.Length + result.Count > 4096) { discard = true; message.SetLength(0); }
                else message.Write(buffer, 0, result.Count);
            }

            if (!result.EndOfMessage) continue;

            if (!discard && result.MessageType == WebSocketMessageType.Text)
                HandleControl(message.ToArray(), input);

            message.SetLength(0);
            discard = false;
        }
    }

    private void HandleControl(byte[] json, InputInjector input)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("t").GetString() != "key") return;

            var key = root.GetProperty("k").GetString();
            var down = root.GetProperty("d").GetInt32() != 0;
            if (key is not null && !input.Handle(key, down))
                _log.LogDebug("Tecla ignorada: {Key}", key);
        }
        catch (Exception)
        {
            // mensagem malformada: ignora
        }
    }

    private static async Task WatchWindowAsync(IntPtr hwnd, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Native.IsWindow(hwnd)) return;
            await Task.Delay(1000, ct);
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task; } catch { /* já tratado */ }
    }
}
