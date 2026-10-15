@echo off
rem Installs LedAgent as a Windows service using Servy (https://github.com/aelassas/Servy).
rem Run this **as Administrator**, on the machine that physically has the motherboard LEDs.
rem
rem Servy is not on PATH on this machine; it is invoked by its full path. If Servy is not
rem installed, install it first:  winget install aelassas.Servy
rem
rem NOTE: MSI Mystic Light (MSLight.dll) is 32-bit, so the agent is published as x86.
rem If this machine has no MSI hardware, change -a x86 to -a x64 and the agent will fall
rem back to the mock backend (or use --backend mock).

set REPO=C:\Users\Chamb\source\repos\ShaderToE131_V2
set PUBLISH=%REPO%\publish-ledagent

mkdir "%REPO%\logs" 2>nul
dotnet publish "%REPO%\LedAgent\LedAgent.csproj" -c Release -a x86 -o "%PUBLISH%" --nologo

"C:\Program Files\Servy\servy-cli.exe" install -n LedAgent --displayName "LedAgent LED Service" -d "LED Agent: serves shader colours to motherboard RGB LEDs (cloned from github.com/phobicadz/ShaderToE131_V2)" -p "%PUBLISH%\LedAgent.exe" --startupDir "%PUBLISH%" --params "--port 6743 --backend auto --idle-off 120" --startupType Automatic --stdout "%REPO%\logs\ledagent.out.log" --stderr "%REPO%\logs\ledagent.err.log" > "%REPO%\logs\install-led-agent.log" 2>&1
echo ExitCode=%ERRORLEVEL%>> "%REPO%\logs\install-led-agent.log"
