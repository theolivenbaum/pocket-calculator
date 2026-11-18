# Upstream review, November 2026

Upstream Obscura (`https://github.com/h4ckf0r0day/obscura`) reviewed from `1a3169d`
(2026-09-20, where the September review stopped, `upstream-review-2026-09.md`) to
`4eb1235` (2026-11-18): 319 commits that are not merges, plus 11 merges that carry
fix content of their own. Each commit was read against the C# under `dotnet/`, with
`todo.md` ("Known deviations", the open `727cc46..1a3169d` items), `SECURITY.md` and
`dotnet/docs/round3-findings.md` in view. This was done by reading code; nothing was
probed through the engine except where a row says so. The security items at the top
were checked again against the C# source before they were recorded here.

`.reference/obscura/` was advanced to `4eb1235` in the same change (see its
`UPSTREAM.md`), so the reference tree is now ahead of the port. Everything classified
**port** or **partial** below is still missing from the C#, and is queued in `todo.md`
under "Upstream sync 1a3169d..4eb1235". The items still open from the September review
stay queued where they were.

## Outcome

| Area | Commits | Port | Partial | Already | Conflict | Skip |
|---|---:|---:|---:|---:|---:|---:|
| Render / layout / paint (incl. vendored taffy) | 57 | 23 | 13 | 13 | 0 | 8 |
| CDP | 54 | 27 | 10 | 10 | 1 | 6 |
| JS runtime / web APIs, part 1 | 36 | 8 | 8 | 16 | 0 | 4 |
| DOM / JS runtime, part 2 | 44 | 15 | 8 | 15 | 0 | 6 |
| Page / navigation / frames / history / storage | 47 | 20 | 11 | 10 | 0 | 6 |
| Net / cookies / robots / SSRF / MCP / CLI | 41 | 23 | 6 | 6 | 1 | 5 |
| README, docs, CI, packaging, deny.toml, Cargo bumps | 40 | 0 | 0 | 0 | 0 | 40 |
| **Total** | **319** | **116** | **56** | **70** | **2** | **75** |

