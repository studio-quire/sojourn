# Third-party code, assets and libraries

Sojourn as a whole is licensed under the GNU Lesser General Public License, version 2.1 only (`LICENSE`). The parts
below came from elsewhere, or are fetched when building, and keep their own licences. Sojourn's own code was written
by Claude models at Studio Quire, including the client core and renderer it forked from Multitude, an earlier Studio
Quire project.

## In this repository

**Second Life viewer code and assets**: Copyright Linden Research, Inc., GNU LGPL 2.1 only
(https://github.com/secondlife/viewer). Ported or included:
- much of the renderer in `src/Sojourn.Render` (the deferred pipeline, its GLSL shaders under `Shaders/`, the volume
  and mesh code; files carry Linden Lab's licence header where they port viewer source), and smaller ports elsewhere
  (the animation loader, flexible prims, terrain);
- the system avatar: `src/Sojourn.Render/Character/` (`avatar_lad.xml` and the `.llm` meshes);
- textures: `src/Sojourn.Render/Textures/` (the default particle and parcel-line images) and
  `src/Sojourn.Client/linden/character/upperbody_color.tga` (the viewer's texture, edited).

Some behaviour follows Firestorm's source (for example antispam, after `NACLantispam.cpp`); Firestorm is also GNU LGPL
2.1.

**SMAA** (`src/Sojourn.Render/Shaders/smaa*.glsl`, `smaa.frag`, `smaa.vert`, and its lookup textures
`Shaders/smaa/area.bin.gz`, `search.bin.gz`): Copyright (C) 2013 Jorge Jimenez, Jose I. Echevarria, Belen Masia,
Fernando Navarro and Diego Gutierrez, MIT licence (in the shader's header), as the Second Life viewer ships it, with
Linden Lab's changes under the LGPL.

**OpenJPEG 2.5.3** (`src/Sojourn.Render/native/openjp2.dll`, built from the release source by `tools/openjpeg.py` with
MinGW-w64): BSD 2-Clause, https://github.com/uclouvain/openjpeg. Text: `src/Sojourn.Access/Package/licences/OpenJPEG.txt`.
The DLL carries GCC's runtime support under the GCC Runtime Library Exception.

**Khronos OpenXR loader** (`src/Sojourn.Client/native/openxr_loader.dll`): Copyright The Khronos Group Inc. and
contributors, Apache License 2.0, https://github.com/KhronosGroup/OpenXR-SDK. Text:
`src/Sojourn.Access/Package/licences/Apache-2.0.txt`.

**Archivo** (`src/Sojourn.Session/Assets/Fonts/`): Copyright 2020 The Archivo Project Authors, SIL Open Font License
1.1. Text: `src/Sojourn.Session/Assets/Fonts/Archivo-OFL.txt`.

**SCOWL** (`src/Sojourn.Session/common-words.txt`, its commonest English words, embedded for the log filter):
Copyright 2000-2018 by Kevin Atkinson, http://wordlist.aspell.net, under SCOWL's permissive notice, which heads the
file.

## Libraries restored by NuGet when building

| Package | Licence |
|---|---|
| LibreMetaverse, LibreMetaverse.Imaging.Skia, .ModelFormats, .Rendering.MeshFoundry (3.2.0; 3.3.0 in one tool) | BSD 3-Clause |
| CoreJ2K, CoreJ2K.Skia (2.4.1) | BSD 3-Clause |
| Silk.NET: Vulkan, OpenXR, Windowing, Input, Shaderc and their extensions (2.23.0) | MIT |
| Silk.NET.Shaderc.Native (shaderc) | Apache 2.0 |
| Ultz.Native.GLFW (GLFW, through Silk.NET) | zlib |
| SkiaSharp (4.152.0) | MIT |
| SkiaSharp.HarfBuzz (4.152.0), HarfBuzzSharp (14.2.1; HarfBuzz itself under the Old MIT licence) | MIT |
| NVorbis (0.10.5) | MIT |
| OggVorbisEncoder (1.2.2) | MIT |
| Microsoft.Diagnostics.Tracing.TraceEvent (3.1, `tools/heapwho`, `tools/alloctrace` only) | MIT |
| The .NET runtime and Windows Forms | MIT |

## Fetched by the optional build steps (not in this repository)

- **libwebrtc** m144, Linden Lab's build (https://github.com/secondlife/3p-webrtc-build), for voice: BSD 3-Clause and
  the licences of the libraries in it.
- **CEF** (Chromium Embedded Framework; BSD 3-Clause, Chromium's components under their own) and **libVLC** (LGPL 2.1
  or later), for media, at the versions pinned in `tools/media.py`.
- **NVDA Controller Client** (NV Access, https://github.com/nvaccess/nvda): LGPL 2.1, for Sojourn Access.
- The assistant's Python packages (`assistant/requirements.txt`: claude-agent-sdk, faster-whisper, soundcard, numpy,
  kokoro-onnx, sherpa-onnx, websockets) under their own licences, and its models: Whisper (MIT), Parakeet through
  sherpa-onnx (CC BY 4.0), Kokoro's voice (Apache 2.0). The assistant runs through Claude Code, which you install and
  use under Anthropic's own terms.

Packaged builds carry the full notices of everything they include in a `Licences` folder (`tools/access.py`).
