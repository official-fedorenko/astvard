@echo off
rem ---------------------------------------------------------------
rem  Astvard dedicated server
rem
rem  Mods live in BepInEx\plugins and load through the winhttp
rem  doorstop, so nothing here has to mention them:
rem    Jotunn.dll            - UI and command framework
rem    AstvardServerMod.dll  - our admin panel, templates, automation
rem    ServerDevcommands.dll - console commands over the network
rem
rem  Admins are listed by Steam ID in saves\adminlist.txt; that list is
rem  what gates the astvardadmin command and every write from the panel.
rem ---------------------------------------------------------------
set SteamAppId=892970
echo Starting Astvard dedicated server - CTRL-C to stop
"%~dp0valheim_server.exe" -nographics -batchmode -name "Astvard" -port 2456 -world "Astvard" -public 0 -savedir "%~dp0saves"
