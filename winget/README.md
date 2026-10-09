# winget manifests (templates)

These three files are templates, not a published version. `script/winget-submit.ps1` fills the
`{{VERSION}}`, `{{TAG}}`, `{{RELEASE_DATE}}` and `{{SHA256}}` placeholders and opens the PR on
microsoft/winget-pkgs; the `winget` job of `.github/workflows/release.yml` runs it for every stable
release. Edit the metadata (description, tags, switches, architecture) here, never in the PR.

The installer values (`x64`, `-s`, `DisplayName`/`DisplayVersion`) are the ones that passed winget
validation; don't let tools such as komac regenerate them from the Velopack Setup.exe (it detects
`x86` and different switches).
