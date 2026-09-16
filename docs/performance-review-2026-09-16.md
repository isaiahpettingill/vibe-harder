# Performance review — September 16, 2026

This is a source-level review with focused regression tests and a Windows x64 NativeAOT publish. It is not a before/after CPU or working-set benchmark. No numeric RAM reduction is claimed.

## Fixed

| Area | Finding | Change |
| --- | --- | --- |
| Remote protocol cache | The revision cache retained full previous message strings and attachment arrays and allocated an attachment array even for unchanged replies. Clearing all 256 entries also invalidated unrelated revisions. | Weak-key message cache stores only revision metadata. Attachment edits increment the message revision. Evicted messages can be collected while the service remains alive. |
| Remote transcript polling | Linear lookup for every returned row made each refresh quadratic in transcript length, including unchanged rows. | Skip unchanged rows before lookup; lazily build one ID index when changes arrive. |
| Remote catalogs | Every poll replaced workspace/chat selector collections even when the response was identical. | Preserve selector instances for unchanged catalogs; still process permission responses and explicitly requested navigation. |
| Hidden remote views | Sleeping stopped full chat polling but kept transcript controls and markdown renderers attached. | Detach the displayed page while sleeping, retain its bounded data and scroll anchor, and restore it on wake. |
| Terminal redraws | Each output update posted another underline redraw; model callbacks were not restored on detach. | Coalesce outstanding redraw requests, skip invisible redraws, and restore callbacks when the view detaches or changes models. |

## Builds and dependencies

- All 16 distinct direct application package references were checked against NuGet. Avalonia 12.1.2, the markdown prerelease, terminal library, and other application packages are current on their existing release channels.
- .NET 11 SDK 11.0.100-rc.1.26425.128 matches Microsoft's latest release metadata. Browser builds stay on .NET 10 because Avalonia's bundled WASM graphics libraries match that toolchain.
- Updated Microsoft.NET.Test.Sdk to 18.10.1, coverlet.collector to 10.0.1, and xunit.runner.visualstudio to 4.0.0.
- Tried xunit.v3 4.0.1: Avalonia.Headless.XUnit 12.1.2 fails discovery with MissingMethodException in TestIntrospectionHelper. Kept xunit.v3 3.2.2; targeted tests pass with the other upgrades.
- New major SkiaSharp/HarfBuzzSharp/SQLite transitive releases exist. These were not forced over the versions selected by Avalonia/SQLite; their managed/native compatibility needs a separate migration. This is not an assertion that every transitive package is latest.
- Release builds explicitly enable Optimize and JIT TieredPGO while retaining workstation GC. AOT, trimming, stripped symbols, compiled bindings, and bounded transcript virtualization were already enabled.
- Release x64 NativeAOT builds now use IlcInstructionSet=x86-64-v3 (Haswell-class AVX2). ARM and non-AOT builds do not get this baseline. Verified evaluated MSBuild properties and an actual Windows x64 AOT publish.

## Follow-up: visual trees and remaining candidates

| Area | Implemented change | Verification |
| --- | --- | --- |
| Indicators | One shared clock replaces per-indicator timers. Hidden ancestors, effective viewport exclusion, and detachment unregister indicators; the clock stops when empty. Spinner geometry is shared. Connecting dots pulse in size, without an opacity layer. | Ancestor hide/show and detach tests; sleep/wake preserves active sessions. |
| Streaming markdown | Coalesce updates into 33 ms batches, decorate once after layout using one descendant traversal, and cache the reflected document field. Stop pending timers on detach and flush the latest text on attachment/copy. Existing weak decoration caches retain no detached trees. | A 100-update burst remains deferred until the batch and displays the final update. Detached updates render on reattachment; rich selection, code copy, syntax highlighting, and renderer collection tests pass. |
| Terminal links | Bound the parsed-line cache to 256 entries, invalidate on output/model changes, column resize, and scroll offset, and reuse it for painting and hit-testing. | Overwriting and deleting a URL invalidates cached links; soft wraps, scrollback, model replacement, local file links, and remote restrictions remain covered. |
| Sidebar | Retain remote workspace/header/menu/list trees across status-only updates. Preserve item sources when membership/order is unchanged, index remote chats once per catalog, and release stale models/groups. Remove nested list scrollers so row virtualization uses the outer sidebar viewport. | 1,000 chats realize fewer than 30 row containers, including after scrolling. Status changes preserve control/model identity. Existing selection, order persistence, and mouse/touch drag tests pass. Lightweight group headers remain retained. |
| Composition | Preblend workspace tints into opaque theme-aware brushes held through weak cache entries. Replace translucent drag feedback and muted text/icons with opaque colors; use an opaque sidebar dismiss surface and flyouts. Disable popup/context-menu shadow hints. No application box shadows or blur effects were present. | Theme changes update existing tint brushes; desktop hover and mobile action visibility tests pass. |
| Clipping | Remove the transcript panel's redundant clip. Hide Android scrollbars instead of rendering them with zero opacity. | Verify the transcript remains inside a clipping ScrollContentPresenter. Keep the terminal underline viewport clip and the necessary terminal selection tint. |
| AOT | Use the JsonNode overload when adding recovered Dirac context, avoiding the generic serialization path and its IL2026/IL3050 warnings. | Windows x64 Speed/Haswell NativeAOT publish and native TLS host smoke checks pass. |

