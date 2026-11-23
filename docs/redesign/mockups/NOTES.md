# Concept A v3 — "Native Calm" (build reference): dark (default) + light

## Rationale
v3 combines v2's calm layout with v1's colour cues:
- **v2 layout:** no nested cards, one divider style, one accent-filled button per screen and the same expanded NavigationView on every screen.
- **v1 colour cues:** confidence chips, KEEP/REMOVE/PROTECTED badges, ✓/≠ attribute marks, a thin role edge with a faint tint on file rows, and coloured activity icons.

Indigo appears only on the primary button, the nav indicator and selection controls (checkboxes, toggles, progress bar). Semantic colours are never used for actions.

The two themes share layout, content, data and element set. Only the theme tokens below change.

- Dark: `A-fluent-dark-v3/`, the default.
- Light: `A-fluent-light-v3/`, ported from dark v3 with WinUI 3 light tokens.

## Dark theme tokens (default)
| Token | Dark value | Used for |
|---|---|---|
| Window / Mica Alt base | `#141414` | title bar, NavigationView |
| Content layer | `#1A1A1A` | page surface |
| Card fill | `rgba(255,255,255,0.04)` | Scan settings group only |
| Stroke / divider | `rgba(255,255,255,0.06)` | all dividers |
| Control fill / selected row | `rgba(255,255,255,0.0605)` / `0.0837` | buttons, inputs / selected rows, nav item |
| Control stroke top / bottom | `rgba(255,255,255,0.093)` / `0.0698` | buttons, combo boxes |
| Strong stroke | `rgba(255,255,255,0.544)` | text box underline, unchecked box, progress track |
| Text primary / secondary / tertiary | `#FFFFFF` / `rgba(255,255,255,0.786)` / `0.60` | tertiary raised from 0.544 for AA (7.0 : 1) |
| Accent (indigo Light 2) | `#9EA3F5` + `#000` text (8.9 : 1) | primary button, nav indicator, checkboxes, toggles, progress |
| Accent disabled | `rgba(255,255,255,0.1581)` + `0.5302` text | disabled "Scan videos" |
| Success / tint | `#6CCB5F` / `#393D1B` | KEEP, High, ✓, Restore icon |
| Caution / tint | `#FCE100` / `#433519` | Medium, ≠, "all selected", delete icon, warning |
| Critical / tint | `#FF99A4` / `#442726` | REMOVE, Low, destructive button text |
| Neutral badge / chip | `#C9CBD8` / `rgba(255,255,255,0.0326)` | PROTECTED, "No removal suggested", neutral log icons |
| Role badges | light semantic fill + **black** text (≥ 10 : 1) | KEEP ✓ / REMOVE ✕ / PROTECTED 🔒 |
| Row edge + tint | 3 px edge in role colour + `rgba(role,0.055)` fading by 55 % | keep / remove rows; protected = neutral edge at 55 % opacity |
| Dialog | smoke `rgba(0,0,0,0.30)`; surface `#2B2B2B`; command area `#202020`; stroke `rgba(117,117,117,0.40)` | confirmation |
| Focus visual | 2 px `#FFFFFF` + 1 px `#000` inner | |

