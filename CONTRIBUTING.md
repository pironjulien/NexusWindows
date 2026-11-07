# Contributing

## Environment

- Windows 10 or 11, the .NET SDK pinned by `global.json`, PowerShell 7;
- an interactive desktop session only for the live tests (`./test.ps1 -Full`), never needed by the default run.

## Rules

1. Open an issue or a pull request against `main`. A change is accepted only when `./test.ps1` passes: the CI
   (`.github/workflows/tests.yml`) runs that same script on every push and pull request.
2. **Every new feature and every bug fix comes with its test** in `tests/` (xUnit). A fix starts with a test that
   fails without it; a new tool, argument or behaviour gets a test that exercises it through the engine.
3. Comments of the C# sources and of the versioned PowerShell and batch scripts are written in English
   (`EnglishCommentsTests` fails on a French comment). Commit messages are in English.
4. No README states the product version by hand, and `CHANGELOG.md` follows Keep a Changelog 1.1.0: add your entry
   under `[Unreleased]` (`RepositoryDocsTests`).
5. Never commit a secret, a token, a log or a build output: builds and test outputs go to
   `%USERPROFILE%\.nexus\build\nexuswindows`, outside the repository.
6. Keep the safeguards: guard rails, shared foreground token, emergency stop. A change that touches them needs a test
   proving they still refuse what they must refuse.

## Checks

```powershell
./test.ps1          # dotnet test (xUnit): engine, launcher and installers; offline, the same command as the CI
./test.ps1 -Full    # plus the live tests: real Explorer launch, shared foreground token, capture session
git diff --check
```

The live tests take the shared foreground token and open real windows: run `-Full` only on a real interactive
Windows session that nobody is using. The hosted CI skips them, so `./test.ps1 -Full` must pass before a stable
promotion (see the Development section of the [README](README.md)).

## Version

One version everywhere: `commun/engine/NexusWindows.Engine.csproj` sets it; `commun/engine/McpServer.cs`, the
launcher, `commun/native-runtime.ps1`, `server.json`, the plugin and bundle manifests follow, and `CHANGELOG.md` has its
`[X.Y.Z]` section. The `Release` workflow (`.github/workflows/release-assets.yml`) checks them, runs `test.ps1`, builds,
attests and publishes a development build on every push to `main`.
