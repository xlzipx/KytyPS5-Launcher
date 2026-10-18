KytyPS5 Launcher
================

A Windows launcher for KytyPS5, the open-source PlayStation 5 emulator for PC.

KytyPS5 and this launcher are not affiliated with or endorsed by Sony Interactive
Entertainment or PlayStation. Neither includes games, firmware or other copyrighted
system software. Play only games that you own and have obtained legally.


Getting started
---------------
1. Put KytyPS5.exe in a folder of its own (or into a KytyPS5 folder you already have)
   and start it.
2. No emulator yet? Click "Download KytyPS5" on the Games page: the launcher downloads
   the latest official release into a KytyPS5 folder beside it. Already have KytyPS5?
   Click "I have KytyPS5: choose its folder…" instead.
3. Click "Add game" and choose the game's folder (the one with eboot.bin and sce_sys)
   or a .zar archive of it.
4. Select the game, check its Game Settings on the right and click Launch.

Needs Windows 10 or 11 (64-bit) with .NET Framework 4.8, and a graphics card with
current Vulkan drivers.


How it works
------------
Games page      Your library. The panel on the right holds the selected game's own
                settings: resolution, display, stability options (Safe GPU mode),
                compatibility fixes, add-ons (DLC), cheats and shader precompile.
Compatibility   The KytyPS5 compatibility list, live from kytyps5.github.io.
Settings        Settings for every game: the emulator builds, user profile,
                controller, graphics card, notifications, frame generation and debug.
Logs            The emulator's logs, for when something goes wrong.

Emulator builds
  A game can run on the official KytyPS5 or on a community build made for particular
  games. Add a build in Settings > Emulators ("Add KytyPS5 build…") and choose it in
  the game's Game Settings. The launcher reads what each build supports and offers a
  game only those options; the others are greyed out.

Updates
  The launcher checks each build's GitHub releases (at start, or with "Check for
  updates") and shows "Update available" beside it. Update downloads the Windows
  package, checks its SHA-256 digest and replaces the program files. Saves, shader
  caches and settings stay; the replaced files are kept in the build's _Update\backup.
  For a build it does not know, enter its GitHub repository once beside it.

The launcher's own updates
  About shows when a newer launcher is out ("Update to …"): it downloads the release,
  checks its SHA-256 digest, replaces its own files and restarts. The library,
  settings and emulator builds stay as they are.

Shaders
  A game stutters the first time it meets new shaders. KytyPS5 records them while you
  play and compiles them before the next start, so a place stutters only once. Builds
  with a static precompile (such as the Demon's Souls build) offer "Precompile shaders"
  before playing.

Your data
  The library and settings are kept beside KytyPS5.exe (launcher-library.json,
  launcher-settings.json). Saves and shader caches are in each build's folder
  (_SaveData, _PipelineCache).


Links
-----
KytyPS5             https://github.com/KytyPS5/KytyPS5
Latest release      https://github.com/KytyPS5/KytyPS5/releases/latest
Compatibility list  https://kytyps5.github.io

The launcher is free software under the GNU General Public License version 2
(see "KytyPS5 Launcher - LICENSE.txt"). KytyPS5 is made by the KytyPS5 developers
and is licensed under GPL-2.0 as well.
