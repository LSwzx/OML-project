# OML · Onion Launcher

**Языки:** [简体中文](../../README.md) · [繁體中文](README.zh-TW.md) · [简体文言](README.zh-classical.md) · [繁體文言](README.zh-classical-t.md) · [English (US)](README.en-US.md) · [English (UK)](README.en-GB.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · **Русский**

Лаунчер Minecraft для Windows 10/11 x64 на .NET 8 и WPF.
OML устанавливает ванильную игру, Forge, Fabric, NeoForge, OptiFine и сборки модов, ищет
контент на Modrinth, чинит неполные установки, не перезаписывая существующие файлы,
самостоятельно скачивает Java и обновляется через Velopack.

> Проект в основном разрабатывается DeepSeek-V4.1-Flash 1017.

Текущая версия: **1.1.2** (стабильная)

## Возможности

- **Версии:** установка ванильной игры, Forge, Fabric, NeoForge и OptiFine; импорт сборок `.mrpack`
- **Совместимость:** распознаёт папки `.minecraft`, созданные PCL2; при повреждённом JSON версии выводится причина, а не тихое пропускание
- **Безопасность:** существующий `versions/<id>/<id>.json` никогда не удаляется и не перезаписывается — недостающие библиотеки и ресурсы добавляются постепенно
- **Java:** определяет установленные среды, скачивает нужную мажорную версию при отсутствии и проверяет указанный путь перед запуском
- **Контент:** поиск по Modrinth для модов, шейдеров и сборок с рекурсивным разрешением зависимостей
- **Интерфейс:** трёхколоночная панель, 3D-просмотр скина (Slim/Classic, два слоя), глобальная очередь задач с отменой, повтором и сторожевым таймером
- **Обновления:** установленная версия проверяет релизы GitHub при запуске и может скачать обновление и перезапуститься в фоне

## Установка

Скачайте последний релиз со страницы
[GitHub Releases](https://github.com/LSwzx/OML-project/releases/latest):

| Пакет | Описание |
|:--|:--|
| `OML-win-1.1.2-Installer.exe` | Мастер установки с выбором папки (рекомендуется) |
| `OML-win-1.1.2-Portable.zip` | Портативная версия: распакуйте и запустите |

Выбирайте папку с правами записи, например `D:\Apps\OML`: в `Program Files` обновлятор
не сможет записывать файлы. Установленная версия обновляется автоматически, портативную
нужно заменять вручную.

Расположение данных:

| Данные | Путь |
|:--|:--|
| Конфигурация | `%AppData%\OML\launcher_config.json` |
| Скачанные среды Java | `%AppData%\OML\runtimes\` |
| Каталог игры по умолчанию | `%AppData%\.minecraft` (изменяется через `--game-dir`) |

## Сборка из исходников

Требования: Windows 10/11 x64 и [.NET SDK 8.0+](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/LSwzx/OML-project.git
cd OML-project

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
dotnet run --project OML.Test -- --no-network

# Автономная сборка одним файлом (около 71 МБ)
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

`PublishTrimmed` должен оставаться `false`: WPF не поддерживает IL-тримминг.

## Лицензия

Проект распространяется по лицензии [GPL-3.0-only](../../LICENSE), Copyright (C) 2026 LSwzx.

> Note: Versions prior to v1.0.2 were released under the MIT License. Future versions are licensed under GPL-3.0.

Компонент `SkinView` остаётся под лицензией MIT; см.
[THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md).
