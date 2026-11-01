# NOTICE / CREDITS

UPS Hub
Copyright (C) 2026 AslanCore

This program is free software: you can redistribute it and/or modify it under the
terms of the GNU General Public License as published by the Free Software Foundation,
either version 3 of the License, or (at your option) any later version. See LICENSE.

## Upstream projects

UPS Hub is a new C# / Avalonia implementation. Its behaviour (settings, shutdown
rules, reconnect and notification logic, the legacy settings import) follows the
WinNUT-Client project, which is licensed GPL-3.0-or-later. UPS Hub is distributed
under the same license.

- **WinNUT** (original): Copyright (C) 2006-2007 Michael Liberman
- **WinNUT-Client**: Copyright (C) 2019-2021 Gawindx (Decaux Nicolas),
  https://github.com/gawindx/WinNUT-Client
- **WinNUT-Client (nutdotnet)**: Copyright (C) the nutdotnet contributors, the maintained
  continuation, https://github.com/nutdotnet/WinNUT-Client
- Fork behaviour notes were taken from ericnewton76, BigWebstas, merodahero and bgr.

## Network UPS Tools

UPS Hub speaks the Network UPS Tools (NUT) network protocol. NUT is
Copyright (C) the NUT project contributors, https://networkupstools.org/ (GPL-2.0-or-later).

The local USB UPS support (`src/UpsHub.Core/Local/`) is a C# port of parts of NUT, used under
GPL-2.0-or-later, which allows distribution under GPL-3.0-or-later as part of UPS Hub:

- `drivers/hidparser.c`: HID report-descriptor parser and value extraction.
  Copyright (C) 1998-2003 MGE UPS SYSTEMS, Luc Descotils.
- `drivers/libhid.c`, `drivers/hidtypes.h`: HID path handling, scaling and units.
  Copyright (C) 2003-2007 Arnaud Quette, John Stamp, Peter Selinger, Arjen de Korte.
- `drivers/usbhid-ups.c`, `drivers/usbhid-ups.h`: variable mapping, status logic and lookup tables.
  Copyright (C) 2003-2022 Arnaud Quette, 2005 John Stamp, 2005-2006 Peter Selinger,
  2007-2009 Arjen de Korte, 2016 Eaton / Arnaud Quette, 2020-2026 Jim Klimov.
- `drivers/apc-hid.c` (APC, VID 051d): Copyright (C) 2003-2009 Arnaud Quette, 2005 John Stamp,
  2005 Peter Selinger, 2009-2010 Arjen de Korte.
- `drivers/cps-hid.c` (CyberPower, VID 0764): Copyright (C) 2003-2008 Arnaud Quette,
  2005-2006 Peter Selinger, 2020-2025 Jim Klimov, 2024 Alejandro González.
- `drivers/mge-hid.c` (Eaton / MGE and OEMs): Copyright (C) 2003-2015 Arnaud Quette,
  2015-2024 Eaton / Arnaud Quette, 2020-2026 Jim Klimov, 2024-2025 "DaRK AnGeL".
- `drivers/blazer.c`, `drivers/blazer_ser.c`: Megatec Q1 parsing and the charge estimate.
  Copyright (C) 2008-2009 Arjen de Korte, 2012 Arnaud Quette.

All of the above are licensed "GPL version 2, or (at your option) any later version";
the full author lists are in the NUT source tree (https://github.com/networkupstools/nut).

The ported code was rewritten in C# and adapted (Windows HID caps instead of the raw descriptor,
read-only use). Mistakes in the port are UPS Hub's, not NUT's.

## Fonts (SIL Open Font License 1.1)

- IBM Plex Sans and IBM Plex Mono: Copyright (C) IBM Corp., with Reserved Font Name "Plex".
  Shipped unmodified. Licence: `src/UpsHub.App/Assets/Fonts/OFL-IBMPlex.txt`.
- Syne: Copyright (C) The Syne Project Authors. Static SemiBold/Bold instances made from the
  variable font. Licence: `src/UpsHub.App/Assets/Fonts/OFL-Syne.txt`.

## Libraries (NuGet, not vendored)

- Avalonia: MIT, https://avaloniaui.net
- CommunityToolkit.Mvvm: MIT, .NET Foundation
- System.IO.Ports (Megatec serial): MIT, .NET Foundation
- .NET runtime and System.* packages: MIT, .NET Foundation
- xUnit (tests only): Apache-2.0
