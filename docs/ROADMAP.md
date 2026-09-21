# OpenLume roadmap

The project advances only through tested vertical slices. A milestone is complete when its user workflow, recovery behavior, and automated tests pass.

## Milestone 1 — foundation and vertical slice

- [x] Public-ready .NET/Avalonia repository structure
- [x] SQLite catalog and referenced-folder import
- [x] Nikon/Canon/Sony/DNG RAW decoding through LibRaw
- [x] Raster preview and nondestructive exposure edits
- [x] Ratings, picks/rejects, Ollama analysis, and JPEG export
- [x] Build and test CI
- [ ] Test with redistributable real-camera RAW fixtures

## Milestone 2 — library and develop beta

- Thumbnail cache, metadata extraction, folders, collections, stacks, filters, compare, and survey
- Versioned edit graph, history, before/after, histogram, crop/rotate, tone curve, HSL, detail, and lens profiles
- Modern Lightroom XMP parser with supported-setting mapping and compatibility reports
- XMP sidecars, catalog backups, missing-file relinking, and managed-copy imports
- JPEG, PNG, and 8/16-bit TIFF export with metadata and ICC policies

## Milestone 3 — local and hybrid AI beta

- Explainable duplicate/burst grouping and recommend-only automatic culling
- Local subject/sky segmentation and mask refinement
- Local object removal baseline plus optional ComfyUI connection
- Bounded auto-enhance recipes and model download/license management
- Aligned, deghosted bracketed HDR merge and nondestructive tone mapping
- Optional remote provider interfaces after secure credential setup

## Milestone 4 — production hardening and 1.0

- 100,000-photo performance and interruption/recovery tests
- Color-management validation and golden-image regression suite
- Accessibility, keyboard workflow, installer/upgrade/uninstall, catalog migrations, and signed releases
- Threat model, dependency/license audit, SBOM, release provenance, user guide, and camera support matrix
- Release gate: no known data-loss defect and no code path that modifies an original

