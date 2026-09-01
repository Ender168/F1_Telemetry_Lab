# F1 Telemetry Lab v0.10.0

Desktop-приложение на C# и Avalonia для записи, нормализации и анализа UDP-телеметрии EA SPORTS F1 с форматом пакетов 2026.

Версия 0.10.0 добавляет Live Race Engineer и меняет формат хранения. Последние три завершённых круга, ресурс шин, позиция после пит-стопа и ERS-стратегия теперь видны во время гонки как в основном окне, так и в отдельном always-on-top оверлее. Автоматически создаваемые данные остаются в `session.sqlite`. Для передачи сессии создаётся RAR5 с максимальным сжатием и ровно одним файлом внутри: согласованным снимком `session.sqlite`.

## Возможности

- Асинхронная UDP-запись через ограниченную очередь и один последовательный SQLite writer.
- Безопасная остановка: очередь дренируется, транзакции фиксируются, затем запускаются анализ и упаковка.
- Исходные UDP-пакеты сохраняются для повторного анализа без повторной гонки.
- Atomic analysis во временной SQLite-базе с заменой рабочей базы только после успеха.
- Lap Compare, Track Map, Race Report, Driver Compare, Stints, Pits и Car Setup.
- Три независимые оценки качества: Capture, Session completeness и Analysis confidence.
- ERS Autopilot с режимами Off, Dry-run и Live, отдельными JSON-профилями трасс и UDP-feedback.
- Live Race Engineer с диапазонами и уровнем доверия, без ложной точности.
- Обучаемые модели износа шин и потери на пит-стопе для каждой трассы.
- Ручной экспорт небольшого `race_summary.xlsx` без автоматических CSV/JSON sidecar-файлов.
- RAR5 через WinRAR: `-ma5 -m5 -md128m`, внутри только `session.sqlite`, после создания выполняется тест архива.

## Быстрый старт

Требования для сборки: .NET 10 SDK. Основной сценарий рассчитан на Windows x64. Для автоматической упаковки должен быть установлен WinRAR.

```powershell
dotnet restore F1TelemetryLab.sln
dotnet run --project src/F1TelemetryLab.App/F1TelemetryLab.App.csproj
```

Настройки в игре:

| Параметр | Значение |
|---|---|
| UDP Telemetry | On |
| UDP Format | 2026 |
| UDP IP Address | 127.0.0.1 |
| UDP Port | 20777 |
| UDP Send Rate | 60 Hz |
| ERS Assist | Off для ERS Live |
| Increase / Decrease ERS Deploy Mode | F8 / F7 для ERS Live |

В приложении задайте корневую папку, нажмите `Start Recording`, проведите сессию и завершите запись кнопкой `Stop`. После остановки анализ выполняется автоматически. Если включён `Auto RAR`, приложение находит WinRAR или использует путь из Settings.

ZIP fallback намеренно отсутствует. Если WinRAR не найден, `session.sqlite` остаётся целым, а в журнале появляется явная ошибка упаковки.

## Live Race Engineer

Блок Race Engineer показывает:

| Карточка | Что рассчитывается |
|---|---|
| Last laps | Время последних трёх подтверждённых завершённых кругов |
| Tyres | Худшее колесо, текущий износ, наблюдаемый темп износа и диапазон кругов до безопасного лимита |
| Pit stop | Диапазон позиций после пита, ожидаемая потеря времени и уровень трафика |
| ERS | Заряд, целевой коридор участка, рекомендация экономить/держать план/атаковать и следующий Boost-участок |

Оценки шин обучаются только по завершённым чистым непитовым кругам. Круги под SC/VSC, invalid и pit laps в выборку не попадают. Tyre Sets packet ограничивает верхнюю границу ресурса, если игра передала usable life. Позиция после пита строится по текущим live gaps и pit-loss профилю, поэтому всегда показывается диапазоном.

Кнопка `Open overlay` открывает отдельное прозрачное always-on-top окно. Его можно перемещать и менять размер. Автоматическое открытие на старте записи включается в Settings.

