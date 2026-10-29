# AtlasForge

Unity editor tool for building and maintaining UI sprite atlases. Use it to pack, repack and merge atlases, edit 9-slice borders and remove duplicate slices, while every sprite reference in your project keeps working.

Open it from **Tools > AtlasForge** or with **Ctrl+Alt+A** (**Cmd+Alt+A** on macOS). You can rebind the shortcut under **Edit > Shortcuts**.

## Installation

In Unity, open **Window > Package Manager**, click **+ > Add package from git URL…** and paste:

```
https://github.com/samicodeslikeit/AtlasForge.git
```

To pin a release, append its tag:

```
https://github.com/samicodeslikeit/AtlasForge.git#v1.0.0
```

Or add it to `Packages/manifest.json`:

```json
"com.samicodeslikeit.atlasforge": "https://github.com/samicodeslikeit/AtlasForge.git#v1.0.0"
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

### Merge Atlases
Merges several sliced atlases into one of them. Clashing slice names and IDs are resolved automatically, and every reference to the merged-in slices is rewritten to point at the merged atlas. The other atlases can optionally be moved to the trash afterwards.

## How references are kept intact

Repack, merge and delete keep each sprite's internal ID. Merging and deleting also rewrite references wherever they're stored as text: scenes, prefabs, ScriptableObjects, materials, animations, and USS/UXML. This requires **Project Settings > Editor > Asset Serialization** to be **Force Text**.

These operations rewrite project files and can't be undone with Undo, so commit or back up first. References in binary assets, and sprites loaded by name from code, are not updated.
