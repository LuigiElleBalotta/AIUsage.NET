# Porting Notes — AIUsage.NET (porting Windows di OpenUsage)

Questo file traccia **ogni scelta di design, semplificazione, omissione o divergenza** rispetto
all'edizione Swift/macOS originale (`openusage/`, progetto "OpenUsage" di Robin Ebers). Scopo:
permettere di riprendere il lavoro in futuro sapendo esattamente cosa è stato adattato e perché, e
cosa resta da fare per arrivare alla parità funzionale completa.

## Rebranding: OpenUsage → AIUsage.NET

Il progetto è stato rinominato da "OpenUsage" ad **AIUsage.NET** (repo:
https://github.com/LuigiElleBalotta/AIUsage.NET) per non presentarsi come un fork/copia sotto lo
stesso nome. Di conseguenza:

- Cartella radice: `openusageWindows/` → `AIUsage.NET/`
- Solution: `OpenUsage.sln` → `AIUsage.sln`
- Progetti: `OpenUsage.Core` → `AIUsage.Core`, `OpenUsage.Tray` → `AIUsage.Tray`,
  `OpenUsage.Cli` → `AIUsage.Cli` (namespace, `AssemblyName`, cartelle, file `.csproj` tutti
  rinominati in coerenza)
- Eseguibili risultanti: `AIUsage.exe` (tray), `aiusage.exe` (CLI, minuscolo come l'originale
  `openusage`)
- File/tipi rinominati: `OpenUsageISO8601` → `AIUsageISO8601`
- Path di configurazione utente: `~/.openusage/config.json` → `~/.aiusage/config.json`;
  cache pricing locale: `%LOCALAPPDATA%\OpenUsage\pricing` → `%LOCALAPPDATA%\AIUsage\pricing`
- Windows Credential Manager target prefix: `OpenUsage:` → `AIUsage:`
- **Logo/icona**: non ancora cambiati (nessuna icona presente finora). Il branding grafico
  (icona tray, `AppIcon`) andrà creato da zero più avanti — richiesto esplicitamente dall'utente
  di non riusare quello originale.

`[DIVERGENTE — deliberato]` **Il fetch live del pricing supplement resta puntato a
`https://robinebers.github.io/openusage/pricing_supplement.json`** (in `ModelPricingStore.cs`).
Scelta esplicita dell'utente: i dati di pricing (aliases, fast-multiplier, modelli Cursor-nativi)
non sono un problema di "copiare il brand", sono dati tecnici pubblici aggiornati da un progetto
terzo e va bene continuare a consumarli così. Da rivalutare solo se in futuro si vuole rendere
AIUsage.NET completamente autonomo anche per i dati.

Il file bundlato `Resources/pricing_supplement.json` resta comunque una copia locale (fallback
offline) dei dati di quello stesso progetto — stesso discorso, nessuna azione richiesta.

Convenzione: ogni voce ha un tag di stato:
- `[SEMPLIFICATO]` — logica portata ma ridotta rispetto all'originale (di solito perché l'infra
  macOS-specifica non ha un equivalente diretto o non è essenziale per la correttezza).
- `[OMESSO]` — funzionalità non ancora implementata, da fare.
- `[DIVERGENTE]` — scelta implementativa diversa per necessità della piattaforma Windows.
- `[FEDELE]` — logica di business portata 1:1 (stessa formula/algoritmo), solo sintassi C#.

---

## Stack e struttura progetto

- **Linguaggio/runtime**: .NET 8, scelto perché già installato e perché C#/WPF è l'equivalente più
  diretto di Swift/AppKit per un'app "tray" su Windows.
- **UI**: WPF (non ancora implementata al momento di questa nota — solo lo scaffold `OpenUsage.Tray`
  esiste). `NSStatusItem` → `System.Windows.Forms.NotifyIcon` (o libreria tray WPF); `NSPanel`
  popover → finestra WPF senza chrome, posizionata vicino alla tray icon.
- Struttura a 3 progetti che rispecchia l'originale:
  - `OpenUsage.Core` ≈ `Sources/OpenUsage` (libreria condivisa: modelli, provider, pricing, servizi)
  - `OpenUsage.Tray` ≈ `Sources/OpenUsageApp` (eseguibile GUI, WPF)
  - `OpenUsage.Cli` ≈ `Sources/OpenUsageCLI` (eseguibile CLI `openusage`)
- `[OMESSO]` Nessun progetto di test ancora creato (l'originale ha `Tests/OpenUsageTests` e
  `Tests/OpenUsageCLITests` con ~140 file). Da fare: `OpenUsage.Core.Tests` con xUnit.

