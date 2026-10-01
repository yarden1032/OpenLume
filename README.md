# OpenLume

OpenLume is a local-first, nondestructive photo library and RAW editor for Windows. It is being built in public as a privacy-respecting alternative for photographers who want a Lightroom-style workflow without requiring a cloud account.

> **Project status:** alpha. Core library, preview, organization, compare/survey, basic develop, XMP preset import, export, and local Ollama workflows work today. Catalog migrations and programmatic SQLite backup/restore are tested, but the app does not yet expose a recovery workflow; keep normal backups while the project is pre-1.0.

## What works

- Referenced-file library backed by SQLite in WAL mode
- Batched folder import with live progress and cancellation at safe transaction boundaries
- Paged browsing tested against a synthetic 100,000-photo catalog
- Persistent, content-keyed thumbnail cache with an in-memory index and bounded 2 GB least-recently-used budget
- Resumable background raster/RAW dimension indexing with source-change invalidation
- Folder browsing, search, ratings/pick/missing filters, collections, ordered stacks, and missing-file relinking
- Multi-selection Compare and Survey views
- Dedicated Library grid and Develop workspace with a navigation filmstrip and grouped controls
- Recursive import for Nikon NEF/NRW, Canon CR2/CR3, Sony ARW/SR2, DNG, JPEG, PNG, TIFF, and WebP
- CC0 camera-original CR3, NEF, and ARW fixtures exercise RAW metadata and preview decoding for Canon, Nikon, and Sony
- LibRaw decoding with camera white balance for RAW previews and exports
- Responsive nondestructive exposure, contrast, highlights, shadows, whites, blacks, visual parametric Tone Curve, temperature, tint, vibrance, saturation, eight-channel HSL Color Mixer, texture, clarity, dehaze, sharpening with radius and edge masking, noise reduction, grain, vignette, and rotation controls
- On-canvas crop workflow with a dimmed surround, rule-of-thirds guides, draggable corner handles, Lightroom-style aspect presets, straighten, quarter-turn rotation, and horizontal/vertical flips
- Professional Optics panel for bounded barrel/pincushion distortion, chromatic-aberration realignment, and lens-vignetting correction with a configurable midpoint
- Independent color noise reduction preserves luminance detail, supports Lightroom XMP, and accepts staged Ollama parameter recommendations
- Persistent edit history with undo/redo, named snapshots, reset, and original preview
- Persistent ratings and reversible pick/reject flags; originals are never modified
- Atomic edited JPEG and PNG export to a new file, with original/existing-file overwrite protection
- Embedded raster ICC profiles are converted into sRGB for Develop; previews and JPEG/PNG exports carry an sRGB profile
- Live RGB/luminance histogram for the edited preview
- Optional local AI Develop Director through an Ollama vision model: it stages explainable parameter recipes—including targeted HSL mixes and Tone Curve regions—for preview, apply, reject, and undo
- Modern Lightroom/Camera Raw XMP preset import with compatibility reporting for unsupported settings
- Dark Windows desktop interface built with Avalonia

The [roadmap](docs/ROADMAP.md) tracks the remaining work toward the production-ready 1.0 release, including full color-managed develop controls, culling, masks/object removal, HDR, and packaging.
The [Develop architecture](docs/DEVELOP_ARCHITECTURE.md) defines the separate global-adjustment and local-mask pipelines.

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
- Exports are written to a temporary file and atomically moved into place without replacing existing files. Choose a new filename for each export; this also protects other catalog originals and files created while an export is running.
- Ollama requests go only to `127.0.0.1` by default.
- AI Develop never regenerates, inpaints, or replaces pixels. A model can only propose bounded `EditRecipe` parameters; OpenLume's deterministic renderer applies them after explicit approval.
- AI proposals are staged and previewed without mutating the active recipe. Applying one creates a normal, reversible edit-history revision.
- Crop, straighten, rotate, and flip are staged in a dedicated mode. Apply creates one reversible revision; Cancel leaves the active recipe untouched.
- No telemetry, account, cloud sync, or cloud provider is enabled.
- OpenAI and other remote providers are architecture extensions only and are not implemented in this milestone.

Please read [SECURITY.md](SECURITY.md) before reporting a vulnerability and [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes.

## License

OpenLume is licensed under the GNU General Public License v3.0 or later. Bundled native dependencies and optional model weights retain their own compatible licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
