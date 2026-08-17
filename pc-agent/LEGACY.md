# Legacy agent

This directory contains the earlier PowerShell Darkroom ISO agent and the
standalone ESP OTA uploader. It is retained for diagnostics and recovery only.

The supported production application is pc-overlay/LightDarkroomOverlay.cs,
installed together with updater/JvdpAutoUpdater.cs by the generated installer.
The installer disables the legacy Startup entry so both implementations cannot
control Darkroom at the same time.

The legacy agent defaults to automatic ESP discovery. Leave serialPort empty in
config.json unless a diagnostic session explicitly needs a fixed port.
