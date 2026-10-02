# ClassicMac design tokens

The neutral frame, light and dark. Every colour, size and radius in the app's chrome comes from these tokens. Mac content (previews, rendered dialogs and menus, icons) never uses them; only its frame does. [Tokens.axaml](Tokens.axaml) is the starting resource dictionary (task F1).

## Colour

Each token becomes a `Color` (`<Name>Color`) per theme in `ThemeDictionaries` and one `SolidColorBrush` (`<Name>`) that points at it with `DynamicResource`, so the app follows the OS theme through `ThemeVariant`.

| Token | Light | Dark | Used for | Replaces today |
| --- | --- | --- | --- | --- |
| **Surfaces** | | | | |
| CmWindowBackground | #F3F3F0 | #1E1E20 | Window behind panes | |
| CmTitleBarBackground | #E6E6E1 | #202023 | Custom title bar (Windows, macOS) | |
| CmChromeBackground | #ECECE8 | #252528 | Menu bar, toolbar, status bar, panel headers | |
| CmPaneBackground | #FFFFFF | #19191B | Inspector, diagnostics list, dialogs | |
| CmSidebarBackground | #F7F7F4 | #1F1F22 | Tree, group rows, read-only bars | |
| **Lines** | | | | |
| CmBorder | #D9D8D2 | #38383C | Pane edges, splitters, tab strip | `SystemControlForegroundBaseLowBrush` |
| CmDivider | #E6E5E0 | #2C2C30 | Rows, section headers | |
| CmCardBorder | #DEDDD7 | #3A3A3E | Preview cards, property groups | |
| **Text** | | | | |
| CmText | #1D1D1B | #EDEDEA | Body text, values | |
| CmTextMuted | #5D5C57 | #A9A8A3 | Labels, metadata, hints | `Opacity` 0.55–0.75 on text |
| **Accent and selection** | | | | |
| CmAccent | #2E4EC2 | #8FA3FF | Primary buttons, focus, links, active tab, waveform | |
| CmOnAccent | #FFFFFF | #0F1530 | Text on accent | |
| CmSelection | #2E4EC2 | #33417A | Selected row, focused list | Fluent default grey |
| CmSelectionText | #FFFFFF | #F2F4FF | Text on selection | |
| CmSelectionInactive | #DCE3FA | #2C3354 | Selected row, focus elsewhere | |
| CmRowHighlight | #EEF2FD | #24283A | Row tied to preview, Editing badge | |
| CmHexCursor | #2E4EC2 | #8FA3FF | Hex byte under the cursor | `#5A80B0FF` in MainWindow.axaml |
| CmMatch | #FFE58A | #6B5714 | Current search match: tree filter and type-ahead letters, hex Find | `#FFE58A` on the browse-tree board |
| CmMatchSoft | #FFF3C4 | #3F3618 | Other matches of the same search | |
| **Controls** | | | | |
| CmControlBackground | #FFFFFF | #2C2C30 | Buttons, inputs, selects | |
| CmControlBorder | #CFCEC8 | #45454A | Buttons, inputs, selects | |
| CmSegmentTrack | #E2E1DC | #2C2C30 | Segmented controls, progress track | |
| CmSegmentOn | #FFFFFF | #45454B | Chosen segment | |
| **Severity** | | | | |
| CmError | #B42318 | #FF8A7A | Error icon and text, form errors | `#C0392B` (FormError) |
| CmErrorTint | #FCE9E7 | #3A1D1A | Error badge, error field ring | |
| CmWarning | #8A5300 | #F2B45A | Warning icon and text, template notes | `#B9770E` (TMPL note) |
| CmWarningTint | #FDF1DA | #3A2C14 | Warning badge, changed hex bytes | |
| CmInfo | #245C9E | #8CBEF5 | Info icon and text | |
| CmInfoTint | #E6EFF9 | #18283C | Info badge | |
| **Mac content frame** | | | | |
| CmCheckerLight | #FFFFFF | #2A2A2D | Checkerboard squares behind previews | `Images.Checkerboard` (fixed) |
| CmCheckerDark | #ECECE8 | #323236 | Checkerboard squares, 8 DIP | |
| CmLoopRegion | #E5EAFB | #263058 | Sound loop range | WaveformView (fixed) |
| CmPlayhead | #B42318 | #FF8A7A | Sound playhead | |

Contrast, checked against WCAG AA: body text 16.9:1 light / 15.0:1 dark; muted text 5.7:1 or better on every surface; severity text on its tint 5.6:1 or better; accent buttons 7.0:1 light / 7.6:1 dark.

Match tints carry normal text: CmText on CmMatch 13.5:1 light / 6.0:1 dark, on CmMatchSoft 15.2:1 / 10.2:1; CmTextMuted on CmMatchSoft 6.0:1 / 5.0:1.

**One exception, handled by a rule:** CmTextMuted on the dark CmSelection is 4.0:1, below AA (worse at reduced opacity). Inside a selected row, all text uses CmSelectionText, including the tree's right-aligned meta and diagnostics codes; never lower its opacity.

Severity is never colour alone: error = filled circle with ×, warning = filled triangle with !, info = outlined circle with i.

