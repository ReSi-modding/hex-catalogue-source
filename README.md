# HEX Catalogue source

Source and build materials for the separately executed, read-only catalogue/model-viewer helper distributed with HEX for Windows x64. The main HEX application, gameplay connector and commerce service are not in this repository.

The helper reads installed Skyrim plugin/archive/mesh files and returns bounded JSON over standard input/output. It does not attach to Skyrim or execute gameplay commands. The original production entry point verifies a provider-signed device authorization before processing requests. Modification and redistribution rights for this GPL-covered helper are described in `LICENSE.md`; they are not limited by a HEX pass's expiry.

## Build the helper

Use Windows PowerShell 5.1 and the .NET SDK version in `sdk.json` (10.0.403). The official SDK download and SHA-512 are recorded there. The SDK is a general build tool; it is not installed into your global PATH by this repository.

```powershell
.\Build-Catalogue.ps1 -Dotnet 'C:\path\to\dotnet.exe' -SourceRoot $PWD.Path -OutputRoot 'C:\new\helper-build'
```

The script uses the production composition (`HexTestBuild=false`), the committed win-x64 .NET 10 lock and the included offline NuGet feed. A fresh short cache is created under the current user's temporary directory to avoid Windows long-path failures; generated outputs stay outside the source repository. Runtime-pack downloading is disabled for this framework-dependent source build; install the standard .NET 10 runtime to run it. The HEX Windows download supplies the separate standard Microsoft runtime. No HEX core/frontend project, game installation, administrative license token or private signing key is required to compile.

The original production program accepts one bounded JSON request on stdin and emits a JSON result on stdout. For example, a `status` request supplies `dataPath`, `pluginsPath`, optional `cccPath` and a customer `licenseProof`. A request without signed authorization fails closed. See the command dispatch in `src/Catalogue/CatalogueEngine.cs`. Runtime paths and customer authorizations are user inputs, not sample credentials included here.

## Source and package inventory

`src` contains the helper's C# files, project/locks and its public verification resource. `vendor/packages` contains the exact locked NuGet archives; `packages.json` distinguishes each archive's whole-file SHA-512 from NuGet's signature-independent locked content hash. `vendor/sources` contains matching preferred library/generator/build sources, including required build submodules and NIF XML generation input. `VENDOR-SOURCES.json` records pinned commits, upstream/archive hashes and the documented omission of test media/generated binaries. The SQLite source ID matches the distributed native library. Microsoft source provenance is retained in `dotnet-vmr-source-manifest.json`.

Upstream repository source can be expanded and built using its own project/build instructions. Nifly's `nifxml` input, NexusMods.Paths' `extern/meta`, and Reloaded.Memory's `src/Reloaded.Project.Configurations` are supplied as separate pinned source archives; restore them into those paths before rebuilding those upstream libraries. Documentation theme submodules and game-derived test/media files are not needed to build the distributed libraries and are not redistributed. Production code/generator/build inputs and notices are retained. General tools and standard system libraries remain under their own licenses.

This repository supplies source for the exact helper release; it does not claim all independently rebuilt vendor binaries or timestamped archives will be byte-identical. The helper-specific source build is verified separately from a live Skyrim or multiplayer session.

HEX: https://hexskyrimmp.com · Support: support@hexskyrimmp.com