## Credenziali: mapping macOS → Windows

| Originale (macOS) | Windows | Stato |
|---|---|---|
| Keychain (`security` CLI) | Windows Credential Manager (`CredRead`/`CredWrite` via P/Invoke, `advapi32.dll`) | `[DIVERGENTE]` — vedi `Services/SystemClients.cs` → `WindowsCredentialAccessor`. Target name prefissato `OpenUsage:` per non collidere con credenziali di altre app. |
| File config (`~/.claude/.credentials.json`, `~/.codex/auth.json`, ecc.) | Stessi file JSON, path espanso da `~` a `%USERPROFILE%` | `[FEDELE]` per i tool CLI cross-platform (Claude Code, Codex CLI, ecc. scrivono lo stesso file su ogni OS) |
| SQLite (Cursor `state.vscdb`) | Stesso file, letto con `Microsoft.Data.Sqlite` invece del CLI `sqlite3` | `[DIVERGENTE]` — vedi `SqliteDataAccessor`. Nessun processo esterno necessario. |
| File con permessi 0600 (scrittura atomica) | Scrittura atomica (temp file + rename) + ACL NTFS ristretta all'utente corrente via `FileSystemAccessRule` | `[DIVERGENTE]` — vedi `LocalTextFileAccessor.WriteText` |
| Login-shell environment capture (`LoginShellEnvironment`, per app lanciate da Finder/Dock) | Non necessario: un'app Windows lanciata da Explorer/Start Menu eredita già le env var utente/macchina persistite (`HKCU\Environment`) | `[OMESSO — non serve]` Nessuna cattura di shell necessaria; `ProcessEnvironmentReader` legge Process→User→Machine env vars direttamente. |

`[OMESSO]` **Claude Desktop fallback**: l'originale ha `ClaudeDesktopAuthStore` per leggere le
credenziali dell'app desktop Claude quando il CLI non è loggato. Non portato — non esiste un
"Claude Desktop" equivalente rilevante da investigare su Windows separato dal CLI. Da valutare in
futuro se Anthropic rilascia un client desktop Windows con storage credenziali proprio.

`[OMESSO]` **Multi-account Claude cards** (`ProviderAccountAssembly`, `ClaudeConfigDirDiscovery`,
account extra via `CLAUDE_CONFIG_DIR` multipli, rename via `ProviderAccountsStore`). Il
`ClaudeProvider` portato supporta solo l'account singolo di default. L'infrastruttura è complessa
(scoped credential store, hashing SHA256 del path per il nome del keychain item, ecc.) — da
riprendere se richiesto esplicitamente.

## Pricing engine (`Pricing/`)

`[FEDELE]` Portato quasi 1:1: `ModelRates`, `PricingCatalog` (fuzzy matching byte-a-byte identico),
`PricingSupplement`, `PricingCatalogCodecs` (formato compatto, parser LiteLLM/models.dev),
`ModelPricingStore`. I 3 JSON bundle (`pricing_supplement.json`, `pricing_litellm_snapshot.json`,
`pricing_models_dev_snapshot.json`) copiati identici in `OpenUsage.Core/Resources/`.

`[DIVERGENTE]` `ModelPricingStore` in Swift è un `actor`; in C# è una classe con `lock` interni —
stessa semantica (accesso serializzato), sintassi diversa.

`[SEMPLIFICATO]` Caricamento risorse bundle: Swift usa `Bundle.module`; C# usa
`Path.Combine(AppContext.BaseDirectory, "Resources", ...)`. Va verificato che il `.csproj`
copi effettivamente `Resources/**` nell'output (impostato con `CopyToOutputDirectory=PreserveNewest`
in `OpenUsage.Core.csproj`, da testare a build reale).

## Logging (`Support/AppLog.cs`, `LogRedaction.cs`)

`[FEDELE]` `LogRedaction` è un port pressoché letterale delle regex Rust/Swift (JWT, API key,
devin-session-token, JSON keys sensibili, `account=`).

`[DIVERGENTE]` Path redaction: aggiunto pattern per path stile Windows (`C:\...`, `\\unc\...`)
oltre ai path Unix originali (`/Users/...` ecc., mantenuti per compatibilità con dati importati).

`[SEMPLIFICATO]` `AppLog`: niente `os.Logger`/Console.app equivalente strutturato — solo file di
log + `Debug.WriteLine`. Da valutare: integrazione con `Microsoft.Extensions.Logging` o ETW se
serve diagnostica avanzata in futuro.

