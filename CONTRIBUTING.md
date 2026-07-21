# Contributing to AIUsage.NET

AIUsage.NET is a Windows port of [OpenUsage](https://github.com/robinebers/openusage). The feature
set intentionally follows the original: tracking AI coding subscription usage, nothing more.
Contributions that expand scope well beyond that, add unnecessary complexity, or compromise the UX
will likely be declined.

If you're unsure whether your idea fits, open an issue first before investing time in a PR.

## Ground Rules

- No feature creep. If it's not about usage tracking, it doesn't belong here.
- No AI-generated commit messages. Write your own.
- Test your changes: run `dotnet build AIUsage.sln` and, where relevant, exercise the CLI or tray app
  manually. If it touches the tray UI, include before/after screenshots.
- Keep it simple. Don't over-engineer.
- One PR per concern. Don't bundle unrelated changes.
- Follow [AGENTS.md](AGENTS.md) for engineering conventions.

## License Agreement

By submitting a pull request, you agree that your contribution is licensed under the
[MIT License](LICENSE) that covers this project.

## How to Contribute

### Fork and PR workflow

1. Open an issue describing the change (skip this for small, obvious fixes)
2. Fork the repo
3. Create a branch (`feat/my-change`, `fix/some-bug`, etc.)
4. Make the change
5. Run `dotnet build AIUsage.sln` (and `script/build_and_run.ps1` to smoke-test) to verify nothing is
   broken
6. Open a PR against `main` using the PR template

### Add a provider

Each provider is a small module under `src/AIUsage.Core/Providers/<Name>/` that implements
`IProviderRuntime`: an auth store reads credentials already on the user's machine, a usage client
calls the provider's API, and a mapper normalizes the response into metric lines. See
[docs/adding-a-provider.md](docs/adding-a-provider.md) for the full walkthrough (and
[docs/architecture.md](docs/architecture.md) for how the pieces fit together).

1. Open an issue — include why the provider fits and how its usage data is accessible
2. Create `src/AIUsage.Core/Providers/<Name>/` and implement `IProviderRuntime`
3. Register the provider in `ProviderCatalog.Make()`
4. Add a provider page in `docs/providers/` (metrics, credential sources, endpoints, troubleshooting)
5. Test it locally with `script/build_and_run.ps1`
6. Open a PR with screenshots showing it working

### Fix a bug

1. Reference the related issue number in your PR, if one exists
2. Describe the root cause and fix
3. Include before/after screenshots for UI bugs
4. Add a regression test if applicable

### Request a feature

[Open an issue](https://github.com/LuigiElleBalotta/AIUsage.NET/issues/new?template=feature_request.yml)
and make your case before opening a PR for anything non-trivial.

## What Gets Accepted

- Bug fixes with clear descriptions
- New providers that follow the existing provider architecture
- Documentation improvements
- Performance improvements with benchmarks
- Accessibility improvements

## What Gets Rejected

- Features that expand the scope beyond usage tracking
- Changes that compromise speed, simplicity, or the existing UX
- PRs without testing evidence
- Code with no clear purpose or explanation
- Cosmetic-only changes without prior discussion

## Code Standards

- .NET 8 / C#, built with the standard `dotnet` CLI (no extra tooling required)
- Follow existing patterns in the codebase — [AGENTS.md](AGENTS.md) is the engineering contract
- User-visible behavior changes must update the matching `docs/` page(s) in the same PR
- UI copy is plain language and sentence case
- No new dependencies without justification; pin exact versions

## Questions?

Open a [bug report](https://github.com/LuigiElleBalotta/AIUsage.NET/issues/new?template=bug_report.yml)
or [feature request](https://github.com/LuigiElleBalotta/AIUsage.NET/issues/new?template=feature_request.yml)
using the issue templates.
