# AutoMeld

AutoMeld imports a xivgear materia plan and verifies it against your currently equipped gear.

## Install From Dalamud

1. Open `/xlsettings` in game.
2. Open **Experimental** and add this custom repository:

   `https://raw.githubusercontent.com/Rycko2/auto-meld/main/repo.json`

3. Open `/xlplugins`, search for **AutoMeld**, and install it.
4. Use the plugin's **Settings** button in `/xlplugins` to open the example configuration window.

## Local In-Game Testing

Build and deploy both the DLL and manifest from WSL/Linux:

```bash
dotnet build AutoMeld.csproj -c Debug
```

The Windows folder must contain both files:

```text
AutoMeld.dll
AutoMeld.json
```

In FFXIV:

1. Open `/xlsettings`.
2. Open **Experimental** and find **Dev Plugin Locations**.
3. Add `C:\scratch\automeld`, not the individual DLL.
4. Reload Dalamud or restart the game.
5. Open `/xlplugins` and check the development plugin list.
6. Run `/xlautomeld` to open AutoMeld.

If the plugin does not appear, confirm that `AutoMeld.dll` and `AutoMeld.json` are in the same folder. Run `/xllog` and look for manifest, load, or dependency errors.

## Use In Game

1. Copy the JSON text from a xivgear export.
2. Run `/xlautomeld`.
3. Paste the export into **xivgear JSON**.
4. Select **Load pasted JSON**.
5. Select **Verify current gear** and review the expected and current item IDs plus planned materia.
6. Select **Start automatic meld** only after the summary is correct.

AutoMeld reads only currently equipped gear. It does not use the armory chest or normal inventory. A missing or mismatched item ID stops the operation before any gear is changed.

## Debug Logs

Run `/xllog` to view AutoMeld logs, including import failures, equipped item IDs, validation mismatches, workflow stops, and unexpected errors.

## Current Limitation

The automated driver uses the current FFXIV materia agent and must be tested carefully against the current game client. It removes existing materia from the selected equipped item, selects the requested materia, confirms the attach dialog, waits for completion, and rechecks the item before continuing. Stop immediately if `/xllog` reports an unexpected agent or dialog state.
