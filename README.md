# OpenLume

OpenLume is a local-first, nondestructive photo library and RAW editor for Windows. It is being built in public as a privacy-respecting alternative for photographers who want a Lightroom-style workflow without requiring a cloud account.

> **Project status:** alpha. Core library, preview, organization, compare/survey, basic develop, XMP preset import, export, and local Ollama workflows work today. Catalog migrations and recovery paths are tested, but keep normal backups while the project is pre-1.0.

## What works

- Referenced-file library backed by SQLite in WAL mode
- Paged browsing tested against a synthetic 100,000-photo catalog
- Persistent, content-keyed thumbnail cache with a bounded 2 GB least-recently-used budget
- Resumable background raster/RAW dimension indexing with source-change invalidation
- Folder browsing, search, ratings/pick/missing filters, collections, ordered stacks, and missing-file relinking
- Multi-selection Compare and Survey views
- Recursive import for Nikon NEF/NRW, Canon CR2/CR3, Sony ARW/SR2, DNG, JPEG, PNG, TIFF, and WebP
- LibRaw decoding with camera white balance for RAW previews and exports
- Nondestructive exposure, contrast, saturation, temperature, tint, and rotation pipeline
- Persistent ratings and reversible pick/reject flags; originals are never modified
- Atomic edited JPEG export
- Optional local photo analysis through an Ollama vision model
- Modern Lightroom/Camera Raw XMP preset import with compatibility reporting for unsupported settings
- Dark Windows desktop interface built with Avalonia

The [roadmap](docs/ROADMAP.md) tracks the remaining work toward the production-ready 1.0 release, including full color-managed develop controls, culling, masks/object removal, HDR, and packaging.

## Build and run

Requirements:

- Windows 11 x64
- [.NET SDK 10.0.400](https://dotnet.microsoft.com/download)
- Optional: [Ollama](https://ollama.com/) and a vision-capable model

```powershell
dotnet restore OpenLume.slnx
dotnet test OpenLume.slnx
dotnet run --project src/OpenLume.App/OpenLume.App.csproj
```

OpenLume never downloads an AI model automatically. To use local analysis, install a vision model in Ollama and choose it before starting the app:

```powershell
$env:OPENLUME_OLLAMA_MODEL = "your-vision-model"
dotnet run --project src/OpenLume.App/OpenLume.App.csproj
```

The catalog is stored at `%LOCALAPPDATA%\OpenLume\catalog.db`; bounded previews live under `%LOCALAPPDATA%\OpenLume\thumbnails`. Imported originals remain in their existing folders.

## Safety and privacy

- Original photographs are opened read-only and never overwritten.
- Reject is a catalog flag; it does not delete or move a file.
- Exports are written to a temporary file and atomically moved into place.
- Ollama requests go only to `127.0.0.1` by default.
- No telemetry, account, cloud sync, or cloud provider is enabled.
- OpenAI and other remote providers are architecture extensions only and are not implemented in this milestone.

Please read [SECURITY.md](SECURITY.md) before reporting a vulnerability and [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes.

## License

OpenLume is licensed under the GNU General Public License v3.0 or later. Bundled native dependencies and optional model weights retain their own compatible licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