`[OMESSO]` Percorso del file di log: nell'originale è `~/Library/Logs/OpenUsage/OpenUsage.log`.
Non ancora deciso l'equivalente Windows (candidato: `%LOCALAPPDATA%\OpenUsage\Logs\OpenUsage.log`).
Il progetto Tray dovrà chiamare `AppLog.Bootstrap(path)` all'avvio — non ancora fatto.

## HTTP client (`Services/HttpClientService.cs`)

`[DIVERGENTE]` `URLSessionHTTPClient` (con delegate TLS loopback custom per il local API HTTPS)
→ `SystemHttpClient` basato su `HttpClient`/`HttpClientHandler`. Il local API server useremo HTTP
semplice su loopback (niente TLS self-signed) quando implementato — da decidere se serve davvero
HTTPS anche su Windows per il local API (`docs/local-http-api.md` dice loopback-only, quindi il
rischio è minimo anche in chiaro).

`[SEMPLIFICATO]` `ProxyConfig`: il proxy SOCKS5 non è supportato nativamente da
`HttpClientHandler`/`WebProxy` su .NET (limite della piattaforma, non una scelta di design). HTTP
e HTTPS CONNECT funzionano pienamente; SOCKS5 configurato viene tentato ma degradato. Da rivalutare
con una libreria terza (es. `SocksSharp` o proxy runtime a parte) se un utente lo richiede.

## Modelli (`Models/`)

`[FEDELE]` `MetricLine`, `MetricValue`, `WidgetData`, `WidgetDescriptor`, `ProviderSnapshot`,
`DailyUsageSeries`, ecc. — stessa struttura dati, in C# come `record`/classi invece di `enum`
associati Swift. La union-type `MetricLine` è modellata con una classe abstract + record annidati
(pattern matching via `switch` su tipo).

`[DIVERGENTE — bug fix nome]` In `MetricLine.cs`, il record `Badge` aveva originariamente un
parametro `Text` che collideva col tipo annidato `MetricLine.Text` (errore di compilazione C#
CS8866). Rinominato in `BadgeText`. Puramente un dettaglio di implementazione C#, nessun impatto
sul comportamento.

`[DIVERGENTE — bug fix nome]` In `WidgetData.MeterState.Level`, il parametro posizionale
`Severity` collideva con la proprietà calcolata `Severity` della classe base. Rinominato in
`LevelSeverity`.

`[OMESSO]` Alcune proprietà di tooltip molto SwiftUI-specifiche in `WidgetData` (es.
`unboundedValueTooltip`, `unboundedTooltip` con soglie di abbreviazione, `expiryTooltip` col
formato "Resets expire in: ...") sono state semplificate o non ancora portate integralmente:
la logica di business core (fraction, meterState, headline, valueText) è fedele; i tooltip più
elaborati verranno rifiniti quando si costruirà la UI WPF e si vede cosa serve davvero mostrare.

## Providers — stato di avanzamento

