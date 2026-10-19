<!-- Source: https://scytheeditor.com/guide/getting-started/quickstart.html -->

# Quick Start Guide

## Pre-Requisite to Make Scythe Happy

Scythe doesn't require special materials. However, Scythe functions best when it can get the intended size of your materials, and it does that by trying to get the texture size from your main texture plugged into Base Color. ![materialnamedparameter.gif](https://scytheeditor.com/guide/images/materialnamedparameter.webp)

Make sure your base material has a parameter for the Base Color texture that's named one of these:

- Albedo
- BaseColor
- Diffuse
- Color
- Texture
- BaseColorTexture

If you want to use a different parameter name, you can add your own to the Scythe settings.\
**\[Project Settings \> Scythe\]**![materialrequirements.png](https://scytheeditor.com/guide/images/materialrequirements.png)

## Activate Scythe Mode

Press Shift + 6 Or select **Scythe** from the **Editor Modes** dropdown.

![Activate Scythe Mode Image](https://scytheeditor.com/guide/images/activatescythemode.png)

## Creating Geometry

![blocktooldragout.gif](https://scytheeditor.com/guide/images/shapetools.png "Shape tools") Open the Block Tools on the left toolbar.

Open the **Add Block** (`Shift + B`) tool.

![blocktooldragout.gif](https://scytheeditor.com/guide/images/blocktooldragout.gif) Drag out a block.

![blocktooldragout.gif](https://scytheeditor.com/guide/images/blocktoolflipup.gif) You can hold Ctrl to flip the block grid towards the camera

![blocktooldragout.gif](https://scytheeditor.com/guide/images/blocktoolnormalmatch.gif) Hold Shift to place the block on a wall.

## Selection

You can switch between selection modes using the buttons in the **Selection Modes** panel ![selectionmode.png](https://scytheeditor.com/guide/images/selectionmodes.png "Selection modes")

**Polygroup Verts** (`1`)\
**Polygroup Edges** (`2`)\
**Polygroup Faces** (`3`)\
**Objects** (`4`)

The **green** modes are **triangle selection modes**, which allow you to select vertices, edges, or faces of the mesh. You can quickly toggle between polygroup/triangle modes with 6.

The **blue** modes are **group selection modes**, which allow you to select groups of vertices, edges, or faces based on their polygroups. You'll mostly work in group selection modes.

INFO

The mesh must have polygroups generated to use the **Group** selection modes. All meshes created with Scythe have polygroups by default, but they can also be generated in modeling tools.

#### Additional Important Selection Shortcuts

**Select All** (`Ctrl + A`)\
**Flood Fill / Edge Loop** (`Double Click`)\
**Select Faces on Plane** (`CapsLock / Mouse4 / Mouse5 + Double Click`)\
**Path Selection** (`Hold Left Mouse Button + Drag`)

### Path Selection

![Path Selection](https://scytheeditor.com/guide/images/selection_pathdrag.webp) Click and hold, then drag to select all in path. Works for Vertices, Edges, and Faces.

### Converting Selections

![Path Selection](https://scytheeditor.com/guide/images/selection_convertselection1.webp) Any element selection can be converted to another element by switching to the other element selection mode with an active selection.

#### Example

In **Polygroup Edges** (`2`) mode:

1.  Select an edge
2.  Switch to **Polygroup Faces** (`3`) to convert the current selection to a face selection

Quick Ring Selection Tip

1.  Select an edge
2.  **Select Edge Ring** (`X`)
3.  Convert it to faces with **Polygroup Faces** (`3`)

You can see that in action in the above gif. It's incredibly useful to do all sorts of selections, even to just select all 4 sides of a pillar quickly without needing to move your camera.

## Extruding Faces

In **Polygroup Faces** (`3`) selection mode ![selectionmode.png](https://scytheeditor.com/guide/images/extrudeface.gif "Selection modes")**Extrude faces** by holding Shift and dragging the gizmo.

This also works in **Tri Faces** (`Shift + Ctrl + 3`) selection mode.

## Pushing/Pulling Faces

In **Polygroup Faces** (`3`) selection mode ![selectionmode.png](https://scytheeditor.com/guide/images/pushpullface.gif "Selection modes")**Extrude faces** by holding Shift + Ctrl and dragging the gizmo.

This also works in **Tri Faces** (`Shift + Ctrl + 3`) selection mode.

## Extruding Edges

In **Polygroup Edges** (`2`) selection mode ![selectionmode.png](https://scytheeditor.com/guide/images/extrudeedge.gif "Selection modes")**Extrude edges** by holding Shift and dragging the gizmo.

This also works in **Tri Edges** (`Shift + Ctrl + 2`) selection mode.

INFO

You can only extrude edges that are boundary edges. They're indicated by a dashed line in the viewport.

## Edge Loop

![selectring.png](https://scytheeditor.com/guide/images/selectring.png) Select an edge, then press **Select Edge Ring** (`X`). ![connectedges.png](https://scytheeditor.com/guide/images/connectedges.png) With edges selected, press **Connect** (`V`) to connect.

INFO

There will be a single tool for edge loops in the future.

You can also do similar things with the **Cut** (`C`) tool in **Polygroup Faces** (`3`) (selection required).

## Applying Materials

![connectedges.png](https://scytheeditor.com/guide/images/activematerial.png) Select an **Active Material** in the lower left.

INFO

Selecting a material in the content browser will automatically set it as the **Active Material**.

![connectedges.png](https://scytheeditor.com/guide/images/applymaterial.gif) Apply the Active Material to selected faces with **Apply Material** (`Shift + T`)

Blockout Materials

Scythe has built in blockout materials you can use. They'll show up in the **Material Browser** by default, but there's also hotkeys to make them the active material.

**Hotkeys**

**Select Blockout Material 1** (`Shift + Alt + 1`)\
**Select Blockout Material 2** (`Shift + Alt + 2`)\
**Select Blockout Material 3** (`Shift + Alt + 3`)\
**Select Blockout Material 4** (`Shift + Alt + 4`)\
**Select Blockout Material 5** (`Shift + Alt + 5`)\
**Select Blockout Material 6** (`Shift + Alt + 6`)\

You can replace Scythe's blockout materials with your own by going to the **Material Browser** and clicking Settings as a quick shortcut to it.

## Lifting Materials

Select a mesh and lift a material from the hovered face with **Lift Material** (`Shift + RMB`) to make it the **Active Material**.

If you have the **Material Browser** open, it'll scroll to the newly lifted material.

## Modify Textures/UVs

There's 2 main ways to manipulate your textures/UVs.

### 1) Fast Texture Tool - A fast UV Editor

Select the face(s) you want to edit the textures of, and open it in the **Fast Texture Tool** (`Shift + Q`)![ftt_quickstart1.webp](https://scytheeditor.com/guide/images/ftt_quickstart1.webp) Very quickly unwrap and manipulate UVs. You can get away with only using this tool for all of your texturing if you want to. The blue rectangle is what the ideal shape is with the current texel density settings.

### 2) Modify Texture Panel

![modifytexture2.png](https://scytheeditor.com/guide/images/modifytexture2.png) This is for quick 1-shot commands if you know exactly what you need.

**Align to Grid** (`Shift + Ctrl + G`) is incredibly useful for having your textures align to the grid. It'll make your textures act like they're world aligned, and will automatically scale and rotate the texture to fit the grid according to your Texel Density settings (Scythe's dev materials aren't affected by texel density). Learn that hotkey and use it often!

WARNING

You should nearly always use **Align to Grid** (`Shift + Ctrl + G`) on your target face(s) before using the fit tools below. It avoids some odd bugs at the moment.

### Standard Wall Alignment

![fitwall.jpg](https://scytheeditor.com/guide/images/fitwallbuttons.jpg) Use **Keep Ratio** and **Treat as One** for the best results when fitting a wall texture to a wall.

![fitwall.jpg](https://scytheeditor.com/guide/images/fitwall.jpg) Easily fit a wall texture to a wall by selecting the wall and using

Fit to Height.

## Wrap Texture

![wraptexture.gif](https://scytheeditor.com/guide/images/wraptexture.gif) Select a face, then

Wrap Texture (`Alt + RMB`) the hovered face next to it. This will wrap the texture/UV from the selected face to the hovered face. It'll also copy the material from the selected face.

## Flow Texture

![flowtexture.webp](https://scytheeditor.com/guide/images/flowtexture3.webp) Select a face, then **Flow Texture** (`Ctrl + Alt + RMB`) the hovered face next to it. This will flow the texture/UV from the selected face to the hovered face, flowing along curves. It'll also copy the material from the selected face. You'll only use this when you want to flow the texture along a curve. This is nearly identical to the **Follow Quads** unwrap in the **Fast Texture Tool**.

## Temp Move Pivot

![movepivot.gif](https://scytheeditor.com/guide/images/temppivot.webp) Drag the gizmo with Middle Mouse Button to move the pivot of the mesh.

This is a temporary move, so it will reset when you make a new selection.

INFO

You can do fun things with this like go into gizmo rotation mode E then hold Shift to extrude while you rotate the gizmo.

## Drag Alignment Mechanic

![dragalignmechanic.webp](https://scytheeditor.com/guide/images/dragalignmechanic.webp) Align the selection to mesh elements by dragging the gizmo and holding CapsLock

It's not the greatest hotkey, but Unreal won't let me use Alt or other useful modifiers for this. Might change in the future as hotkeys are figured out.
