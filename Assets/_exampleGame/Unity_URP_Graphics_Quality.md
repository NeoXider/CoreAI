# Graphics in Unity (URP): Brief and Practical

A guide for **Universal Render Pipeline (URP)** - how settings and presets work, and why objects can "disappear" at distance.

---

## 1. How Graphics Work in Unity (URP)

In URP, the chain looks like this:

| Layer | What it is |
|------|---------|
| **Render Pipeline Asset** | Main URP "profile": shadows, MSAA, HDR, default post-processing, global limits. |
| **Renderer (Forward/Forward+)** | How objects are drawn: transparency, deferred effects, additional passes. |
| **Camera** | Depth clipping (**Far Clip**), layers (**Culling Mask**), post-processing on the camera. |
| **Lighting** | Scene lighting mode, shadows, ambient, reflection probes. |
| **Volumes** | Post effects and atmosphere (Bloom, Color Adjustments, Fog, Depth of Field, etc.) - by zone or globally. |

The game is not "drawn" by one switch, but by a chain: **pipeline -> renderer -> camera -> materials/terrain LOD**.

---

## 2. Presets: What to Create and Where

A **preset** is a saved set of parameters that can be reused and switched.

### Common URP Preset Types

1. **URP Asset (Pipeline Asset)**  
   `Create -> Rendering -> URP Pipeline Asset`  
   Different presets per platform: PC / mobile / WebGL - different shadows, render scale, upscaling.

2. **Renderer Data**  
   `Create -> Rendering -> URP Renderer Data`  
   A separate renderer for a scene or mode (for example, without post-processing for a UI camera).

3. **Volume Profile**  
   `Create -> Volume Profile`  
   A set of effects (fog, exposure, bloom). Assigned to a **Global Volume** or **Box Volume**.

4. **Quality Level**  
   `Edit -> Project Settings -> Quality`  
   This defines **Scriptable Render Pipeline Settings** (which URP Asset), **VSync**, **LOD Bias**, **Shadows**, etc. You can duplicate a level to create a "quality preset".

5. **Graphics Preset (if used)**  
   Newer Unity versions have **Graphics Presets** in graphics settings - quick templates; the idea is the same: one click, another URP/quality set.

**Practice:** for "PC high / PC low / mobile", teams usually create **several Quality levels**, each with its own **URP Asset** (or one shared asset with different overrides, if that is the project convention).

---

## 3. Graphics vs Quality - What Is the Difference?

Both sections are in **Project Settings**, but their roles differ:

### Project Settings -> **Graphics**

- Which **Default Render Pipeline** is used (including the default URP Asset).
- Binding **Scriptable Render Pipeline Settings** to quality levels (which URP Asset on which Quality).
- Sometimes global defaults for **shaders/gradients/camera** (depends on Unity version).

**Simply:** "which pipeline is used at all, and how it connects to quality levels".

### Project Settings -> **Quality**

- Specific **Quality Levels** (Low, Medium, High...).
- For each level: **shadows**, **textures**, **anisotropy**, **LOD Bias**, **Particle Raycast Budget**, binding to **URP Asset**, etc.
- What is often switched at runtime: `QualitySettings.SetQualityLevel(...)`.

**Simply:** "how pretty and expensive the rendering is in this preset".

**In short:**  
**Graphics** is the framework and pipeline. **Quality** is the quality ladder inside that framework.

---

## 4. Why Trees Are Not Visible from About 20 m, Even Though "Terrain Is 5000"

People often confuse **terrain size** with **tree render distance**.  
**5000** in terrain settings is usually the **world size in units** (length/width), not the distance at which trees are rendered.

### Check in Order

#### A. Terrain - Trees Tab

- **Tree Distance** - maximum **tree rendering** distance (this is what cuts visibility).  
  If this is around **20-50**, trees **will not render** beyond that even if the terrain is huge.
- **Billboard Start** - distance at which trees switch to billboards. If billboards are disabled or broken in the shader, the image can appear to "cut off" strangely.
- Make sure you are editing **the same Terrain** that is in the scene (not a duplicate in the project).

#### B. Camera

- **Far Clip Plane** - if it is **20** or less, everything beyond it is simply **clipped**. For an open world, this is usually hundreds to thousands (depending on the task).

#### C. LOD on the Tree Prefab

- In **LOD Group**, the last LOD can be **Culled** (not rendered) at a close distance.
- **Maximum LOD Level** in **Quality** can forcibly disable distant LODs.

#### D. URP / Quality

- **LOD Bias** in Quality: a large bias can cause an earlier switch to an "empty" LOD.
- **Shadow distance** does not hide the meshes themselves, but it is sometimes confused with object disappearance - check separately.

#### E. Volumetric Fog (Volume Fog)

- Strong **Fog** in a **Volume** can visually "eat" tree silhouettes on the horizon - this is not the same as culling, but it looks like "not visible".

#### F. Occlusion Culling

- If **Occlusion Culling** is enabled and the bake is aggressive, distant trees can be considered occluded. For terrain with many trees, this often needs tuning.

---

## 5. Cheat Sheet: "Trees Disappear in the Distance"

| Symptom | Where to Look |
|--------|----------------|
| Flat boundary around 20 m | **Camera Far Clip**, **Terrain -> Tree Distance** |
| Terrain is large, "5000" in size | This is not tree distance - raise **Tree Distance** |
| Trees exist in the editor but not in-game at distance | **LOD Group**, **Maximum LOD Level**, **LOD Bias** |
| Everything fades in the distance | **Volume -> Fog**, exposure |
| Disappears only around a corner/behind a hill | **Occlusion Culling** |

---

## 6. Useful Editor Paths

- `Edit -> Project Settings -> Graphics` - default pipeline, Quality binding.  
- `Edit -> Project Settings -> Quality` - quality levels, LOD, shadows.  
- Select **Terrain** -> inspector -> tree settings / **Tree Distance**.  
- **Main Camera** -> **Clipping Planes -> Far**.  
- **Window -> Rendering -> Lighting** - scene environment.  
- Object with **Volume** + **Volume Profile** - fog and post.

---

*This document targets URP; names and some options differ in Built-in and HDRP.*
