# ReShade for RimWorld (unofficial)

Loads ReShade .fx shaders in RimWorld 1.7, using ReShade's own effect compiler.

Not made by or affiliated with crosire or the ReShade project.

Press Home in game to open the overlay. From there you can turn effects on and off, change their order and settings, edit the shaders and download shader packages from the same list ReShade uses. PrintScreen saves a screenshot.

Shaders, presets and screenshots go in the RimWorld save folder, in ReShade. You can also copy your own shaders into ReShade/reshade-shaders/Shaders.

Presets use the same format as ReShade, so you can bring yours over.

Works on Linux and Windows, Mac is coming.

Shader packages keep their own licenses.

Needs Harmony.

The native compiler is built with the scripts in Native (Linux, Windows with MinGW, Mac) from a checkout of crosire/reshade, they apply Native/windows-paths.patch so includes written for Windows also work on Linux and Mac, and the Mac build also applies Native/apple.patch. GitHub Actions builds all three and puts the whole mod in one zip.

AI was used to help code/fix issues
