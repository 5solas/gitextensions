# W28: Startup Profiling and Pre-Git .NET Gap Optimization Report

## 1. Executive Summary

- Comprehensive profiling of GitExtensions startup across two repositories (`gitextensions` and `git/git`) revealed that the pre-git delay was dominated by UI thread .NET overhead: MEF assembly resolution storms on satellite resources (~720–1,100 ms), English XML translation deserialization and control traversal (~950–1,200 ms), and redundant remote config parsing (~100 ms).
- We implemented three targeted, merge-friendly fixes in `ManagedExtensibility.cs`, `Translator.cs`, and `GitModule.ConfigSnapshot.cs` (total diff: 4 files, +40/-4 lines, with ≤ 6 lines changed in existing upstream files).
- Pre-git offset dropped from 640.9 ms to 365.8 ms (-42.9%) in `gitextensions` and from 1,463.9 ms to 275.4 ms (-81.2%) in `git/git`. Real application warm startup time to window display improved by 41.2% (2,704 ms → 1,591 ms), while strictly maintaining the zero UI-thread git process budget.

---

## 2. Ranked Breakdown Table

Profiling conducted with `dotnet-trace` (CPU sampling + .NET runtime events) on .NET 10.0 x64.

| Rank | Bottleneck Area | Self (ms) | Inclusive (ms) | % of Pre-Git Startup | Callstack Hint | Implemented / Actionable Fix |
|---|---|---|---|---|---|---|
| **1** | MEF AssemblyResolve Probing Storm | ~350 ms | **1,099 ms** | 27.6% | `ManagedExtensibility.CurrentDomain_AssemblyResolve` → `FileVersionInfo.GetVersionInfo` | **Fixed**: Short-circuit `.resources` requests and base directory requesting assemblies (+4/-2 lines). |
| **2** | English Translation XML & Reflection | ~280 ms | **980 ms** | 24.6% | `Translator.GetTranslation` → `XmlSerializer` → `Translator.TranslateControls` | **Fixed**: Bypass XML load and control reflection when language is "English" (+1/-1 lines). |
| **3** | JIT Compilation of WinForms UI Controls | ~850 ms | **850 ms** | 21.4% | `FormBrowse.InitializeComponent` → ToolStrips, Splitters, Grid JIT | Evaluated: Recommended enabling `PublishReadyToRun=true` in build pipeline. |
| **4** | Remote Settings Config Parsing | ~40 ms | **110 ms** | 2.8% | `RevisionGridControl` → `GitModule.GetRemoteColors` → `ConfigFileRemoteSettingsManager.LoadRemotes` | **Fixed**: Read remote colors directly from in-memory `GitConfigSnapshot` (+32 lines partial). |
| **5** | Status & Revision History Data Source Init | ~65 ms | **95 ms** | 2.4% | `RevisionGridControl.CreateControl` → BindingSource / DataGridView setup | Acceptable baseline initialization cost. |
| **6** | Ghostty Terminal Console Control | ~8 ms | **12 ms** | 0.3% | `ConEmuHostControl` / Terminal creation | Lazy by default; terminal initialization deferred until tab selected. |

---

## 3. Flamegraph & Speedscope Artifact Pointers

Trace data collected via `dotnet-trace` and converted to Speedscope JSON format:

- Baseline Real App Cold (`gitextensions`): `realapp_ge_debug.speedscope.json` (로컬 보관, 저장소에 포함하지 않음)
- Baseline Real App Warm (`gitextensions`): `realapp_ge_warm.speedscope.json` (로컬 보관, 저장소에 포함하지 않음)
- Baseline Real App Cold (`git/git`): `realapp_git_debug.speedscope.json` (로컬 보관, 저장소에 포함하지 않음)
- Baseline Real App Warm (`git/git`): `realapp_git_warm.speedscope.json` (로컬 보관, 저장소에 포함하지 않음)
- Post-Fix Real App Warm (`gitextensions`): `realapp_ge_warm_after.speedscope.json` (로컬 보관, 저장소에 포함하지 않음)

---

## 4. JIT vs. R2R vs. NativeAOT Evaluation

### Runtime Knob Measurements (.NET 10.0 x64)

Benchmarked with `scratch/test_runtime_knobs.ps1` over 3 iterations per configuration:

| Configuration | Warm Avg Startup (ms) | Delta vs Default | Observation / Trade-off |
|---|---|---|---|
| **Default (.NET 10 Tiered JIT + PGO)** | **2,704.3 ms** | Baseline | Best overall balance for standard JIT. |
| `DOTNET_TieredPGO=0` | 2,658.7 ms | -45.6 ms | Slightly faster startup due to lower PGO tracking overhead, but slightly lower peak throughput. |
| `DOTNET_TC_QuickJit=0` | 2,916.3 ms | +212.0 ms | Tier 1 JIT at startup creates significant UI thread stalls. |
| `DOTNET_TC_QuickJitForLoops=0` | 2,898.7 ms | +194.4 ms | Disabling loop quick JIT increases compilation overhead. |
| `DOTNET_TC_QuickJitForLoops=1` | 2,868.0 ms | +163.7 ms | Marginal slowdown compared to default loop tiering. |
| `DOTNET_TieredCompilation=0` | 2,827.7 ms | +123.4 ms | Monolithic full JIT hurts interactive startup latency. |

