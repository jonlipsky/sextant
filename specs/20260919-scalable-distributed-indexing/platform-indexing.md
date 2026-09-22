# Platform-Aware and Client-Assisted Indexing

## Position

The Linux ProcessStack deployment can index most ordinary SDK-style C# projects, including many projects that target a platform different from the host. It cannot be assumed to faithfully evaluate every Windows, Android, iOS, macOS, Mac Catalyst, MAUI, custom SDK, or native-integrated project.

Sextant does not need to package or sign the final application, so it often needs fewer capabilities than a full build. It still depends on MSBuild project evaluation, SDK resolution, workload imports, reference assemblies, generated sources, analyzers, and design-time targets. Missing platform workloads can therefore prevent a correct Roslyn compilation even when final packaging is not requested.

The architecture uses capability-aware workers first and authenticated client/CI contributions second. It never silently downgrades to syntax-only indexing and reports completeness explicitly.

## Platform considerations

### Ordinary SDK projects

Projects targeting `netstandard`, ordinary `netX.Y`, and most portable libraries should evaluate on Linux when the required .NET SDK, package feeds, and restored assets are available.

### Windows-targeted projects

The .NET SDK supports targeting Windows from a non-Windows host when `EnableWindowsTargeting=true` and the required targeting packs can be restored. This is useful for many class libraries, WPF, and WinForms compilations, but it does not guarantee that custom Windows SDK imports, COM tooling, native tasks, source generators, or repository-specific build steps will evaluate on Linux.

Official reference: [NETSDK1100](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100).

### Android and optional workloads

Optional workloads are SDK feature-band and workload-set specific. Linux workers may install supported Android/MAUI workload packs, but images must be versioned and selected from the repository's `global.json` and workload requirements rather than mutating a shared worker opportunistically.

Official reference: [`dotnet workload install`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-install).

### Apple targets

Apple workloads and faithful app-project evaluation may depend on macOS-only Xcode and workload components. A Linux worker may be able to evaluate shared projects but must not claim complete Apple-target coverage when SDK imports, generated bindings, or reference packs are unavailable. macOS/Xcode workers are the authoritative route for those target frameworks.

.NET MAUI documentation identifies iOS and Mac Catalyst requirements and the need for a networked Mac for iOS development: [Supported platforms for .NET MAUI apps](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms).

### Custom SDKs and build tasks

Repositories can import arbitrary SDK resolvers, MSBuild tasks, source generators, generated inputs, and external tools. Capability discovery must treat these as explicit requirements or produce an unsupported diagnostic. MSBuild evaluation is sandboxed because project evaluation can execute repository-controlled build logic.

## Capability fingerprint

Each index request and worker advertises a comparable fingerprint:

```text
host_os
host_architecture
dotnet_sdk_version
dotnet_sdk_feature_band
workload_set_version
installed_workload_ids
msbuild_version
sdk_resolvers
targeting/reference_pack_ids
visual_studio_or_build_tools_version
xcode_version
android_sdk_and_jdk_versions
custom_tool_labels
network/package_feed_policy
```

The project discovery step emits required and preferred capabilities. The planner selects the least-specialized worker that satisfies all required capabilities:

1. Linux sandbox for ordinary projects.
2. Linux workload image for supported mobile/optional workloads.
3. Windows worker for Windows-only SDKs/tasks.
4. macOS worker for Apple workloads/Xcode dependencies.
5. Authenticated client/CI contribution when an appropriate managed worker is unavailable or the build environment is intentionally local.

If no route is available, the project is `unsupported` with structured missing-capability diagnostics. A snapshot containing unsupported projects is `partial`, never `complete`.

## Why not a mandatory compiler analyzer

A compiler analyzer or source generator is not the default integration because:

- it sees one compilation at a time and is poorly suited to solution-wide reverse references;
- compiler extensions should avoid network and persistent side effects;
- adding full graph extraction to every build increases developer build latency;
- analyzer execution and generated-code timing differ across IDE, command-line, and CI builds; and
- it would couple Sextant correctness to every repository's analyzer configuration.

The default client implementation is a background local Sextant daemon that uses the developer's installed SDK/workloads and observes successful builds. CI may invoke an explicit post-build contribution command. An optional MSBuild target can enqueue background capture but must be incremental, non-blocking by default, and independently disableable.

## Client and CI contribution flow

1. The client authenticates to the Sextant service through its ProcessStack/GitHub tenant identity.
2. It proves repository and exact commit identity. Dirty trees are rejected for committed contributions unless an explicit non-publishable diagnostic mode is selected.
3. It indexes one project/TFM using the canonical Sextant analyzer and schema versions.
4. It emits a deterministic contribution manifest and compact semantic payload.
5. It uploads the artifact or artifact reference.
6. The server verifies hashes against provider Git blobs, validates schema and configuration, enforces size/policy limits, and records provenance.
7. Ingestion combines only mutually compatible contributions into an atomic snapshot.

An upload never contains ambient absolute paths, credentials, package-feed tokens, or uncommitted source by default.

## Contribution manifest

Required fields:

- tenant and repository identity;
- commit and tree SHA;
- logical project path, target framework, runtime/configuration dimensions;
- schema, analyzer, CLI, Roslyn, MSBuild, SDK, and workload versions;
- configuration/profile hash;
- evaluated project/import/reference/source fingerprints;
- referenced project-version keys;
- completeness and workspace diagnostics;
- counts and content hashes for each payload section;
- producer identity and execution provenance; and
- creation timestamp and optional signature/attestation.

The server rejects a contribution when Git content, evaluation inputs, analyzer version, project graph, or authorization do not match the requested snapshot.

## Working-tree indexing

Uncommitted indexing remains local:

- the base committed snapshot may come from any compatible worker or contribution;
- the local daemon evaluates and stores only the overlay;
- local MCP merges base and overlay;
- remote queries receive no working-tree source or overlay unless the user explicitly enables a separate collaborative-session feature; and
- platform capabilities of the developer machine can still make the local overlay more complete than the server's default Linux worker.

## Test matrix

| Fixture | Expected route | Expected result |
|---|---|---|
| `netstandard` library | Linux default | Complete |
| multi-target portable library | Linux default | One project version per TFM |
| `netX.Y-windows` with targeting packs | Linux when evaluation succeeds; otherwise Windows | Complete or actionable reroute |
| WPF/WinForms with custom Windows task | Windows | Complete |
| Android/MAUI Android | Linux workload image when supported | Complete |
| iOS/Mac Catalyst/macOS app | macOS/Xcode | Complete |
| custom SDK resolver | labeled capable worker or client/CI | Complete or unsupported |
| missing workload | no publish as complete | Partial with missing-capability diagnostic |
| successful developer build at exact commit | client contribution | Accepted after validation |
| dirty developer tree | local overlay only | Remote committed contribution rejected |

## Acceptance criteria

1. Capability discovery is deterministic and stored with every project/snapshot.
2. A Linux worker does not publish Apple- or Windows-sensitive projects as complete after workspace failures.
3. Windows and macOS workers can contribute to the same repository snapshot without mixing incompatible schema/configuration versions.
4. Client/CI contributions are content-verified, authorized, provenance-recorded, and idempotent.
5. Build integration is opt-in, incremental, and does not make normal compilation depend on service availability.
6. Unsupported workloads produce actionable required-capability output through CLI, service status, ProcessStack, and MCP metadata.
