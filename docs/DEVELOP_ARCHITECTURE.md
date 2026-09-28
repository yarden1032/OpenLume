# Develop architecture

OpenLume separates nondestructive editing into two explicit layers. This keeps whole-image corrections predictable while allowing precise regional work without baking pixels into an original.

## Global Develop

Global adjustments run in a stable, versioned pipeline:

1. RAW decode, camera white balance, and input color transform
2. Optics and chromatic-aberration correction
3. Crop, straighten, geometry, and transform
4. White balance and color adaptation
5. Light controls and tone curve
6. HSL/color mixer and color grading
7. Texture, clarity, dehaze, vignette, and grain
8. Sharpening and noise reduction
9. Output color transform and encoding

Every parameter belongs to the serialized `EditRecipe`, has a bounded normalized value, participates in undo/redo and snapshots, and must render identically in preview and export within an explicit golden-image tolerance. Issue [#21](https://github.com/yarden1032/OpenLume/issues/21) tracks this layer.

Recipe version 2 implements exposure, contrast, highlights, shadows, whites, blacks, temperature, tint, vibrance, saturation, vignette, and rotation. Older version 1 JSON recipes upgrade with neutral defaults for the added controls.

## Local Develop and masks

Local work is a mask graph applied after the global base development. A mask owns its geometry or segmentation data and a local adjustment recipe. Masks can be reordered, renamed, enabled, removed, and combined with add, subtract, and intersect operations.

The planned mask sources are:

- Manual brush with erase, feather, flow, and density
- Linear and radial gradients
- Color and luminance ranges
- Locally segmented subject and sky
- Locally assisted object removal

Manual masks must work with no model installed. AI-assisted masks use local model adapters first; Ollama or dedicated local inference may provide orchestration, while optional remote-provider interfaces remain dormant until a user explicitly configures credentials. Photos are never uploaded implicitly. Issue [#22](https://github.com/yarden1032/OpenLume/issues/22) tracks this layer.

## Product rules

- The original file is immutable.
- A missing model disables only the relevant AI tool.
- Preview work is cancellable and runs away from the UI thread.
- Export consumes the same recipe and mask graph as preview.
- New recipe versions must migrate older catalogs without losing edits.
- A control is not presented as available until its renderer and persistence path are implemented and tested.