These are control reuse and bounded data caches, not full-page bitmap caches that would increase retained GPU/RAM usage. Hover-only actions still use opacity 0/1 to preserve layout and keyboard focus. The terminal's translucent text-selection overlay is necessary because the upstream renderer paints selection after glyphs.

The upstream Markdown.Avalonia and SyntaxHigh trimming/AOT warnings remain visible. Their reflection metadata is explicitly rooted in TrimmerRoots.xml; suppressing these warnings or removing the roots would not establish compatibility. The native smoke test covers pairing, persistence, restart, and workspace/chat RPC; native markdown interaction was not rechecked on a desktop window in this follow-up.

Validation: 32 focused visual/memory/terminal/sidebar/sleep tests passed; mobile and browser shared UI Release builds; Windows x64 NativeAOT publish; published-host smoke test. Full Android packaging and WASM native linking were not rerun. No before/after CPU, GPU, or working-set result is claimed.

## References

- [Avalonia performance troubleshooting](https://docs.avaloniaui.net/troubleshooting/app-performance-issues): compiled bindings, virtualization, shallow visual trees, and reduced-size image decoding.
- [Avalonia performance guide](https://docs.avaloniaui.net/docs/app-development/performance) and [performance tips](https://avaloniaui.net/blog/10-avalonia-performance-tips-to-supercharge-your-app).
- [.NET 11 performance improvements](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/) and [current release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json).
- [Native AOT optimization tradeoffs](https://learn.microsoft.com/dotnet/core/deploying/native-aot/optimizing) and [instruction set configuration](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/optimizing.md).

The supplied DEV, Reddit, and Raygun discussions were reviewed as background; changes are based on this repository and primary documentation. The supplied Steven Giesel page could not be retrieved.

## Size versus Speed experiment

Built the same working tree twice for Windows x64 Release NativeAOT, with x86-64-v3 in both. Both OptimizationPreference and IlcOptimizationPreference were explicitly set to Size or Speed, so the existing ILC setting could not override the experiment. Excluded .pdb/.dbg files as the release packager does; ZIPs use the same compression level. Raw publish directories also contain upstream native debug symbols, which are not shipped.

| Artifact | Size preference | Speed preference | Increase |
| --- | ---: | ---: | ---: |
| VibeHarder.exe | 46,777,856 bytes (44.61 MiB) | 59,704,832 bytes (56.94 MiB) | 27.64% |
| Stripped payload | 70,318,795 bytes (67.06 MiB) | 83,245,771 bytes (79.39 MiB) | 18.38% |
| Stripped ZIP | 32,701,837 bytes (31.19 MiB) | 35,670,905 bytes (34.02 MiB) | 9.08% |

Selected Speed for Release builds: roughly 2.83 MiB extra compressed download is modest. The comparison measures binary size, not execution speed or working-set RAM. Other architectures were not size-benchmarked. Explicit Release Optimize, TieredPGO for JIT builds, workstation GC, and the Haswell x64 AOT baseline remain enabled.

Validation: 36 focused memory/terminal/protocol/pairing tests passed; Windows x64 AOT publishes succeeded for both preferences. Mobile and browser shared UI Release builds passed. The final scroll-anchor restoration adjustment was checked separately with the memory tests.
