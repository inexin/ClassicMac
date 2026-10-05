# CLAUDE.md: ClassicMac.App

Guidance for the Avalonia app and `tests/ClassicMac.App.Tests`. What the app does is in `README.md`; the
repository-wide rules are in the root `CLAUDE.md`.

- **Format-free:** the app adds nothing format-specific. Reading, writing, decoding and checks live in the libraries
  (`ClassicMac.Files`, `.Resources`, `.Resources.Decoders`) and the app calls them; a rule the app needs goes in a
  library, with its spec and tests there.
- **Design:** the design brief (`design/APP-DESIGN-BRIEF.md`), the tokens (`design/TOKENS.md`, `design/Tokens.axaml`)
  and the boards (`design/boards/*.md`) say how each view looks and behaves; the app follows them. Don't edit
  `design/` unless asked.
- **Layout:** views in `Views/` and `Dialogs/` (XAML with code-behind), view models in `ViewModels/`
  (CommunityToolkit.Mvvm; menus and commands grouped in `*Actions` classes), controls drawn in code in `Controls/`,
  platform services (settings, sound, window shell) in `Services/`. Code-behind and view models are the only
  `partial` classes.
- **Settings** last in `Settings` (a record) through `JsonSettingsStore`; a new setting gets a default, a menu item and
  a test.
- **Tests** run the app headless. On Windows, `Baselines` compares screenshots with `tests/golden/app` (light, dark
  and 150%); a change that moves pixels looks at the diff images in the temp folder, then rewrites the baselines with
  `CLASSICMAC_UPDATE_BASELINES=1`. CI sets `CLASSICMAC_SKIP_BASELINES=1`.
- **Icon:** `Assets/icon` holds copies of `design/icon`'s files (`classicmac.ico`, PNGs 16–128); `AppIcon` draws the
  title bar's pixel version 1:1 by display scaling, never resampled; `AppIconTests` checks the assets exist.
- **Rez** stays in the CLI; the app gets it only when asked for.
