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

Кнопка `Open overlay` открывает прозрачный always-on-top Overlay с шестью независимыми боксами. Через `Edit overlay` каждый бокс можно перемещать, масштабировать и скрывать. После закрепления окно становится click-through. Раскладка сохраняется автоматически с учётом разрешения экрана.

Профили находятся в `<root>/race_profiles/*.json`. Первый профиль `China_Race.json` содержит исходные значения для Китая. После анализа приложение обновляет `<root>/race_profiles/learned/Track_<id>.json`. Повторный анализ одной и той же `session_uid` не добавляет наблюдения второй раз.

## ERS Autopilot

| Режим | Поведение |
|---|---|
| `Off` | Контроллер не запускается |
| `Dry-run` | Решения рассчитываются и сохраняются в SQLite, клавиши не отправляются |
| `Live` | F7/F8 отправляются scan-code нажатиями, каждый следующий режим подтверждается по Packet 7 |

Профили автопилота находятся в `<root>/ers_profiles/`. Управляющий журнал и точный снимок выбранного профиля теперь сохраняются в таблицах `ers_control_events` и `ers_profile_snapshots`, а не в CSV/JSON.

Live-ввод поддерживает сетевые и офлайн-сессии. Для управления нужны сухой профиль, активный гоночный круг, свежая телеметрия и активное окно F1 25. Он блокируется при включённом ERS Assist, паузе, spectator mode, Safety Car, VSC, formation lap и нахождении в питах. F12 аварийно отключает ввод до следующей записи.

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


### Nearby-driver overlay (0.10.7)

Open **Race Engineer → Open overlay → Edit overlay** (Russian: **Открыть оверлей → Настроить оверлей**).
Three additional cards appear alongside the existing widgets:

- **TYRES · AGE / ШИНЫ · ВОЗРАСТ**: the player and race positions ±2, including names, compound and laps on the current set. Place it next to the game's upper-left leaderboard. `0` is a fresh set; `?` means unavailable or stale data, not an estimated age. S/M/H are Soft/Medium/Hard; I/W are Intermediate/Wet.
- **AHEAD / ВПЕРЕДИ** and **BEHIND / СЗАДИ**: the last three completed laps of the driver one race position ahead or behind. Cards follow overtakes and use the same lap-time format as the player's card. PIT, INVALID and observed SC/VSC laps retain their labels. Current unfinished laps are excluded.

Drag the cards beside your existing lap panel, adjust their scale with ± or the mouse wheel, then **Lock / Закрепить**. The saved layout persists across launches. **Show all / Показать все** restores hidden cards without resetting their positions. At the front/back of the field there may be fewer than five nearby rows. Neighbours are selected by race position, not physical proximity to a lapped car.

The cards work while recording UDP. Missing opponent telemetry is shown explicitly; no tyre age is inferred from wear. Recent lap history can fill in from the game's Session History packets after Lap Data identifies the current lap.


### ERS: пит на этом кругу (0.10.11)

Во время записи нажмите **треугольник + кружок (△ + ○)** одновременно и отпустите обе кнопки. Одно нажатие включает режим, следующее выключает. Удержание не вызывает повторных переключений. Кнопки считываются из события `BUTN` телеметрии игры (Triangle/Y `0x2`, Circle/B `0x4`); отдельный драйвер геймпада приложению не нужен. Если игра не передаёт эти события, на плашке остаётся `?`. Обычные действия этих кнопок в игре сохраняются.

Кнопка переключает переменную состояния `pitLapBurn`. В JSON профиля добавьте правила с `"condition": "pitLapBurn"`: они доступны только при включённом флаге. Участки `start_m/end_m`, режим `target_mode`, приоритет, минимальная батарея/газ/скорость, таймер, лимит расхода и `once_per_lap` работают как в обычных правилах. Например, элемент массива `rules`:

```json
{
  "id": "pit-straight",
  "segment": "Pit-lap straight",
  "condition": "pitLapBurn",
  "start_m": 4050,
  "end_m": 5200,
  "target_mode": "boost",
  "priority": 2500,
  "minimum_battery_pct": 8,
  "minimum_throttle_pct": 88,
  "minimum_speed_kph": 160,
  "maximum_active_ms": 15000,
  "maximum_deploy_pct": 100,
  "once_per_lap": true
}
```

Для этих правил обычный энергетический коридор не ограничивает расход: действует `minimum_battery_pct` самого правила. Явно заданный `minimum_energy_surplus_pct` сохраняется. Приоритет по-прежнему определяет победителя среди подходящих правил. За пределами участков, после исчерпания лимитов или при отсутствии правил `pitLapBurn` работает обычная стратегия. Выключение флага прекращает активное пит-правило, но не обнуляет `once_per_lap`. Верхнеуровневый блок `pit_lap_strategy` не исполняется: поведение задаётся массивом `rules`. JSON не перезаписывается при нажатии кнопки. В Dry-run меняются только рекомендации, в Live отправляются команды в игру.

Режим относится к текущему кругу и выключается при въезде в пит-лейн, смене круга/сессии, завершении гонки/записи или Flashback. Это не команда вызова механиков. Все проверки паузы, SC/VSC, свежести данных, ERS Assist и активного окна игры сохраняются.

Плашка **«ERS · ПИТ НА ЭТОМ КРУГУ»** показывает ВКЛ/ВЫКЛ, номер круга, состояние сочетания и режим управления. Её можно перемещать, масштабировать и скрывать через «Настроить оверлей». Если телеметрия пропала, плашка явно показывает это и не выдаёт последнее состояние кнопок за текущее.

### ERS: проверка устойчивости (0.10.12)

Автопилот получает MotionEx только своей машины: угол передних колёс, yaw rate, углы увода и slip ratio обоих задних колёс. Новые блоки JSON `traction_gates.hotlap` и `traction_gates.boost` задают ограничения и время непрерывной устойчивости. Правило может переопределить их через `traction_gate`. Профили без этих блоков работают как раньше.

При ожидании устойчивости усиление не отправляется, `once_per_lap` не расходуется и таймер нового правила не запускается. Проверяются также повторные команды и режим пит-круга. Причина ожидания выводится в подробном статусе ERS и журнале. Это проверка перед усилением, автоматического снижения мощности после срыва здесь нет.

Готовый **экспериментальный** профиль Японии: `data/ers_profile_examples/Japan_Race_traction.json`. Для включения скопируйте его в `ers_profiles` внутри папки записи и перезапустите запись. Начальные пороги требуют проверки в Dry-run и на трассе. Полная схема: [ERS profile guide](data/ers_profiles/README.md).
