# Builds dist\BattletechCoop-<version>-ModTek-<modtek>.zip: the official ModTek release, unmodified,
# plus this mod's Release build and install notes. Everyone who plays together installs the same zip,
# so their Mods folders produce the same fingerprint.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1
param(
    [string]$ModTekZip = "$PSScriptRoot\..\local\downloads\modtek-4.5.1\ModTek.zip",
    [string]$ModTekVersion = '4.5.1',
    [string]$ModTekSha256 = '53c038c1142c1552dcf7687bbee2b00d795b4691ff50a8c10901913cfa2339a9'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Resolve-Path "$PSScriptRoot\.."

$actual = (Get-FileHash $ModTekZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $ModTekSha256) { throw "ModTek.zip hash mismatch: $actual" }

dotnet build "$root\src\BattletechCoop" -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$bin = "$root\src\BattletechCoop\bin\Release"
$version = (Get-Content "$bin\mod.json" -Raw | ConvertFrom-Json).Version

$stage = "$root\local\release-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($ModTekZip, $stage)

$modDir = New-Item -ItemType Directory "$stage\Mods\BattletechCoop"
Copy-Item "$bin\BattletechCoop.dll", "$bin\mod.json" $modDir

$notes = @"
BattleTech Co-op $version (with ModTek $ModTekVersion)
=====================================================

PRE-ALPHA. Co-op skirmish and Campaign/Career missions, with shared control: every player can
command every mech in the player lance. Between missions the friend sees the host's ship and can
refit mechs and train or rename pilots; the host's game applies those changes.

Install (every player, same zip)
--------------------------------
1. In Steam: right-click BATTLETECH > Manage > Browse local files. This opens the folder
   that contains BattleTech.exe.
2. Extract this zip's contents into that folder, so winhttp.dll sits next to BattleTech.exe
   and the Mods folder sits next to it too.
3. Your Mods folder must contain only ModTek and BattletechCoop (plus the .modtek folder that
   ModTek creates). Any other mod makes your mod list differ, and joining is refused.
4. Start the game from Steam. The version text in the bottom-left of the main menu should end
   with "/W MODTEK". The first start takes longer than usual.

Connection test
---------------
- Everyone starts the game and waits in the main menu until the co-op window (Ctrl+Shift+C)
  says "Steam ready". This can take about 20 seconds after the game starts.
- Host: click "Host via Steam", then "Invite Steam friends".
- Friend: accept the invite in Steam. The co-op window should list both players.
  If the invite does nothing, try either of these:
    a) In the Steam friends list, right-click the host and choose "Join Game".
    b) The host clicks "Copy ID" next to the lobby number and sends it over chat. The friend
       pastes it into "Lobby ID" and clicks "Join Steam lobby".
- Both: click "Send test message" and "Send 2 MB test". The other side's window shows the
  message at the bottom.
- Check that the "Mods:" line at the bottom of the window is identical for everyone.
- Afterwards, send the host the file Mods\BattletechCoop\BattletechCoop.log from each PC.

Co-op skirmish
--------------
1. Connect as above. The friend stays in the main menu.
2. Host: open Skirmish, pick your lance and the opposing lance and a map, and launch as usual.
   The friend's game loads the same battle by itself.
3. Both: click "Begin mission".
4. Either player can select and command any mech in the player lance. Wait for an order to
   finish playing out before giving the next one. If both of you act at the same moment, the
   order that reaches the host first wins, and the other player sees "Order not accepted" and
   can give it again.
5. The top-right corner shows sync status. If a red "CO-OP DESYNC" banner appears, the two games
   no longer match: finish or quit the battle and send both BattletechCoop.log files and both
   Mods\.modtek\battletech_log.txt files.

Co-op Campaign / Career missions (opening for everyone new in 0.0.13)
---------------------------------------------------------------------
1. Host: load your campaign (or career) save as usual and connect; the friend joins from the
   main menu and sees your ship.
2. Host: pick a contract and launch it as usual. The friend's game loads the same mission by
   itself (this takes a little longer than yours).
3. Either of you presses "Begin mission": it starts for both of you once both briefings are
   loaded ("Waiting for the other players..." until then).
4. Both of you watch the intro and the opening conversation. Either player's Continue moves the
   conversation on for both.
5. Play as in skirmish. The friend's game never saves while connected (it is running your
   campaign, which must not overwrite their own saves). Your game saves as normal.

Shared ship (0.0.9; two-way updates 0.0.10; Store, Hiring Hall and more 0.0.12)
------------------------------------------------------------------------------
1. Host: load your campaign or career and connect; the friend joins from the main menu.
   A few seconds later the friend's game loads your ship (the top of the friend's screen says
   "Viewing <your company>").
2. Friend: as usual, refit, repair, rename, ready, store or scrap mechs in the Mech Bay; train,
   rename or dismiss pilots in the Barracks; hire in the Hiring Hall; buy and sell in the
   Store; cancel work orders; and change the company name, crest and colors in the Captain's
   Quarters. Each change is sent to the host. A notice at the top of both screens says
   whether the host's game applied or refused it (for example: not enough C-Bills, or that mech
   already has a refit in progress on the host).