## Type

IBM Plex Sans and IBM Plex Mono, bundled as app assets (SIL Open Font License), so Windows, macOS and Linux match. Plex Mono replaces the "Cascadia Mono, Consolas, Menlo, monospace" chain.

| Resource | Value | Weight | Font | Used for |
| --- | --- | --- | --- | --- |
| CmFontSizeTitle | 22 | CmFontWeightStrong | CmFontSans | Empty-state heading |
| CmFontSizeHeading | 17 | CmFontWeightStrong | CmFontSans | Inspector header name |
| CmFontSizeBody | 13 | Regular | CmFontSans | Tree, tables, forms |
| CmFontSizeMeta | 12 | Regular | CmFontSans | Metadata, hints, buttons, toolbar labels |
| CmFontSizeCaption | 11 | CmFontWeightStrong, uppercase, CmLetterSpacingCaption | CmFontSans | Column headers, section labels |
| CmFontSizeMono | 13 | Regular | CmFontMono | Hex, offsets, codes |
| CmFontSizeMonoSmall | 12 | Regular | CmFontMono | Type/creator, OSTypes, IDs |

Other type resources: `CmFontSans`, `CmFontMono` (FontFamily), `CmFontWeightStrong` = SemiBold (FontWeight), `CmLetterSpacingCaption` = 0.66 (0.06 em at 11). All are defined in [Tokens.axaml](Tokens.axaml) with these exact names.

Four-character codes (`'ICN#'`, `FNDR · MACS`) and diagnostic codes (`archive.fork-crc`) are always mono.

## Spacing and sizes

4 px base.

| Token | Value | Used for |
| --- | --- | --- |
| CmSpace1 | 4 | Icon to text |
| CmSpace2 | 8 | Control gaps |
| CmSpace3 | 12 | Card padding |
| CmSpace4 | 16 | Pane padding (inspector uses 18 horizontally) |
| CmSpace6 | 24 | Section gaps |
| CmRowTree | 22 | Tree row |
| CmRowList | 24 | Diagnostics row, hex line |
| CmRowGroup | 26 | Diagnostics group header |
| CmRowForm | 34 | Read-only form row |
| CmControlHeight | 30 | Buttons, inputs (28 inside tables) |
| CmBarTitle | 34 | Title bar |
| CmBarMenu | 26 | Menu bar |
| CmBarTool | 48 | Toolbar (buttons 34 high) |
| CmBarTabs | 36 | Inspector tab strip |
| CmBarStatus | 26 | Status bar |
| CmPanelHeader | 34 | Diagnostics header |
| CmTreeIndent | 16 | Per tree level |
| CmTreeWidth | 340 | Default tree pane width |
| CmDiagnosticsHeight | 196 | Default diagnostics height (34 collapsed) |

## Radius and elevation

| Token | Value | Used for |
| --- | --- | --- |
| CmRadiusSmall | 4 | Segments, chips, tree rows |
| CmRadius | 6 | Buttons, inputs, cards |
| CmRadiusLarge | 8 | Panels, property groups |
| CmRadiusDialog | 10 | Dialogs |
| CmShadowPopover | 0 10 28, black 18% | Popovers, type-ahead pill |
| CmShadowDialog | 0 18 50, black 35% | Modal dialogs |

Panes have no shadow; a 1 px CmBorder separates them.

## Pixel content at any display scaling

1. **Device pixels per Mac pixel:** `k = max(1, floor(zoom × RenderScaling))`. Draw the bitmap at k device pixels per Mac pixel and size the control at `width × k / RenderScaling` DIPs.
2. **Snap the origin** to a whole device pixel (`UseLayoutRounding` on the window, and a snapped offset inside custom controls), or rows of pixels double up.
3. **No smoothing:** `RenderOptions.BitmapInterpolationMode="None"` on every preview, icon and rendered dialog or menu.
4. **Tree icons** sit in a 16 DIP slot: 1 device pixel per Mac pixel at 100%, 125% and 150% (centred, slightly small), 2 at 200%. Never 1.25 or 1.5.
5. **Zoom label** shows what the user picked (2×). At 150% scaling that is drawn as 3 device pixels per Mac pixel.
6. **Headless tests** render at 100% and 200%; add a 150% case that checks a 1 px checkerboard stays 1:1.

## Where tokens meet Fluent

- Set `SystemAccentColor` (and its Light/Dark variants) to CmAccent, so Fluent's own controls, focus rings and check boxes pick it up.
- Override the TreeView, ListBox and TabItem selection brushes with the CmSelection tokens. Fluent's key names change between releases: check them against Avalonia 12's FluentTheme source before relying on them.
- Own styles live in `Styles/ClassicMac.axaml` as classes, not per-view setters: `.chrome`, `.pane`, `.badge.error` / `.warning` / `.info`, `.segmented`, `.readonly-bar`, `.editing-badge`, `.caption`.
- Custom-drawn views (WaveformView, the hex cursor, the checkerboard) read brushes with `TryFindResource` and redraw on `ActualThemeVariantChanged`.
- The Platinum look on the canvas was dropped; the folder preview already shows the real Platinum window.