## Light theme tokens
| Token | Light value | Used for |
|---|---|---|
| Window / Mica base | `#F3F3F3` | title bar, NavigationView |
| Content layer | `#F9F9F9` (LayerFillColorDefault `rgba(255,255,255,0.5)` over Mica) | page surface |
| Card fill / stroke | `rgba(255,255,255,0.7)` / `rgba(0,0,0,0.0578)` | Scan settings group only |
| Divider | `rgba(0,0,0,0.0803)` | all dividers |
| Control fill | `rgba(255,255,255,0.7)` | buttons, inputs, selected segment (`#FFFFFF` with a hairline shadow) |
| Selected row / nav item | `rgba(0,0,0,0.0373)` (table rows `0.0241`) | SubtleFillColorSecondary |
| Control stroke top / bottom | `rgba(0,0,0,0.0578)` / `rgba(0,0,0,0.1622)` | buttons, combo boxes |
| Strong stroke | `rgba(0,0,0,0.4458)` | text box underline, unchecked box, progress track |
| Text primary / secondary / tertiary | `rgba(0,0,0,0.8956)` / `0.6063` / `0.56` | tertiary raised from 0.4458 for AA (≈ 4.7 : 1 on `#F9F9F9`) |
| Accent (indigo Dark 1) | `#3F44B0` + `#FFFFFF` text (≈ 8.5 : 1) | primary button, nav indicator, checkboxes, toggles, progress |
| Accent disabled | `rgba(0,0,0,0.2169)` + white text | disabled "Scan videos" |
| Success / tint | `#0F7B0F` / `#DFF6DD` | KEEP, High, ✓, Restore icon. Text on tint is 4.8 : 1, on white 5.4 : 1 |
| Caution / tint | `#9D5D00` / `#FFF4CE` | Medium, ≠, "all selected", delete icon, warning. On tint 6.6 : 1, on white 7.1 : 1 (never use the dark-theme yellow on light) |
| Critical / tint | `#C42B1C` / `#FDE7E9` | REMOVE, Low, destructive button text. On tint 4.8 : 1, on white 5.7 : 1 |
| Neutral badge / chip | `#5C5F70` / `rgba(0,0,0,0.0241)` | PROTECTED, "No removal suggested", neutral log icons |
| Role badges | **dark** semantic fill + **white** text (5.4, 5.7 and 6.5 : 1) | the inverse of dark's light fill with black text, so both themes read as the same system |
| Row edge + tint | 3 px edge in role colour + `rgba(role,0.045–0.05)` fading by 55 % | keep / remove rows; protected = neutral edge at 45 % opacity |
| Dialog | smoke `rgba(0,0,0,0.30)`; surface `#FFFFFF`; command area `#F3F3F3`; shadow `0 32px 64px rgba(0,0,0,.19)` | confirmation |
| Focus visual | 2 px `rgba(0,0,0,0.8956)` + 1 px `#FFF` inner | |
| Thumbnails | same frames, `brightness(1.12)` | so they don't look muddy on light surfaces |

## Type (both themes)
- Segoe UI Variable: Display 28/36 semibold title, 20/28 subtitle, Text 14/20 body, Small 12/16 caption, 11 px badges and chips.
- Cascadia Mono 11–12 for paths.

## Style rules (implementation guide, both themes)
1. **One focal point and one accent-filled button per screen.** Indigo is used only for the primary button, the nav indicator and selection controls (checkbox, toggle, progress).
2. **One container level.** Use spacing and a single divider. There is no card inside a card and no borders around file rows. Role colour on a row is a 3 px left edge plus a faint fading tint.
3. **One chip style, for confidence only.** Pill, tinted background, semantic text: High success, Medium caution, Low critical, "No removal suggested" neutral.
4. **One badge style, for file role only.** A filled 4 px-radius badge with an icon: KEEP ✓, REMOVE ✕, PROTECTED 🔒. Dark uses light fills with black text; light uses dark fills with white text.
5. **Compact metadata, two lines at most.**
   - Line 1: the time and where it came from.
   - Line 2: resolution · duration · size.
   - On candidates, ✓ (success) marks a match with the keeper and ≠ (caution) marks a difference. The keeper's line is plain because it is the reference.
6. **Explanations are one muted sentence.** The only boxed callout is a real-risk warning (the confirmation dialog).
7. **Don't repeat information.** The selection total lives only in the bottom bar. Scan settings are a muted subtitle line. Rare actions go in the "…" overflow menu.
8. **Same expanded NavigationView everywhere.** Counts are tertiary text.
9. **Selection.** Each removable file has exactly one checkbox. Keepers and protected files have none.
10. **Activity log icons** sit in 26 px tinted circles. Restore is success, permanent delete is caution, and move, export and scan are neutral.

## Data consistency
- Review shows 23 files · 18.4 GB selected across 11 groups.
- Recovery holds 25 files · 19.5 GB: today's 23-file move plus 2 files moved on Oct 18. The log lists both moves.
- The version is 1.2.0 everywhere.

## Files (in each folder)
- `concept.html`: pick a screen by adding `#scan`, `#scan-empty`, `#review`, `#confirm` or `#activity` to the end.
- `render.sh`
- `01-scan.png`, `01b-scan-empty.png`, `02-review.png`, `03-confirm.png`, `04-activity.png` (1280×800 @1x)
