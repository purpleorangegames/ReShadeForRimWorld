# ReShade for RimWorld (unofficial)

Loads ReShade .fx shaders in RimWorld 1.7, using ReShade's own effect compiler.

Not made by or affiliated with crosire or the ReShade project.

It was made to run on native Linux RimWorld, since ReShade needs Wine/Proton with Windows RimWorld to work on Linux. And to have a ReShade port that can be installed directly from the Workshop, so ReShade can reach more people.

All thanks to ReShade: https://github.com/crosire/reshade

Press Home in game to open the overlay. From there you can turn effects on and off, change their order and settings, edit the shaders and download shader packages from the same list ReShade uses. PrintScreen saves a screenshot.

Effects change the map only, the UI stays as it is. Tick "Game effects also change the UI" to have them change the UI too, like ReShade does. There is also a UI tab with effects that only change the UI, turn it on in Settings.

Shaders, presets and screenshots go in the RimWorld save folder, in ReShade. You can also copy your own shaders into ReShade/reshade-shaders/Shaders.

Presets use the same format as ReShade, so you can bring yours over. Import picks a preset .ini, installs only the shaders it uses and loads it. Presets that other mods have in a presets folder show up there too.

Works on Linux and Windows. Mac should work too but isn't tested yet.

Shader packages keep their own licenses.

Needs Harmony.

The native compiler is built with the scripts in Native (Linux, Windows with MinGW, Mac) from a checkout of crosire/reshade, they apply Native/windows-paths.patch so includes written for Windows also work on Linux and Mac, and the Mac build also applies Native/apple.patch. GitHub Actions builds all three and puts the whole mod in one zip.

AI was used to help code/fix issues
