# AtlasForge

Unity editor tool for building and maintaining UI sprite atlases. Use it to pack, repack and merge atlases, edit 9-slice borders and remove duplicate slices, while every sprite reference in your project keeps working.

Open it from **Tools > AtlasForge** or with **Ctrl+Alt+A** (**Cmd+Alt+A** on macOS). You can rebind the shortcut under **Edit > Shortcuts**.

In the sprite grid, click to select, Ctrl/Cmd+click to toggle, Shift+click to select a range, or drag to paint a selection. Ctrl/Cmd+A selects everything that matches the search, Esc clears the selection, and on the Repack Atlas tab Delete marks or unmarks the selected slices. Drag the divider under the grid to resize the selection panel.

Select a single sprite to edit its 9-slice border visually: drag the green guides on the preview, as in the Sprite Editor. Edits apply to that sprite immediately and are written to the asset when you Pack, Repack or Merge. With several sprites selected, set the values once and click **Apply** to give them all the same border.

On the Repack Atlas and Merge Atlases tabs, switch a selected slice from **Border** to **Rect** to move or resize it inside its atlas: drag an edge or corner to resize, or the inside to move. The view shows the surrounding part of the atlas with the other slices outlined, and warns when the new rect overlaps another slice. The change is written on the next Repack or Merge.

## Installation

In Unity, open **Window > Package Manager**, click **+ > Add package from git URL…** and paste:

```
https://github.com/samicodeslikeit/AtlasForge.git
```

To pin a release, append its tag:

```
https://github.com/samicodeslikeit/AtlasForge.git#v1.2.1
```

Or add it to `Packages/manifest.json`:

```json
"com.samicodeslikeit.atlasforge": "https://github.com/samicodeslikeit/AtlasForge.git#v1.2.1"
```

Requires Unity 2021.3 or newer and Git installed on the machine.

## Features

### Pack Folder
Packs every sprite in a folder into one sliced atlas, trimmed to the smallest 4px-aligned size that fits. You can force power-of-two sizes instead. Each sprite can get a 9-slice border: set it by hand, or let AtlasForge detect the stretchable middle automatically.

### Repack Atlas
Reclaims the empty space in an atlas that's already sliced, in place, and lets you edit its slices' borders. You can also delete slices:

- **Select Duplicates** finds slices that are pixel-identical copies of another slice.
- **Mark for Deletion** removes the selected slices on the next repack.
- References to a deleted duplicate are redirected to the copy that's kept.
- Before anything is written, you're warned about any references that would become Missing.

**Export** writes the selected slices out as standalone sprite textures. Each exported sprite keeps the atlas's import settings (pixels per unit, filter mode, compression and platform overrides) and the slice's pivot, 9-slice border, outline and physics shape. With one slice selected you can give it a custom name; a batch keeps the slice names. Turn on **Remove from Atlas** to delete the slices from the atlas and redirect every reference to the exported sprites. The remaining slices are then repacked so the atlas shrinks to fit. Leave it off to export a copy.

**Add Sprites** packs standalone textures into the atlas as new slices and repacks it. Pick them from the Project selection or a file browser in the grid toolbar, or drop them onto the grid. Pixels are read straight from each PNG or JPG file, so the texture's own import settings (resizing, compression, texture type) don't affect the slice. A sprite keeps its pivot, 9-slice border, outline and physics shape, and a name already used in the atlas gets a numbered suffix. If anything references the source sprites, you can redirect those references to the new slices. The source files are left in place.

### Merge Atlases
Merges several sliced atlases into one of them. Clashing slice names and IDs are resolved automatically, and every reference to the merged-in slices is rewritten to point at the merged atlas. The other atlases can optionally be moved to the trash afterwards.

## How references are kept intact

Repack, merge and delete keep each sprite's internal ID. Merging, deleting and exporting with removal also rewrite references wherever they're stored as text: scenes, prefabs, ScriptableObjects, materials, animations, and USS/UXML. This requires **Project Settings > Editor > Asset Serialization** to be **Force Text**.

These operations rewrite project files and can't be undone with Undo, so commit or back up first. References in binary assets, and sprites loaded by name from code, are not updated.