| Provider | Stato | Note |
|---|---|---|
| Claude | **Fatto** (auth store, usage client, mapper, log scanner, provider) | `[SEMPLIFICATO]` niente Desktop fallback, niente multi-account, niente Cowork sandbox scan (macOS-only, cartelle sessione dell'app desktop Claude) |
| Codex | **Fatto** (auth store, usage client, mapper, log scanner, provider) | `[FEDELE]` logica di pricing/parsing rollout (child-session replay gate, service tier, long-context rates, auto-review fallback) portata 1:1. Keychain di sistema come fallback secondario (i file `auth.json` sotto `%USERPROFILE%\.codex` restano la fonte primaria, identica su ogni OS) |
| Cursor | **Fatto** (auth store, CSV parser, usage client, mapper, summary mapper, provider) | `[DIVERGENTE]` `state.vscdb` letto con `Microsoft.Data.Sqlite` invece del CLI `sqlite3` — stesso file (equivalente Windows: `%APPDATA%\Cursor\User\globalStorage\state.vscdb`), stessa struttura, nessun processo esterno necessario |
| Copilot | **Fatto** (auth store, usage client, mapper, org billing client/mapper, provider) | `[DIVERGENTE]` cache dell'org di billing: l'originale usa `UserDefaults`, qui un piccolo `FileSettingsStore` JSON in `%LOCALAPPDATA%\AIUsage\settings.json` (vedi `Services/SettingsStore.cs`) — stessa semantica chiave/valore |
| Grok | **Fatto** (auth store, usage client, credits config decoder, log scanner, mapper, provider) | `[FEDELE]` |
| Devin | **Fatto** (auth store, usage client, mapper, provider) | `[DIVERGENTE]` `state.vscdb` via `Microsoft.Data.Sqlite`; credentials.toml sotto `%LOCALAPPDATA%\devin\` (equivalente Windows di `~/.local/share/devin/`) |
| OpenCode | **Fatto** (paths, auth store, Go window math, mapper, scanner, provider) | `[FEDELE]` la math delle finestre (session/weekly/monthly anchor) era già scritta cross-platform nell'originale (`OpenCodeGoWindows.swift`), portata 1:1. `[SEMPLIFICATO]` niente edge-triggered read-failure reporter persistente — solo logging per-refresh (le query SQLite sono già cheap, a differenza dei parse JSONL) |
| OpenRouter | **Fatto** (auth store, usage client, mapper, provider) | `[FEDELE]` solo API key da env var o file di config, nessuna credenziale di sistema |
| Z.ai | **Fatto** (auth store, usage client, mapper, provider) | `[FEDELE]` come OpenRouter |
| Antigravity | **Fatto** (auth store, metric, usage client, mapper, provider, language server discovery) | `[DIVERGENTE]` `LanguageServerDiscovery`: l'originale usa `ps` + `lsof` (macOS); qui WMI (`Win32_Process.CommandLine` via `System.Management`) per l'elenco processi + `netstat -ano` per le porte in ascolto — vedi `Services/LanguageServerDiscovery.cs`. Keychain → Credential Manager (stessa struttura go-keyring-base64, letta identicamente) |
| Pi | `[OMESSO]` | provider "aggregatore" cross-provider (attribuisce uso avvenuto dentro l'app "pi" ad altri provider); rimandato, bassa priorità |

**Tutti i 10 provider principali sono ora portati e la solution compila senza errori** (`dotnet build`
su `AIUsage.Core`, `AIUsage.Tray`, `AIUsage.Cli`). Nessun test automatico ancora eseguito (nessun
progetto di test creato) — la verifica finora è solo "compila", non "si comporta correttamente a
runtime". Da fare prima di considerare i provider production-ready: test unitari sui mapper (che
sono pure function, facilmente testabili) e un test manuale end-to-end con credenziali reali di
almeno un paio di provider (Claude/Codex via CLI, Cursor via editor).

## Infrastruttura di scansione log (JSONL)

`[SEMPLIFICATO]` L'originale (`IncrementalJSONLScanner`) è un `actor` con cache **persistita su
disco** (property list binari in Application Support, con manifest/versioning, scrittura
debounced, pruning delle identità stale, ecc. — vedi `JSONLScanCacheStore.swift`,
`JSONLScanCacheCoordination.swift`). Il port C# (`JsonlScanning.cs`, `IncrementalJsonlScanner<T>`)
mantiene **solo la cache in-memory** (path+size+mtime), niente persistenza su disco.

Impatto: ad ogni riavvio dell'app, il primo refresh ri-scansiona tutti i file di log invece di
riutilizzare una cache da un run precedente. Per provider con storici di 30 giorni questo è un
costo di CPU/IO occasionale, non un problema di correttezza. Se in futuro serve ottimizzare i tempi
di avvio, si può aggiungere persistenza (es. con `System.Text.Json` su file, invece di plist).

## Stores (`Stores/`) — **fatto**

`[SEMPLIFICATO]` `WidgetRegistry`, `ProviderEnablementStore`, `DefaultLayout`, `LayoutStore`,
`RefreshSetting`, `ProviderSnapshotCache`, `WidgetDataStore` sono tutti portati e la solution
compila. Note sulle semplificazioni:

- `LayoutStore`: omette lo undo stack, la navigazione a schermate nel popover (Dashboard/Settings)
  e le transient notice pills della UI SwiftUI — non hanno ancora un equivalente WPF da collegare.
  Il modello dati (placed widgets, ordine provider/metrica, pin, metriche espanse) è invece portato
  1:1.
- `WidgetDataStore`: omette l'aggregazione peer-history iCloud, le notifiche di "quota pace" e gli
  hook di telemetria (nessuno di questi è ancora stato portato lato Windows). Il nucleo — cache con
  TTL, backoff sui provider falliti, refresh concorrente, risoluzione `MetricLine` → `WidgetData` —
  è fedele.
- `ProviderSnapshotCache`: omette lo stamping dell'identità account (`producedByIdentityKey`),
  parte della feature multi-account Claude non ancora portata.
- `ProviderEnablementStore`: aggiunta la proprietà `EnabledIds` (getter pubblico dell'insieme
  correntemente abilitato, o `null` se lo store non è mai stato "seedato") — serve a
  `FirstRunSeeder` per distinguere un install fresh da uno già personalizzato dall'utente
  (assente nello snippet originale mostrato in `AppContainer.swift`, ma presente nell'edizione
  Swift completa come `enabledIDs`).

`[DIVERGENTE — bug fix]` `CodexUsageMapper.ClassifiedWindowLine`: il codice C# iniziale confrontava
un `WindowCandidate?` (record, quindi reference type) con `default(WindowCandidate)` per capire se
`FirstOrDefault` avesse trovato qualcosa — ma `FirstOrDefault` su un reference type ritorna `null`,
non un'istanza "vuota", quindi `exact.Equals(...)` lanciava `NullReferenceException` ogni volta che
nessuna finestra `session`/`weekly` esatta veniva trovata (praticamente sempre, dato che Codex non
etichetta le finestre col nome — le classifica per durata). Risultato: **Codex falliva sempre con
"Object reference not set to an instance of an object."** anche con credenziali valide. Corretto
usando il pattern idiomatico C# (`exact ?? candidates.FirstOrDefault(...)` con controllo `is null`).
Trovato testando la CLI end-to-end con credenziali reali — nessun test unitario esisteva per questo
percorso. Verificato che dopo il fix `aiusage codex --force` produce dati corretti (sessione, spark,
credits) con le credenziali Codex reali di questa macchina.

## App layer (`App/`) — **fatto** (versione minima)

`[SEMPLIFICATO]` `AppContainer` (`AIUsage.Core/App/AppContainer.cs`): composition root diretto,
porta la costruzione del catalogo provider, `WidgetRegistry`, `ProviderEnablementStore`,
`LayoutStore`, `WidgetDataStore`, il first-run seeding e il loop di refresh periodico (5 minuti,
`Task.Delay` cancellabile). **Omesso** (nessun equivalente Windows ancora, vedi sotto): multi-account
Claude, `ICloudUsageSyncStore`, `NotificationSettingsStore`/quota-pace notifications, `TelemetryRecorder`,
`CodexResetClaimService` (il claim dei reset-credit Codex dal popover), `LocalUsageServer` (l'API
HTTP locale su :6736), la cattura login-shell environment (non necessaria su Windows, vedi tabella
credenziali più sopra).

`[FEDELE]` `FirstRunSeeder` (`AIUsage.Core/App/FirstRunSeeder.cs`): stessa logica in due fasi
(seed sincrono col fallback Claude/Codex/Cursor, poi probe asincrono delle credenziali locali per
sostituire il fallback col set effettivamente rilevato) — porta 1:1 la sequenza Swift, incluso il
guard che rispetta un toggle utente cambiato durante il probe.

`[OMESSO]` `NewProviderSeeder` (riconciliazione automatica quando un aggiornamento introduce un
nuovo provider mai visto da questa installazione) non ancora portato — bassa priorità, serve solo
dopo il primo rilascio con un provider aggiunto in un secondo momento.

## CLI (`AIUsage.Cli`) — **fatto**

`[FEDELE]` `CliArguments.Parse`: stesso parsing (un provider posizionale opzionale, `--force`,
`-v/--version`, `-h/--help`; qualsiasi altra opzione o un secondo provider è un errore d'uso).
Stessi codici di uscita: `0` successo, `2` errore d'uso/provider sconosciuto, `4` refresh con
warning o errore generico.

`[DIVERGENTE]` `UsageReader` (`AIUsage.Core/Services/UsageReader.cs`): condivide lo stesso
`ProviderSnapshotCache` su disco della tray app (stesso file `%LOCALAPPDATA%\AIUsage\settings.json`,
quindi CLI e tray non divergono mai su "è ancora fresco?"), ma **non** instrada l'output attraverso
`LocalUsageAPI` (non ancora portata) — restituisce invece un dump JSON diretto delle
`ProviderSnapshot` (displayName/plan/lines/refreshedAt/warning/error), leggibile ma non lo stesso
identico schema della risposta HTTP locale Swift. Da rivedere quando/se si porta `LocalUsageAPI`,
per tenere un solo schema di risposta condiviso da CLI e API locale (com'è nell'originale).

Verificato manualmente con credenziali reali su questa macchina: `aiusage claude --force` e
`aiusage codex --force` restituiscono dati corretti (limiti di sessione/settimanali, cronologia
spesa, credits Codex). I provider senza credenziali configurate falliscono con messaggi di errore
leggibili (non crash), esattamente come nell'originale.

## UI WPF (`AIUsage.Tray`) — rifinita in una sessione successiva, ancora non allo stesso livello del DashboardView SwiftUI

`[DIVERGENTE]` Non esiste un'API WPF nativa per un'icona di tray (l'equivalente di `NSStatusItem`).
Usato `System.Windows.Forms.NotifyIcon` (richiede `<UseWindowsForms>true</UseWindowsForms>` oltre a
`UseWPF`) — è l'approccio standard per le tray-icon in app WPF. Effetto collaterale: `Application`,
`Brush`, `Brushes`, `Orientation`, `HorizontalAlignment`, `CheckBox` sono ambigui tra
`System.Windows.*` e `System.Windows.Forms`/`System.Drawing` quando entrambi i toolkit sono
referenziati nello stesso progetto; risolto centralizzando gli alias in
`AIUsage.Tray/GlobalUsings.cs` (`global using Application = System.Windows.Application;` ecc., WPF
vince ovunque) invece di ripetere alias locali in ogni file — `TrayIconFactory`/`TrayController`
tornano a usare i tipi WinForms espliciti dove servono davvero (`NotifyIcon`, `ContextMenuStrip`,
`MouseButtons`, `Icon`).

`[SEMPLIFICATO — ma molto più vicino all'originale ora]` Riscritta la UI con un tema scuro custom
(`AIUsage.Tray/Theme/Styles.xaml`): palette coerente con l'originale (sfondo scuro, card più chiare,
accent blu, severità normale/warning/critical), bottoni flat con hover/pressed state,
scrollbar sottile in stile macOS/VS Code (niente più freccette/thumb di sistema), toggle switch
in stile iOS/macOS per i settings. Finestra principale (`MetricsWindow`) ridisegnata come bordo
arrotondato (`CornerRadius=14`) con ombra (`DropShadowEffect`), trasparente/`AllowsTransparency`
invece del semplice `WindowStyle=None` con sfondo pieno di prima, con una vera title bar
draggabile (`DragMove()` su `MouseLeftButtonDown`) e bottoni icona per refresh/settings/close invece
del solo bottone testuale "Close".

`[FEDELE — nuovo]` Le card per-provider ora hanno: badge dell'icona brand (vedi sotto), nome del
provider, pillola col nome del piano (se noto) — la stessa disposizione header vista nello
screenshot dell'originale (icona + titolo + badge piano a destra). Ogni riga con un limite (`IsBounded`)
ha una vera barra di progresso (non solo testo) colorata per severità (blu/giallo/rosso, leggendo
`WidgetData.GetMeterState().Severity` — la stessa logica di pace/severità già portata fedelmente),
larga in proporzione a `Fraction`/`RemainingFraction` secondo il `DisplayMode` corrente.

`[DIVERGENTE — icone]` Non esiste ancora un renderer SVG runtime nel progetto (una dipendenza come
SharpVectors è stata valutata e scartata per ora, per non introdurre una dipendenza NuGet aggiuntiva
per un set fisso e conosciuto di 10 icone). `Theme/ProviderIconCatalog.cs` porta invece la geometria
`<path d="...">` di ciascun SVG in `Resources/ProviderIcons/*.svg` direttamente come stringhe di
`Geometry.Parse` per un `System.Windows.Shapes.Path` WPF (il mini-linguaggio dei path SVG è un
sottoinsieme di quello WPF, quindi le stringhe si copiano invariate), dentro un badge colorato con il
colore brand di ciascun provider. Se in futuro serve caricare SVG arbitrari (es. icone caricate
dall'utente), riconsiderare una libreria come SharpVectors.Wpf.

`[NUOVO]` `SettingsWindow` — prima finestra Settings funzionante del port (l'originale non aveva
nemmeno un placeholder utile: era un semplice `MessageBox`). Mostra l'elenco dei 10 provider con
badge icona, nome, e un toggle collegato direttamente a
`AppContainer.Enablement.SetEnabled/IsEnabled` (`ProviderEnablementStore`, già portato fedelmente).
`[OMESSO]` Nessun controllo a livello di singola metrica, nessun drag-reorder, nessun pin — la
Customize screen completa dell'originale (vedi `docs/adding-a-provider.md` e `docs/dashboard.md`)
resta lavoro futuro; questa è solo la fetta "providers on/off" collegata allo store reale.

`[BUG FIX — race condition]` `WidgetDataStore.RefreshAllAsync` lancia un task `RefreshAsync` per
ogni provider in parallelo (`Task.WhenAll`), ma `Snapshots`, `ProviderErrors`, e
`_failureRetryAfter` erano `Dictionary<>` semplici scritti senza sincronizzazione da task
concorrenti — con più di un provider che falliva nello stesso batch (comune: quasi tutti falliscono
per mancanza di credenziali su una macchina pulita), .NET lanciava
`InvalidOperationException: A concurrent update was performed on this collection and corrupted its
state`, mandando in crash silenzioso l'iterazione del refresh loop (l'eccezione veniva loggata da
`AppContainer` ma il batch successivo comunque tentava di ripartire, quindi il sintomo era "la UI
non si aggiorna mai più / il log si riempie dell'errore ogni 5 minuti"). Corretto avvolgendo ogni
mutazione di quei tre dizionari in un `lock (_mutationLock)`. Trovato durante il test manuale della
nuova UI (mai emerso prima perché la UI precedente non veniva rinfrescata abbastanza spesso da
esporre la race in una singola sessione di test breve).

`[OMESSO]` Nessuna icona `.ico` reale — `TrayIconFactory` disegna un placeholder circolare "AI" a
runtime finché non viene disegnato un logo vero (deliberatamente rimandato, vedi sezione
Rebranding). Se in futuro compare `Resources\aiusage.ico` nel progetto Tray, viene usato
automaticamente al suo posto.

`[OMESSO — resta]` Nessun grafico per `MetricLine.Chart` (Usage Trend), nessun breakdown modelli al
hover, nessun drag-reorder, nessuna vera animazione di apertura/altezza, nessuna vibrancy/
transparency opzionale. La UI è più vicina all'originale di quanto non fosse (vera barra di
progresso, vero tema scuro, vere icone brand, vera finestra Settings) ma è ancora una versione
sostanzialmente più semplice del `DashboardView` SwiftUI completo.

Verificato manualmente: l'app si avvia, l'icona compare nella tray, il click sinistro apre/chiude la
finestra metriche con il nuovo tema, "Refresh Now" e "Quit" funzionano, la finestra Settings si apre
e i toggle provider persistono nello store reale, e il log
(`%LOCALAPPDATA%\AIUsage\Logs\AIUsage.log`) mostra più batch di refresh consecutivi completarsi
senza più l'eccezione di concorrenza, per tutti e 10 i provider (2 riusciti con credenziali reali —
Claude e Codex — 8 falliti con messaggi di errore leggibili per mancanza di credenziali —
comportamento atteso su questa macchina di test).

