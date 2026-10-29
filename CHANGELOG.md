# Changelog

## [1.1.0] - 2026-10-29

### Added
- Repack Atlas: export the selected slices as standalone sprites, keeping their pivot, 9-slice border, outline, physics shape and the atlas's import settings. A single slice can be given a custom name. Optionally remove the slices from the atlas, repack the rest, and redirect their references to the exported sprites.
- Selecting a single sprite opens a live 9-slice editor: drag the left, right, top and bottom guides on the preview, Sprite Editor style, snapping to whole pixels. Changes go straight onto that sprite and are written by the tab's Pack, Repack or Merge action.
- Repack and Merge Atlases: switch a single slice to **Rect** to move or resize it inside its atlas, Sprite Editor style, by dragging its edges, corners or body, or by typing X, Y, Width and Height. The slice is re-cut immediately, overlaps with other slices are flagged, and **Reset** restores the original rect.

### Changed
- Redesigned the window: tabs in a toolbar, packing settings folded away, a sprite grid that fills the window with search and thumbnail size, a resizable selection panel with the 9-slice preview beside its fields, and a fixed footer with the status and main action.
- The selection panel no longer repeats the selected sprites as a second grid.
- Merge Atlases uses a reorderable list that accepts atlases dragged from the Project window.
- Grid shortcuts: Ctrl/Cmd+click, Shift+click ranges, Ctrl/Cmd+A, Esc, and Delete to mark slices on Repack Atlas.

## [1.0.0] - 2026-10-29

### Added
- Window under **Tools > AtlasForge**, opened with **Ctrl+Alt+A** (Cmd+Alt+A on macOS).
- Pack Folder: pack a folder of sprites into a single sliced atlas with manual or auto-detected 9-slice borders.
- Repack Atlas: reclaim empty space in an existing atlas in place, edit slice borders, and delete slices (with duplicate detection and reference redirection).
- Merge Atlases: merge several sliced atlases into one, rewriting every reference to the merged slices.
