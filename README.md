# Taskbar Monitor

CPU, memory and network readouts inside the Windows 11 taskbar, in the empty space
left of the Start button, plus a panel naming the heaviest process per resource.

![CPU, RAM, network and top process shown inside the taskbar](docs/preview.png)

It is a child window of the taskbar rather than an overlay floating above it, so
Explorer shows, hides and clips it along with everything else on the bar.

## Install

Download `TaskbarMonitor.exe` from [Releases](https://github.com/ahmadallouch/taskbar-monitor/releases/latest)
and run it. No installer, no runtime, no admin rights. Right click it for the menu,
which includes a Start with Windows toggle.

Needs Windows 11, or Windows 10 version 1607 or later, on x64.

It is not code signed, so SmartScreen will warn on first run. Choose More info, then
Run anyway. Each release lists its SHA-256 if you want to check the download.

## What it shows

CPU and memory go green, orange, red at 60% and 85%. Network stays neutral, since a
busy link is not a fault condition.

The right hand panel rotates every three seconds:

```
CPU 34%    RAM 7.8 GB    DISK 12 MB/s    GPU 61%
chrome     Code          MsMpEng         dwm
```

Each resource is ranked on its own. There is no combined score, because 30% of CPU
and 8 GB of memory do not belong on one scale. Resources with nothing to report drop
out of the rotation, and kernel pseudo processes like Memory Compression are skipped.

Per process network is missing on purpose: Windows only exposes it through an ETW
session that needs elevation.

## Configuration

`settings.json` sits next to the executable. Edit it, then pick Reload settings from
the menu.

| Key | Default | |
| --- | --- | --- |
| `IntervalMs` | 1000 | Sample and redraw period |
| `Margin` | 14 | Gap from the taskbar edge, logical px |
| `Anchor` | `Auto` | `Left`, `BeforeTray`, or by Windows version |
| `ShowOnAllTaskbars` | true | Draw on secondary monitors too |
| `GroupGap` | 18 | Space between groups, logical px |
| `ShowCpu` `ShowRam` `ShowNetwork` | true | Which readings to include |
| `ShowBars` | true | Fill bars under CPU and memory |
| `RamAsGigabytes` | false | `9.8/31.7 GB` instead of `54%` |
| `FontScale` | 1.0 | Multiplier on the taskbar's text size |
| `WarnPercent` `CriticalPercent` | 60, 85 | Orange and red thresholds |
| `ColorizeValues` | true | Tint the number, not just the bar |
| `ShowTopConsumer` | true | The rotating process panel |
| `TopConsumerWidth` | 108 | Fixed width so it does not resize as names change |
| `TopConsumerIntervalMs` | 2000 | How often processes are ranked |
| `TopRotateMs` | 3000 | Dwell time per resource |
| `IncludeGpu` | true | Include GPU in the rotation |

## Behaviour worth knowing

- **Crowding.** A centred taskbar spreads outward as you open windows. The widget
  measures the real gap on each redraw and drops groups from the right until it fits,
  restoring them as space returns, and hides if nothing fits.
- **Multiple monitors.** One instance per taskbar, each at its own DPI. Implemented
  but never tested on real multi monitor hardware.
- **Windows 10.** Supported by design, unverified. Start sits in the corner there, so
  the widget anchors before the notification area instead.
- **Vertical taskbars** are skipped. The layout is a horizontal row.
- **Fullscreen apps.** Drawing and ranking stop completely while one is in front.

## Cost of running it

Around 7 MB of working set and about 1% of one core, which on a 16 core machine is
under 0.1% of the whole. It is compiled ahead of time to native code, so there is no
.NET runtime and the executable is the whole program.

## How it works

Windows 11 removed deskbands and replaced them with nothing, so there is no supported
way to add to the taskbar. This creates a `WS_CHILD` window parented to
`Shell_TrayWnd` and draws with `UpdateLayeredWindow` and per pixel alpha, compositing
onto the real acrylic. Colours and text sizes come from the shell's own settings.

That is not a sanctioned API. A Windows update could change the taskbar's window
structure and break it, in which case the widget stops drawing and nothing else
happens.

Readings come from `GetSystemTimes`, `GlobalMemoryStatusEx`, adapter counters, one
`NtQuerySystemInformation` call for every process at once, and PDH for GPU. No
`PerformanceCounter` anywhere, which avoids both counter name localisation and its
warm up delay.

## Build

```
build.cmd
```

Needs the .NET 10 SDK and the Visual Studio C++ build tools that native AOT links
against.

## License

MIT
