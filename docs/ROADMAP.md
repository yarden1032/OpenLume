# OpenLume roadmap

The project advances only through tested vertical slices. A milestone is complete when its user workflow, recovery behavior, and automated tests pass.

## Milestone 1 — foundation and vertical slice

- [x] Public-ready .NET/Avalonia repository structure
- [x] SQLite catalog and referenced-folder import
- [x] Nikon/Canon/Sony/DNG RAW decoding through LibRaw
- [x] Raster preview and nondestructive exposure edits
- [x] Ratings, picks/rejects, Ollama analysis, and JPEG export
- [x] Build and test CI
- [x] Test with redistributable CC0 Canon CR3, Nikon NEF, and Sony ARW camera fixtures

## Milestone 2 — library and develop beta

- [x] Bounded persistent thumbnail cache and resumable raster/RAW dimension indexing
- [x] Folders, collections, ordered stacks, server-side filters, compare, and survey
- [x] Missing-file detection and relinking that preserves edits, ratings, and analysis
- [x] Synthetic 100,000-photo catalog paging/search regression test
- [x] Versioned edit history, undo/redo, named snapshots, reset, and original preview
- [x] Batched large-folder import, amortized thumbnail indexing, and cached display-resolution previews
- [x] Interactive exposure, contrast, saturation, temperature, tint, and rotation controls
- [x] Versioned global tone/presence controls: highlights, shadows, whites, blacks, vibrance, and vignette
- [x] Separate Library grid and Develop canvas with grouped global controls and filmstrip navigation
- [x] Live RGB/luminance histogram
- [x] Texture, clarity, dehaze, sharpening, luminance noise reduction, and grain
- [x] Eight-channel hue, saturation, and luminance Color Mixer with Lightroom XMP mappings
- [x] Visual four-region parametric Tone Curve with adjustable splits and Lightroom XMP mappings
- [x] On-canvas crop/straighten with aspect presets, 90-degree orientation, flips, one-step apply/cancel, and Lightroom XMP mappings
- [x] Manual optics pipeline for distortion, chromatic aberration, lens vignetting, XMP mappings, and future local profile-provider hooks
- [x] Independent luminance-preserving color noise reduction with XMP and Ollama parameter support
- Advanced sharpening masking and a packaged lens-profile database
- [x] Modern Lightroom XMP parser with supported-setting mapping and compatibility reports
- XMP sidecar writing, catalog backups, and managed-copy imports
- JPEG, PNG, and 8/16-bit TIFF export with metadata and ICC policies

## Milestone 3 — local and hybrid AI beta

- Explainable duplicate/burst grouping and recommend-only automatic culling
- Local subject/sky segmentation and mask refinement
- Local object removal baseline plus optional ComfyUI connection
- [x] Local AI Develop Director with bounded, explainable, staged parameter proposals and explicit apply/reject
- Model download/license management and provider selection
- Aligned, deghosted bracketed HDR merge and nondestructive tone mapping
- Optional remote provider interfaces after secure credential setup

## Milestone 4 — production hardening and 1.0

- 100,000-photo performance and interruption/recovery tests
- Color-management validation and golden-image regression suite
- Accessibility, keyboard workflow, installer/upgrade/uninstall, catalog migrations, and signed releases
- Threat model, dependency/license audit, SBOM, release provenance, user guide, and camera support matrix
- Release gate: no known data-loss defect and no code path that modifies an original
