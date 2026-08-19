# AutoMeld

AutoMeld imports a xivgear materia plan and applies it to your gear.

## Install

1. Open `/xlsettings` in Dalamud.
2. Open the **Experimental** tab and add:

   `https://raw.githubusercontent.com/Rycko2/auto-meld/main/repo.json`

3. Install **AutoMeld** from the Dalamud Plugin Installer.

## Use In Game

1. Export your gear set as JSON from xivgear.
2. Enter `/xlautomeld` to open the configuration window.
3. Enter the JSON file path and select **Load**.
4. Review the displayed item and materia plan.
5. Select **Start automatic meld**.

AutoMeld verifies that every equipped slot matches the imported item ID before removing or adding materia. Any missing, unexpected, or mismatched gear stops the operation without changing materia.

Use **Stop** to cancel the workflow.