Merges with their own fix content are reviewed separately at the end ("Merges that
carry fixes").

## Security first

Ordered by severity. "Exposed" means the C# has the defect today.

1. **Cookie `Expires` parsing hangs a thread** (`8162c0f`), exposed, confirmed in
   source. `CookieJar.TryParseHttpDate` (`PocketCalculator.Net/Cookies/CookieJar.cs`,
   the `for (y = 1970; y < year; y++)` loop) takes the year as a `ulong` with no
   range check, so one `Set-Cookie` or `document.cookie` write with a huge year spins
   inside C# where no V8 interrupt reaches. Day 0 underflows and the final cast can
   wrap. Validate the RFC 6265 5.1.1 ranges before any arithmetic; port before
   `a134a7e`, which reuses the parser for `Last-Modified`/`Date`. Also: `Max-Age`
   near `long.MaxValue` overflows `Now() + secs`, and neither attribute is capped at
   Chromium's 400 days.
2. **Grid track redistribution can loop forever** (`281470c`, vendored taffy), exposed,
   confirmed in source. `Layout/Grid/TrackSizing.cs` `while (spaceToDistribute >
   Threshold)` has upstream's pre-fix shape and the file never calls
   `WorkCancellation`, so a stall in f32 rounding is an uninterruptible CPU hang in
   `serve`/`mcp`. Port the progress check, add a cancellation check, port the 3 tests.
   Related: `025088c` (`span 0` placements accepted by `grid-column`/`grid-row`).
3. **CDP commands that resume a suspended page run with no watchdog** (`e83d803`),
   exposed and worse than upstream because of page suspension. `Dispatch.cs` arms
   `CdpWatchdog` only when the session page already has an isolate; the handler then
   resumes the page and evaluates unbounded. Resolve the page (resuming it) before
   arming, also on the parked-command path.
4. **mXSS through serialized attribute values** (`8dbf7e1`, with `cf5067e`), exposed,
   confirmed in source. `DomTree.Serialize.cs` `EscapeAttr` escapes only `&` and `"`;
   Chromium also escapes `<`, `>` and U+00A0 (and U+00A0 in text). An attribute under a
   raw-text parent such as `<noscript>` reparses as live markup in `outerHTML`,
   `--dump html` and MCP output. `IsVoidElement` lacks `basefont`, `bgsound`, `frame`,
   `keygen`. `d45bee5`/`efb57d8`/`f2bf4c3`/`f152fcf` round out the serializer.
5. **An unparsable fetch URL skips the SSRF gate** (`5bfac24`), exposed, confirmed in
   source. `FetchOps.cs` runs `ValidateFetchUrl` only `if (UrlRecord.Parse(url) is {}
   ...)`, at the initial URL and at the intercept rewrite; a string our WHATWG parser
   rejects goes on to `System.Uri`, a different parser. Fail closed at both sites.
6. **Markdown extraction is exponential on nested tables** (`1ee3ac1`), exposed.
   `MarkdownScript.HtmlToMarkdown` converts each `<tr>` child twice (2^depth) and
   recurses without a bound.
7. **`style.setProperty` of an unchanged value queues a mutation** (`daac810`, merge
   `4724f4b`), exposed. A `MutationObserver` that re-applies the same style spins until
   the watchdog.
8. **Stale MCP element refs act on a different element** (`a449a80`), exposed.
   `BrowserState.RefToSelector` only checks the ref is in the table; refs are reused
   `e1..eN` per snapshot and copied by `cloneNode`.
9. **Failed or evicted images are re-fetched on every relayout** (`74ac75f`, `a3171f3`,
   `0d11d55`, `5efc4a1`), exposed: request amplification through the page transport.
10. **Unbounded caches in long-lived contexts:** robots cache (`0e17221`, no eviction, no
    body cap) and Web Storage (no total budget; upstream `677f6d2` caps 32 MiB per store
    and 256 origins).
11. **Cookie prefixes on import** (`b3475a5`, `7a8c0e3`, `fd5c729`): `Set-Cookie` and
    `document.cookie` enforce `__Host-`/`__Secure-`, but `SetCookiesFromImport` (CDP
    `setCookie(s)`, `cookies.json`, MCP storage restore) does not. `DomainMatches` still
    suffix-matches IP literals (defence in depth; not reachable from `Set-Cookie`).
12. **`confirm()` returns `true`** (`33aeb3f`): a non-interactive engine silently accepts
    destructive confirmations. Upstream now dismisses (false) when no client handles the
    dialog; check Chromium's headless behaviour with no `Page.javascriptDialogOpening`
    handler before porting.
13. **Lower:** frame realms run `op_script_try_start`/`mark_started` against the page's
    document (`9b05070`); `DOM.setFileInputFiles` reads files before validating the
    target (`0d8a200`); Fetch interception never pauses main-document loads (`b76d966`);
    the linked-stylesheet `load` script addresses the `<link>` by query index
    (`c8e5b9e`); WebCrypto `wrapKey`/`unwrapKey` ignore usages (`d853a73`); a detached
    iframe's timers live until the next release pass (`2f448c3`).

Not exposed, recorded so the next sync does not re-review them: Teredo (`733a85e`,
`SsrfGuard` refuses all of `2001:0::/32`), the standalone render loader SSRF (`e5904a6`),
CSS-sized raster bounds (`5bde9d4`, H7 caps), the two Rust panics in `eac01f5`,
`file://` gates in Page and the loader (`36c0aef`, `e217e99`), deep-DOM CDP stack aborts
(`62c3be6`, `StackGuard`), RCDATA serialization (`c1ffa73`), CDP object-id injection
(`eeed4e3`), pushState URL spoofing (`3549ead`), the deno `__bootstrap` alias (`0963795`;
port its every-global test as a guard), forbidden script request headers (`6342fff`),
srcdoc object publication (`17b5d7d`), cross-origin storage restore (`dcdc6d0`), cookie
store file modes (`1b259b9`; a failed save is still swallowed silently).

## Conflicts and decisions

- **`8a2f957` is not ported.** It accepts any loopback `Origin` on the CDP WebSocket, so
  any locally served page could drive the browser; the port refuses every browser
  `Origin` (CLAUDE.md, "Control planes are gated"). Upstream's follow-up `1faf17f`
  narrows it to `http://<request Host>/` plus an env allowlist; that is portable as
  `POCKETCALCULATOR_CDP_ALLOWED_ORIGINS`, keeping the Host check after it.
- **`05d7c13` (MCP keeps every tab's runtime) contradicts the port's tab suspension.**
  Chromium never suspends, so Chromium wins; decide it together with the open `d792bae`
  item from the September review (keep every CDP page's isolate live).
- **`9a3d12e` retires a recorded deviation.** Upstream now measures `measure_text` by the
  em square, which is what Chromium does. The port's `DomTextMeasure` reproduces Rust's
  old height-based scale on purpose ("DomTextMeasure reproduces ab_glyph's height-based
  scale" in `todo.md`); the reason for that deviation is gone, so switch to em scale and
  remove the deviation.
- **`bootstrap.js` in the reference tree is now upstream's verbatim.** The fork's early
  edits to it (canvas 2D, the SPA fixes, innerText, fragment navigation) conflicted with
  upstream rewrites of the same regions, so the reference copy was taken whole from
  upstream. Those fixes live in the port's own copy. Parity runs that exercise canvas or
  fragment navigation against a reference binary rebuilt at `4eb1235` therefore show
  differences that are upstream's, not regressions.

## Per-area reviews

Each table has one row per commit in upstream order. Classes: **port** (missing in C#),
**partial**, **already** (C# has it; cited), **conflict**, **skip** (cannot apply).
Priority is H/M/L. File paths are relative to `dotnet/src/` unless they say otherwise.

### Render, layout and paint


**Count: port 23 / partial 13 / already 13 / conflict 0 / skip 8.**

I read the code but did not run anything. "Unverified" means the classification comes from code reading only, and the upstream test input should be run through the C# engine before the item is closed. The C# paths below are relative to `dotnet/src/PocketCalculator.Render/` unless they say otherwise.

#### Security

1. **`281470c`, grid track redistribution can loop forever. C# is exposed today. (H)**
   - `Layout/Grid/TrackSizing.cs:1413` `DistributeSpaceUpToLimits` matches upstream's pre-fix loop line for line.
   - When f32 rounding absorbs every increase, `spaceToDistribute` never drops below `Threshold` and the loop never exits.
   - The loop never calls `WorkCancellation.ThrowIfCancellationRequested()`, so the watchdog cannot stop it.
   - The result is an uninterruptible CPU hang inside a layout op in `serve` and `mcp`. Only the CLI's process-level hard deadline would end it.
   - Fix: port the progress check, add a `WorkCancellation` check to the loop, and port the 3 upstream unit tests.
2. **`025088c`, `span 0` and line `0` grid placements are accepted. (M, unverified)**
   - `Style/StyleGrid.cs:1266` accepts `span >= 0`, and `grid-column` / `grid-row` and their longhands do no validity check (`SetGridPlacement`, `StyleGrid.cs:1075`). Only `grid-area` validates.
   - In Rust this aborted the process (Shopify themes reach it through `span var(--n)` with the variable at 0).
   - In C#, `TrackSlice.Range` (`Layout/Grid/GridTrack.cs:169`) yields `Count == -1` with no bounds check. That probably does not throw, but the placement is wrong.
   - Auto-placement with a zero span has not been checked for a hang.
3. **`74ac75f` / `a3171f3`, failed and evicted images are fetched again on every relayout. (L-M)**
   - `Paint/RenderResourceCache.cs:106` sets `MissingResourceRetryAfter` to 2 s, and the cache keeps no outcome for an evicted entry.
   - A page with broken images, or with more images than cache entries, plus a periodic layout reader, re-requests those URLs indefinitely. That is request amplification through the page transport. C# is exposed.
4. **Not exposed in C#:**
   - `e5904a6` (SSRF in the standalone render loader): already fixed. `ImageAgent` goes through `PocketCalculatorHttpClient` with `SsrfGuard` and a connect-time check (`Paint/RenderResourceCache.cs:829-900`).
   - `5bde9d4` (a CSS-sized image or mask raster allocating GBs): mitigated by the H7 fixes, namely `Pixmap.New` capped at 64M pixels, a windowed untransformed raster and `DecodeWithinBudget`. What remains is correctness only.
   - `eac01f5` (two Rust panics): neither applies. UTF-16 slicing is safe in `PocketCalculator.Browser/PageHelpers.cs:1020`, and the viewBox parse requires 4 parts (`Paint/PaintSvg.cs:247`).

#### Commits

| sha | subject | class | C# evidence / what to port | priority |
|---|---|---|---|---|
| bf10c46 | apply word spacing to text layout and paint | port | No `word-spacing` anywhere in Render (only an SVG presentation-attribute name, `Paint/PaintSvg.cs:785`). Add a `LayoutStyle.WordSpacing` field plus a raw value resolved after font-size, inheritance, a HarfBuzz advance on U+0020/U+00A0 in `Inline/TextShaping`, `DomTextMeasure.TextWidth` and `PaintText.DrawText`, and `@supports`. Chromium also spaces other word separators. | M |
| c9978c3 | media query changes + reduced-motion emulation | port | `Emulation.setEmulatedMedia` falls through to `{}` (`PocketCalculator.Cdp/Domains/Emulation.cs` default arm). `Css/CssMediaQuery.cs:108` and `bootstrap.js:12094` hard-code no-preference. `MediaQueryList` never fires `change` (`bootstrap.js:24894` says so). Port the change delivery on viewport or preference change (`__obscura_recompute_media_queries`), the reduced-motion state through to the CSS media type, and validation. | M |
| be2502d | portable reduced-motion rendering evidence | skip | render-repros fixture plus a Python capture script. Copy `reduced-motion-emulation.html` to the root `render-repros/` together with c9978c3. | L |
| df5ee65 | ignore transforms on ordinary inline boxes | port | `Dom/DomTransforms.cs:47-51` (`ResolveClipRects`) and `Dom/LayoutDomOnce.cs:1349` apply `transform` to non-replaced inline boxes. Gate both with `style.IgnoresUsedBoxSizes()`, as Chromium does (a transform does not apply to a non-atomic inline). | M |
| 5bde9d4 | bound image and mask rasters that CSS sizes | partial | No DoS (see Security). Huge destinations that are transformed, SVG (`SvgRenderer.Render(bytes, dw, dh)`, `Paint/PaintImages.cs:846`) or masks (`Pixmap.New(boxWidth, boxHeight)`, `Paint/PaintImages.cs:1006`) now hit the 64M cap and paint nothing. Port `bounded_raster_size` with a bilinear upscale on draw, and the recolor window for masks. | L |
| f019ebe | checkVisibility() + inherited computed visibility | partial | Computed visibility is already inherited (`ComputedVisibilityHidden`, `Paint/PreparedRender.cs:1095`). `checkVisibility()` is still the `return true` stub (`bootstrap.js:6931`). Port it: a `check_visibility` op over the prepared render (flat-tree ancestors with `display:none`, `display:contents` on self, `opacity` and `visibility` options with their legacy aliases, disconnected), plus the inline-style fallback. | M |
| b210d15 | avoid CDP reply stalls and redundant DOM traversal | already | Render part: `Dom/DomCascade.cs:714-716` already reads `tree.Count` only for slotted nodes. The selector change is a Rust borrow-cost fix (skip). The CDP I/O-thread split and the microtask-only checkpoint for a resolved eval were not reviewed here; leave them to the CDP group (unverified). | L |
| e5904a6 | SSRF deny-set for the renderer's standalone HTTP | already | `Paint/RenderResourceCache.cs:829-900`: `ImageAgent` uses `PocketCalculatorHttpClient(allowPrivateNetwork:false)` and `SsrfGuard.ValidateUrl`, with a check on every hop and at connect time. | - |
| 542df14 | Playwright compatibility and engine performance | partial | Already in C#: `op_computed_style_static` (the style-only CSSOM read), the load event not reaching window (`bootstrap.js:2864`). Missing: explicit `contain: size / inline-size / strict` (`Style/ComputedStyle.cs:3269` only sets the CB trigger, and `ApplyContainerSizeContainment` covers `container-type` only). Unverified: empty grid tracks contributing scrollable overflow (taffy `end_offset`), overflow across `display:contents` wrappers. Run `render-repros/contained-flex-grid.html` and `sparse-grid-scroll.html`. The CDP / JS / DOM parts belong to other groups, and the Rust retained-structure performance work is skip. | M |
| 30e8d7a | stop transform measurement layout churn | partial | Already: `offsetWidth/Height` are untransformed (`op_layout_offset`, `bootstrap.js:6646`), and the `load` path. Performance: a transform-only inline style write still invalidates layout (`Transform` is not in `PaintOnlyMembers`, `Dom/RetainedLayoutReuse.cs:90`). Benchmark a Swiper-like loop before porting. | L |
| abf247b | idle polling + reuse unchanged rendering work | partial | Mostly Rust-only retained and caching structures (skip). The taffy leaf change (no ratio transfer when both axes are definite) is effectively already there (`Layout/GeometryExtensions.cs:336-345` transfers only when `preferred` is null). Worth benchmarking: C# decodes and resizes every raster on every paint (`RasterToPixmap`, `Paint/RenderResourceCache.cs:1140`), so a static-raster paint cache (skipping GIF, WebP and APNG) may pay. The browser idle-deadline part was not reviewed. | L |
| 160c044 | vendored taffy Style size expectations | skip | Rust `assert_type_size` only. | - |
| b219e52 | exclude bottom/right hit-test edges | already | `Paint/HitTest.cs:607` uses half-open `x < X + Width`, `y < Y + Height`. | - |
| f8a36a4 | legacy font size hints + keyword scale | port | `Style/StylePrimitives.cs:1189` still has 9.6/12/13.3 and no `xxx-large`. Chromium uses 9/10/13/16/18/24/32/48. There is no `<font size>` presentational hint (the HTML legacy-font-size rules). Add it to `ApplyPresentationalHints`, and add `font`+`size` to the retained attribute list (`Dom/RetainedStyle.cs:323`). | M |
| 4e82527 | inherit font shorthand on native controls | already | `Style/ComputedStyle.cs:1261` (`ApplyFontShorthandKeyword` clears size, family, weight and line-height for `inherit`/`unset`). | - |
| 6234ed3 | intrinsic size for stretched grid controls | partial | `Dom/DomStyleFixups.cs:1161` (`AssignNativeControlSize`) skips the stretched axes, and publishes `NativeControlContent` only for a percentage width. An auto-width stretched control contributes no intrinsic size (unverified). The upstream test (`width:100%`) may already pass. Extend `NativeControlContent` to stretched axes with independent axes and no ratio. | L |
| a41696e | preserve absolute inline static positions | port | `Dom/DomBuildMixed.cs:237-241` appends absolutely positioned children after the flow, so an auto-inset abs box whose original display was inline gets a block static position at the end. Port the zero-size inline placeholder and `static_position_inline`, and feed it into `StaticPositionCandidate` (`Dom/DomPasses.cs:560`). | M |
| 0e99b3a | % heights across anonymous inline wrappers | port | `Dom/DomPasses.cs:140` `ResolveAtomicPercentageHeights` still requires a float or inline-block parent. Add upstream's block-parent arm (not multi-column, taffy parent is an anonymous wrapper). | M |
| a430ca5 | caret positions from rendered text geometry | port | No `caretRangeFromPoint` / `caretPositionFromPoint`, only null stubs in a frame realm (`bootstrap.js:26427`). Needs `op_layout_caret` and `op_layout_caret_rect` over shaped runs with UTF-16 offsets. C# offsets are already UTF-16, so it is simpler than upstream. | L |
| 62e8e7f | preserve mixed inline owner geometry | already | C# flattens decoration-free inlines but synthesizes their rects (`Dom/LayoutDomOnce.cs:1217`, `SynthesizeSplicedInlineRects`). Unverified against upstream's test. It relates to the open todo item "inline span reports width 0". | L |
| c7ec60a | honor color-scheme media overrides | port | C# answers `prefers-color-scheme` as light only (`Css/CssMediaQuery.cs:100`, `PocketCalculator.Js/Ops/LinkedStylesheetLoader.cs:209`, `bootstrap.js` `_evaluateMediaFeature`). Port together with c9978c3 as one `setEmulatedMedia` with all-or-nothing validation, persistence across navigations, and print with dark. Also consider `media: "print"` emulation, which is also ignored today. | M |
| 361f15d | cancel pending loads and honor beforeunload | port | No `beforeunload` dialog on `Page.close`, no `Page.handleJavaScriptDialog`, and `stopLoading` does not cancel anything (no hits in PocketCalculator.Cdp or Browser). Render part: a document-scoped "stopped" set in `RenderResourceCache`. Mostly CDP-led. | M |
| 35b9448 | avoid repeated computed-style work | partial | C# has its own retained static computed-style path (`PocketCalculator.Js/Ops/RenderOps.cs:611`). It still serialises every custom property per element snapshot (`ComputedCustomProperties`, lines 638-668). Benchmark upstream's 1024 rows x 2500 variables test, and consider lazy custom properties if it is slow. | L |
| a2bfd32 | redundant layout + variable-map retention | skip | Follow-up to Rust's `NonGeometricStyleCache`, which C# does not have. | - |
| a59ea06 | mutations/observer records per DOM standard | partial | DOM/JS part (≈1.5k lines of bootstrap) belongs to the JS group. Render part: C# gives an inline-block with only block children real block layout only for a definite width (`Dom/DomBuildCore.cs:553-569`), and auto width keeps the flex-row stand-in. Run `render-repros/inline-block-block-children.html` (expected 144x74 shrink-to-fit). | M |
| 3c56f59 | bounded actionable MCP snapshots | port | MCP wire change: the `browser_snapshot` description and `max_chars` semantics, control lines, the 256-char URL/title cap, and the "controls omitted" line (port byte for byte). Render part: an `accessibility_styles_without_layout` helper (optional, perf). Mostly the MCP group's. | M |
| 3085f5a | functional flex-basis vs containing block | already | `Style/StyleTransform.cs:788`, `Core/LayoutStyle.cs:1521` (deferred flex-basis expression). | - |
| 423e58a | float keeps % width with % margin | already | Unverified. C# floats are CSS floats (`BuildMixedBlock` floatFlow) and do not use the native float-band path. Run `render-repros/float-percentage-margin.html`. | L |
| b60536d | actually run functional-flex-basis test | skip | Rust test wiring only. | - |
| c280615 | five longhands + cssFloat in getComputedStyle | partial | `border-collapse` is present (`Paint/PreparedRender.cs:1052`). `vertical-align`, `text-transform`, `list-style-type` and `table-layout` are missing from the snapshot. `cssFloat` maps to `css-float` through `_cssCamelToKebab` (`bootstrap.js:2005`), for both inline and computed style. | M |
| 9a3d12e | scale measure_text by em square | port | C# reproduces Rust's old height-scaled advances on purpose (`Dom/DomTextMeasure.cs:136`, deviation `todo.md:5079` "DomTextMeasure reproduces ab_glyph's height-based scale"). Upstream now matches Chromium (em-based), so the reason for the deviation is gone: switch `TextWidth`/`MeasureText` (used for `<select>`, word leaves and markers) to em scale, recheck `PaintText.DrawText`, retire the deviation and `DomTextMeasureScaleTests`. Chromium values in the upstream test. | M |
| d40f503 | report insets as insets | already | C# is better: resolved used insets, as Chromium reports them (`Paint/PreparedRender.cs:1468`, `UsedInsets` at 1996). Upstream reports `auto` for a static box even when `top:5px` is declared, where Chromium returns `5px`. Do not take upstream's version. | - |
| 6a7bbf3 | display:none float must not widen parent | port | `Dom/DomBuildCore.cs:516-523` counts `display:none` floats in `hasFloatChild`, which sends the container down `BuildMixedBlock(floatFlow:true)`. Add `childStyle.Display != Display.None`. The width effect is unverified. | L |
| b223a2c | cyclic inline size behaves as auto | partial | Expressions already neutralise to `auto` (`Dom/DomPassesSubgrid.cs:617-646`). The native-control part is missing: a control with a cyclic `calc()` width gets no intrinsic fallback (`native_control_intrinsic_width`). Unverified. | L |
| 3fb9e3c | keep picture inline; transparent bg flattening | already | `<picture>` is inline (`Style/ComputedStyle.cs:100`). The transparent-background flattening was reverted upstream by f57610b, and C# matches the final state (`Dom/DomBuild.cs:717` `BackgroundColor is null`). | - |
| 2b61abf | exclude hidden rows from table layout | port | `Dom/DomTableSupport.cs:894` `CollectTableRows` ignores `display:none` rows and sections, so they still create tracks, rowspan slots and width. Pass `styles` and skip `Display.None`. | M |
| 87b79ee | render-repros zero-count assertions | skip | Python `check.py` harness. | - |
| 7bf782b | honor table row and group display | partial | CSS table structure exists (`CollectCssTableStructure`, `Dom/DomBuildTable.cs:680`). Native rows are still collected by tag name, not by used display (no `table_row_display`), so `display:table-row` restoring a hidden row and authored block display on rows differ. Port together with 2b61abf. | M |
| 541984c | metadata retain with shadow roots | already | C# plans shadow sheets in its retained style (`Css/CssInvalidation.cs:364-386`, `AddShadowDamage`). Check that a functional `:host(...)` is keyed (unverified). | L |
| 7c20597 | update compression deps | skip | Cargo / vendored wreq only. | - |
| 32fb98f | invalidate attribute-derived retained content | partial | Stylesheet `attr()` dependencies are recorded (`Css/CssInvalidation.cs:915`). Unverified: inline-style custom properties (`style="--l:attr(data-x)"` read through `var()`) and `attr()` names supplied by `var()` (the dynamic-read fallback). Run upstream's `retained_content_tracks_attributes_read_through_inline_custom_properties` cases. | L |
| eac01f5 | two panics on @import and short viewBox | already | `PocketCalculator.Browser/PageHelpers.cs:1020` (UTF-16 span, no byte slicing), `Paint/PaintSvg.cs:247` (`parts.Length == 4`). | - |
| 025088c | ignore grid placements with span 0 | port | See Security 2. Validate `grid-column`/`grid-row` and the 4 longhands (zero or negative span, line 0, part count) so the declaration is dropped. Reuse the `grid-area` check (`Style/StyleGrid.cs:1053`). | M |
| 281470c | stop grid redistribution when FP stalls | port | See Security 1. `Layout/Grid/TrackSizing.cs:1424`. | H |
| 66da859 | clear release build warnings | skip | Rust warnings only. | - |
| 2c81268 | hide closed dialogs via UA cascade | already | `Dom/DomCascade.cs:677-685`, retained attribute `Dom/RetainedStyle.cs:325`. | - |
| 74ac75f | stop refetching evicted/failed images | port | See Security 3. Add a document-scoped mode (set where `SetSyncLoadingEnabled(false)` is set), keep failures for the life of the document, and remember evicted outcomes (intrinsic metadata) in a bounded map. | M |
| a3171f3 | retain failed image attempts per element | port | Refines 74ac75f: the failure belongs to the element's request (generation, node identity, slot reuse), so a new `<img>`, or CSS `content:` with the same URL, may retry. Port together. | M |
| 0d11d55 | reset image attempts for data source selection | port | Small follow-up to a3171f3 (`requests.forget(node)` when selection moves to a `data:` URL). | M |
| f3bfec4 | retain transparent inline boxes for animations | skip | Reverted upstream by f57610b. | - |
| f57610b | limit picture change to inline display | already | Net upstream state equals C#'s (see 3fb9e3c). | - |
| 9f64ce9 | tolerate float fit rounding at row boundaries | port | `Layout/FloatLayout.cs:68` and `:110` compare `>= 0.0f`. Add the tolerance `min(\|bfcWidth\| * float.Epsilon (f32 epsilon), 1/1024)`. Fixes Bootstrap offset columns wrapping. Port both upstream tests. | M |
| 1fff1f7 | clip and centre native control text | port | `Paint/PaintDom.cs:1550-1600` top-aligns the input value and placeholder (`textY = rect.Y + padding + border`) and clips only to the ancestor `boxClip`. Port the content-box horizontal clip for `<input>`, the padding-box clip for `<textarea>`, and vertical centring with the font height. | M |
| 3155d6a | intrinsic sizing for percentage flex bases | port | `Layout/FlexboxLayout.cs:703-730` (`definiteBasis = flexBasis ?? mainSize`) and `:963-1041` (clamping basis, shrink factor) have neither this nor its predecessor ("only auto retrieves the authored main size"). The predecessor arrived through merge commits fded066 and 322f46c, which are outside the non-merge list. Port the merges' render diffs (vs their second parent) first. | M |
| a04e024 | retain fixed math bases in intrinsic rows | partial | C# has no `definite_row_flex_intrinsic_width` (the earlier review found that behaviour already present by probe). Unverified for `flex: 0 0 calc(300px)` / `min(300px,400px)`. Run the upstream test. | L |
| 1b6f423 | root gutters in computed insets | port | `Paint/PreparedRender.cs:2168` (`ContainingBlockBox`) falls back to the full viewport. Use the initial containing block reduced and shifted by `scrollbar-gutter` (as `Dom/LayoutDomOnce.cs:427-438` does), with vw and vh unchanged. | M |
| 7b632d9 | sticky insets from scroll content | port | `UsedInsets` returns null for sticky (`Paint/PreparedRender.cs:2003-2007`), so `getComputedStyle` reports the declared `10%`. Chromium resolves it against the scroll container's content box. Paint: `Dom/StickyLayout.cs:209-216` uses the scrollport size as the percentage basis, and upstream subtracts the scroller's padding (unverified whether C#'s scrollport is the padding box). | M |

The upstream non-merge list misses some render changes that landed only through merge commits titled as fixes. Diff each against its second parent:

- `fded066` (functional flex bases on the main axis: taffy `flexbox.rs` and `style.rs`), needed before 3155d6a.
- `322f46c` (intrinsic sizes for cyclic flex widths, `dom.rs`).
- `d04b661` (computed insets from containing blocks). C# already has its own version (`UsedInsets`).

#### Suggested port order

1. **`281470c`**: the grid redistribution hang. Add a `WorkCancellation` check to the same loop.
2. **`025088c`**: grid placement validity. Check auto-placement with a zero span for hangs at the same time.
3. **`74ac75f` → `a3171f3` → `0d11d55`**: document-scoped image outcome retention, as one series.
4. **`c9978c3` + `c7ec60a` (+ the `be2502d` fixture)**:
   - `Emulation.setEmulatedMedia` (features and media).
   - `MediaQueryList` change events.
   - Reduced-motion and dark preferences in the CSS media type.
5. **`f019ebe`**: `checkVisibility()` op.
6. **Computed-style batch**: `c280615`, `1b6f423`, `7b632d9`.
7. **Table and float layout**: `2b61abf` + `7bf782b`, `9f64ce9`, `6a7bbf3`, `0e99b3a`, `a41696e`, `df5ee65`, then the render part of `a59ea06`.
8. **Flex**: the merge content of `fded066` / `322f46c`, then `3155d6a`, `a04e024`, `b223a2c`, `6234ed3`.
9. **Text**:
   - `bf10c46` word-spacing.
   - `f8a36a4` keyword scale and `<font size>`.
   - `9a3d12e` em-based `DomTextMeasure`, which retires the `todo.md` deviation.
   - `1fff1f7` native control text.
10. **CDP/MCP-led**: `361f15d` (beforeunload, stopLoading, stopped resources) and `3c56f59` (MCP snapshot wire text). Coordinate with those groups.
11. **Features**: `contain: size` from `542df14` (also check the grid empty-track scroll extent), and `a430ca5` caret APIs.
12. **Performance, benchmarked before and after** (interleaved, as CLAUDE.md requires): the static raster paint cache (`abf247b`), transform-only style writes (`30e8d7a`), custom properties per snapshot (`35b9448`), and the bounded scaled raster for huge destinations (`5bde9d4`).

### CDP


54 commits: **port 27 / partial 10 / already 10 / conflict 1 / skip 6**.

This is based on reading the code only. Nothing was run. Paths are relative to `dotnet/` unless they say otherwise. "Unverified" marks the classifications that depend on runtime behavior this review could not observe.

#### Security

1. **`e83d803`: commands that resume a suspended page run without a watchdog (exposed, worse than upstream).** `Dispatcher` arms `CdpWatchdog` only when `ctx.GetSessionPage(req.SessionId)?.IsolateHandle` is non-null (`src/PocketCalculator.Cdp/Dispatch.cs:157`). With "Page suspension stays", every page except the active one has `Js == null`. The handler then calls `GetSessionPageMut`, which runs `ResumeJs()` (`CdpContext.cs:934-972`), and the evaluation that follows has no budget. So a `Runtime.evaluate` / `callFunctionOn` on a second tab hangs the connection forever if it loops. That includes a loop inside a page getter that the client's expression happens to read. Fix: resolve the page with `GetSessionPageMut` before arming, for `Runtime.evaluate`, `Runtime.callFunctionOn` and `addScriptToEvaluateOnNewDocument{runImmediately}`, and apply the same in the parked-command path (`Dispatch.cs:334`).
2. **`8a2f957` / `1faf17f`: CDP `Origin` policy. The C# is not exposed.** `GetControlRefusal` refuses any `Origin` (`Server.Control.cs:224`). `8a2f957` accepted every loopback origin on any port, which would let any locally served page drive the browser. Do not port it. `1faf17f` replaces it: accept only `http://<request Host>/` exactly, or an entry in an env allowlist. Port that as `POCKETCALCULATOR_CDP_ALLOWED_ORIGINS` (naming rule). Keep the Host check after it, because that check is what stops DNS rebinding. The 403 body gains a `detail` field. That body is a wire surface, so decide whether the env name in that text keeps Obscura's name or uses the port's.
3. **`b76d966`: Fetch interception never sees main-document navigations (exposed as a policy bypass).** A client that uses `Fetch.enable` patterns to block or rewrite top-level navigations gets no pause, and the request goes to the network. Page navigation has no interception hook (`src/PocketCalculator.Browser/Page.Navigation.cs`; interception is wired only into the JS runtime, `Page.cs:533-540`). The CDP client is trusted, so this is not an escalation, but it breaks a control the client believes it has.
4. **`0d8a200`: `DOM.setFileInputFiles` reads files before checking the target (low).** `Dom.cs:317-377` reads every path, then calls `__obscura_host.setInputFiles` on any node. `_setInputFiles` (`bootstrap.js:14450`) does not check `input[type=file]`, ignores `multiple`, swallows failures, and fires trusted `input`/`change` on whatever element it was given. The flag gate (`--allow-file-access`) is already in place. Missing: validate first, then read; reject non-string paths; slice to one file without `multiple`; return CDP errors.
5. **Already fixed in C#, recorded so the next sync does not re-review them:** `36c0aef` (`file://` gate inside Page, SECURITY.md H1-H3, `Page.Navigation.cs:132,995`), `e217e99` (`file:` gated in the loader, C4, `PocketCalculatorHttpClient.cs:696`), `62c3be6` (deep-DOM stack aborts, C5, `StackGuard`, `tests/PocketCalculator.Cli.Tests/DeepTreeTests.cs`), `c1ffa73` (RCDATA serialization mXSS, M9, `DomTree.Serialize.cs:247`).

#### Commits

| sha | subject | class | C# evidence / what to port | priority |
|---|---|---|---|---|
| a74e050 | Emit document replacement lifecycle for Playwright setContent | partial | CDP side already: `document_close` -> `ScriptDocumentLoads` -> `Dispatcher.DrainDocumentLoads` (`DomOps.cs:786`, `Dispatch.cs:697`, deviation commented at `bootstrap.js:8206`). Missing in-page: `open()` does not clear `<head>`, so `document.title` keeps the old title (unverified). `readyState` is not set back to `loading`. `close()` dispatches no `readystatechange` / `DOMContentLoaded` / window `load`. Port the generation-guarded setTimeout sequence into `Document.close()`. | M |
| b1b3abe | decode the base64 postData a continueRequest carries | port | `ServerSupport.HandleFetchResolution` (`ServerSupport.cs:356-363`) and `Fetch.cs:161` pass `postData` through raw. `FetchOps.cs:550` then UTF-8-encodes the base64 text. | H |
| 3f9acc2 | carry the decoded body as bytes, refuse invalid base64 | port | Make `InterceptResolution.Continue` / `FetchResolution.Continue` carry `byte[]?`, and add a strict decoder (replacing the lenient `ServerSupport.DecodeBase64`, `ServerSupport.cs:79`) for postData and the fulfill body. Answer -32602 naming the field, and build the resolution before `interceptedPaused.Remove` so a bad command does not consume the pause. | H (with b1b3abe) |
| 36c0aef | enforce the file:// navigation gate in Page itself | already | H1-H3: `NavigationPolicy.RefusesPageInitiated` in `Page.NavigateWithWaitPostAsync` / `ProcessPendingNavigationOutcomeAsync`; MCP `Tools.RefuseFileNavigation` covers tab_new/back/forward/reload (`Tools.cs:60-80`). | - |
| d88df74 | bypass author accessors during native activation | partial | CDP click already uses bootstrap's captured accessors (`h.get/h.set`, `Input.cs:502-535`, `_hostDom` `bootstrap.js:25906`). `Element.click()` (`bootstrap.js:5689-5727`) still reads and writes `this.checked` / `this.indeterminate` through page-overridable accessors. Switch it to internal `_inputChecked` / `_setInputChecked` helpers. | M |
| 3f8a8d1 | lifecycle subscriptions independent of Runtime | port | `Page.setLifecycleEventsEnabled` is a no-op (`Page.cs:1241`). `DrainDocumentLoads` sends to every session. Add `LifecycleEnabledSessions` (cleared in `RemovePage` / detach), validate the boolean, and send `document.open` lifecycle events only to subscribers. Chromium sends `Page.lifecycleEvent` only after enable. | M |
| de227e9 | route lifecycle subscriptions through stateful dispatch | port | Remove `Page.setLifecycleEventsEnabled` from the fast path (`ServerSupport.cs:139`) and from the stateless list (`Dispatch.cs:46`). | M (with 3f8a8d1) |
| b8586de | deliver target network events to observer sessions | port | `Page.EmitNavigationEvents` / `EmitRuntimeNetworkEvents` (`Page.cs:216,524`) send only to the driving session. Fan the three Network events out to the other sessions on the target. Better than upstream: send only to sessions that called `Network.enable`, as Chromium does. | M |
| a94d571 | record script network events and bodies (stealth) | already | C# has one transport, so `FetchOps.cs:1108-1131` records events and bodies on every path. There is no separate stealth branch. | - |
| 5e58c86 | service routed requests during awaited evaluations | partial | Already in place: `ParkedCommand` (`ParkedCommands.cs`) keeps the processor loop servicing `interceptRx` and `HandleFetchResolution` while an awaited eval is pending (unverified under Fetch). Missing: `Network.loadingFailed` (`net::ERR_<REASON>`, `canceled` when Aborted) on `Fetch.failRequest`. C# never emits `loadingFailed` anywhere. | M |
| 318b57f | yield between ready page tasks and network work | skip | tokio LocalSet starvation. The C# HTTP work runs on the thread pool. The upstream test (MessageChannel busy loop plus fetch) is still worth porting as a regression check (unverified). | L |
| c714042 | clear removed focus; scroll nested containers | port | `activeElement` (`bootstrap.js:8059`) returns `_getFocused()` even after the element is removed. Removal never clears focus. `scrollIntoView` (`bootstrap.js:6951`) moves only the window, not scrollable ancestors. `DOM.scrollIntoViewIfNeeded` calls `scrollIntoView` (start alignment) rather than if-needed (`Dom.cs:297-306`). Check in Chromium whether blur/focusout fire on removal before porting the "no blur" assertion. | M |
| 9960967 | publish navigation frames before load completion | port | `Server.Navigation.cs:353` queues the screencast frame after `EmitNavigationEvents`, so it lands after `loadEventFired`. Queue it before the load phase inside `EmitNavigationEvents`, and drop `Page.navigate/reload/navigateToHistoryEntry` from `CommandCanChangeScreencastFrame` (`Page.Capture.cs:818`). | L |
| 8bb46be | report V8 heap usage and collect garbage | port | No `Runtime.getHeapUsage` and no `HeapProfiler.collectGarbage` (HeapProfiler is a blanket no-op, `Dispatch.cs:214`). Map them to ClearScript `GetRuntimeHeapInfo()` / `CollectGarbage(true)`. Must not run page tasks. | L |
| 7940526 | bind redirected navigation responses to the final URL | already | `RecordDocumentNetworkEvent` (`Page.Network.cs:44`, `isNavigation:true`, with redirects), and `MainDocumentIndex` / `EmitDocumentRequests` in `Page.cs`. Already a documented fix against Chromium. | - |
| 4291b71 | gate Unix readiness file permissions | skip | Rust cfg(unix) build fix. C# `serve` has no `--ready-file` at all (track under the CLI group if wanted). | - |
| d132880 | honor beforeinput when inserting text | port | `InsertTextJs` (`Input.cs:50-73`) has no `beforeinput` (cancelable InputEvent), no readonly/disabled guard, and sends a plain `Event('input')` rather than `InputEvent{data,inputType:'insertText'}`. Also missing: the IME-commit caret rule for `Input.insertText`. Bundle with the open `ec62004` / `a156914` input items. | H |
| aa4b4e7 | honor canceled keydown before editing defaults | port | The keydown snippet (`Input.cs:344-351`) discards the `dispatch` result, so a cancelled keydown still inserts text, Backspace, and Enter. | H |
| 3f7cccd | guard readonly deletion and textarea newlines | port | Neither `BackspaceJs` nor `EnterJs` checks `readonly` (no matches in `Input.cs`). The Delete key and keypress handling that upstream guards are not in C# at all (earlier open input commits). | M |
| 34346af | snapshot intercepted-request observer routes | port | Builds on b8586de and 5e58c86. The intercepted `requestWillBeSent` should go to the requesting session plus observers (`Server.Processor.cs:342` `HandleInterceptedRequest` uses `live.SessionId`). | M (with b8586de) |
| 29cce7d | test: restore module buffer env settings | skip | Rust test hygiene. | - |
| 192561c | removeScriptToEvaluateOnNewDocument errors on unknown id | partial | Owner scoping is already done (`CdpContext.PreloadScripts` Owner, `Page.cs:1257-1261`). Missing: unknown or already-removed id returns "Script not found", with removal also limited to the caller's own target. Empty sources are not stored (`Page.cs:1259`), so their id cannot be removed. `navigateToHistoryEntry` (`Page.cs:1399`) does not refresh `SetPreloadScripts`. | M |
| 875c531 | return freed heap to the OS while workers stay busy | partial | `RemovePage` (`CdpContext.cs:595-636`) leaves `NavEventsEmitted` and `BindingSessions` entries behind. Port that cleanup. Skip the glibc `mallopt`/trim part: Rust-specific, though V8/Skia native heaps could benefit (measure first; relates to open `af955b3`). | L |
| 48953ef | read callFunctionOn objectId before value | port | `BuildArgs` (`PocketCalculatorJsRuntime.Cdp.cs:757`) tests `value` first, so go-rod's `{"value":null,"objectId":..}` passes null. | M |
| 28756b4 | accept objectId in DOM.getOuterHTML | port | `Dom.cs:125-133` accepts only nodeId/backendNodeId. Use `ResolveNodeId` plus `DocumentOf` / `WithFrameDom` so a child-frame handle reads its own DOM (c05cf3b). | M |
| e5bcd75 | implement Page.close and Page.stopLoading | port | Both go to "Unknown Page method" (`Page.cs:1553`). `Page.close` should share the `Target.closeTarget` teardown (detachedFromTarget plus targetDestroyed). `stopLoading` acks. | M |
| 939086a | acknowledge Page.setBypassCSP | port | Missing, so Playwright `bypassCSP:true` contexts fail at `new_page()`. C# enforces no CSP. | H (one line) |
| e70183d | ack setBypassCSP (validate `enabled`) | port | Same handler; require a boolean `enabled`. | H (with 939086a) |
| dc07e2e | test: retain network error state in nav fixture | skip | Rust test fixture. | - |
| 183362f | claim busy heap trims atomically | skip | Follow-up to the glibc trim in 875c531. | - |
| a15617c | remove shadowed CSP bypass handler | skip | Rust cleanup of a duplicate arm. | - |
| e217e99 | gate file:// reads inside the loader | already | `SsrfGuard.ValidateUrl(url, ..., SsrfGuard.FileAccessAllowed(request))` before `FetchFileUrlAsync` (`PocketCalculatorHttpClient.cs:696-705`, C4). Gated on the initiator, where upstream gates on the flag. The flag gate stays at the CDP/MCP entry points. | - |
| 8fe6d99 | activate summary clicks to toggle details | port | No `<summary>` activation at all: neither `Element.click()` nor the CDP click snippet (`Input.cs:540+`) has a details path. | M |
| 717dd92 | support light-DOM node queries | partial | Already: `DOM.requestNode`, `getNodeForLocation`. Missing: `DOM.requestChildNodes`, `DOM.setChildNodes` pushes (per-session sent-children state), `DOM.documentUpdated` on navigation, and `DOM.disable`. Attributes serialize `Name.Local` rather than the qualified name (`Dom.cs:765`). | M |
| 2e0b63f | queue details toggle on open changes | port | Coalesced `ToggleEvent('toggle')` on any change to `<details open>` (setAttribute / removeAttribute / NS variants / IDL). Only popovers fire toggle today (`bootstrap.js:5850-5924`). | M (with 8fe6d99) |
| 17dcbe1 | allow CDP commands while evaluations await promises | already | `ParkedCommand` / `CommandParkedException` (`ParkedCommands.cs`, `Runtime.cs:540`): Chromium-style out-of-order answers, added independently. | - |
| 3a4a0b5 | store response bodies as raw bytes, decode on read | port | Same cost in C#: `StoreResponseBody` base64-encodes or UTF-8-decodes at store time (`Page.Network.cs:137-140`). `StoredResponseBody(string, bool)` (`Page.cs:58`) and `StoredNetworkResponseBody` (`PocketCalculatorState.cs:665`) should hold `byte[]`. Encode in `Network.getResponseBody`. `takeResponseBodyAsStream` gets bytes directly. Benchmark old vs new. | L |
| 7034ad6 | scope initialization scripts to each target | already | Owner-scoped `PreloadScripts` / `WorldPreloadScripts`, released in `RemovePage` (`CdpContext.cs:131,616`). | - |
| b76d966 | pause main-document requests before transport | port | See Security 3. Add an intercept hook to Page's document fetch: http(s) only, blocklist patterns, Fulfill without transport (multiple Set-Cookie stored in the jar, size cap), Continue with overrides and no scheme change, Fail mapped to `net::ERR_*`. Mark the event `intercepted`. Probably overlaps the open `dfc546d` (Fetch domain). | H |
| 1959c5e | replay current document lifecycle on subscription | port | Record the timestamp each phase was reached on each page (commit, DOMContentLoaded, load, networkAlmostIdle, networkIdle). Replay them on `setLifecycleEventsEnabled(true)`, as Chromium does. `init` from `document.open` clears the later phases. Depends on 3f8a8d1. | M |
| 62c3be6 | prevent deep DOM CDP stack aborts | already | C5 / `StackGuard` / `WorkCancellation`. Port the upstream CDP-connection test (10,000 nested divs, `getBoundingClientRect`) as a regression check. | L |
| 0d8a200 | validate file uploads in their owning realm | partial | The realm part is done (`DocumentOf`, a host helper rather than a global). See Security 4 for the rest. | L |
| c05cf3b | preserve remote DOM node ownership | partial | `describeNode` by objectId already uses the minting realm's frame DOM (`Dom.cs:147-170`, `AddContentFrameId`). Missing: `getOuterHTML` by objectId in a child frame (with 28756b4). `documentURL`/`baseURL` on frame document nodes and `frameId` on their documentElement are unverified. | L |
| eb2b515 | scope native CDP DOM selectors to the subtree | port | `DOM.querySelector[All]` (`Dom.cs:89-123`) ignores `nodeId` and searches the whole document. Missing: query from the node (0 or absent means the document), "No node with given id found", and an error on an invalid selector (C# returns 0 or []). | M |
| 75bd891 | preserve native form ownership and submitted fields | port | `get form()` (`bootstrap.js:6402`) walks ancestors only. The `form="id"` attribute is honoured only for custom elements (`_ceFormOwnerOf`, `bootstrap.js:3505`). The CDP Enter/click paths use `h.closest(.., 'form')` (`Input.cs:117,559,563`). Port form owner (attribute plus parser association), external controls in the submitted set, live select choices, and the submitter's entry. Relates to the open `88998d6` / `deef294`. | M-H |
| 7341597 | preserve document identity through native navigation | already | Page-initiated navigations carry their initiator, with Referer / Origin / Sec-Fetch / SameSite per Chromium 140 (todo security follow-ups). The interception-continuation part goes with b76d966. | - |
| 59276a3 | restore native select indexing and legacy table APIs | port | `HTMLTableElement` is empty (`bootstrap.js:16668`): no `rows`, `tBodies`, `tHead`/`tFoot`/`caption`, `insertRow`/`deleteRow`/`createTBody`. Section and row APIs exist (`16781-16884`). No indexed `select[i]` getter/setter. Upstream's first `table.rows` commit may sit in another group. | M |
| 8a2f957 | accept loopback origins on the CDP WebSocket | conflict | Weaker than Chromium (111+ refuses non-allowlisted origins) and superseded by 1faf17f. Do not port. | - |
| 1faf17f | limit default CDP origins to the endpoint authority | port | See Security 2. Port the `Server.Control.GetControlRefusal` origin check (same-authority `http://Host/` only, strict URL shape) plus the env allowlist, and the 403 `detail` body. Apply to the TLS path (`Server.Tls.cs`) too. | M |
| 229d1ed | expose live child frame IDs on CDP owner elements | partial | `AddContentFrameId` (`Dom.cs:581`) annotates only the `describeNode` root. Annotate every iframe/frame in the `getDocument` tree and in `setChildNodes` pushes (717dd92). | L |
| c1ffa73 | test: serialized markup survives a parse round trip | already | `IsRawTextParent` escapes title/textarea and SVG style (M9). Port the test file as coverage. | L |
| e1acbe5 | route control requests by path; keep new profiles private | partial | `Server.JsonEndpoint` (`Server.cs:959-973`) matches substrings anywhere in the head, so a `/devtools/browser` upgrade with `/json/version` in a header or query gets an HTTP answer. Parse the request-target (absolute-form too) and match exact paths. The profile-permission part is already covered: the cookie dir is 0700 (`CookieJar.CreateOwnerOnlyDirectory`), and C# does not persist localStorage. | L |
| 31e1dfe | accept trailing slashes in discovery URLs | already | Substring matching already accepts `/json/version/` etc. Keep the trailing-slash forms when porting e1acbe5's exact routing. | L (with e1acbe5) |
| e83d803 | arm watchdog for first script commands | port | See Security 1. Affects C# more than upstream because of page suspension. | H |

#### Suggested port order

1. **Watchdog on resumed pages:** `e83d803`.
2. **Fetch domain correctness:** `b1b3abe` + `3f9acc2` (byte bodies, strict base64), then `b76d966` (main-document interception, with the Continue/initiator part of `7341597`), then `5e58c86`'s `Network.loadingFailed`. Line this up with the open `dfc546d`.
3. **Playwright and go-rod blockers, cheap:** `939086a` + `e70183d` (setBypassCSP), `e5bcd75` (Page.close/stopLoading), `48953ef` (objectId before value), `28756b4` + `c05cf3b` (getOuterHTML by objectId, frame-aware), `192561c` (remove-script errors, history preload refresh).
4. **CDP Input editing:** `aa4b4e7`, `d132880`, `3f7cccd`, together with the open `ec62004` / `a156914` input items. Then `d88df74` (click via internal accessors), `8fe6d99` + `2e0b63f` (summary/details), `c714042` (focus fix-up, nested scrollIntoView, if-needed).
5. **Lifecycle:** `3f8a8d1` + `de227e9` (subscriptions), `1959c5e` (replay), then finish `a74e050` (in-page readyState and events, clearing head).
6. **Sessions and observers:** `b8586de` + `34346af` (Network fan-out gated on Network.enable), `875c531` (per-page state cleanup).
7. **DOM domain:** `eb2b515` (subtree queries), `717dd92` + `229d1ed` (requestChildNodes/setChildNodes/documentUpdated, frame ids in trees), `0d8a200` (upload validation).
8. **Control plane:** `1faf17f` (origin allowlist, same authority), `e1acbe5` + `31e1dfe` (exact path routing).
9. **DOM APIs:** `75bd891` (form owner and submission), `59276a3` (table APIs, select indexing).
10. **Perf and misc, benchmark first:** `3a4a0b5` (raw body bytes), `8bb46be` (heap usage and GC), `9960967` (screencast frame before load), plus regression tests from `318b57f`, `62c3be6`, `c1ffa73`.

### JS runtime and web APIs, part 1


**Summary: port 8 / partial 8 / already 16 / conflict 0 / skip 4** (36 commits; `da2bce8` is a re-landing of `0019e64`)

Paths below are relative to `dotnet/src/PocketCalculator.Js/` unless they say otherwise. `bootstrap.js` means `js/bootstrap.js` (the port's copy). This was a code-reading review only. Nothing was run, and anything marked "unverified" needs a probe.

#### Security

- **Medium, C# exposed: `daac810` (DoS, CPU spin).** `CSSStyleDeclaration.setProperty` and `removeProperty` (bootstrap.js:2161-2168) always call `_push()`, which calls `setAttribute("style")`. That queues a mutation record even when nothing changed. A MutationObserver that re-applies the same style therefore re-triggers itself forever. The task watchdog bounds each task, but the CPU burns until then. Upstream saw this on real sites (OpenCities, `body top:0`).
- **Low, C# probably not exposed (unverified): `b23d89a` (process abort).** Upstream's native `compileStreaming` reached deno_core's wasm streaming callback and panicked. V8 installs the native streaming entry points only when an embedder streaming callback is set, and ClearScript does not appear to set one. If so, the port's `WebAssembly.compileStreaming` is simply `undefined`. Check `typeof WebAssembly.compileStreaming` in the port. If it is a function, this is a crash and becomes High.
- **Low, C# exposed: `2f448c3` (resource retention).** A detached iframe's realm and timers are released only when `Page.ReleaseDetachedFrames` (PocketCalculator.Browser/Page.Frames.cs:323) next runs, not at removal. Child timers may fire in between (unverified).
- **Low, self-only: `cbcafc1` and `d57489c`.** Page script can make `new Response(..., {type:'opaque', url, redirected})` and forge network metadata on its own objects. A `toJSON` on `Object.prototype` changes the bytes `wrapKey('jwk')` produces (the port captures `JSON.stringify` at boot, bootstrap.js:21119, but `toJSON` still applies). Neither crosses an origin.
- **Not exposed (already fixed in C#):**
  - `3549ead` pushState URL spoofing (SECURITY.md L9; bootstrap.js:15855-15885, stricter than upstream).
  - `eeed4e3` CDP object-id and `unserializableValue` injection (`IsolatedWorld.cs:430` `JsStringLiteral`, `PocketCalculatorJsRuntime.Cdp.cs:776-797` `IsUnserializableValue`; the port throws where upstream substitutes `undefined`).
  - `5b9d62c` offsetParent infinite loop.
  - `0019e64`/`da2bce8` Origin header.

#### Commits

| sha | subject | class | C# evidence / what to port | priority |
|---|---|---|---|---|
| f5b32ff | test(js): pin the Chrome shape of a blob URL and its synchronous store | already | Test-only upstream. bootstrap.js:20461-20518 already mints `blob:<origin>/<uuid>`, throws Chrome's TypeErrors, stores synchronously and revokes. Optional: port the two tests, but read the store via the host (`_hostVars.__blobStore` is closure state, not `globalThis.__blobStore`). | L |
| 6ba3613 | Keep media property handlers independent from explicit listeners | port | `MediaQueryList.onchange` is a plain inner field (bootstrap.js:24898, `_ifaceAttrs`). It is never registered as an event handler, so `dispatchEvent(new MediaQueryListEvent('change'))` does not call it, and change events are never fired at all (comment at 24894). Depends on `c9978c3` (media query change delivery, render group). Port both: an event-handler slot that keeps its listener position, independent of `addEventListener` with the same function. Upstream test: `media_query_events.rs`. | M |
| 98d779b | Canvas: implement stroke() and make gradient color stops count | already | The port's canvas is a real rasterizer: `stroke()` at bootstrap.js:19404, `_CanvasGradient.addColorStop` at 18725, `createLinearGradient` at 19618 (todo.md:965 "Canvas 2D was a stub and is now a rasterizer", `Canvas2dConformanceTests`). Upstream's Bresenham and scanline version is weaker. Do not port. | - |
| 3549ead | fix(js): enforce same-origin in history.pushState/replaceState | already | bootstrap.js:15855-15885 `assertRewritable` applies HTML's "can have its URL rewritten" rule, with Chromium's message, and the host re-checks through `op_history_url` (SECURITY.md L9). It is stricter than upstream for file: and opaque origins. | - |
| 0019e64 | fix(js): send Origin on same-origin non-GET fetch and XHR requests | already | Ops/FetchOps.cs:780-792 sends Origin on a cross-origin CORS request or on any method other than GET/HEAD. It also handles the tainted `null` origin. | - |
| da2bce8 | Fix Origin on same-origin non-GET requests | already | Same diff as `0019e64`, landed again. | - |
| eeed4e3 | fix(js): embed object ids as literals, bound unserializableValue, release linked sheets | already | Ids: `CdpScope.Retrieval` / `JsStringLiteral` (Runtime/IsolatedWorld.cs:430, Runtime/PocketCalculatorJsRuntime.Cdp.cs:177,417,505). unserializableValue: Cdp.cs:776-797 rejects anything outside the CDP set. Linked sheets: the port deliberately keeps a detached link's bytes so re-insertion restores the sheet (bootstrap.js:3966-3968, deviation from 04418a5). The bytes are dropped when the node slot is freed, and generational ids stop a recycled id from inheriting them (PocketCalculator.Dom/DomTree.cs:867). | - |
| 4dfc90f | fix(js): define the missing concrete SVG element interfaces (#1043) | partial | The port defines ~30 interfaces with Chromium's parents (bootstrap.js:17056-17093; `SVGImageElement` etc. already exist). **Missing:** `SVGViewElement`, `SVGAnimationElement`, `SVGAnimateElement`, `SVGAnimateMotionElement`, `SVGAnimateTransformElement`, `SVGSetElement`, `SVGComponentTransferFunctionElement`, `SVGFEFunc{R,G,B,A}Element`, and every `SVGFE*Element` except `feImage`. Do not copy upstream's parents: `SVGContainerElement` and `SVGDescriptiveElement` are not Chromium globals, and `SVGGradientElement` and `SVGSVGElement` have different parents in Chromium (the port's parents are right). FE primitives extend `SVGElement`. Map them by local name in `_svgTagClasses`. | M |
| 5299b73 | test(js): gate pseudo content check on rendering | skip | Rust `#[cfg(feature="render")]` test gate. | - |
| d01bc8d | Test host session history bridge and traversal | skip | Test-only. The bridge it tests (`set_session_history`, `take_pending_history_traversal`, from `1a0773d`/`ba807f4` in the browserjs group) does not exist in C#. Port the test with that feature. | - |
| 45c43b6 | fix(js): yield after ready browser tasks | already | deno_core poll mechanics. The port's turn already returns without parking once a pump has run timers (Runtime/PocketCalculatorJsRuntime.EventLoop.cs:336, 433), so a ready interval does not get a second wake in the same turn. | - |
| a5f4dc9 | fix(js): honor mutation observer attribute filters | already | bootstrap.js:13495-13497 (attributeFilter per registration). Duplicate `observe()` handling is F39 (round3-findings). | - |
| bcf2b57 | test(js): count interval ticks across maintenance wakes | skip | Rust test flake fix (V8 maintenance wakes under deno_core). | - |
| f32db47 | fix(cssom): use owning frame state and invalidate resized styles | partial | Per-frame render state is already there: `PocketCalculatorOps.BindDocumentRenderOps` binds a frame realm's layout, computed-style and hit-test ops to its own document, and `FrameRealm.SetViewport` (Runtime/FrameRealm.cs:319) resets the prepared render. **Missing (1):** the computed-style snapshot cache (bootstrap.js:12180-12200) is keyed only on `_domMutationEpoch`, and `SetViewport` (Runtime/PocketCalculatorJsRuntime.cs:938) does not bump it. A retained `getComputedStyle(el)` therefore keeps viewport-dependent values (vw/vh, media queries) after a resize (unverified). Bump the epoch on a viewport change, or key the snapshot on innerWidth/innerHeight. **Missing (2):** `getComputedStyle(foreignFrameEl)` from the parent runs the parent's op with a frame-local nid. Delegate to `el.ownerDocument.defaultView.getComputedStyle` when that view is another realm. | M |
| e88edc2 | fix(cssom): use receiver realm for borrowed document hit tests | port | `Document.prototype.elementFromPoint.call(frameDoc, ...)` uses the caller realm's ops and the frame's nid. Upstream routes through `op_document_realm_member`, which `24d8742` (browserjs group, synchronous initial iframe realms) introduced. The port's initial about:blank iframe is an in-realm `_IframeDocument` (bootstrap.js:18161), so port this together with `24d8742`, or apply the receiver-realm lookup only to frames that have a realm. | L |
| e4b6138 | feat(js): implement User Timing entries and observers | partial | `performance.mark`/`measure` exist (bootstrap.js:15445-15490), but without validation: no SyntaxError for an unknown mark name, no TypeError for invalid option combinations, and no `detail` structured clone. `PerformanceObserver` is a no-op stub (bootstrap.js:14313) while `supportedEntryTypes` advertises mark/measure/navigation/resource/paint, so `observe({type:'mark', buffered:true})` never delivers. Port the observer (buffered delivery after the current task, `droppedEntriesCount`, takeRecords), the clear* semantics and the validation. Upstream has 11 tests. | M |
| b23d89a | fix(js): never reach the native wasm streaming entry points | partial | bootstrap.js:10259-10276 shims only `instantiateStreaming`. It still falls through to the native function (if one exists) and then to `WebAssembly.instantiate(nonResponse)`. **Port:** add the `compileStreaming` shim, and reject a non-Response source with Chromium's TypeError message instead of calling native. The crash risk is probably absent under ClearScript (see Security; unverified). | M |
| 5b9d62c | fix(js): offsetParent is null for the root and body elements | already | `op_layout_offset_parent` / `_offsetParentOf` (bootstrap.js:7181-7233) return null for body and html. Pinned by tests/PocketCalculator.Js.Tests/SubpixelGeometryScriptTests.cs:119,133 (`"body": [null, ...]`). | - |
| daac810 | fix(js): leave the style attribute alone when a declaration does not change | port | bootstrap.js:2161-2168 rewrites the attribute every time. Return early when the value equals the old value, or when the property being removed is absent (`removeProperty` returns ""). Also check `cssText` set to the same text and the camelCase proxy setter, which goes through `setProperty`. **Security/DoS** (see above). Note: upstream's expected `"top: 0px;"` ignores `!important`. Chromium serializes `"top: 0px !important;"`, so assert Chromium's text, not upstream's. | **H** |
| cbcafc1 | fix(js): validate Response construction and static factories | port | Port `Response` (bootstrap.js:11428-11487): `init.type`/`url`/`redirected` are honoured from page script, type defaults to `'basic'` (Chromium: `'default'`), and there is no status range check, statusText validation or null-body-status TypeError. `Response.redirect` has no status or URL validation, `Response.json(undefined)` does not throw, and `Response.error()` has type `basic`. Add an internal constructor path for fetch (bootstrap.js:10709), data: and blob: (10613, 10639) and `clone()`. | M |
| aea9bca | fix(js): preserve queued work after microtask exceptions | partial | The deno_core-specific part (its reporter terminates the checkpoint) does not apply: the shim's `queueMicrotask` is `Promise.resolve().then(fn)` (bootstrap.js:1576), so later work survives. **Missing:** a throw inside a `queueMicrotask` callback becomes an `unhandledrejection` rather than a window `error` ErrorEvent with `Runtime.exceptionThrown` (Chromium reports the exception). Wrap the callback to report through the window-error path (unverified). | L |
| 373bc8b | fix(js): report unsupported editing commands truthfully | already | `queryCommandSupported()` returns false (bootstrap.js:8294). The receiver brand check and the arity TypeError are missing (trivial, optional). | L |
| ee74973 | fix(js): mirror foster-parented written nodes at live anchors | port | `Modules/DocumentWriteStream.cs:12` `Placement(Parent, Node)` has no `Before` anchor, so a foster-parented node written by `document.write` (text or `<b>` before a `<table>`) is appended rather than inserted before the table. Add `before` to the placement payload (an op_dom wire change: `[parent, node, before]`), walk preceding siblings at the handed-over boundary, grow the existing text node, and use the anchor's live parent in `_insertWrittenNodes`. Four upstream tests. Related to the still-open `2b07b76`. | M |
| 2f448c3 | fix(js): retire timers and realms for detached frames | partial | The port disposes the realms of detached frames in `Page.ReleaseDetachedFrames` (PocketCalculator.Browser/Page.Frames.cs:323-440), lazily, not at removal. Check whether a removed frame's timers can fire before then (unverified). **Missing:** `insertBefore(n, n)` returns early (bootstrap.js:4057, 13556). DOM says ref becomes `n.nextSibling` and the remove-and-insert steps run, so an iframe inserted before itself reloads and observers see records. Upstream test: `inserting_a_frame_before_itself_retires_its_old_document_without_reordering`. | M |
| 667eb59 | fix(js): exclude iframe ids from Window named access | port | `_windowNamedValue` (bootstrap.js:17351-17359) returns `contentWindow` for a single iframe matched by id, and an HTMLCollection when an iframe `name` collides with another element. Chromium: only a frame's `name` (the child browsing context name) yields the WindowProxy, and that takes priority over the collection. Overlaps the open `a156914` "window named properties" item. | L |
| 690ae0b | fix(js): add the Option named constructor (#1168) | already | bootstrap.js:25377-25389 (legacy factory with the new.target check). | - |
| b6ab95e | fix(js): clear borrowed frame annexes before isolate teardown | skip | rusty_v8/deno_core context-slot annex teardown. ClearScript has no equivalent. | - |
| 86f0ec0 | fix(js): define the FileList global so boot references stop crashing | already | bootstrap.js:14433-14439 (`_makeFileListOf`; real FileList, deviation recorded there). | - |
| 9ba4ccf | fix(js): give HTMLIFrameElement its own brand instead of aliasing Element | already | `class HTMLIFrameElement extends HTMLElement` (bootstrap.js:16669), mapped at 16968. | - |
| f495cee | perf(storage): index entries and reuse checked document origins | already | `Ops/WebStorage.cs:22-31` already uses a Dictionary plus an order list and a running char count (O(1) get/set; remove is O(n), as upstream's reindex is). The cached document-origin part was not checked: see whether the storage key is re-derived (URL parse) on every `localStorage` call. Benchmark if it shows in profiles. | L |
| 8440fe6 | fix(js): report failed module network requests | port | Modules/PocketCalculatorModuleLoader.cs:663-695 fetches through `FetchResourceWithCallbacksAsync`. A transport failure or timeout throws `ModuleLoadException` with no failure network event, and `CallbackRegistry` has no failed callback. The CDP Network domain probably sees `requestWillBeSent` with no `loadingFailed` (unverified). Emit one `net::ERR_FAILED` failure per failed module request (resource type Script, no stored body). | L |
| acff100 | perf(dom): avoid unused mutation bookkeeping | partial | Most of it is Rust clone avoidance (C# `GetNode` returns a reference). The applicable part: Ops/DomOps.cs:1330 computes `RenderInvalidation.RetainedMutation(...)` (an ancestor walk) even when `state.PreparedRender` is null, then throws the result away. Guard it on `PreparedRender is not null`. Benchmark first (10% noise floor). | L |
| 19fa7a3 | fix(js): clone documents and attributes without reparsing | partial | Native document clone already exists (bootstrap.js:8227, `clone_node`, keeps kind and quirks), and DOMParser documents are native (8384 deviation). **Missing:** `Attr` (bootstrap.js:22362) is not a `Node`. It has no `cloneNode`, `compareDocumentPosition`, `getRootNode`, `isConnected`, `ownerDocument` or `childNodes`, so `attr instanceof Node` is false (Chromium: true). Port Attr as a Node subclass with independent clones, preserving namespace, prefix and ownerDocument. Probe the remaining clone tests (URL/baseURI/contentType of a cloned document, template content, form state) against the port. | M |
| c63ff88 | js: reflect HTMLTableCellElement.colSpan/rowSpan to the content attributes | already | bootstrap.js:16856-16859. Spec clamping (colSpan 1..1000, rowSpan 0..65534) is not applied (minor). | L |
| 8a7aa8c | fix(js): navigate to fragments in the same document | already | `_fragmentNavigate` / `_tryFragmentNavigate` (bootstrap.js:9629-9675), Location component setters via `_locationSetPart` (9705), and `<a href="#x">` handled at 18877. | - |
| d57489c | fix(js): wrap keys with AES-KW and serialize wrapped JWKs natively | port | `wrapKey`/`unwrapKey` (bootstrap.js:21117-21131) go through `aesCipher`, so AES-KW keys can be generated (20933, 21043) but not used. Add an RFC 3394 op (`op_subtle_aes_kw`, managed over `Aes.EncryptEcb` in Ops/CryptoOps.cs, as AesCtr does at :130), plus `op_subtle_jwk_json` / `op_subtle_jwk_parse` so a page's `toJSON` cannot change wrapped bytes. Export JWK members in lexicographic order (exportKey at 21024). Keep the op names (op protocol). Upstream tests: `webcrypto_tests.rs`. | M |

#### Suggested port order

1. **`daac810`**: one-line guard in `setProperty`/`removeProperty`. DoS on real sites. Assert Chromium's `!important` serialization.
2. **Fetch/Response surface: `cbcafc1`**, then `d57489c` (AES-KW and native JWK serialization, three new ops). Both are self-contained shim and CryptoOps work.
3. **Frames and realms: `f32db47`** (computed-style epoch on resize, cross-realm `getComputedStyle`), `2f448c3` (`insertBefore(n, n)`, prompt frame retirement), then `e88edc2` together with `24d8742` (browserjs group).
4. **DOM/interface conformance:** `19fa7a3` (Attr as a Node), `4dfc90f` (remaining SVG interfaces, with Chromium parents), `667eb59` (frame name vs id in named access, with the open `a156914` item).
5. **Events/timing:** `e4b6138` (PerformanceObserver and User Timing validation), `6ba3613` together with `c9978c3` (render group) for MediaQueryList change delivery, `aea9bca` (report queueMicrotask throws as ErrorEvent).
6. **Parser:** `ee74973` (document.write foster-parent anchor; op_dom payload change), alongside the open `2b07b76`.
7. **Low / benchmark-gated:** `b23d89a` (`compileStreaming` shim; probe ClearScript first), `8440fe6` (module failure network event), `acff100` (guard RetainedMutation), `f495cee` (origin cache, only if profiles show it).
8. **Tests only:** `f5b32ff` (blob URL shape), and `d01bc8d` once session history (`1a0773d`) is ported.

### DOM and JS runtime, part 2


**Summary: 15 port, 8 partial, 15 already, 0 conflict, 6 skip.**

I read the code but did not run the engine. "Unverified" marks the places where the outcome depends on runtime behaviour. Paths are relative to `dotnet/src/`. "Shim" means `PocketCalculator.Js/js/bootstrap.js`.

#### Security

- **M: `8dbf7e1` mXSS through attribute values. The C# port is exposed today.** `EscapeAttr` (`PocketCalculator.Dom/DomTree.Serialize.cs:214`) escapes only `&` and `"`.
  - Script can append an element under a raw-text parent such as `<noscript>`, which `IsRawTextParent` (line 247) emits literally, and give it `title="</noscript><img src=x onerror=1>"`.
  - That element's `outerHTML` reparses into a live `<img>`. The same output reaches `--dump html` and MCP consumers.
  - Chromium escapes `<`, `>` and U+00A0 in attribute values, and U+00A0 in text.
- **M: `1ee3ac1` markdown extraction DoS. The C# port is exposed today.**
  - `MarkdownScript.HtmlToMarkdown` (`PocketCalculator.Js/Runtime/MarkdownScript.cs:88-93`) converts every `<tr>` child twice: once while building `children`, and again in the `tr` case. Nested tables therefore cost 2^depth.
  - The walk is also unbounded recursion, so a deep DOM exhausts the JS stack.
  - Both block `--dump markdown` and `LP.getMarkdown` for a hostile page.
- **M (safety): `33aeb3f` `confirm()` returns `true`** (shim:20592), so a non-interactive engine silently accepts destructive confirmations ("Delete this record?"). The port has no CDP dialog surface that would override it.
- **L: `cf5067e` void elements.** Children that script appends under `frame`, `keygen`, `basefont` or `bgsound` are serialized, plus an end tag. On reparse they land elsewhere in the tree, which gives a markup-shape change and a small mXSS surface. `IsVoidElement` (Serialize.cs:227) also ignores namespace.
- **L: `d853a73` WebCrypto `wrapKey`/`unwrapKey` ignore key usages and algorithm** (shim:21117-21131). A key without the `wrapKey` usage can wrap. `key.algorithm` is also the live caller object.
- **L: `c0495ed` PBKDF2.** The port is already bounded, more tightly than upstream: at most 4x10M iterations x blocks, in `PocketCalculator.Js/Ops/CryptoOps.cs:154-212`. The work still runs synchronously on the page thread, where the watchdog cannot interrupt it. That is a bounded stall of tens of seconds (unverified).
- **None: `0963795` `__bootstrap` alias.** The port is not exposed: there is no deno_core alias. The equivalent `__obscura_deno_core` and `Deno` are deleted in every realm (`PocketCalculator.Js/Runtime/BootstrapLoader.cs:152-166`, used by the page, frame and isolated-world realms). The upstream test checks every reachable global rather than a fixed list of names; porting it is a cheap regression guard, because ClearScript can add globals of its own.
- **None: `6342fff`.** The forbidden request headers are already filtered in the shim (`_isForbiddenRequestHeader`, shim:10751) and on the host side (`FetchOps.Cors.cs:207`).

#### Per commit

| sha | subject | class | C# evidence / what to port | priority |
|---|---|---|---|---|
| aa094b9 | refactor(dom): drop the raw pointer from the tree sink's element name | skip | A Rust lifetime refactor with no behaviour change. The C# tree builder has no equivalent. | - |
| d45bee5 | fix(dom): escape textarea and title text when serializing | already | `IsRawTextParent` (`DomTree.Serialize.cs:247`) checks for the HTML namespace and lists style, script, xmp, iframe, noembed, noframes, plaintext and noscript, which is exactly upstream's new set. It is a recorded deviation (SECURITY.md M9). | - |
| 8dbf7e1 | fix(dom): escape nbsp, and < > in attribute values, when serializing | port | `EscapeText` (Serialize.cs:187) lacks `&nbsp;`. `EscapeAttr` (Serialize.cs:214) lacks `&nbsp;`, `&lt;` and `&gt;`. Chromium escapes all three. Security M. | H |
| efb57d8 | perf(dom): copy unescaped runs whole when serializing | port | C# appends one char at a time. Use `IndexOfAny` over a `SearchValues<char>` and append whole spans. Do it together with 8dbf7e1. | L |
| cf5067e | fix(dom): serialize basefont, bgsound, frame and keygen as void | port | `IsVoidElement(string)` (Serialize.cs:227) lacks the four obsolete tags and has no namespace check: an SVG `<br>` loses its end tag. `InnerHtml` should return "" for a void HTML element. The XML serializer uses `IsVoidElement` with an `html` guard (`DomTree.SerializeXml.cs:168`), so it needs the same list. | M |
| f2bf4c3 | fix(dom): write xml, xlink and xmlns attributes with their fixed prefix | port | Serialize.cs:101 writes the stored prefix. `setAttributeNS(XML_NS,'abc:lang')` therefore serializes as `abc:lang` instead of `xml:lang`, and `setAttributeNS(XLINK_NS,'href')` as `href` instead of `xlink:href`. Port the `push_attribute_name` rule. | M |
| 503a66d | perf(dom): allocate reused-node identities on demand | already | N/A: C# encodes the generation in `NodeId` (`NodeId.cs:33-45`, `DomTree.cs:859`), so there is no side map to allocate. | - |
| d853a73 | fix(js): validate key wrapping permissions | port | The shim's `wrapKey`/`unwrapKey` (shim:21117) check no usage or algorithm, and `makeKey` (shim:20893) exposes the caller's algorithm object. Port the material record `{bytes, algorithm, usages}` and the checks. The shim has no AES-KW; upstream's AES-KW branch needs `op_subtle_aes_kw`, which may come from an earlier upstream commit. | L |
| fb23f18 | fix(js): preserve fragment history state | partial | The leading-`##` hash setter is fine: `_urlSetOp`. Missing: a navigation to the *same* URL should be a replace. `_locationNavigate` → `_fragmentNavigate(target,false)` (shim:9655, 9626) pushes a duplicate entry, so `history.length` grows. Upstream also keeps `history.state` on that path, while the C# comment says "fresh entry with null state, per spec". Measure Chromium before deciding the state half. | M |
| 2f302d9 | fix(js): reflect ordered-list and list-item attributes | partial | The brands exist (shim:16680-16681), and `OL.reversed`/`OL.start` are reflected (shim:23270). `LI.value` falls through to the generic string `value` getter instead of a `long` with default 0. Check `OL.type` against Chromium (relocated generic `type`, shim:23103). | L |
| 91fe4de | fix(js): make document read-only | already | `_shapeWindowAttributes` (shim:26451ff) makes `document` an unforgeable getter, as in Chromium 141. | - |
| 6594672 | fix(js): mark op_initial_frame reentrant to prevent srcdoc iframe aborts | skip | deno_core rejects reentrant ops; ClearScript has no such rule. | - |
| 24a731c | perf(js): share bootstrap source across isolates | skip | Snapshot/extension mechanics. The port has no snapshot API, and a code cache was measured as a net loss (todo.md "Cold start"). The per-isolate copy of the shim source is a memory cost (unverified) that ClearScript cannot share. | - |
| 579e1cb | fix(cdp): evaluate scripts without page eval | partial | Page-eval immunity is already there: `__obscura_cdp.eval` is captured at bootstrap (`PocketCalculatorJsRuntime.Cdp.cs:181-215`). Missing: the source still runs through an *indirect eval*, so `let x = 40` from one `Runtime.evaluate` is not visible to the next. Chromium compiles a script, so global lexical bindings persist. Port by compiling the source as a script (`V8ScriptEngine.Evaluate`/`Compile`) in the scope. | M |
| 3a9eb06 | perf(js): allocate inline styles on demand | port | Every Element wrapper eagerly builds a `CSSStyleDeclaration` + Proxy (shim:5130, 5166, getter 5419). Move it to a WeakMap with lazy creation. Many `this._style` call sites need updating (5476ff, 5536). Benchmark it. | M |
| 7d1abca | Bound parallel runtime startup and precompile hot helpers | skip | The precompile half relies on a snapshot. The creation-concurrency bound has no counterpart: the port takes no lock in `PocketCalculatorJsRuntime` (cs:91-130). Worth measuring under `serve` concurrency, but not a port. | L |
| 00850f0 | Batch Window named-property lookup in native DOM | partial | The shim still runs the per-lookup `_qsa` scan plus a JS filter (shim:17333-17440). Port the native `window_named_names`/`window_named_candidates` op_dom commands (a wire surface, byte for byte). Correctness gap: upstream returns a named iframe's window whenever one matches by **name**, even among several matches. C# returns an HTMLCollection when there are more than one, and returns `contentWindow` for an iframe matched only by `id` (shim:17350-17357). Check against Chromium. | M |
| f66ac0a | Invoke CDP functions natively and preserve JSON arguments | partial | `callFunctionOn` already uses the captured `c.apply`/`c.eval` (Cdp.cs:396-460). Missing: (1) `BuildArgs` (Cdp.cs:748) pastes `value` JSON as a JS object literal, so `{"__proto__":{...}}` sets the prototype instead of an own property. Parse it with the captured JSON.parse, or build the value natively. (2) `objectId` should take precedence over `value` when both are sent (rod sends `value:null`). | M |
| a36ec39 | Release discarded native function source metadata | port | `_nativeFns`/`_nativeStr` are a strong `Set`/`Map` (shim:410, 414), so every `_markNative`'d function a page deletes stays alive. Switch to `_private(new WeakSet())`/`WeakMap`; `_private` already guards against page overrides. | L |
| 98653ad | Precompile common browser methods in startup snapshot | skip | Snapshot only. | - |
| 2c18068 | fix(js): iframe shadow root document routing | already | The shadow ops bind to the calling realm's document (`PocketCalculator.Js/Ops/PocketCalculatorOps.cs:482-490`, "Port fix"). Initial `about:blank` frames are not verified. | - |
| 414e649 | Expose window.event while dispatching events | already | `_currentEvent` with shadow-tree exclusion and save/restore (shim:2314-2738); [Replaceable] accessor (shim:3160-3167). | - |
| 6342fff | fix(fetch): ignore forbidden script request headers | already | Shim: `_isForbiddenRequestHeader` (10751), the Headers guard (10857) and XHR (11040). Host side: `FetchOps.Cors.cs:175-280`. | - |
| e4f11db | fix(js): expose ECMAScript builtins and web APIs as properties of the worker scope | partial | `globalThis` is already the scope (shim:20270). Missing: `self.Object`, `self.Promise`, `self.JSON` and the other `WORKER_GLOBALS` as non-enumerable scope properties. Go's `wasm_exec` panics without them. | M |
| 24adea2 | fix(js): add DOMImplementation global | already | `_ifaceIllegal('DOMImplementation')` and the per-document implementation object (shim:24492-24509). `Document.prototype.adoptedStyleSheets` is configurable (shim:13336). | - |
| cfce062 | fix(js): run custom element reactions | already | A full reactions model (shim:3177-3430; todo.md "Custom elements follow HTML's reactions model"). | - |
| 831cb29 | Give missing HTML element interfaces distinct brands | already | `_htmlTagClasses` (shim:16955-16985) covers base, td/th, tbody/thead/tfoot, tr, col, picture, source, title and time; tested in `tests/PocketCalculator.Js.Tests/HtmlElementInterfacesTests.cs`. | - |
| a1dd711 | Read supplied Response streams on demand | port | `new Response(readableStream)` goes through `_bodyToUint8Array` → `String(stream)`, giving the body "[object ReadableStream]" (shim:10246, 11430). Port the stream source, lazy read, tee on `clone()`, locked/disturbed checks, and "lock does not disturb" (the C# `getReader` hook sets `bodyUsed`, shim:11460). | M |
| 6d25c43 | fix(js): preserve nested frame parents in message sources | port | The `_frameWindowFor` proxy (shim:18355) forwards `parent` to the frame realm's own stand-in. For a grandchild source, `event.source.parent.postMessage` then sends with the wrong source frame. Port the `parent` resolution: `globalThis` for a direct child, otherwise the frame window or remote window of the parent. | M |
| 8ba9e5e | fix(js): update attributes before custom element reactions | already | `_ceAttributeChanged` runs last in setAttribute, setAttributeNS, removeAttribute and removeAttributeNS (shim:5476, 5491, 5528, 5539), after the style and handler updates. | - |
| 0963795 | fix(js): remove deno_core's __bootstrap alias from page realms | already | There is no deno_core alias. `__obscura_deno_core`, `Deno` and the handoffs are deleted in every realm (`BootstrapLoader.cs:152-166`). Port the reachability test. | M (test) |
| 738ec02 | fix(js): keep DOMParser XML check independent of page startsWith | already | XML parsing is native (`_newParsedDoc` → `PocketCalculator.Dom/XmlParsing.cs`, shim:15044); no JS `startsWith` is involved. | - |
| 7451e39 | fix(js): handle setRangeText defaults and input type fallback | port | `setRangeText` (shim:22716) returns early on an empty value. It does not adjust a selection that the replaced range overlaps or follows, and does not throw IndexSizeError (start > end), TypeError (bad selectMode) or InvalidStateError (unsupported input type). Its `select`/`start`/`end` modes set direction to `'none'`; upstream says "forward" (check Chromium on Linux). Reuse `_hasTextSelection` for the type fallback (shim:22635). | M |
| 48538d6 | fix(js): preserve input selection and convert range offsets | partial | Value assignment that does not change the value already keeps the selection without page calls (`_caretToEndOnValueChange`, shim:22647). Missing: ToUint32 offset conversion with clamping to the value length, in setRangeText and setSelectionRange. C# uses `parseInt`/`Math.max(0)`, so -1 becomes 0, and `setSelectionRange` never clamps (shim:22702). | L |
| c0495ed | Fix replaceable window origin and bounded asynchronous PBKDF2 | partial | `window.origin` is already replaceable and inherits for about:blank (shim:24676-24698). The PBKDF2 bounds are already tighter (`CryptoOps.cs:163-207`). Missing: running large derivations off the page thread, cancellably (`deriveBits` is synchronous, shim:21089). | L |
| 33aeb3f | Dismiss noninteractive confirmation dialogs by default | port | `globalThis.confirm = () => true` (shim:20592). Change it to `false` and note the deviation from the old Rust behaviour. It also applies in child realms through the same shim. | H |
| ccdaf10 | Give HTML elements distinct native interface brands | already | Same as 831cb29; the 24 interfaces are in `HtmlElementInterfacesTests.cs:18-27`. | - |
| 1ee3ac1 | Bound markdown traversal and reuse table cell conversions | port | `MarkdownScript.cs:88-93` re-converts `<tr>` children, giving 2^depth on nested tables, and the walk is recursive and unbounded. Port the reuse of cells and the iterative walk past depth 128, keeping the C# host-helper (`h.*`) form. Security M. | H |
| 1e4238e | Keep storage usable after its global constructor is replaced (#1500) | port | `_mkStore`'s `isReal` reads the global `Storage.prototype` on every access (shim:~15745; `Storage` is `globalThis.Storage`, shim:15740). `window.Storage = {}` breaks localStorage and sessionStorage. Capture the prototype once. | M |
| 8310ebf | perf(js): reuse live select snapshots for length reads | port | `select.length` re-queries the options on every read (shim:23389 `optionsOf(this).length`), so `for (i < sel.length)` is O(n^2). The C# implementation differs; add a cached snapshot keyed on the DOM mutation epoch. Benchmark it. | L |
| 790a77d | fix(js): preserve constructed DocumentFragment wrapper identity | port | The `DocumentFragment` constructor (shim:~7470) does not `_cache.set` the wrapper. The native fragment reports node_type 9 (`Ops/DomOps.cs:322-327`, 894-895), so `_wrap` (shim:9388) builds a second wrapper, and `frag.removeChild(child)` can throw NotFoundError. One line, plus the test. | M |
| 786fde5 | fix(js): share in-flight module fetches between graph loads | already | `_loads` in-flight `Task<Document>` map (`PocketCalculator.Js/Modules/PocketCalculatorModuleLoader.cs:107`, 472-520). | - |
| 7d9ed8a | fix(js): amortize pending module fetch cleanup | already | Entries are removed on completion (`PocketCalculatorModuleLoader.cs:744-745`); there is no per-load sweep. Check the failure path for a stale `_loads` entry (unverified). | - |
| 5efc4a1 | perf(js): reset image requests only during attribute mutations | skip | The port has no per-op `forget_html_image_request` string parse; image invalidation is typed in `PocketCalculator.Render/Dom/RetainedStyle.cs:298-309`. | - |

#### Suggested port order

1. **Serializer, one commit per upstream fix:** `8dbf7e1` (security), `cf5067e`, `f2bf4c3`, then `efb57d8` (perf, reuse the same escape loop). Port the upstream unit tests into `PocketCalculator.Dom.Tests` and check the parity sweep for `--dump html`.
2. **Safety and DoS:** `33aeb3f` (confirm → false), `1ee3ac1` (markdown iterative walk plus cell reuse), and the `0963795` reachability test.
3. **Small JS identity and robustness fixes:** `790a77d` (DocumentFragment cache), `1e4238e` (Storage prototype capture), `a36ec39` (WeakSet/WeakMap native metadata).
4. **CDP evaluate and call semantics:** `579e1cb` (compile as a script so `let` persists), `f66ac0a` (`__proto__` as an own property in JSON args, `objectId` precedence).
5. **Frames and workers:** `6d25c43` (nested message-source `parent`), `e4f11db` (builtins on the worker scope), `00850f0` (native named-property op plus the iframe-name precedence; measure Chromium).
6. **Form controls:** `7451e39` + `48538d6` together (setRangeText, ToUint32 offsets, selection clamping; measure `selectionDirection` in Chromium), then `2f302d9` (`LI.value` as a long).
7. **Fetch and crypto:** `a1dd711` (stream-backed Response), `d853a73` (wrap usages; AES-KW may need an earlier commit), the `c0495ed` async PBKDF2 offload (optional).
8. **Navigation:** `fb23f18`. Make a same-URL fragment navigation a replace; decide the `history.state` half after measuring Chromium.
9. **Perf, each benchmarked interleaved:** `3a9eb06` (lazy inline style), `8310ebf` (select length).

### Page, navigation, frames, history and storage


**47 commits: port 20, partial 11, already 10, conflict 0, skip 6.**

This review is based on reading the code. Nothing was run. Paths are relative to `dotnet/src/` unless they say otherwise.

One architectural fact shapes most of the frame commits. Upstream runs a child frame's realm in the page's V8 context, so `contentWindow` can be the frame's real global. The C# port runs each frame realm in a separate `V8ScriptEngine` (`PocketCalculator.Js/Runtime/FrameRealm.cs:160`), and `FrameRealm.PublishRealmObjects` (around line 605) records that ClearScript cannot hand a script object across engines. So in C# the parent sees an `_IframeWindow` / `_IframeDocument` stand-in (`bootstrap.js:17829`, `:18457`). Upstream tests that read a child global through `contentWindow` (`child.eval(...)`, `contentWindow.childOnly`, `contentWindow.preloadCount`) cannot pass in C# without redesigning that. Those commits are marked partial, with the reachable part named.

#### Security

- **Medium. `5bfac24`: an unparsable fetch URL skips the SSRF gate. C# is exposed.** `FetchOps.cs:484` and the interception-rewrite check at `:562` both run only `if (UrlRecord.Parse(..) is { } ...)`. A string the WHATWG parser rejects then reaches `new HttpRequestMessage(method, url)`, which parses with `System.Uri`, a different parser. Any input the two parsers disagree on bypasses the gate. Not probed. Port: return `Blocked(...)` when the parse fails, at both sites. The change is small, so do it first.
- **Low to Medium. Web Storage has no total budget (DoS). C# is exposed.** `PocketCalculator.Js/Ops/WebStorage.cs:20` caps each storage key at 5 MiB but has no cap on the number of areas or total bytes. Iframes on many origins, or partitioned keys, can grow a long-lived `serve` context without bound. Upstream `677f6d2` adds 32 MiB per store and 256 origins. Add the same limits when porting storage.
- **Low. `9b05070`: frame realms run the script-start ops against the page's state.** `op_script_try_start` and `op_script_mark_started` are bound to `Page` (`PocketCalculatorOps.cs:246-249`), and `BindRealmOverrides` (`:441`) does not rebind them. A frame's node id is therefore looked up in the top document's arena. Two effects:
  - Scripts that frame code inserts never run when that id is not a script on the page.
  - When the id does name an unstarted script on the page, the frame claims it and the page's script is suppressed. This is a cross-realm integrity bug.
  - The image and canvas ops are already bound per frame (`PocketCalculatorOps.cs:552`).
- **Low. `c8e5b9e`: the linked-stylesheet `load` script addresses the `<link>` by its query index.** The index is snapshotted once (`Page.DocumentLoad.cs:1193-1200`, `PageHelpers.cs:1084`). A `load` handler that mutates the list makes a later `registerLinkedStylesheet(link, undefined, responseUrl)` bind the wrong link to another sheet's response URL. The CSS bytes still go to the right node (`AppendExternalStylesheet(link.Node, ...)`). I did not check whether `originClean` can cross over. Port: carry the `NodeId` into the script.
- **History ports (`1a0773d`, `0ecc417`): two things to keep when porting.**
  1. Upstream's `__obscuraHistory` (`take`, `traverse`, `reset`) is a page-visible frozen global. In C# it belongs on `__obscura_host` (CLAUDE.md "Page script never reaches the engine").
  2. `op_history_traverse` navigates to a stored session-history URL. That navigation must go through the same scheme gate as `Page.navigateToHistoryEntry` (`PocketCalculator.Cdp/Domains/Page.cs:1409`, `NavigationRefusal`), so `history.back()` cannot reach a `file://` entry from a web document (SECURITY.md H3). Frames must keep an empty session history, as upstream does.
- **Not exposed in C#:**
  - `17b5d7d`: opaque srcdoc objects published to the creator. C# keeps frame state in the private `_iframeStates` WeakMap and closure `_frameObjects` (SECURITY.md C2/L10).
  - `8dddc6f`: `contentWindow.location.replace` navigated the top document upstream. The C# stand-in's `location.assign/replace` do nothing (`bootstrap.js` `_IframeWindow`).
  - `dcdc6d0`: restore into declared origins. C# already refuses cross-origin restore (`Tools.Tier2.cs:724`, SECURITY.md L9).
- **Robustness: `1737a4b`.** An endless `parent` walk froze pages that run PerimeterX. C# is not affected: the stand-in's `parent` is `globalThis`.

#### Commits

| sha | subject | class | C# evidence / what to port | priority |
|---|---|---|---|---|
| c6402a7 | preserve pending console messages across suspend | port | Bigger than the commit. The MCP console feature (#951) is not wired at all. `CoreOps.ConsoleMessage` (`Ops/CoreOps.cs:53`) has no subscriber, and nothing appends to `BrowserState._consoleMessages` (`Mcp/BrowserState.cs:28`), so `browser_console_messages` always returns "No console messages." Port the feed first. If messages are buffered in `BrowserState` or `Page`, the suspend fix comes for free. | H |
| c8e5b9e | linked stylesheet `<link>` by node id, not query index | port | `Page.DocumentLoad.cs:1193-1200`. Pass the `NodeId` to `PageHelpers.LinkedStylesheetLoadScript` (`:1084`) and resolve it host-side. Port the upstream test. | M |
| 1a0773d | expose session history to window.history | port | C# `History` (`bootstrap.js:15910`) knows only the document's own `stack`: `length` is `stack.length`, and `go()` clamps instead of leaving the document. Needs `op_session_history` and `op_history_traverse`, plus `Page.History` / `HistoryIndex` (`Page.cs:308`) handed to the realm and moved on traversal instead of appended. See Security for the scheme gate. | M |
| 5bfac24 | block unparsable fetch URLs instead of skipping the SSRF gate | port | `FetchOps.cs:484`, `:562`. See Security. | H |
| 2ae2ff8 | test: count resource completions across service turns | skip | Rust test harness only. | - |
| 07a3dc5 | test: wait for fixture request bytes on macOS | skip | Rust test fixture on macOS. | - |
| 6635dde | test: wait for concurrent fixture requests on macOS | skip | Rust test fixture on macOS. | - |
| ba807f4 | Expose session history to window.history | skip | The same change as 1a0773d (merge copy); handle it under 1a0773d. | - |
| 55e76f0 | Return rendered text from innerText | already | Native `op_inner_text` (`bootstrap.js:5331` `_innerTextOf`; todo.md:1956), plus inherited visibility, `<details>` and `dialog` handling (todo.md:1775-1777). Run the upstream test as a check. Detached elements returning `textContent` ("QRS", "TU") is unverified. | L |
| 2d323dd | pause only requests matching Fetch.enable patterns | port | `FetchOps.cs:469` pauses every fetch/XHR whenever interception is on. `Cdp/Domains/Page.cs:325` emits `Fetch.requestPaused` for every post-navigation request regardless of `ctx.FetchIntercept.Patterns`. `PageHelpers.UrlMatchesCdpPattern` (`:1115`) already exists. Hand the patterns to the runtime (`SetInterceptBlockPatterns` exists) and filter in both places. | M |
| 108121a | test: wait for complete prefetch fixture request | skip | Rust test harness only. | - |
| c8b209d | bound iframe discovery microtasks with the watchdog | already | `Page.Navigation.cs:682` uses `RunEventLoopBoundedAsync(RoundMs)`. | - |
| 24d8742 | create initial iframe realms synchronously | partial | The initial about:blank is a parent-side stand-in (`_blankIframeState`, `bootstrap.js:18161`), not a real realm, so `child.eval`, `contentWindow` identity and globals cannot match (see the architecture note at the top). Reachable without a redesign: the nested-initial-frame parent ids, and not replaying the parent's scripts. `compatMode` of the stand-in is hard-coded `CSS1Compat` (`:17873`), while Chromium's initial blank is BackCompat (721e40f test). | L |
| 60107d1 | record navigation lifecycle timestamps | partial | C# has real lifecycle marks hooked from JS (`bootstrap.js:15388`). Network phases are synthesized proportionally (`_navTimingReset`, `:15182`), where upstream and Chromium report real values or 0. The marks reset on `ResumeJs`. They come from page-visible DOMContentLoaded/load listeners, so a synthetic event dispatched first sets them. Port: host-side monotonic timestamps from the navigation fetch, and drop the fabricated DNS, TCP and TLS values. | L |
| 721e40f | parse replacement documents and flush written input at EOF | port | Known gap, todo.md:6125: `document.open()` empties only the body (`bootstrap.js:8196`), with no new document, doctype or quirks change at EOF. Port the open/close tree replacement and `compatMode` from the parsed mode. Overlaps the open item `2b07b76`. | M |
| 359362f | preserve destinations for dynamic resources | port | `op_fetch_url` takes `internalLoad` as a bool. Dynamic scripts (`bootstrap.js:666`), stylesheets and frame documents all report `ResourceType.Fetch` (`FetchOps.cs:509/626/1085`), and XHR reports Fetch too. Add the destination argument (Script, Stylesheet, Document, XHR, Fetch). Keep the C# `internalLoad` semantics derived from it. | M |
| 0155807 | cancel requests and body readers with AbortSignal | port | C# `fetch` (`bootstrap.js:10642`) ignores `init.signal` and `Request.signal`: there is no pre-abort rejection, no transport cancel and no body error. An aborted fetch keeps `PageInFlight` held, so network-idle is late. Needs a cancel handle op and a CTS in `FetchOps`. | M |
| 3f37d91 | record ES module network responses and bodies | port | `Page.Scripts.cs:166` records the entry module only, after successful evaluation, as 200 with no headers or body. Imported modules are not recorded at all. Record each fetch in `PocketCalculatorModuleLoader` as a `JsNetworkEvent` with its body. | M |
| f661c2e | keep Web Storage across navigation, persist it, back IndexedDB | partial | Already done: host-held localStorage per context and sessionStorage per page (`Ops/WebStorage.cs`, todo.md:2237). Missing: `--storage-dir` persistence (todo.md:2243 says none), MCP `--storage-dir` (`Mcp/BrowserState.cs:49` uses `WithOptions`, so no profile, and cookies aren't persisted for MCP either), and IndexedDB (3dea548). | M |
| 22eb6b4 | build a plain-text document for non-HTML text responses | port | `Page.Navigation.cs:533-538` parses every text body as HTML, and `document.contentType` is guessed from the URL (`bootstrap.js:7618`). Build `<pre>` for text/plain and text/markdown, and report the response MIME type. | M |
| 335f929 | preserve plain-text document metadata and newlines | port | Port with 22eb6b4. | L |
| 73358fd | preserve response bodies during runtime suspension | port | `SuspendJs` (`Page.Evaluate.cs:322`) drops `State.NetworkResponseBodies` (`FetchOps.cs:1108`). Move them into `Page._responseBodies` before `Js = null`. Pending JS network events should move too. | L |
| 677f6d2 | retry profile writes and preserve browser strings | partial | Lone surrogates already survive: .NET strings are UTF-16 (unverified through ClearScript). Profile write and retry, the total budgets (see Security) and the MCP storage flag are missing. Port together with f661c2e persistence. | L |
| 3dea548 | context-owned IndexedDB transactions | port | C# IndexedDB is the old stub (`bootstrap.js:19925-20013`): one Map per store per transaction, `onupgradeneeded` never fires, `objectStoreNames` is always empty, cursors return null. Port the context-owned store and the `indexed_db.js` shim. The CDP part (`with_live_peers` pumping peer pages while awaiting) needs separate review against the C# server loop. | M |
| 20299be | finish document work before iframe load | partial | C# fires the iframe `load` as soon as the fetch settles (`bootstrap.js:6360`), before the host has built the realm and run its scripts. Chromium fires it after. Move the dispatch host-side, after the frame's scripts run. The CDP `await_cdp_evaluation` cooperativity is unreviewed against `CdpAwaitParking`. | M |
| dcdc6d0 | restore web storage into its declared origins | partial | `browser_storage_state` exports only the active origin, and `browser_set_storage_state` skips other origins (`Mcp/Tools.Tier2.cs:638/724`). With host-held `WebStorage`, write each declared, canonical http(s) origin directly into `Context.LocalStorage` (sessionStorage into the active page), validating before any mutation. This replaces the L9 deviation safely. | L |
| a3aea0a | preserve evaluation errors and await form submission | port | `Mcp/Tools.cs:279`: `browser_evaluate` is synchronous. It does not await promises, has no `timeoutMs`, and does not surface exceptions or rejections as `isError`. `browser_fill_form` does not complete a queued submit navigation. Port the tool behaviour and the MCP wire text byte for byte. | M |
| 911c45c | load iframe srcdoc in native child realms | already | Port addition (todo.md:1748; `op_frame_document_srcdoc`, `bootstrap.js:18105`; `SrcdocFrameTests`). Run upstream's 5 tests. `contentWindow.childOnly` and `d.defaultView === f.contentWindow` cannot pass (architecture). `<noscript>` with sandboxed scripts is unverified. | L |
| 17b5d7d | avoid publishing opaque child objects | already | Private `_iframeStates`; `_frameObjects` is closure state (`bootstrap.js:245`, `:26664`). | - |
| 955524a | honor named child-frame targets | already | Port addition: `bootstrap.js:6574` (`op_navigate_frame`). Missing: the `<base target>` default. | L |
| 47a3431 | initialize blank documents during insertion | partial | Initial about:blank has no realm, so no synchronous `load` on append and no preload scripts in the child (architecture). Reachable: an SVG-namespace `<iframe>` must not create a browsing context, but `_loadDocumentFrames` uses `_qsa(..., 'iframe')` (`bootstrap.js:18132`), which also matches foreign elements (unverified). | L |
| 8dddc6f | Load frame Location navigations in their owning frame | partial | Safe today (see Security), but `iframe.contentWindow.location.replace/assign/href=` from the parent is a no-op in C#. Chromium navigates the frame, and lazy-load scripts use this. Route the stand-in's Location setters to `op_navigate_frame` by frame id. Pass `_realmFrameId` on `op_navigate`. | L |
| 0a14a4c | Fail network-idle navigation at its actual deadline | port | `Page.Navigation.cs:581-637` uses a fixed 5 s, breaks out, and sets `NetworkIdle` even when idle was never reached. Upstream uses the navigation timeout and fails with an error. The stealth half does not apply. Check CLI exit codes and Puppeteer `networkidle0` expectations against Chromium. | M |
| 3a29788 | Honor explicit user agents in stealth requests | already | One transport: `Page.cs:513` `rt.SetUserAgent(HttpClient.UserAgent)`. The wreq half is a skip (no stealth TLS transport). Stealth combined with `--user-agent` is unverified. | - |
| 1737a4b | end a loaded frame's parent chain at the page | already | The stand-in `_IframeWindow` has `parent = top = globalThis` (`bootstrap.js:18461`), and `_CrossOriginFrameWindow.parent` is `globalThis`. `_frameWindowFor`'s Proxy (`:18365`) is never reached, because `_frameObjects[id].window` is never set. Add the upstream `parent`/`top` lines there defensively. | L |
| d8f1467 | live table rows without extra wrapper bridge reads | port | `HTMLTableElement` is an empty class (`bootstrap.js:16668`), so it has no `rows`, `tBodies`, `tHead` or `tFoot`. Playwright's implicit table-header role calculation needs `table.rows`. The perf half is moot until it exists. | M |
| 9a5d9c6 | Isolate CDP connection storage with shared entries | partial | Connections already get isolated contexts with a fresh `LocalStorage` (`BrowserContext.IsolatedCopy`, `BrowserContext.cs:170`; `Cdp/Server.cs:377-388`). Copy-on-write snapshot and merge at close only matter once localStorage persists (f661c2e). Port together. | L |
| 25700b9 | native select option ownership and legacy APIs | partial | `select.options` is `_qsa(this, 'option')` (`bootstrap.js:6446`), so a nested `datalist` or `select` contributes options. `add` and `remove` exist. Missing: `namedItem`, `length` setter with the 100,000 cap, `option.index` ownership. | L |
| 7a2b9ae | Route child-frame CDP calls through their evaluation worlds | already | Child-frame default contexts and per-world realms (todo.md:1816, M6). | - |
| 12b40f6 | expose parent child frames and named siblings across origins | port | A frame's `parent`/`top` is `_RemoteWindow`, with `length` 0, `frames` returning itself and no indexed or named access (`bootstrap.js:18416-18430`). Cross-origin `parent.frames[i]` / `parent.frames['name']` are needed for Stripe Elements. Needs host ops for the frame hierarchy. | M |
| bcf6d03 | Track child windows by browsing context across frame lifetimes | port | Builds on 12b40f6. Browsing-context records that survive navigation and keep remote Window identity, and SecurityError on forbidden cross-origin property access (C#'s `_CrossOriginFrameWindow` returns undefined). Queued message sources must survive retirement. | M |
| f152fcf | parse the foreign xmlns attribute without an empty prefix | already | `Dom/HtmlTreeBuilder.Tables.cs:206` maps `xmlns` with a null prefix. | - |
| 9b05070 | resolve the calling frame document in node-id operations | partial | Image and canvas ops are frame-bound (`PocketCalculatorOps.cs:552-563`). `op_script_try_start` and `op_script_mark_started` are not (see Security). Rebind them in `BindDocumentOps` against `document`. | H |
| a695bfc | skip classic scripts marked nomodule | already | `bootstrap.js:3627` (dynamic), `Page.DocumentLoad.cs:304` (parser). | - |
| 36e7987 | reject empty HTTP error navigations | port | No check in C#. A 4xx/5xx with an empty body commits an empty document. Add `net::ERR_HTTP_RESPONSE_CODE_FAILURE` after recording the Document network event. Confirm Chromium and Puppeteer `goto` rejection first. | M |
| 5e526a0 | gate renderer-only helpers in no-render builds | skip | Rust cargo feature gate. | - |
| 0ecc417 | preserve same-document history traversal | port | `Cdp/Domains/Page.cs:1399-1462` always reloads through `NavigateWithWaitAsync`. Upstream traverses within the document (popstate, `Page.navigatedWithinDocument`, no `frameNavigated` or context clear) and syncs pushState entries into `Page.History`. Needs 1a0773d first. The take/traverse/reset object goes on `__obscura_host`. | M |

#### Suggested port order

1. **Security and small fixes:** 5bfac24 (SSRF parse); 9b05070 (rebind the script ops per frame); c8e5b9e (link by node id); add the WebStorage total and origin caps.
2. **Network fidelity for CDP clients:** 2d323dd (Fetch pattern filter); 359362f (resource destinations, which changes the op argument); 3f37d91 + 73358fd (module events and bodies, bodies kept across suspend); 0155807 (AbortSignal).
3. **Navigation outcomes:** 36e7987 (empty error response fails); 0a14a4c (network-idle deadline); 22eb6b4 + 335f929 (plain-text documents).
4. **History:** 1a0773d (with ba807f4), then 0ecc417. Use the host object, the scheme gate, and no history in frames.
5. **MCP:** wire the console feed (c6402a7 / #951); a3aea0a (evaluate errors, promises, timeout, fill_form submit); dcdc6d0 (restore by declared origin into the host stores).
6. **Storage:** f661c2e persistence + 677f6d2 (retry, budgets) + MCP `--storage-dir`; then 9a5d9c6 (per-connection copy-on-write); then 3dea548 (IndexedDB).
7. **Frames, within the separate-engine design:**
   - 20299be (fire load after the child's scripts run).
   - 12b40f6 + bcf6d03 (cross-origin `frames[]`, named siblings, SecurityError).
   - 8dddc6f (stand-in Location navigates the frame).
   - 47a3431 (ignore foreign iframes).
   - 1737a4b (defensive `parent`/`top`).
   - Real-realm `contentWindow` for 24d8742, 47a3431 and 911c45c needs a design decision first; record it in todo.md.
8. **DOM and document:** 721e40f (document.open replaces the document, compatMode; with open `2b07b76`); d8f1467 (`table.rows`); 25700b9 (select ownership); 60107d1 (honest navigation timing).

### Net, cookies, robots, SSRF, MCP and CLI


41 commits from `group-netmcpcli.txt`. Each diff and its tests were read against the C# under
`dotnet/src/PocketCalculator.{Net,Mcp,Cli}` (and `.Browser`/`.Js` where a commit reaches them).
This is code reading only. Nothing was built or run, and anything marked "unverified" needs a test.

**Count: port 23 / partial 6 / already 6 / conflict 1 / skip 5** (41)

#### Security

High

- **`8162c0f` Expires parsing has no bounds: exposed.** `CookieJar.TryParseHttpDate`
  (`Net/Cookies/CookieJar.cs:1099-1157`) parses the year as a `ulong` and walks
  `for (y = 1970; y < year; y++)` (line 1142). So `Set-Cookie: a=b; Expires=Thu, 01 Jan
  99999999999 00:00:00 GMT` from any server, or the same string through `document.cookie`,
  hangs a thread for practical purposes. The loop runs on the transport and op path. It is not
  under `WorkCancellation`, and a V8 interrupt cannot stop it. Day 0 also underflows
  (`day - 1` on a `ulong`), and the final `(long)` cast can wrap, which gives a garbage expiry
  that can delete the cookie. Fix: check the RFC 6265 5.1.1 ranges (day 1..31, year 1601..9999,
  hour 23 or less, minute and second 59 or less) before any arithmetic, and treat a date outside
  them as a session cookie. Port this before `a134a7e`, which reuses the parser for
  Last-Modified and Date.
- **`a449a80` stale MCP element refs: exposed.** `BrowserState.RefToSelector`
  (`Mcp/BrowserState.cs:249-258`) only checks that the ref is in the table. It does not check
  that `[data-obscura-ref=eN]` still resolves to the recorded node. Refs are reused as `e1..eN`
  on every snapshot (`Tools.cs:570-580`). A page that clones or re-renders an element (the clone
  copies the attribute), or a later snapshot that renumbers elements, makes an agent's click land
  on a different element (upstream's test: an old `Buy` ref clicks a new `Delete`). This is an
  integrity bug in agent actions, and page content can trigger it.

Medium

- **`0e17221` robots cache unbounded: exposed** when `--obey-robots` is on. `RobotsCache._cache`
  (`Net/Robots.cs:8`) keeps one entry per origin for the life of the context, with no eviction
  and no 500 KiB body cap. A long-lived `serve` context grows without bound.
- **`b3475a5` / `7a8c0e3` / `fd5c729` cookie prefixes on import: partly exposed.** Set-Cookie and
  `document.cookie` already enforce `__Secure-`/`__Host-`, case-insensitively, including an
  explicit `Path=/` and no Domain (`PrefixRulesHold`, `CookieJar.cs:346-363`, called at :235
  with `hasPathAttr ? path : null`). `SetCookiesFromImport` (`CookieJar.cs:753-830`), which
  backs CDP `setCookie(s)`, `cookies.json` load and MCP storage-state restore, has **no prefix
  check**. A domain-scoped or non-Secure `__Host-` cookie can be imported. Those callers are
  trusted control planes, so the exposure is low, but a planted or edited `cookies.json` keeps it.
- **`8162c0f` IP-literal Domain: not exposed on Set-Cookie** (`TryResolveCookieDomain` refuses
  any Domain on an IP host except the address itself, `CookieJar.cs:1208-1214`, a recorded
  deviation). **Defence in depth is missing:** `DomainMatches` (`CookieJar.cs:1261`) still
  suffix-matches IP addresses. An imported, non-host-only cookie on `168.1.100` would go to
  `10.168.1.100`. Add upstream's "never suffix-match when either side is an IP" check.

Low / not exposed

- **`733a85e` Teredo: not exposed.** `SsrfGuard` already refuses all of `2001:0::/32`
  (`Net/Http/SsrfGuard.cs:105`, deviation I9), which is stricter than upstream's check of the
  embedded addresses.
- **`b3475a5` poisoned jar lock: not applicable.** .NET `Lock` does not poison.
- **`1b259b9` cookie-store privacy: not exposed.** The directory is created 0700 and the file
  0600 already (`CookieJar.cs:906-960`, `BrowserContext.cs:202`, SECURITY.md I4). Still missing:
  a failed save is swallowed silently (`BrowserContext.cs:208`), where upstream logs an error.
- **`1922f4a` Secure cookies on loopback HTTP.** This relaxes rather than hardens. C# treats only
  `https` as a secure cookie context (`CookieJar.cs:229`, `:544`, `:622`). Chromium treats
  `http://localhost`, `*.localhost`, `127.0.0.0/8` and `[::1]` as secure for cookies. Port it,
  scoped to loopback hosts only.
- **`9855ef4` `web_search`** sends agent queries to a third party (Keenable). Upstream lists the
  tool only when `OBSCURA_WEB_SEARCH=keenable` is set, and fetches through the context client
  (SSRF guard, proxy). Keep the opt-in. The port reads `POCKETCALCULATOR_WEB_SEARCH`, and parity
  maps the name.
- Not from upstream, found while reading: `Max-Age` near `long.MaxValue` overflows `Now() + secs`
  (`CookieJar.cs:187`, unchecked), so the cookie is stored as expired and deleted. Chromium also
  caps expiry at 400 days, and the port caps neither Expires nor Max-Age.

#### Per commit

| sha | subject | class | C# evidence / what to port | priority |
|---|---|---|---|---|
| cbc2f97 | map search offsets back through Unicode case-folding | port | `Mcp/Tools.Tier2.cs:501-502` lowercases with `ToLowerInvariant` and uses the haystack offset in the body. UTF-8 length still changes (U+0130 is 2 bytes and becomes `i`, 1 byte). Port `search_body` with the haystack-to-body offset map. Use Rust's full lowercase mapping (`İ` to `i̇`) so offsets match on the wire. | M |
| f75d23d | grow the TextDecoder output buffer instead of truncating | already | `Net/Encoding/ContentEncoding.cs:47` goes to `WhatwgEncoding.DecodeTable`, which uses a `StringBuilder`, or to .NET `Encoding.GetString`. There is no fixed output buffer. Port the test (`0x80` x12 in windows-1252, fatal and non-fatal). | L |
| 0e17221 | bound the robots.txt cache | port | `Net/Robots.cs:8,14-21`: no entry cap, no body cap. Add 4096-origin FIFO eviction and a 500 KiB body cut at the last complete line. | M |
| 8162c0f | bound Expires fields; stop suffix-matching IP cookie domains | partial | Missing: field bounds in `TryParseHttpDate` (`CookieJar.cs:1099-1157`, unbounded year loop, day 0 underflow), and the IP guard in `DomainMatches` (:1261). Present: IP-origin Domain refusal (:1208-1214). | H |
| b3475a5 | enforce cookie prefixes, survive poisoned lock, match robots tokens | partial | Prefixes on Set-Cookie and JS: already (`CookieJar.cs:235,346-363`). Lock: N/A. Missing: prefix check in `SetCookiesFromImport` (:753), and RFC 9309 product-token matching in robots. `Robots.cs:94-96` still substring-matches both ways, so `User-agent: Moz` governs a Mozilla UA. | M |
| 7a8c0e3 | require explicit Path=/ for __Host- cookies | partial | Set-Cookie and JS: already (`hasPathAttr ? path : null`, `CookieJar.cs:151,172,235`). Missing: `__Host-` validation on CDP and cookies.json import (with b3475a5). | M |
| 733a85e | refuse Teredo addresses that embed a forbidden IPv4 | already | `Net/Http/SsrfGuard.cs:105` refuses all of `2001:0::/32` (I9). Port upstream's 4 asserts. The public/public case is refused here, so assert true, and note the deviation in the test. | L |
| 1922f4a | allow secure cookies on loopback HTTP | port | `CookieJar.cs:229` (set), `:544` (collect), `:622` (WebSocket) use `scheme == https`. Add `IsSecureCookieContext`: https, or http to localhost, `*.localhost`, or a loopback IPv4/IPv6 address. | M |
| 15dcbc3 | give the scrape worker time to start in robots test | skip | Rust test timing only. Check the C# obey-robots scrape test's budget if it flakes. | L |
| 11d6ea8 | expose final HTTP status in worker navigation responses | port | `Cli/WorkerHost.cs:115-134` returns only `title` and `url`. Add `status`: the first `Document` network event with an http(s) URL, else `null`. This is a wire change, so match the field order exactly. | M |
| afb0d61 | keep blocked requests without a fabricated HTTP status | port | With 11d6ea8: `status == 0` (blocked) gives `null`. | M |
| ec0186b | accept per-navigation readiness modes | port | `WorkerHost.cs:115`: read optional `waitUntil` (`load`, `domcontentloaded`, `networkidle0`, `networkidle2`, strict) and call `Page.NavigateWithWaitAsync` (`Browser/Page.Navigation.cs:77`). An invalid value gives serde's `ok:false` text. | M |
| 9a6dd86 | explain local network access denials | port | No hint exists. Add `PrivateNetworkErrorHint` to Net, matching the `SsrfGuard.cs:218,226,238` messages (strip `Network error: `). Apply it to CLI fetch and navigate errors and to stdio MCP only (not MCP HTTP). Metadata addresses (169.254.x, 100.100.100.200, fd00:ec2::254) get no hint. The text uses `--allow-private-network` / `POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK=1`, and parity unbrands it. | M |
| 3aa2f59 | resolve fetch responses before buffering their bodies | port | `op_fetch_url` still returns the whole body and `bootstrap.js` has no `_fetchBody`, `op_fetch_body` or `op_try_close`. `fetch()` resolves only after the full body, where Chromium resolves at headers. Large Js/Net change: an async body resource, cancel on `body.cancel()`, opaque bodies empty, clone refcount. Keep the C# internal-load `bodyToken` (C3) and the fetch deadline (M3). | M |
| 15c00de | accept folded HTTP/1 response headers | already | `SocketsHttpHandler` accepts obs-fold continuations (CRLF or bare LF followed by SP/HT) in response headers. Unverified: port upstream's fixture (folded CSP after a bare LF) as a test. The wreq half is skip. | L |
| f2b9e99 | reap and restart failed CDP workers | port | `Cli/Commands/ServeCommand.cs:183-196,274`: no per-worker supervisor. A worker that dies keeps receiving its round-robin share. Port: a supervisor per child (100 ms retry, 5 s readiness wait), an availability flag, skipping unavailable workers, 503 when none is available, and inheriting worker stderr. | M |
| 657cede | remove expired resource keys from eviction order | already | `Net/Http/PocketCalculatorHttpClient.cs:1711-1726` (`Get` removes an expired entry from `_insertionOrder`), and `Insert` re-queues it. Port the two tests. | L |
| a4dca82 | bound spawned batch jobs by concurrency | already | `ScrapeCommand.cs:78-104` and `FetchCommand.cs:661` use a `SemaphoreSlim`. Only a lightweight `Task` per URL is created up front, and work, processes and pages stay bounded by `--concurrency`. Results stay in input order. | L |
| 4b14a76 | stop workers on SIGTERM/SIGINT, pass --max-connections | partial | `--max-connections` is forwarded already (`ServeCommand.cs:452-453`). Missing: `PosixSignalRegistration` for SIGTERM/SIGINT in the balancer, which should send SIGTERM to workers, wait 3 s, then kill. Today a SIGTERM to the balancer orphans its workers. | M |
| 52d8ddd | include the error's cause chain in network error messages | partial | `PocketCalculatorHttpClient.cs:823,896,900` use `error.Message` only. .NET TLS failures say "...see inner exception". Append each `InnerException` message once (skip one already contained). The message text cannot match Rust byte for byte anyway. | L |
| 7500e6d | browser_evaluate awaits a promise; add browser_wait_for_function | port | `Mcp/Tools.cs:279-286` evaluates synchronously. `ToolSchemas.cs` has no `timeoutMs` and no `browser_wait_for_function`, so the tool JSON diverges (`ToolListMatchesRustSource`). Also brings in `OBSCURA_STORAGE_DIR` for MCP: C# `BrowserState` (`BrowserState.cs:46-49`) ignores `--storage-dir`, which means `POCKETCALCULATOR_STORAGE_DIR` here. Put the settle slot in host state rather than a page-visible `__obscura_mcp_expression` global (page-visible-global rule). | H |
| 5171717 | accept explicit font directories before startup | port | `CliDefinition.cs:296`: `mcp` has no `--font-dir`. Add it, and validate before either transport starts (messages `Font directory does not exist`). | L |
| 604d30b | use existing cookie persistence on shutdown | port | `McpServer.cs:84-87` returns on EOF with no save. Call `Context.SaveCookies()` (needs the storage dir from 7500e6d). | M |
| 0f27e69 | recycle workers by connections, RSS or age | port | New serve flags `--worker-max-connections/-rss/-age/-drain-timeout` and the pool, drain and replace logic. Depends on f2b9e99 and 4b14a76. Off by default. RSS is read from /proc on Linux. | L |
| 52126c4 | read the release tag from source tarballs | skip | Rust `build.rs` only. The C# version is CalVer from the pipeline, or resolved at run time (`Cli/CommandLine/BuildVersion.cs`). | L |
| a449a80 | reject stale element references before acting | port | `BrowserState.cs:249-258` plus `Tools.cs:570-580`. Add a generation prefix (`e{gen}-{i}`) and check before acting that the selector resolves to the stored `NodeId`, else `stale ref '...'; call browser_snapshot to refresh the ref table`. This changes the ref text on the wire. | H |
| 5c27e76 | drive active pages between HTTP requests | port | `Mcp/Http.cs:671-688`: the HTTP dispatcher only processes requests. Pump `AdvanceActivePageTasksAsync` between requests, as stdio does (`McpServer.cs:66-80`), and finish navigation outside the cancellable wait. | M |
| 05d7c13 | preserve runtimes across tab switches | conflict | `BrowserState.cs:107-127` suspends inactive tabs on purpose ("observable", like the CDP "Page suspension stays" decision, todo `d792bae`). Chromium keeps every tab's realm, so Chromium wins: port it together with the `d792bae` decision. The ClearScript LIFO concern does not apply. | M |
| 1949e9b | make --user-agent a global flag | port | `CliDefinition.cs:64` keeps `RootUserAgent` root-only, and `CliArgs.cs:209` reads it, but nothing uses it. `scrape` has no UA option. Make it global (subcommand wins) and forward it to the scrape worker as `POCKETCALCULATOR_USER_AGENT`. `WorkerHost.cs:57` reads only `_OBEY_ROBOTS`. | M |
| b13604b | drive page tasks during function waits | port | Comes with 7500e6d's `browser_wait_for_function`. `PumpForAsync` (`Tools.cs:339`) is the C# helper. Cap the last interval at the deadline. | M |
| 004bcdd | native UTF-8 decoding; preserve explicit encodings | port | (1) `bootstrap.js:11905-11952` `_utf8DecodeBytes` emits garbage for malformed UTF-8 where Chromium emits U+FFFD. Route non-fatal, non-ignoreBOM UTF-8 to a native op, keeping the M4 memory bound. (2) `ContentEncoding.DecodeWithLabel` goes to `WhatwgEncoding.Decode`/`DecodeFatal` (`SniffBom`, `WhatwgEncoding.cs:110,136,212`), which lets any BOM override the label. TextDecoder must strip only the label's own BOM (`utf-8` + `FF FE` gives `��`, `windows-1252` + `EF BB BF` keeps the bytes). Record it as a shim deviation. | M |
| 8dd9d48 | keep consecutive user-agent lines in one group | port | `Robots.cs:66-150`: rewrite as groups (consecutive UA lines, the first rule closes the group), and `IsAllowed` (:36-62) as longest match wins, Allow wins a tie. | M |
| 9e0b8b6 | separate wildcard and named agent matches | port | Follows 8dd9d48: a named match selects only named groups, else `*` groups. | M |
| 9e1523e | avoid redundant certificate input buffering | skip | `vendor/wreq-util` zstd certificate decompression is in the stealth TLS stack (recorded gap). | L |
| 9855ef4 | correct PR #1373 (web_search MCP tool) | port | No `web_search` in C#. Opt-in tool (env `..._WEB_SEARCH=keenable`, `KEENABLE_API_KEY`). The tool list entry is conditional, byte-identical. POST through the context's `RequestClient` (SSRF and proxy), 1 MiB response cap, output formatting as upstream. | L |
| a134a7e | Last-Modified heuristic freshness; stricter max-age | port | `PocketCalculatorHttpClient.cs:1568-1604`: no heuristic (10% of Date minus Last-Modified, 24 h cap, status 200/203/204/206, no Expires, age and response delay subtracted). Lenient `max-age` parsing: a repeated, quoted, invalid or spaced value must give no cache. Needs the bounded date parser from 8162c0f. | M |
| 1b259b9 | keep the cookie store private and report failed saves | partial | Already: 0700 directory and 0600 file (`CookieJar.cs:906-960`, I4). Missing: log an error on a failed save (`BrowserContext.cs:204-211` swallows it), and a test that an uncreatable directory makes `SaveToFile` throw. | L |
| fd5c729 | restore imported public-suffix cookies with host-only scope | port | `SetCookiesFromImport` (`CookieJar.cs:765-770`) drops a public-suffix domain. Upstream stores it host-only instead, and a dotted Domain still invalidates `__Host-`. Also affects MCP `set_cookie` and `set_storage_state`. | L |
| 79b9ca9 | rebuild the version string when git HEAD moves | skip | Cargo rerun-if only. C# runs `git rev-parse` at run time (`BuildVersion.cs`), so it never goes stale. | L |
| 4861843 | watch missing packed branch refs | skip | Same as 79b9ca9. | L |
| 4e857be | handle closed fetch output pipes | already | .NET ignores SIGPIPE and the Unix console stream drops `EPIPE` writes (`Console.Out`, `OpenStandardOutput`, `FetchCommand.cs:732,761,784`). Unverified: port upstream's closed-pipe test. Other write errors (ENOSPC) surface as an unhandled IOException where Rust prints `Failed to write to stdout: ...`. | L |

#### Suggested port order

1. **Cookie hardening (security):** 8162c0f date bounds and IP `DomainMatches`, then
   b3475a5 + 7a8c0e3 + fd5c729 as one import-path rule (prefix check plus host-only for public
   suffixes), then 1922f4a for loopback secure contexts. Also cap Max-Age and Expires at 400 days.
2. **MCP integrity:** a449a80 stale refs. Then 7500e6d + b13604b (await, wait_for_function,
   tool JSON, MCP storage dir) and 604d30b (save on EOF). Then 5c27e76 (HTTP pump).
3. **Robots:** 0e17221 cap, then the robots part of b3475a5, 8dd9d48 and 9e0b8b6 together
   (rewrite `Robots.cs` once).
4. **Worker protocol:** 11d6ea8 + afb0d61 (status), ec0186b (waitUntil), 1949e9b (global UA and
   scrape forwarding).
5. **serve supervision:** f2b9e99 (restart), 4b14a76 (signals), then 0f27e69 (recycling).
6. **Fetch and encoding:** 004bcdd (TextDecoder BOM and malformed UTF-8), 3aa2f59 (resolve at
   headers), a134a7e (heuristic caching, after step 1's date parser).
7. **Operator UX:** 9a6dd86 hints, 52d8ddd cause chain, 5171717 `mcp --font-dir`, the
   1b259b9 error log, cbc2f97 search offsets.
8. **Decision:** 05d7c13 together with `d792bae` (keep every tab's realm, measure memory first).
9. **Opt-in feature:** 9855ef4 web_search.
10. Test-only ports for the "already" rows: 733a85e, 657cede, f75d23d, 15c00de, 4e857be.

### Skipped: README, docs, CI, packaging, dependency bumps

- `d0137f8` docs(stealth): document TCP/IP passive fingerprint as known limitation
- `aa90521` chore(deny): ignore the rustybuzz and ttf-parser unmaintained advisori
- `5ba6c05` Update README.md
- `e31c5dd` Document scoped builds and snapshot recovery
- `1eba7ef` Correct effective V8 heap flag documentation
- `0434933` chore(deny): allow the IJG license for jpeg-encoder
- `2f6e09a` Update README.md
- `75bb852` Merge pull request #1133 from h4ckf0r0day/fix/focus-scroll-20260930
- `43399c2` ci: populate trusted dependency caches for PR checks
- `055453c` docs(skill): document --allow-private-network for local fetches
- `8912562` fix(ci): require all obstacle stages before release
- `9b0383f` test(ci): isolate obstacle summary output
- `e71d6dd` docs: document worker recycling options
- `3aba28e` ci: recover incomplete V8 native-library caches
- `a8814c4` ci: discard incomplete archives and reject directory cache paths
- `d4b3e57` fix(deps): update rustls for security advisories
- `374ff48` fix(ci): measure process exits without timeout polling
- `a400061` docs: correct the top-level flag scope claim in the CLI reference
- `62354c7` Update README.md
- `b1acb89` Update AGENTS.md
- `7ec0a28` Add files via upload
- `8b49681` Update README.md
- `26552e2` Update README.md
- `fdeac44` Update README.md
- `bdb6fa1` Update Tokio and TLS verification dependencies
- `cb585bb` Fail performance CI on obstacle latency regressions
- `6901d7e` Run CI helper tests for CI-only pull requests
- `f081135` ci: catch sustained DOM and CPU regressions
- `fe86888` docs: correct navigator.webdriver value
- `3964312` ci: approve the reviewed compression backport snapshot
- `5150dc6` ci: approve the buffered certificate decoder snapshot
- `b2861c1` docs: remove GitBook and use GitHub documentation
- `1693ad5` docs: align guides with current CLI and CDP behavior
- `e382826` Update README.md
- `8b1841a` fix(install): correct Scoop manifest and Windows instructions
- `673f522` Bundle and validate Windows runtime dependencies
- `bd0044d` docs: list the full SSRF deny-set under OBSCURA_ALLOW_PRIVATE_NETWORK
- `196d10c` ci: freeze the release binary before workspace tests
- `bfb300e` ci: validate capture cleanup on native ARM64 (#1575)
- `b28cf06` fix(packaging): update Scoop to v0.2.7 (#1605)

