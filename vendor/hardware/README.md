# Hardware monitoring dependencies

LibreHardwareMonitorLib 0.9.6 is restored from NuGet and used unmodified.
Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701
It includes DiskInfoToolkit 1.1.2, RAMSPDToolkit-NDD 1.4.2 and signed PawnIO modules.
Module source: https://github.com/namazso/PawnIO.Modules

Full MPL/LGPL notices are in `licenses/` and are embedded in the root executable.
The portable/installed application also includes these files in `licenses/hardware`.
Resolved NuGet package versions and their licence metadata are copied into that folder at packaging time.
Rebuilding this public project with a replacement LibreHardwareMonitor assembly/package is supported; no modified vendor library is shipped.

PawnIO itself is NOT bundled or installed by the application installer. The dedicated
button downloads official PawnIO Setup 2.2.0, checks exact size, SHA256 and Authenticode,
then runs the standard signed installer. It does not select unrestricted mode, disable
Windows security features, or uninstall a shared PawnIO installation on app removal.
Pinned download metadata: `src/Zapret.Desktop/Assets/Hardware/components.json`.
Driver source and exception: https://github.com/namazso/PawnIO
Official distribution: https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0
