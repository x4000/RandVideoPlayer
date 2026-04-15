# License

## Application source code — MIT License

Copyright (c) 2026 Chris McElligott Park

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

## Third-party components

RandVideoPlayer is built on top of the following third-party components. Each
has its own license; the terms below apply to those components only, not to
the MIT-licensed application code above. When you redistribute a binary build
of RandVideoPlayer, you must redistribute these notices alongside it.

### VLC / libVLC / libVLCcore
> Copyright © the VideoLAN team and contributors.
> Licensed under the **GNU Lesser General Public License, version 2.1 or (at
> your option) any later version** (LGPL-2.1-or-later).
> https://www.videolan.org/vlc/
> Full license text: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html

This application dynamically links to unmodified `libvlc.dll` / `libvlccore.dll`
binaries distributed via the VideoLAN project. Under LGPL-2.1 Section 6, the
user of this application retains the right to relink the application against a
modified version of the libVLC libraries; because this application loads
libVLC at runtime from its own directory (`libvlc/win-x64/`), that right is
preserved by simply replacing those DLLs.

### LibVLCSharp and LibVLCSharp.WinForms
> Copyright © VideoLAN and contributors.
> Licensed under the **GNU Lesser General Public License, version 2.1 or
> later** (LGPL-2.1-or-later).
> https://code.videolan.org/videolan/LibVLCSharp

### VideoLAN.LibVLC.Windows (NuGet packaging of libVLC binaries)
> Copyright © the VideoLAN team.
> Licensed under **LGPL-2.1-or-later** (covering the libVLC binaries) and
> **MIT** (covering the NuGet packaging files).
> https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/

### Squirrel Eiserloh — SquirrelNoise5 / "Squirrel3" integer-noise RNG
The shuffle engine's random source is a clean-room C# port of the
noise-based integer hash described in Squirrel Eiserloh's GDC 2017 talk,
"Math for Game Programmers: Noise-Based RNG". The algorithm itself is
released by its author for free public use; no portion of Eiserloh's
original source code is included.

### Segoe MDL2 Assets (icon font)
Glyphs used in the transport bar are rendered from the `Segoe MDL2 Assets`
font, which ships with Windows 10 and Windows 11. The font is a Microsoft
system font and is not redistributed by this project; it is consumed in place
on the end-user's system.

### Application icon (`book_4.png` / `app.ico`)
The application icon is provided by the project author under the same MIT
terms as the application source code.

---

## Summary for redistributors

If you ship a build of RandVideoPlayer:

- The application code is MIT — attach this file.
- libVLC and LibVLCSharp are LGPL — you must allow end users to substitute
  their own `libvlc.dll` / `libvlccore.dll`. Shipping those DLLs alongside
  the EXE in a writable directory (as this build does) satisfies that
  requirement.
- Do not modify the libVLC DLLs and then ship the result as the official
  upstream build; either ship the unmodified upstream binaries or clearly
  indicate that you have modified them (LGPL-2.1 §6).
