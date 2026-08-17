# Darkroom ISO agent

This Windows agent listens to the ESP32-C3 over USB serial. It waits until the
desired ISO has remained unchanged for at least five seconds.

It only changes ISO when:

1. Darkroom Booth is running.
2. Its main window appears to be in full-screen photobooth mode.
3. The requested ISO differs from the last successfully applied ISO.

The agent then:

1. Sends Escape.
2. Opens Settings.
3. Opens Camera.
4. Selects the requested ISO from the Darkroom dropdown.
5. Selects Start Booth.

UI controls are located by their accessible names. There are no fixed screen
coordinates. If a control cannot be identified, the action stops and the agent
tries to return to booth mode.

Run `Inspect-DarkroomUi.ps1` first while Darkroom's settings screen is visible
to confirm the exact accessible control names. Run `Install-Agent.ps1` to
install the agent for the current Windows user.

Logs are stored below:

`%LOCALAPPDATA%\JvdP\DarkroomIsoAgent\logs`