### ReadyToRun (R2R)
- **Feasibility**: High. GitExtensions can be compiled with `<PublishReadyToRun>true</PublishReadyToRun>`.
- **Expected Impact**: Reduces JIT compilation time during `InitializeComponent` by ~400–600 ms on first start.
- **Trade-off**: Increases binary size by ~2x (~50 MB → ~100 MB). Fully compatible with WinForms and MEF plugins.
- **Recommendation**: Enable R2R for the Release installer/distribution packages.

### NativeAOT
- **Feasibility**: Low (not viable today).
- **Blockers**: GitExtensions heavily depends on MEF (`System.ComponentModel.Composition`), dynamic XML serialization, COM interop, and dynamic plugin assembly loading, which violate NativeAOT trimming and code generation constraints.
- **Recommendation**: Do not pursue NativeAOT at this time; focus on R2R and targeted startup micro-optimizations.

---

## 5. Before / After Performance Comparison

Measurements collected using `eng/hj/Measure-RepoOpen.ps1` (5 iterations per repo) and `dotnet-trace`.

### In-Process Harness Benchmark (`docs/hj/perf/after-hj4/` vs `docs/hj/perf/after-w28/`)

| Repository | Metric | Baseline (after-hj4) | Post-Fix (after-w28) | Absolute Improvement | Relative Improvement |
|---|---|---|---|---|---|
| **gitextensions** | **First Git Process Start** | **640.9 ms** | **365.8 ms** | **-275.1 ms** | **-42.9%** |
| | First Batch Revisions | 1,028 ms | 518 ms | -510 ms | -49.6% |
| | Revisions Loaded | 1,320 ms | 786 ms | -534 ms | -40.5% |
| | Total Wall Clock | 1,451 ms | 899 ms | -552 ms | -38.0% |
| **git/git** | **First Git Process Start** | **1,463.9 ms** | **275.4 ms** | **-1,188.5 ms** | **-81.2%** |
| | First Batch Revisions | 1,192 ms | 536 ms | -656 ms | -55.0% |
| | Revisions Loaded | 2,123 ms | 1,484 ms | -639 ms | -30.1% |
| | Total Wall Clock | 2,216 ms | 1,582 ms | -634 ms | -28.6% |

### Real Application Window Startup

| Scenario | Metric | Baseline | Post-Fix | Improvement |
|---|---|---|---|---|
| `GitExtensions.exe` Warm Launch | `FormBrowse..ctor` Inclusive | 3,979.7 ms | 1,442.7 ms | **-63.7%** (-2,537.0 ms) |
| | `FormBrowse.InitializeComponent` | 3,360.1 ms | 1,047.6 ms | **-68.8%** (-2,312.5 ms) |
| | Time to Form Visible | 2,704.3 ms | 1,591.0 ms | **-41.2%** (-1,113.3 ms) |

### Git Process Budget Verification

| Repository | Total Git Processes (after-hj4) | Total Git Processes (after-w28) | UI Thread Processes (after-hj4) | UI Thread Processes (after-w28) | Budget Status |
|---|---|---|---|---|---|
| `gitextensions` | 11 | 11 | 0 | 0 | **Maintained (0 regression)** |
| `git/git` | 10 | 10 | 0 | 0 | **Maintained (0 regression)** |

---

## 6. Next Opportunities

1. **ToolStrip and Menu Lazy / Deferred Initialization**
   - **Description**: `FormBrowse.InitializeComponent` constructs dozens of ToolStrip items, menus, and dropdowns synchronously on the UI thread before the form is shown. Deferring non-visible menu dropdown construction until first click would shave off additional control instantiation and layout time.
   - **Estimated Gain**: ~150–200 ms.
   - **Risk**: Low (isolated to menu construction).

2. **PublishReadyToRun in Release Builds**
   - **Description**: Add `<PublishReadyToRun>true</PublishReadyToRun>` to the publish profile for standalone/installer releases. Pre-compiles IL into machine code ahead of time.
   - **Estimated Gain**: ~400–600 ms cold startup.
   - **Risk**: Low (transparent fallback to JIT for uncompiled methods; increased installer size).

3. **Asynchronous Left Panel Tree / Submodule Population**
   - **Description**: While git processes run off the UI thread, WinForms node creation in `LeftPanelControl` can trigger layout passes that intermittently contend with revision grid rendering.
   - **Estimated Gain**: ~50–100 ms to interactive revision grid.
   - **Risk**: Medium (requires thread synchronization with the UI tree view control).
