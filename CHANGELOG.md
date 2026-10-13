# Changelog

## 1.0.0 (2026-10-13)

First published release. Windows x64, NativeAOT single-file build, MIT licensed.

### Added

- "Serene Desk" visual system (stages A1–A5): unified light/dark palette and typography; toolbar with save dot, measured folding and a More overflow; text-only tabs with clean/dirty/active states; overlay find (floating panel on wide windows, bar on narrow ones) and a table-of-contents ruler that open without moving the body; bottom-edge reading progress line; three-line tables; code blocks with language label and copy button; narrow-window, keyboard, high-contrast and reduced-motion fallbacks.
- Return-to-reading hint ("返回原处 Alt+←") after a successful jump.
- Explicit Zen mode (F11 or More ▸ 设置与维护 ▸ 沉浸模式): stops drawing the toolbar without moving the body; recalled by pointer at the top edge or F6; remembered in the configuration.
- Reading-mode rendering of whitelisted HTML blocks as equivalent Markdown; single-table HTML blocks become table blocks.
- GitHub gemoji shortcodes (1,913 aliases), Obsidian callouts and wikilinks (vault lookup under `.obsidian` roots, including `![[image]]`).
- GitHub-style soft line breaks (joined with a space; not between CJK characters) and CJK punctuation compression.
- Native inline math for formulas Unicode cannot express (letter sub/superscripts, `\vec`), with TeX atom spacing in block and native inline math and more LaTeX commands (`\mathbf`, `\hat`, `\top`, `\mathbb`, `\mathcal`, `\mathrm`, `\left`/`\right`, `\text`).
- `THIRD-PARTY-NOTICES.txt`, shipped in the release ZIP.

### Changed

- Bare URLs end at CJK or full-width text (e.g. `访问https://x.com获取` links only the URL), and `www.` right after Chinese text is recognised; a deliberate departure from GFM, which runs to the next space.
- Headings H2–H6 use weight 700 like H1, so Latin and CJK in one heading share the same weight (YaHei has no semibold).
- Mermaid diagrams draw quietly: Muted 1.2 DIP edges and labels, ControlBorder 1 DIP rounded boxes and diamonds filled with Canvas, Ink node text; diagrams narrower than their panel are centred.
- Collapsed outline ruler ticks use ControlBorder; the current section keeps the Accent mark.
- Code, literal-HTML and diagram panels bleed equally into both margins (22 DIP, kept 4 DIP inside the window when margins are narrow); the code text spans the full column, so lines up to the column width no longer scroll sideways.
- The reading column follows the window width with fixed 48 DIP margins (no 40em cap, no automatic window resizing); a pinned directory makes room at V ≥ 816 DIP, narrower windows keep it as an overlay.
- Spacing after lists, sparse-line justification limits, half-width closing punctuation never starts a line, continuous quote rails, digit sub/superscripts shown as Unicode, list-item code blocks and quotes styled like top-level ones, dense TOC rulers thinned instead of drawn as a barcode.

### Removed

- Remove the read-only Markdown-source reader from the Windows system menu, together with its UIA-based tests; screen-reader support is outside the product scope.

### Fixed

