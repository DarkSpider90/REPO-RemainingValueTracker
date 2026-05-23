# Remaining Value Tracker

The mod scans valuables at the start of a level, then reveals every remaining undiscovered valuable once the configured discovery threshold is reached.

## Config

The config file is generated as `BepInEx/config/DarkSpider90.RemainingValueTracker.cfg`.

- `Enable Mod`: enables or disables the tracker.
- `Tracking Mode`: `Value` tracks discovered value, `Count` tracks discovered item count.
- `Trigger Percent`: percent that must be discovered before the rest is revealed. Default is `85`.
- `Scan Interval`: seconds between progress checks.
- `Reveal Hotkey`: key used to reveal all remaining valuables manually. Default is `F10`.
- `Show Message`: shows a top-center message when the reveal happens.
- `Message Text`: text shown by the reveal message.
- `Debug Logs`: writes progress details to the BepInEx log.

## Install

Build the project and place `RemainingValueTracker.dll` in:

`BepInEx/plugins/RemainingValueTracker/`

## Credits / Inspirations

Special thanks to QNCNXW8R and his FindRemainingValuables mod as inspiration.
