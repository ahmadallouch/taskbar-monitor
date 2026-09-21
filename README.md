# Taskbar Monitor

CPU, memory and network readouts inside the Windows 11 taskbar, in the empty space
left of the Start button, plus a panel naming the heaviest process per resource.

![CPU, RAM, network and top process shown inside the taskbar](docs/preview.png)

## Install

Download `TaskbarMonitor.exe` from [Releases](https://github.com/ahmadallouch/taskbar-monitor/releases/latest)
and run it. No installer, no runtime, no admin rights, about 7 MB resident. Right
click the widget for its menu.

Needs Windows 11, or Windows 10 version 1607 or later, on x64. Windows 10 support is
by design but has not been verified on real hardware.

It is not code signed, so SmartScreen will warn on first run. Choose More info, then
Run anyway. Each release lists a SHA-256 checksum for the download.

Windows offers no supported way to extend the taskbar, so this attaches to the
taskbar's own window. A Windows update could change that and stop it drawing.

## What it shows

```
CPU 34%    RAM 7.8 GB    DISK 12 MB/s    GPU 61%
chrome     Code          MsMpEng         dwm
```

CPU and memory turn orange at 60% and red at 85%. Network stays neutral. The right
hand panel rotates every three seconds, showing the process using the most of each
resource. Resources with nothing to report drop out of the rotation.

As the taskbar fills up and the centred buttons spread outward, readouts are dropped
from the right to fit the space left, and come back when there is room.

Per process network is not shown. Windows only exposes that through an ETW session
needing elevation, which this does not ask for.

## Configuration

`settings.json` sits next to the executable. Edit it, then pick Reload settings from
the menu. Every key is in that file; the ones worth knowing:

| Key | Default | |
| --- | --- | --- |
| `IntervalMs` | 1000 | Sample and redraw period |
| `Anchor` | `Auto` | `Left`, `BeforeTray`, or by Windows version |
| `ShowCpu` `ShowRam` `ShowNetwork` | true | Which readings to include |
| `WarnPercent` `CriticalPercent` | 60, 85 | Orange and red thresholds |
| `ShowTopConsumer` | true | The rotating process panel |
| `ShowOnAllTaskbars` | true | Secondary monitors too, untested on real hardware |

## Build

```
build.cmd
```

Needs the .NET 10 SDK and the Visual Studio C++ build tools that native AOT links
against.

## License

MIT