Профили находятся в `<root>/race_profiles/*.json`. Первый профиль `China_Race.json` содержит исходные значения для Китая. После анализа приложение обновляет `<root>/race_profiles/learned/Track_<id>.json`. Повторный анализ одной и той же `session_uid` не добавляет наблюдения второй раз.

## ERS Autopilot

| Режим | Поведение |
|---|---|
| `Off` | Контроллер не запускается |
| `Dry-run` | Решения рассчитываются и сохраняются в SQLite, клавиши не отправляются |
| `Live` | F7/F8 отправляются scan-code нажатиями, каждый следующий режим подтверждается по Packet 7 |

Профили автопилота находятся в `<root>/ers_profiles/`. Управляющий журнал и точный снимок выбранного профиля теперь сохраняются в таблицах `ers_control_events` и `ers_profile_snapshots`, а не в CSV/JSON.

Live-ввод разрешён только для офлайн-сессии, сухого профиля, активного гоночного круга, свежей телеметрии и активного окна F1 25. Он блокируется при включённом ERS Assist, паузе, spectator mode, Safety Car, VSC, formation lap и нахождении в питах. F12 аварийно отключает ввод до следующей записи.

Прототип управляет стандартным `ersDeployMode`: None, Medium, Hotlap и Boost. Overtake Mode 2026 остаётся ручным.

## Данные и экспорт

Новая папка сессии минимальна:

```text
session.sqlite              рабочая база, raw UDP и весь результат анализа
<session-name>.rar          опционально: только snapshot session.sqlite
race_summary.xlsx           опционально: ручной краткий экспорт
```

`race_summary.xlsx` создаётся кнопкой `Export race summary` и содержит пять листов: Laps, Tyres, Pits, ERS и Quality. Он не входит в RAR.

Старые сессии с CSV, JSON, ZIP или `chatgpt_pack.sqlite` продолжают читаться, но новый анализ не создаёт эти файлы заново.

## Сборка и тесты

```powershell
dotnet build F1TelemetryLab.sln --configuration Release
dotnet run --project tests/F1TelemetryLab.SelfTest/F1TelemetryLab.SelfTest.csproj --configuration Release --no-build
dotnet test tests/F1TelemetryLab.Tests/F1TelemetryLab.Tests.csproj --configuration Release --no-build
dotnet publish src/F1TelemetryLab.App/F1TelemetryLab.App.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/F1TelemetryLab-win-x64
```

GitHub Actions выполняет Release build, self-tests, xUnit-регрессии и создаёт self-contained Windows x64 artifact. Контрактные тесты отдельно проверяют RAR-аргументы, SQLite integrity, отсутствие sidecar-файлов, расчёты Race Engineer и структуру XLSX.

Подробности: [архитектура](docs/ARCHITECTURE.md), [схема данных](docs/DATA_MODEL.md), [release notes](docs/RELEASE_0.10.0.md), [история изменений](CHANGELOG.md).

## Ограничения

- Поддерживается только `packetFormat = 2026`. Другие форматы сохраняются как raw, но не участвуют в анализе.
- Ресурс шин и позиция после пита являются оценками, поэтому приложение показывает диапазон и confidence.
- Первый track-specific Race Engineer профиль подготовлен только для Китая. На другой трассе используется generic low-confidence fallback до появления отдельного профиля.
- RAR создаётся только через WinRAR. Это обеспечивает требуемый формат и максимальное сжатие, но требует установленный `WinRAR.exe`.
- ERS Live сначала следует проверять в Dry-run и короткой офлайн-сессии.

Структуры парсера сверены с официальной [EA SPORTS F1 2026 Season Pack UDP specification](https://forums.ea.com/blog/f1-games-game-info-hub-en/ea-sports%E2%84%A2-f1%C2%AE25-2026-season-pack-udp-specification/12187347).

## Лицензия и товарные знаки

Проект не аффилирован с Electronic Arts или Formula 1. Названия игр и серий принадлежат соответствующим правообладателям. Перед распространением приложения добавьте выбранную лицензию в `LICENSE`.
