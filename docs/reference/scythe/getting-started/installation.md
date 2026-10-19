<!-- Source: https://scytheeditor.com/guide/getting-started/installation.html -->

# Installation

WARNING

Scythe supports **Unreal Engine 5.5 and newer versions only**. There are no plans to support older versions.

## How to Install

### Option A. Install into the Engine

Extract the ZIP into: YourUnrealEngineLocation/Engine/Plugins/Marketplace/

### Option B. Install into a Project

Extract the zip into your UE project in the \YourProject\Plugins\\ folder.

**Optional, but recommended .uproject modification:**

``` shiki
{
   "Name": "Scythe",
   "Enabled": true,
   "TargetAllowList": [
     "Editor"
   ],
   "TargetConfigurationAllowList": [
     "Development"
   ]
}
```

## Where's the Source Code?

Source code is **not** included in the Patreon builds.\
It will be released on Fab (one‑time fee) once Scythe reaches 1.0.

### Extra Instructions for UE Source Engine Builds

Because these are pre‑compiled Win64 binaries without source, you need to manually align your module BuildID:

Follow the steps under **“Binary Plugin, Source Engine Build”** in the [Gradientspace Toolbox Installation Guide](https://www.gradientspace.com/uetoolbox-installation).

tldr:

1.  Open `Engine/Binaries/Win64/UnrealEditor.modules`
2.  Open `Scythe/Binaries/Win64/UnrealEditor.modules`
3.  **Copy** the `BuildID` from the former into the latter.