3. The host's own changes of those kinds show up on the friend's screen within a moment, and
   an open Mech Bay, Barracks, Store or Hiring Hall redraws by itself on both sides (it waits
   while the MechLab is open or a pilot has unconfirmed training).
4. Anything else the friend does on the ship (travelling, passing time, accepting contracts,
   pilot portraits) is NOT kept: the friend's ship is a copy, and those are the host's to do. For the host's other changes, use
   "Refresh the host's ship" in the co-op window (Ctrl+Shift+C); a full reload takes 15-20
   seconds, and a notice at the top says when it is done.
5. When the host launches a mission, the friend's game follows as before.

Movement fix (0.0.14)
---------------------
A mech can no longer move, jump, melee or DFA again after it has moved this activation, and the
friend's controls switch from moving to firing as in single-player. A refused order shows a
notice at the top of the screen.

Shared combat pacing (0.0.15)
-----------------------------
While connected, the friend's game uses the host's combat pacing and cinematic settings
(Options > Game: speed up combat, attack and target cams, follow cams, zoom and cinematic
chances), so both screens play out the same way. Camera speeds and other personal settings are
unchanged. The friend's own settings are never overwritten: they come back when the session
ends, and any change the friend makes while connected is kept for afterwards.

Live company updates (0.0.16; ordering and travel fixes 0.0.17 to 0.0.24)
-------------------------------------------------------------------------
0.0.24: arriving in a system no longer makes the friend's game reload, and a reload no longer
leaves the friend in the Navigation room without menus.
0.0.22/0.0.23: after a jump the friend's Store and Hiring Hall now stay as the host's (before, the
friend's game replaced them with its own a moment later, which forced a reload), and a reload
no longer gets stuck on the loading screen (a game error in the Barracks button's Hiring Hall
menu stopped the reload from finishing).
0.0.21: the travel entry on the friend's schedule now counts down day by day with the host's,
starting a trip no longer fails when the friend hasn't opened the starmap, and a refit the
host accepts during a jump no longer gets progress for a day that passed before it.
0.0.20: travelling no longer makes the friend's game reload the ship (black screen) when the
host presses Go, at each jump, or after travel events. The friend's ship follows the host's
route and jumps, and the Store and Hiring Hall in the new system show the host's stock and
pilots. The friend's contract list stays empty after a jump (contracts are the host's).
0.0.19: the schedule's finance report and travel countdowns now tick on the friend's screen
as they do on the host's, and browsing a room the host hasn't visited no longer makes the
friend's game reload.
0.0.18: after a jump, the friend now docks at the planet together with the host (before, the
friend stayed "in space" until a refresh, so the Store was unavailable), and a work order one
player cancels now also leaves the other player's schedule.
The friend's view of the host's company now keeps up without reloading. As the host passes
time, the friend sees the day, C-Bills, morale, inventory, pilot XP and injuries change, and
refits and healing finish on the same day as on the host. Event results that change those
numbers show up within a second.
After a mission, the friend's ship now includes the mission's C-Bills and pilot XP (before, it
arrived just before they were paid, so the host's pilot training could not be shown).
If the friend's copy ever stops matching the host's (for example an event that adds a mech),
it catches up with a fresh ship by itself, waiting until the
friend closes the MechLab or confirms pilot training. This happens at most once every 5 minutes;
otherwise use "Refresh the host's ship".

Known limits: no saving or loading during co-op combat; if someone disconnects mid-battle, the
battle cannot be resumed together.

Uninstall
---------
Delete winhttp.dll, doorstop_config.ini, libdoorstop.dylib, libdoorstop.so, run.sh, the
doorstop folder and the Mods folder from the game folder. Then, in Steam, right-click
BATTLETECH > Properties > Installed Files > Verify integrity of game files.

Licenses
--------
ModTek $ModTekVersion is included unmodified under the LGPL-2.1 (see Mods\ModTek\LICENSE).
Source: https://github.com/BattletechModders/ModTek/tree/v$ModTekVersion
Official download: https://github.com/BattletechModders/ModTek/releases/tag/v$ModTekVersion
(ModTek.zip SHA-256 $ModTekSha256)
"@
Set-Content "$stage\BattletechCoop-README.txt" $notes -Encoding UTF8

New-Item -ItemType Directory "$root\dist" -Force | Out-Null
$zip = "$root\dist\BattletechCoop-$version-ModTek-$ModTekVersion.zip"
if (Test-Path $zip) { Remove-Item $zip }
# Windows PowerShell's ZipFile.CreateFromDirectory writes '\' separators, which other unzip tools
# extract as flat file names. Write entries with '/' instead.
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($item in Get-ChildItem $stage -Recurse) {
        $entry = $item.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if (-not $item.PSIsContainer) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.FullName, $entry, 'Optimal')
        }
        elseif (-not (Get-ChildItem $item.FullName -Force)) {
            [void]$archive.CreateEntry("$entry/") # keep empty folders, e.g. ModTek's AssembliesOverride
        }
    }
}
finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Created $zip"
Write-Host "Size    $((Get-Item $zip).Length) bytes"
Write-Host "SHA-256 $hash"