## Servizi locali non ancora iniziati

`[OMESSO]` I servizi `LocalUsageServer`/`LocalLimitsAPI`/`LocalUsageAPI` (il local HTTP API su
:6736, che CLI e altre app locali potrebbero consumare) non sono ancora stati progettati per
Windows. La CLI attuale bypassa il problema leggendo/scrivendo direttamente la cache condivisa
(vedi sopra) invece di passare per un server HTTP locale.

`[OMESSO]` **iCloud Sync**: nessun equivalente Windows deciso ancora (OneDrive? File locale +
nessun sync multi-macchina per ora?). Da discutere.

`[OMESSO]` **Sparkle auto-update**: da sostituire con un meccanismo Windows-nativo (es. Squirrel.Windows,
oppure un semplice check-and-download-installer contro le GitHub Releases). Non iniziato.

`[OMESSO]` **KeyboardShortcuts** (libreria Swift per lo shortcut globale) → da sostituire con
`RegisterHotKey`/`UnregisterHotKey` (Win32) o una libreria .NET equivalente (es. `NHotkey`).

`[OMESSO]` **PostHog telemetry**: da valutare se portare (SDK PostHog ha un client .NET) o
disabilitare di default nella build Windows iniziale.

## Script (richiesta esplicita: .bat/.ps1 al posto di .sh) — **fatto**