- Renaming a document no longer duplicates the new name in the recent-files list when it was already listed, and the list stays within the configured size.
- The recent-files list treats paths that differ only in letter case as the same file, as tabs and bookmarks already do; existing duplicates merge when the configuration is loaded.
- Preferences and bookmarks written by a newer version are left untouched; changes made in this session stay in memory. The version is read before any other field, so a newer file whose field types changed is recognised too.
- An unreadable `preferences.json` or `bookmarks.json` (corrupt, oversized, wrong field types, no schema) is no longer overwritten: it is renamed to `*.corrupt-<id>` before the next save.
- Settings a newer version adds to the window configuration are kept when this version saves it, including keys that start with `recent` other than `recent1`, `recent2`, ….
- Recent files and tabs on a drive or network share that is temporarily unavailable are kept instead of being dropped; such a tab that is reopened and then closed during the session is not restored again.
- Footnotes: jumping back from the footnote list to the reference works (it never did); labels with capitals jump both ways; a reference without a definition stays literal text as in GFM.
- Literal HTML panels use the same 6 DIP rounded corners as code blocks.
- Find highlights matches inside Mermaid diagram labels (previously counted but invisible).
- High contrast: diagram lines and boxes use the text colour and outline ticks use GrayText again (the quiet palette applies to normal themes only).
- Reading positions saved in gaps between blocks or in the bottom margin (back/forward, bookmarks, reading memory) now return exactly instead of snapping to the previous block edge.
- A pinned directory follows the window width: it folds away when the window becomes too narrow to pin it (instead of covering the start of each line), comes back when the window is wide again, and is restored at startup when it was left pinned.
- Mermaid flowcharts with cycles (retry loops, self loops) are drawn instead of falling back to source: back edges are laid out reversed and drawn with the arrow into their target; self loops sit beside their box.
- Mermaid edge labels on upright lines sit beside the line instead of on top of it.
- Mermaid labels on a two-way pair of edges (A → B and B → A) sit on the outer side of each line instead of between the two lines.
- Mermaid panels have equal padding above and below the diagram (the bottom used to be 16 DIP deeper), and edge labels no longer poke past the top or left margin — a long label on the left of an upright line used to be clipped.
- Paragraphs with long URLs: a short lead-in (“Long link:”) no longer sits alone on a line when the URL fits after it; a URL never breaks between “https:” and “//”; a URL wider than the column starts on the lead-in line, fills it and breaks after a “/” instead of overflowing and leaving its tail on a line of its own.
- Mermaid flowcharts: wide diagrams are clipped to their panel and scroll sideways like wide code; edges spanning layers are routed between boxes instead of through them, layers are ordered to reduce crossings, parents are centred over their children, and long labels wrap at a word boundary.
- Preserve independent file-open requests during concurrent single-instance handoff.
- Transfer foreground activation permission before handing a document to the existing window.
- Make the LF save regression fixture independent of Git autocrlf checkout settings.
- Table column alignment: `:---:` rendered right-aligned and `---:` centered.
- Emphasis matching no longer pairs `*` inside inline math.
- Reference-style links wrapping reference-style images display normally.

### Security

- Local links to executable file types (as classified by Windows: `.exe`, `.bat`, `.lnk`, …) ask with a warning that defaults to No; paths with an NTFS alternate data stream (`setup.exe::$DATA`) get the same warning.
- HTML export (and Copy as HTML) now filters raw HTML with an allowlist that tokenizes like a browser and re-serializes allowed tags. The previous blocklist could be bypassed in seven browser-confirmed ways (quotes inside comments or unquoted attribute values, and `noscript`/`xmp`/`textarea`/`title`/`noembed` tricks), letting script run when the exported file opened. Unknown tags, PHP/CDATA/DOCTYPE now appear as literal text, HTML comments are dropped, and 31 CommonMark raw-HTML pass-through examples no longer match in export (see ADR-0002).

### Engineering
- HostSmoke finds the fixture image in a capture and clicks it in physical pixels; the hard-coded point predated the S80 layout and missed the image.

- Add bounded handoff and failed-save/recovery-retry tests.
- Validate release ZIP contents and emit SHA-256 and source/check-status manifests.
- Record raw performance samples, median and maximum without relaxing existing gates.
- Record the UI test environment.
- Reuse exact paragraph measurements within a bounded relayout scope; preserve geometry/copy baselines and performance gates.
- Validate .NET 10 LTS NativeAOT with an isolated SDK and no new production dependencies; add opt-in real keyboard checks.
- Build the native shim with `/Brepro` so identical sources give a byte-identical `MDNative.dll` and a Publish leaves the worktree clean.
- Release ZIP must contain the executable and third-party notices.

### Release status

1.0.0 passed the full local `Verify` (formatting, 3200+ self-tests, per-example specification baseline, corpus golden) with the native shim rebuilt from source, and the published single-file executable was exercised by the local window suites (22 suites; two checks were affected by foreground desktop use). Real keyboard/IME/touch and system high-contrast checks last passed at d59e363 and were not rerun since. Performance gates and window checks depend on the machine and an interactive desktop, so the GitHub Actions workflow runs Verify and Publish only. The build is unsigned and released under the MIT license.
