RouterPilot Installer project

Expected repository layout:

  RouterPilot.sln
  RouterPilot\
  RouterPilot.Installer\
  publish\win-x64\RouterPilot.exe

Before building the MSI, publish the application from the solution directory:

  dotnet publish .\RouterPilot\RouterPilot.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64

Then rebuild RouterPilot.Installer in Visual Studio.

Expected MSI output name:

  RouterPilot-2.0.2-x64.msi

Do not commit bin, obj, or publish output folders.
