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

## Remaining profiling candidates

1. Loading/connecting/sidebar indicators use separate dispatcher timers. Some only test their own visibility, so an invisible ancestor can leave timers ticking. Consolidate or move suitable animations to the compositor, with effective-visibility tests.
2. ChatMarkdown refresh walks descendants immediately and again after layout for streamed text. Measure batching rendered markdown to a frame cadence; preserve copy/selection and final-token delivery.
3. Terminal link detection builds text/cell mappings for visible logical lines when drawing or hit-testing. A revision/scroll-aware cache may reduce allocation under heavy output; profile first to avoid stale links.
4. Workspace sidebar controls are rebuilt when catalog status changes, and the outer grouped layout is not fully virtualized. Large workspace/chat counts warrant incremental row updates and a flattened virtualized model.
5. Native AOT still reports existing markdown trimming warnings and a JsonArray generic-add warning in ChatRuntime. Native smoke testing remains useful after dependency upgrades.

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
