**UPS Hub**  
A Windows client for [Network UPS Tools (NUT). It watches your UPS and shuts the PC down cleanly before the battery runs out.](https://networkupstools.org/ "https://networkupstools.org/")  
 [![](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAABmJLR0QA/wD/AP+gvaeTAAAACXBIWXMAAA7EAAAOxAGVKw4bAAAAxElEQVR4nO1W0Q6AIAiE/v+bpaeaYyYc6NLVvbRm484DL1lEKAIRIWaG1zQ4IgAhsHCMJkc31BXQKmbtvF73iOkKQIvV8LbpFqAJ9Dvac2Z2iQ4N4UiYQ2gJ9G7g6TsupQw7UpHjefTCBCEmwueEaPAMhBzQBVDC+tkit2pOOQWXEx5H0gIy/ScK/Atq0os4k5ihHOilHJyYyyfhTIjIJg48idQ5EME6DrwlZB0HthaQcRG+FbeQuaZ/ewb2ScJfQBSe9p77EZ7a26s/yQAAAABJRU5ErkJggg==)](https://github.com/AslanCore/UPS-Hub/releases/latest "https://github.com/AslanCore/UPS-Hub/releases/latest")  
Built it from scratch in C# (.NET 10 + Avalonia).  
![](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAABmJLR0QA/wD/AP+gvaeTAAAACXBIWXMAAA7EAAAOxAGVKw4bAAAAxElEQVR4nO1W0Q6AIAiE/v+bpaeaYyYc6NLVvbRm484DL1lEKAIRIWaG1zQ4IgAhsHCMJkc31BXQKmbtvF73iOkKQIvV8LbpFqAJ9Dvac2Z2iQ4N4UiYQ2gJ9G7g6TsupQw7UpHjefTCBCEmwueEaPAMhBzQBVDC+tkit2pOOQWXEx5H0gIy/ScK/Atq0os4k5ihHOilHJyYyyfhTIjIJg48idQ5EME6DrwlZB0HthaQcRG+FbeQuaZ/ewb2ScJfQBSe9p77EZ7a26s/yQAAAABJRU5ErkJggg==)  
**What it does**  
- Monitors one or more NUT servers, and every UPS on them  
- Finds NUT servers on your network by itself  
- Shuts down, hibernates or sleeps the PC on low battery or low runtime, after a countdown you can cancel  
- Sits in the tray with a status icon, and sends Windows notifications on power events  
- Imports your old WinNUT-Client settings  
- Reads a UPS plugged straight into the PC over USB (experimental, see below)  
- Updates itself from GitHub Releases, checking the signature first  
**Install**  
Grab the latest build from [Releases. There's an installer (`UpsHub-Setup-x.y.z.exe`, per-user, no admin needed) or a portable zip. You need Windows 10 or 11, 64-bit.](https://github.com/AslanCore/UPS-Hub/releases "https://github.com/AslanCore/UPS-Hub/releases")  
The builds aren't code-signed yet, so SmartScreen will probably warn you. Click "More info" and then "Run anyway". If you want to check the file first, compare its hash with `SHA256SUMS.txt` from the release:  
```  
Get-FileHash .\UpsHub-Setup-0.6.2.exe -Algorithm SHA256  
   
```  
`SHA256SUMS.txt` is also signed with [minisign. The key and the commands are in](https://jedisct1.github.io/minisign/ "https://jedisct1.github.io/minisign/") [docs/DETAILS.md.](#anchor-1 "#anchor-1")  
On first start, a short setup wizard helps you connect to your NUT server. If you don't have one yet, the server-side config is in [docs/DETAILS.md.](#anchor-2 "#anchor-2")  
**Direct USB is experimental**  
Reading a UPS directly over USB, without NUT, is a port of NUT's `usbhid-ups` driver. So far it's only been tested against an APC BX1200MI.  If you try it, please [open an issue with your UPS model and whether it worked. Going through a NUT server is the tested path.](https://github.com/AslanCore/UPS-Hub/issues "https://github.com/AslanCore/UPS-Hub/issues")  
**Building**  
You need the .NET 10 SDK.  
```  
dotnet test  
 dotnet publish src/UpsHub.App -f net10.0-windows10.0.19041.0 -p:PublishProfile=win-x64  
   
```  
That gives you a single self-contained `artifacts/win-x64/UpsHub.exe`. `dotnet run --project src/UpsHub.App -f net10.0 -- --demo` runs a demo with a simulated UPS, on Windows or Linux. More (installer, command line options, layout) is in [docs/DETAILS.md.](docs/DETAILS.md "docs/DETAILS.md")  
**Credits**  
- [WinNUT-Client by Gawindx, and the original WinNUT by Michael Liberman.](https://github.com/gawindx/WinNUT-Client "https://github.com/gawindx/WinNUT-Client")  
- [nutdotnet/WinNUT-Client, the maintained continuation, and its contributors.](https://github.com/nutdotnet/WinNUT-Client "https://github.com/nutdotnet/WinNUT-Client")  
- [Network UPS Tools. The USB support uses NUT's `usbhid-ups` mapping tables and HID parser.](https://github.com/networkupstools/nut "https://github.com/networkupstools/nut")  
Full attributions, including fonts, are in [NOTICE.md.](NOTICE.md "NOTICE.md")  
**License**  
GPL-3.0-or-later, same as WinNUT-Client. See [LICENSE.](LICENSE "LICENSE")  
