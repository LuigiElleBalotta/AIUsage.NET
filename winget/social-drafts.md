# Bozze pronte da pubblicare (richiedono il tuo account)

Non ho credenziali per Reddit/HN, quindi pubblica tu — copia/incolla e adatta se vuoi.
Questo file è solo per uso interno, non fa parte del repo pubblico (puoi cancellarlo dopo l'uso).

---

## 1. Hacker News — "Show HN"

**Titolo:**
```
Show HN: AIUsage.NET – a Windows tray app for AI coding usage limits (.NET 8/WPF port of OpenUsage)
```

**URL:** https://github.com/LuigiElleBalotta/AIUsage.NET

**Primo commento (da postare tu stesso subito dopo, è prassi su HN):**
```
Hi HN, I ported OpenUsage (a macOS menu bar app, 3.4k stars) to native .NET 8 / WPF for Windows,
since it didn't have a Windows equivalent. It sits in the system tray and shows session/weekly
limits, credits, and local spend estimates for Claude, Codex, Cursor, Copilot, and a few others,
reading credentials already stored on the machine (no extra login for most providers).

The interesting part for HN, technically: while porting the auth flow I hit a real bug where a
proactive token refresh (5 minutes before expiry) raced with the underlying tool's own background
token rotation, which caused AWS to revoke the whole token family and force-logout the user's live
IDE session. Fixed it by switching to reactive-only refresh (after an actual 401/403, re-reading
the live credential source first) plus an in-memory SHA-256 fingerprint of "known dead" refresh
tokens so a rejected token isn't retried every cycle. Wrote up the full incident timeline in
PORTING_NOTES.md if anyone's curious: https://github.com/LuigiElleBalotta/AIUsage.NET/blob/main/PORTING_NOTES.md

MIT-licensed, no telemetry, local HTTP API + CLI if you want to script against your own usage data.
Feedback welcome, especially on the auth/token handling since that's the part I'd most like more
eyes on.
```

Note: HN valuta molto la sostanza tecnica, non il marketing. Il taglio "ho trovato un bug reale e
l'ho risolto così" performa meglio di un semplice annuncio prodotto.

---

## 2. Reddit — r/dotnet o r/csharp

**ATTENZIONE — regole del subreddit sui post di self-promotion (verificate dall'utente):**
- Va pubblicato **solo nel weekend** (oggi è mercoledì 22/07/2026 → prossima finestra utile:
  sabato 25 / domenica 26/07/2026)
- Va **flaired "Promotion"** al momento della pubblicazione
- **Non deve essere scritto dall'AI** — niente copia/incolla di un testo generato, va scritto di
  persona ("put some effort into it")
- Va **legato a una versione di release** (es. "v0.4.1"), non un annuncio generico del progetto

Per questo motivo qui sotto NON c'è un testo pronto da incollare — sarebbe contro la regola del
subreddit e rischi la rimozione (o una segnalazione). Scaletta di punti da cui partire, da scrivere
con le tue parole:

- Cosa hai fatto: porting di OpenUsage (macOS, 3.4k⭐) a .NET 8 + WPF nativo per Windows
- Perché: nessun equivalente Windows esisteva, avevi bisogno di tracciare più abbonamenti AI insieme
- Cosa mostra: limiti sessione/settimanali, crediti, stima spesa locale, in un popup dalla tray,
  refresh ogni 5 minuti
- Dettaglio tecnico che interessa a r/dotnet: niente Electron/Tauri, WPF puro; ogni provider è una
  pipeline auth-store → usage-client → mapper
- Menziona la versione specifica (v0.4.1) per rispettare la regola sul release tag
- Link al repo e (se vuoi) alla landing page
- Chiudi con una domanda aperta alla community (aumenta l'engagement, e mostra che non è solo un
  annuncio a senso unico)

Nota: verifica anche le regole specifiche di r/csharp separatamente, potrebbero non essere identiche.

---

## 3. Reddit — r/ClaudeAI / r/cursor (SOLO se/quando compare la domanda "esiste per Windows?")

Non postare come thread a sé — rispondi con onestà quando qualcuno chiede esplicitamente un
tray/monitor per Windows. Esempio di risposta:

```
If you're on Windows, I built a .NET port of OpenUsage called AIUsage.NET — tray app, same idea
(session/weekly limits, credits, local spend), reads credentials already on your machine.
https://github.com/LuigiElleBalotta/AIUsage.NET
```

---

## Suggerimento sul timing

Pubblica prima l'HN post (di solito unico, non ripetibile a breve distanza), aspetta l'esito, poi
eventualmente il post su r/dotnet nel weekend successivo (vincolo imposto dal subreddit, vedi sopra).
Evita di postare tutto lo stesso giorno — sembra spam anche se non lo è.

Prima di pubblicare su un qualsiasi subreddit, controlla sempre le regole della community (sidebar
o wiki) — variano molto e cambiano nel tempo; quelle indicate sopra per r/dotnet sono quelle che
hai riportato tu, non verificate da me in tempo reale.
