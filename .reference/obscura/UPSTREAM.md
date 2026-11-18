# Upstream sync point

This tree is `https://github.com/h4ckf0r0day/obscura` at:

- Commit: `4eb123528b40c25ee015ed53b4ebdad4b9069898`
- Date: 2026-11-18
- Subject: Merge pull request #770 from alexskinner/fix/computed-inset-properties

Previous sync point: `727cc46d56290995245fbe790caed52fc699452a` (2026-09-06).

moved from the repository root into `.reference/obscura/`, plus the edits the fork
made to it before the Rust tree became read-only. The C# port in `dotnet/` does not
yet cover upstream up to this commit: the tree was advanced on 2026-11-18 and the
commits still to port are queued in `todo.md` under "Upstream sync 1a3169d..4eb1235"
(review in `dotnet/docs/upstream-review-2026-11.md`).

## Where this tree differs from that commit

Build and CI were switched off when the port started (`e64d15d`):
`.github/workflows/*.yml`, `scripts/ci/*.py` and `Dockerfile` carry a `.disabled`
suffix, and `skills/obscura/SKILL.md` was adapted for the port. `build.log`,
`.gitignore`, `.gitattributes` and `tools/` stay at the repository root.

Early in the port some fixes were made to both engines at once, so the reference
binary kept parity with the C#. They touch `crates/obscura-js/src/runtime.rs` and `crates/obscura-render/src/{dom,inline,lib,paint,style}.rs`
with `crates/obscura-render/tests/layout_test.rs`:

- `1837d51` Implement canvas 2D in the shared shim, replacing a one-operation stub
- `97cf9f8` render: give blurred box-shadow a gaussian falloff in both engines
- `d55c362` render: honor radial-gradient length stops and the shorthand's color layer
- `8ed5c49` render: paint inset box-shadow, and size buttons with the shaper
- `71a0d1d` render: implement filter: blur() in both engines
- `c974d15` render: implement backdrop-filter: blur() in both engines
- `af1e63a` render: draw rounded corners as arcs, not parabolas
- `86740c6` Resolve calc() percentages against a flex item's used width
- `8747a6e` Fix six bootstrap.js defects found driving a real SPA over CDP
- `761d732` innerText: tab-separate table cells like Chromium
- `172014d` Report the subresources the host fetched, and answer fonts.check() from the renderer
- `7ef4f00` Keep the document on a fragment navigation, and fire the right events

They also touched `crates/obscura-js/js/bootstrap.js` (`1837d51`, `8747a6e`,
`761d732`, `172014d`, `7ef4f00`). At the `4eb1235` sync upstream had rewritten the
same regions (location, history, innerText, element interfaces, canvas), so the
file was taken from upstream whole: those edits now live only in the port's own
copy, `dotnet/src/PocketCalculator.Js/js/bootstrap.js` (CLAUDE.md, rule 5). The
other conflicts at that sync kept both sides: the fork's tests next to upstream's
new ones, the fork's `measure_control_label` in `dom.rs`, and the fork's expected
`CSS.supports` value in `runtime.rs` under upstream's formatting.

No further edits are made here (CLAUDE.md, rule 1). Because of these, an upstream
diff does not apply cleanly to every file; the sync procedure in the root
`CLAUDE.md` applies it three-way, and conflicts land in those files only.

`render-repros` is a symlink to the repository's own `render-repros/`, which the
Rust render tests `include_str!` from.
