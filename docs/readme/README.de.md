# OML · Onion Minecraft Launcher

**Sprachen:** [简体中文](../../README.md) · [繁體中文](README.zh-TW.md) · [简体文言](README.zh-classical.md) · [繁體文言](README.zh-classical-t.md) · [English (US)](README.en-US.md) · [English (UK)](README.en-GB.md) · [Français](README.fr.md) · **Deutsch** · [Русский](README.ru.md)

Ein Minecraft-Launcher für Windows 10/11 x64, entwickelt mit .NET 8 und WPF.
OML installiert Vanilla, Forge, Fabric, NeoForge, OptiFine und Modpacks, durchsucht Modrinth,
repariert unvollständige Installationen ohne vorhandene Dateien zu überschreiben, lädt Java
automatisch herunter und aktualisiert sich über Velopack.

> Dieses Projekt wird überwiegend von DeepSeek-V4.1-Flash 1017 entwickelt.

Aktuelle Version: **1.1.2** (stabil)

## Funktionen

- **Versionen:** Installation von Vanilla, Forge, Fabric, NeoForge und OptiFine; Import von `.mrpack`-Modpacks
- **Kompatibilität:** erkennt von PCL2 verwaltete `.minecraft`-Ordner; beschädigte Versions-JSONs werden mit Begründung gemeldet statt stillschweigend übersprungen
- **Sicherheit:** eine vorhandene `versions/<id>/<id>.json` wird niemals gelöscht oder überschrieben — fehlende Bibliotheken und Assets werden schrittweise ergänzt
- **Java:** erkennt installierte Laufzeiten, lädt bei Bedarf die benötigte Hauptversion herunter und prüft den konfigurierten Pfad vor dem Start
- **Inhalte:** Modrinth-Suche für Mods, Shader und Modpacks inklusive rekursiver Abhängigkeitsauflösung
- **Oberfläche:** dreispaltiges Dashboard, 3D-Skinvorschau (Slim/Classic, zwei Ebenen), globale Aufgabenwarteschlange mit Abbrechen, Wiederholen und Watchdog
- **Updates:** installierte Builds prüfen beim Start GitHub Releases und können im Hintergrund herunterladen und neu starten

## Installation

Lade die neueste Version von
[GitHub Releases](https://github.com/LSwzx/OML_test/releases/latest) herunter:

| Paket | Beschreibung |
|:--|:--|
| `OML-win-1.1.2-Installer.exe` | Installationsassistent mit Ordnerauswahl (empfohlen) |
| `OML-win-1.1.2-Portable.zip` | Portable Version, entpacken und starten |

Wähle ein beschreibbares Verzeichnis wie `D:\Apps\OML`; unter `Program Files` kann der
Updater keine Dateien schreiben. Die installierte Version aktualisiert sich automatisch,
die portable Version muss manuell ersetzt werden.

Datenspeicherorte:

| Inhalt | Pfad |
|:--|:--|
| Konfiguration | `%AppData%\OML\launcher_config.json` |
| Heruntergeladene Java-Laufzeiten | `%AppData%\OML\runtimes\` |
| Standard-Spielverzeichnis | `%AppData%\.minecraft` (mit `--game-dir` änderbar) |

## Aus dem Quellcode bauen

Voraussetzungen: Windows 10/11 x64 und das [.NET SDK 8.0+](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/LSwzx/OML_test.git
cd OML_test

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
dotnet run --project OML.Test -- --no-network

# Selbstenthaltende Einzeldatei (etwa 71 MB)
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

`PublishTrimmed` muss `false` bleiben: WPF unterstützt kein IL-Trimming.

## Lizenz

Dieses Projekt steht unter [GPL-3.0-only](../../LICENSE), Copyright (C) 2026 LSwzx.

> Note: Versions prior to v1.0.2 were released under the MIT License. Future versions are licensed under GPL-3.0.

Die enthaltene Komponente `SkinView` bleibt unter MIT-Lizenz; siehe
[THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md).
