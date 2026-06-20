# RegHunter

A lightweight Windows tool for monitoring registry activity of a running process in real time.

## What it does

Attach to any running process and watch every registry key it creates, modifies, or deletes — live, as it happens. Useful for reverse engineering, malware analysis, app behavior inspection, or just curiosity.

## Features

- Attach to any process via a searchable process picker
- Live feed of registry creates, changes, and deletions with timestamps
- Full key paths resolved to friendly names (HKLM, HKCU, HKCR, etc.)
- Right-click any entry to open it directly in Regedit, copy the path, or delete the key/value
- Tracks all instances of multi-process apps (e.g. Chrome, Electron apps)

## Requirements

- Windows 10 or later
- .NET Framework 4.8
- **Must be run as Administrator** (ETW kernel tracing requires elevation)

## Usage

1. Run as Administrator
2. Click **ATTACH**
3. Select a process
4. Watch the registry activity roll in

## Notes

Registry events are captured via ETW (Event Tracing for Windows) kernel sessions. No drivers, no injection.