`[DIVERGENTE]` `script/build_and_run.ps1`: niente app bundle da comporre/firmare come su macOS —
è un semplice `dotnet build` + `Start-Process` sull'exe della tray (o `dotnet run` per la CLI con
`-Mode cli`). Molto più semplice dell'originale perché .NET non ha l'equivalente della cerimonia
"bundle .app + entitlements + iCloud provisioning profile + Sparkle embed + codesign".

`[DIVERGENTE]` `script/release.ps1`: `dotnet publish` self-contained/single-file per `win-x64`,
zippato in `dist/`. Nessuna firma codice (serve un certificato Authenticode che non abbiamo ancora),
nessuna notarizzazione (concetto Apple-only), nessun DMG (concetto macOS-only). Da estendere con
`signtool` quando si arriva al packaging per la distribuzione pubblica.

`[FEDELE]` `script/update_pricing_snapshots.ps1`: stessa codifica compatta (per-milione, cache write
di default = input rate, cache read di default = un decimo, flag `cre:false` per i valori
sintetizzati) dello script Python embedded nell'originale, riscritta in PowerShell puro (nessuna
dipendenza da Python introdotta in un repo Windows-only). Non ancora eseguito/verificato contro le
API live — da testare la prima volta che serve un refresh dei pricing snapshot.

