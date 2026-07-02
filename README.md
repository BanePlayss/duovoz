# DuoVoz

Intercomunicador de voz **ponto-a-ponto na rede local (LAN)** para um casal: um canal de
voz privado, **totalmente separado do Discord**. Um lado fala, o outro ouve no fone. Tem
um botão **"Compartilhar música"** que manda o áudio do PC junto com a voz.

Roda **nos dois PCs** (na mesma rede de casa) e **se descobre + conecta sozinho** — não
precisa digitar IP, porta nem escolher microfone.

## Recursos

- **Voz P2P** na LAN, privada (o Discord nunca ouve).
- **Auto-conexão**: acha o par na rede e conecta sozinho; salva como "amigo" e reconecta rápido.
- **Supressão de ruído** no microfone (SpeexDSP), ligada por padrão.
- **Chat** com histórico + **ping** (widget pisca e toca um sininho no outro lado).
- **Widget flutuante** minúsculo, sempre por cima, pra pingar/abrir o chat sem sair do jogo.
- **Enviar arquivos grandes (5 GB+)** direto pela rede local, com progresso e verificação.
- **Botão Atualizar**: baixa e instala a versão nova sozinho (Velopack).
- **Compartilhar música**: manda o som do seu PC pro par (exclui o próprio DuoVoz, pra não
  criar eco).

---

## Instalar (recomendado — sem complicação)

Baixe e rode o instalador. **Não precisa de .NET nem de linha de comando.**

### **[⬇ Baixar o instalador (DuoVoz-win-Setup.exe)](https://github.com/BanePlayss/duovoz/releases/latest/download/DuoVoz-win-Setup.exe)**

1. Abra o `DuoVoz-win-Setup.exe` baixado.
2. Na 1ª vez o Windows mostra "App não reconhecido" →
   **Mais informações → Executar assim mesmo** (é seguro; só não é assinado digitalmente).
3. Instala sozinho (**sem senha de administrador**), cria atalho no Menu Iniciar e na Área
   de Trabalho, e já abre.

Faça isso **nos dois PCs**. Use **fone de ouvido** dos dois lados. Cada um acha o outro na
rede e conecta automaticamente. Se o Windows pedir, **libere no Firewall** (rede Privada).

> Sem cancelamento de eco: em caixa de som dá microfonia. Fone nos dois lados.

---

## Rodar a partir do código (alternativa — para devs / assistente)

Precisa do **.NET 10 SDK**. Se não tiver:

```powershell
winget install Microsoft.DotNet.SDK.10
```

Na raiz do repositório:

```powershell
dotnet run -c Release
```

Se você é um **assistente (Claude)** configurando na máquina do parceiro: garanta o .NET 10
SDK, rode `dotnet run -c Release`, deixe **"Conectar automaticamente"** marcado, e libere o
`DuoVoz.exe` no Firewall (rede Privada) se aparecer o prompt. Ambos os lados: mesma LAN +
fone. Log de diagnóstico em `%TEMP%\DuoVoz\duovoz.log`.

---

## Gerar o instalador (mantenedor)

Requer `.NET 10 SDK`, `vpk` (`dotnet tool install -g vpk`) e `gh` autenticado. Depois:

```powershell
./build-installer.ps1 -Version 1.0.1
```

Isso publica/compila, empacota o `Setup.exe` (Velopack) e sobe uma nova Release no GitHub.

---

## Notas técnicas

- C# / .NET 10 / WinForms / **NAudio 2.3.0**. Instalador/atualização: **Velopack**.
- Voz: UDP, PCM 48 kHz mono 16-bit, frames de 10 ms; jitter buffer por stream.
- Descoberta: UDP broadcast na porta **50779** (sai por todas as placas de rede, então
  funciona mesmo com adaptadores virtuais tipo VirtualBox no meio). Auto-filtro por
  `instanceId` para não se conectar a si mesmo. Porta de voz padrão: **50777**.
- "Compartilhar música": captura o som do sistema via **WASAPI loopback** (sem driver) e
  faz downmix de N canais (lida com saídas 5.1/7.1) para 48 kHz mono.
- **Fase 1** (este repo): voz nos 2 sentidos + compartilhar música entre os dois PCs.
  Mandar a música também para o **pessoal do Discord** é uma fase futura (precisa de um
  cabo de áudio virtual, ex.: VB-CABLE, porque criar microfone virtual exige driver).
