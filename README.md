# GameStream.Server

Servidor (Windows, C#/.NET 10) que roda um jogo, transmite a janela em H.264 e recebe comandos de teclado
por WebSocket. Pensado para jogos 2D feitos para teclado (RPG Maker, Godot).

> Este esqueleto foi escrito sem compilar (o ambiente onde foi gerado não tem o .NET SDK).
> Se der erro de compilação, copie a mensagem e eu corrijo.

## Requisitos

- Windows 10/11 e [.NET 10 SDK](https://dotnet.microsoft.com/download). No Visual Studio, instale a carga de trabalho "ASP.NET e desenvolvimento para a Web".
- `ffmpeg.exe` em `tools/` (build "essentials" do ffmpeg para Windows, com libx264 e gdigrab).
  Se não estiver lá, o servidor tenta o `ffmpeg` do PATH.

## Rodar

```
cd GameStream.Server
dotnet run
```

O console mostra o PIN de pareamento, a pasta dos jogos e os limites por jogo.
Para fixar o PIN, preencha `Server:Pin` em `appsettings.json`.

## Pasta dos jogos

Por padrão: `Documentos\jogos_servidor` (ex.: `C:\Users\usuario\Documents\jogos_servidor`).
Para mudar, preencha `Server:GamesDir` em `appsettings.json`.

O servidor cria a pasta e uma pasta esqueleto (`meu-primeiro-jogo`) na primeira execução.
Para criar outras:

```
dotnet run -- novo "Nome do Jogo"
```

Estrutura de cada jogo:

```
jogos_servidor\
  Nome do Jogo\
    (arquivos do jogo, em qualquer subpasta)  OU  um atalho .lnk para o .exe
    game.json        <- gerado sozinho na primeira vez; edite se quiser
    dados\           <- informações do servidor sobre este jogo
      appdata\       <- saves de jogos que gravam em AppData (veja abaixo)
    LEIA-ME.txt
```

Pastas que começam com `_` ou `.` são ignoradas.

### Como ele escolhe o que executar

1. Se `game.json` já tem `exe`, usa esse.
2. Senão procura um **atalho (.lnk)** que aponte para um .exe (até 2 níveis de pasta). O destino pode estar em outra pasta.
3. Senão procura um **.exe** (até 3 níveis), preferindo `Game.exe` (RPG Maker), depois o mais raso e o maior.
   Ignora desinstaladores, instaladores, redistribuíveis, updaters e crash handlers.

A escolha vai para o `game.json` (campo `exe`). Se ele errar, corrija esse campo.
Jogos novos aparecem sem reiniciar o servidor (as pastas são relidas a cada `/games` e a cada conexão).

## Limites por jogo

Padrão em `appsettings.json`: `GameCores: 2`, `GameMemoryMb: 4096`, `CoreStart: 0`.
No i5-3470 (4 núcleos, sem hyper-threading): o jogo fica nos núcleos 0 e 1 e o ffmpeg nos núcleos 2 e 3.
Cada jogo pode ter valores próprios em `game.json` (`cores`, `memoryMb`).

- A memória é o total confirmado (commit) de todos os processos do jogo; passou disso, o jogo recebe erro de alocação.
- O servidor avisa no console se o limite de RAM for grande demais para a máquina.

## Saves

- **RPG Maker**: grava na própria pasta do jogo.
- **Godot** e outros jogos que usam AppData: com `redirectAppData: true` (padrão) o servidor aponta `APPDATA` e
  `LOCALAPPDATA` do jogo para `dados\appdata`, então os saves ficam dentro da pasta do jogo.
  Jogos que ignoram essas variáveis ainda gravam no AppData do Windows.
- Todos os jogadores de um mesmo jogo compartilham os mesmos saves (por enquanto é um jogador por vez).

## Testar sem o APK

1. Abra `http://localhost:5055/test.html` no Chrome/Edge, no mesmo PC.
2. Digite o PIN e o id do jogo (nome da pasta em minúsculas), clique em Conectar.
3. As teclas do navegador viram comandos, desde que estejam em `allowedKeys`.

Para o celular na rede local, o WebCodecs não funciona em HTTP fora de localhost; é aí que entra o APK.
Libere a porta 5055 no firewall do Windows quando for testar pela rede.

## Protocolo

`ws://<ip>:5055/stream?game=<id>&pin=<pin>`

| Direção | Formato |
|---|---|
| servidor -> cliente | texto `{"t":"hello","game","w","h","fps"}` ao iniciar |
| servidor -> cliente | binário: `[1][flags][quadro H.264 Annex B]` (flags bit0 = keyframe) |
| servidor -> cliente | texto `{"t":"error","msg":"..."}` se algo falhar |
| cliente -> servidor | texto `{"t":"key","k":"Z","d":1}` (d: 1 apertou, 0 soltou) |

`GET /games?pin=<pin>` lista os jogos, as teclas permitidas e o layout padrão dos botões.

## Estrutura do código

- `Config/`: opções, perfil (`game.json`), descoberta de .exe/atalho, pastas esqueleto.
- `Sandbox/`: lançador do jogo, limites de CPU/RAM e Job Object (mata o jogo ao fechar a sessão, bloqueia clipboard/resolução etc.).
- `Streaming/`: ffmpeg (captura + H.264) e divisor de quadros.
- `Input/`: scancodes e `SendInput` (só teclado, só teclas do perfil).
- `Net/`: PIN e sessão WebSocket.
- `Interop/`: P/Invoke do Windows.

## Limites conhecidos (próximas etapas)

- **Sandbox**: o Job Object não isola arquivos nem rede. Falta token restrito / AppContainer (ou Sandboxie), com escrita liberada só na pasta do jogo.
- **Foco**: `SendInput` vai para a janela em primeiro plano. Ideal: monitor virtual ou sessão do Windows dedicada ao jogo.
- **Captura**: `gdigrab` por título de janela pode mostrar tela preta em alguns jogos. Alternativa: Windows Graphics Capture.
- **Rede**: HTTP/WebSocket puro e PIN em query string. Só para rede local. Falta TLS, limite de tentativas e token por dispositivo.
- **Latência**: transporte é TCP; se o Wi-Fi oscilar, o atraso cresce. Depois: descartar quadros, UDP, bitrate adaptativo.
- **Um jogador por vez** (MVP).