Gli script macOS-only dell'originale (`compile_icon.sh`, `embed_sparkle.sh`,
`find_icloud_provisioning_profile.sh`, `render_icloud_entitlements.sh`, `apply_github_protections.sh`)
non hanno controparte: sono legati a concetti Apple-specifici (Icon Composer, Sparkle framework
embedding, iCloud provisioning profiles) che non esistono nel port Windows.

`[NUOVO]` `Directory.Build.props` alla radice della solution centralizza il `<Version>` per tutti i
progetti (letto da `AIUsage.Tray/AppVersion.cs` e `AIUsage.Cli/CliVersion.cs` per mostrare la
versione nella title bar della finestra, nella finestra Settings, e in `aiusage --version`).
`script/release.ps1` passa `-p:Version=$Version` a `dotnet publish` così l'eseguibile pubblicato
riporta sempre la stessa versione del tag git usato per la release, anche se `Directory.Build.props`
non fosse stato aggiornato prima del tag.

`[BUG FIX — collisione case-insensitive NTFS]` La prima versione di `release.ps1` copiava
`AIUsage.exe` (tray) e `aiusage.exe` (CLI) nella stessa cartella per produrre uno zip "flat". NTFS su
Windows è case-insensitive per default, quindi `AIUsage.exe` e `aiusage.exe` sono lo **stesso path**
per il filesystem: il secondo `Copy-Item` sovrascriveva silenziosamente il primo, e lo zip risultante
conteneva solo la CLI (o solo la tray app, a seconda dell'ordine), mai entrambi. Nessun errore
visibile — bisognava aprire lo zip per notare che ne mancava uno. Corretto tenendo la CLI nella sua
sottocartella `cli/` invece di appiattire tutto in una singola directory.

## Documentazione (`docs/`)

`[OMESSO]` Non ancora copiata/adattata. Piano: creare `openusageWindows/docs/` con gli stessi file,
aggiornando i riferimenti a path macOS (`~/Library/...`, Keychain, `.sh`) con gli equivalenti
Windows via una sub-agent dedicata, una volta che il comportamento reale è implementato (i docs
descrivono comportamento, non vogliamo scrivere doc che poi il codice non rispetta).

## Stato attuale: porting end-to-end funzionante

Con questa sessione il port è **funzionalmente completo e end-to-end**: tutti i 10 provider,
lo stores layer, il composition root (`AppContainer`), la CLI (`aiusage`) e una tray app WPF minima
ma funzionante sono scritti, compilano senza errori, e sono stati verificati manualmente con
credenziali reali su questa macchina (Claude e Codex restituiscono dati corretti; gli altri
provider — senza credenziali configurate qui — falliscono con messaggi di errore leggibili, non
crash).

## Prossimi passi (rifinitura, non più "completare il porting")

1. Script `.ps1`/`.bat` per build/run (mirror di `script/*.sh`) — non ancora scritti.
2. Test unitari sui mapper di ogni provider (pure functions, facilmente testabili senza I/O) —
   nessuno scritto finora. Il bug fix di `CodexUsageMapper.ClassifiedWindowLine` (vedi sopra) è
   stato trovato solo grazie al test manuale con credenziali reali — un buon promemoria che i
   mapper meritano copertura di test prima di dichiararli production-ready.
3. UI WPF di rifinitura: card per-widget con barre di progresso, grafici per `MetricLine.Chart`,
   drag-reorder, pin, schermata Settings, panel non-activating in stile macOS. Attualmente
   `MetricsWindow` è un semplice elenco di righe di testo.
4. `LocalUsageServer`/`LocalUsageAPI` (API HTTP locale su :6736) — non ancora portata; la CLI la
   bypassa leggendo/scrivendo direttamente la cache condivisa.
5. Icona vera per la tray (attualmente un placeholder generato a runtime) e per l'eseguibile —
   deliberatamente rimandato dall'utente.
6. Multi-account Claude, iCloud sync, quota-pace notifications, telemetria, Sparkle/aggiornamenti,
   scorciatoia da tastiera globale — tutte le feature elencate come `[OMESSO]` sopra, da valutare
   una per una in base a cosa serve davvero all'utente finale.

---
*Aggiornare questo file ad ogni sessione di lavoro significativa, aggiungendo nuove voci o
spostando elementi da "Omesso/Da fare" a "Fatto" con relative note.*
