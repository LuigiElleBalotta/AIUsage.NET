# IconGen

A tiny standalone tool that renders the AIUsage brand glyph (a blue gradient circle badge with an
"AI" wordmark) at the standard Windows icon resolutions (16, 20, 24, 32, 40, 48, 64, 128, 256px) and
packs them into a single multi-resolution `.ico` file using PNG-compressed frames.

Not part of `AIUsage.sln` and not shipped — it exists only to (re)generate
`src/AIUsage.Tray/Resources/aiusage.ico`, which both `AIUsage.Tray` and `AIUsage.Cli` reference via
`ApplicationIcon`, and which `TrayIconFactory` loads at runtime for the tray icon itself.

## Usage

```powershell
cd tools/IconGen
dotnet run -c Release -- ..\..\src\AIUsage.Tray\Resources\aiusage.ico
```

Re-run whenever the brand glyph needs to change (e.g. if a real logo replaces this placeholder
design later).
