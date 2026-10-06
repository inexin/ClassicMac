# Releasing

A release is made by pushing a version tag. `.github/workflows/release.yml` then builds the downloads on Windows, macOS
and Linux runners and publishes them as a GitHub release.

## Making a release

1. In `CHANGELOG.md`, rename `## Unreleased` to the version (`## 0.1.0`) and start a new empty `## Unreleased` above
   it. The release's notes are that section (without one, the Unreleased section is used).
2. Commit and push `main`.
3. Tag and push the tag:

   ```
   git tag v0.1.0
   git push origin v0.1.0
   ```

The version comes from the tag (`v0.1.0` builds 0.1.0; `Directory.Build.props` is not edited). A version with a hyphen
(`v0.2.0-beta.1`) is published as a pre-release. Progress is under the repository's Actions tab; a failed build
publishes nothing, and the tag can be deleted and pushed again.

## What is published

| File | What it is |
| --- | --- |
| `ClassicMac-<version>-win-x64.zip` | The desktop app's folder; run `ClassicMac.exe` |
| `ClassicMac-<version>-osx-arm64.zip`, `-osx-x64.zip` | `ClassicMac.app` for Apple silicon and Intel Macs, with its icon |
| `ClassicMac-<version>-linux-x64.tar.gz` | The app, its icons and a `.desktop` file; `install.sh` installs it for the user (`--uninstall` removes it) |
| `ClassicMac-CLI-<version>-<runtime>.zip` / `.tar.gz` | The command line as one file, `classicmac` (`classicmac.exe` on Windows) |
| `*.nupkg`, `*.snupkg` | The library packages and the `classicmac` .NET tool, with symbols |

Everything is self-contained: users need no .NET install. The NuGet packages are also pushed to nuget.org when the
repository has a `NUGET_API_KEY` secret (Settings ▸ Secrets and variables ▸ Actions); without it they are only attached.

## Building one platform locally

`tools/Release/package.sh` is what the workflow runs, one runtime at a time:

```
tools/Release/package.sh win-x64 0.1.0-test dist
```

It needs the .NET SDK, bash and Python (for the zips). Runtimes: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`,
`linux-x64`, `linux-arm64`. The macOS bundle's `Info.plist`, the Linux `.desktop` file and `install.sh` are in
`tools/Release/`; the icons come from `design/icon/`. `tools/Release/notes.sh <version>` prints the release notes.

## Not done yet

- **Signing.** The builds are unsigned. macOS Gatekeeper warns until the app is signed and notarized (an Apple
  Developer ID); users open it the first time with right-click ▸ Open. Windows SmartScreen warns until the executables
  are signed with a code-signing certificate; users choose More info ▸ Run anyway.
- **Installers.** No `.msi`, `.dmg`, AppImage or Flatpak; the downloads are archives.
