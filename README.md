# DuoVoz

Intercomunicador de voz **ponto-a-ponto na rede local (LAN)** para um casal: um canal de
voz privado, **totalmente separado do Discord**. Um lado fala, o outro ouve no fone. Tem
um botão **"Compartilhar música"** que manda o áudio do PC junto com a voz.

Roda **nos dois PCs** (na mesma rede de casa) e **se descobre + conecta sozinho** — não
precisa digitar IP, porta nem escolher microfone.

---

## Como rodar (Windows)

Precisa do **.NET 10 SDK**. Se não tiver, instale:

```powershell
winget install Microsoft.DotNet.SDK.10
```

Depois, na raiz deste repositório:

```powershell
dotnet run -c Release
```

(Isso baixa o NAudio, compila e abre o app. O executável também fica em
`bin/Release/net10.0-windows/DuoVoz.exe`.)

Faça isso **nos dois PCs**. Deixe **"Conectar automaticamente"** marcado — cada um acha o
outro na rede e conecta sem você mexer em nada.

### Importante
- **Use fone de ouvido nos dois lados.** Não há cancelamento de eco; em caixa de som dá
  microfonia/eco.
- Na primeira conexão o Windows pode pedir para **liberar no Firewall** → **Permitir**
  (rede **Privada**).
- Os dois PCs precisam estar na **mesma rede local** (mesmo Wi-Fi/roteador).

---

## Para o assistente (Claude) na máquina do parceiro

Se você é um assistente configurando isto:

1. Verifique o .NET 10 SDK (`dotnet --version`). Se faltar:
   `winget install Microsoft.DotNet.SDK.10`.
2. Rode `dotnet run -c Release` na raiz do repo (restaura NAudio e compila).
3. Na janela, deixe **"Conectar automaticamente (descobrir na rede)"** marcado. O app
   anuncia-se por UDP broadcast (porta 50779) e conecta no par assim que o encontra.
4. Se aparecer o prompt do **Firewall do Windows**, permita o `DuoVoz.exe` na rede
   **Privada** (a descoberta usa broadcast UDP; bloqueado, não acha o par).
5. Ambos os lados precisam: mesma LAN + fone de ouvido. Porta de voz padrão: 50777.
6. Para diagnóstico, há um log em `%TEMP%\DuoVoz\duovoz.log` (eventos de descoberta,
   conexão, etc.).

---

## Notas técnicas

- C# / .NET 10 / WinForms / **NAudio 2.3.0**.
- Voz: UDP, PCM 48 kHz mono 16-bit, frames de 10 ms; jitter buffer por stream.
- Descoberta: UDP broadcast na porta **50779** (sai por todas as placas de rede, então
  funciona mesmo com adaptadores virtuais tipo VirtualBox no meio). Auto-filtro por
  `instanceId` para não se conectar a si mesmo.
- "Compartilhar música": captura o som do sistema via **WASAPI loopback** (sem driver) e
  faz downmix de N canais (lida com saídas 5.1/7.1) para 48 kHz mono.
- **Fase 1** (este repo): voz nos 2 sentidos + compartilhar música entre os dois PCs.
  Mandar a música também para o **pessoal do Discord** é uma fase futura (precisa de um
  cabo de áudio virtual, ex.: VB-CABLE, porque criar microfone virtual exige driver).
