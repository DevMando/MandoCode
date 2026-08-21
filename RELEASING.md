# Releasing MandoCode (CLI / engine)

The engine drives the version generation: its `major.minor` (0.14, 0.15, …) is shared with
MandoCode Desktop, while each product's patch number advances independently. Bump the minor
here only for a new generation; Desktop follows in its next release.

## 1. Prepare

- [ ] Bump `<Version>` in `src/MandoCode/MandoCode.csproj` — this single value drives the
      banner, `--doctor`, the update checker, and the NuGet package version.
- [ ] Roll `docs/CHANGELOG.md`: retitle `[Unreleased]` to `[X.Y.Z] - YYYY-MM-DD`. House style
      for released versions: a bold narrative opener, a **"Why this matters (plain-language
      summary)"** section written for a non-engineering reader, then Fixed/Changed/Added and a
      **Test coverage** note. (The changelog lives in `docs/`, not the repo root.)
- [ ] PR the above, merge to `main`.

## 2. Verify the artifact

```bash
dotnet pack src/MandoCode/MandoCode.csproj -c Release
# Install the exact nupkg in isolation (does NOT touch your global install):
dotnet tool install MandoCode --version X.Y.Z --tool-path %TEMP%\mc-smoke --add-source src\MandoCode\bin\Release
%TEMP%\mc-smoke\mandocode.exe --doctor
dotnet tool uninstall MandoCode --tool-path %TEMP%\mc-smoke
```

`--doctor` must report the new version, a healthy environment, and a **.NET 10** runtime. The
package carries a net8.0 build too; seeing 8.x here on a machine with the .NET 10 SDK means the
net10.0 asset did not make it into the package.

## 3. Tag and publish on GitHub

```bash
git tag -a vX.Y.Z <merge-commit> -m "MandoCode X.Y.Z — <tagline>"
git push origin vX.Y.Z
```

Create the GitHub release on the tag. Conventions: title `vX.Y.Z — <tagline>`, body adapted
from the changelog's plain-language section, marked as latest. If a version was ever skipped
on NuGet, the next release's notes must carry the union of changes since the last version
users could actually install (see v0.14.3, which absorbed the unpublished 0.14.0).

## 4. Publish on NuGet

```bash
dotnet nuget push src/MandoCode/bin/Release/MandoCode.X.Y.Z.nupkg --api-key <KEY> --source https://api.nuget.org/v3/index.json
```

- The API key is NOT stored on any machine — it lives in the password manager. Keys expire
  (max 1 year); a 403 on push means regenerate at nuget.org/account/apikeys.
- Indexing takes ~5–15 minutes. Verify:
  `curl -s https://api.nuget.org/v3-flatcontainer/mandocode/index.json`
- Existing installs learn about the release automatically: `UpdateCheckService` polls NuGet
  at most once per 24h and shows the update nudge.

## Notes

- Tags are the release record — every published version must have one (v0.14.0 was published
  without a tag once; it was backfilled later, don't repeat it).
- GitHub releases don't distribute the package; NuGet does. A release without the NuGet push
  is invisible to users.
