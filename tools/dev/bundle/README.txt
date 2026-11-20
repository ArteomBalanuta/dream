Dream desktop client (self-contained bundle)
============================================

Nothing needs to be installed: the .NET runtime is inside this folder.

Start
  Windows : double-click dream.exe  (or run it from a terminal)
  Linux   : ./run-dream.sh          (extract with: tar -xzf dream-<version>-linux-x64.tar.gz)

Your world and settings
  The local world database is created next to the program (config/player-profiles.db).
  To keep it elsewhere, for example when this folder is read-only, set DREAM_DATA_DIR to a writable folder first.
  Delete nothing under config/content: that is the game data.

Windows notes
  vcomp140.dll (OpenMP runtime used by the physics library) is included, so the Visual C++ redistributable is not needed.

Linux notes
  Needs a desktop with OpenGL 3.3 core (Mesa or a vendor driver) and X11 or Wayland.
  Packages: libicu, libstdc++6, libopenal1 (without it the game runs silent), glibc 2.14 or newer.

Checks
  dream --validate-content   checks the bundled game data and exits 0 when it is fine.
