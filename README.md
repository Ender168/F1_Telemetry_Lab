# F1 Telemetry Lab v0.7.1

Desktop-приложение на C# и Avalonia для записи, нормализации и анализа UDP-телеметрии EA SPORTS F1 с форматом пакетов 2026.

Версия 0.7.1 сохраняет все изменения 0.7.0 и исправляет online-сессии со sparse vehicle indices: фиксированные массивы UDP 2026 всегда разбираются по 24 слотам. Только официальный `FLBK` подтверждает rewind; обратные счётчики без `FLBK` сохраняются отдельно как suspected state reset. При отсутствии packet 8 классификация явно маркируется provisional.

## Возможности

- Асинхронная UDP-запись через ограниченную очередь и один последовательный SQLite writer.
- Безопасная остановка: очередь дренируется, транзакции фиксируются, затем запускаются анализ и упаковка.
- Lossless-хранение исходных UDP-пакетов для повторного анализа без повторной гонки.
- Поддержка Motion, Session, Lap Data, Event, Participants, Car Setup, Car Telemetry, Car Status, Final Classification и Car Damage.
- Официальные события `FLBK` как основной источник flashback и отдельная защита от ложного rewind на финише.
- Официальная классификация из packet 8 и явно помеченный provisional fallback из последнего Lap Data.
- Lap Compare с reference-серией, контекстом метрики, интерактивным курсором и интерполяцией только коротких пропусков.
- Track Map с геометрической X/Z-дистанцией и встроенной панелью Track Detail.
- Race Report, Driver Compare, Stint Report, Pit Report и просмотр истории Car Setup.
- Три независимые оценки качества: Capture, Session completeness и Analysis confidence.
- Компактный `chatgpt_pack.sqlite` и ZIP без тяжёлой базы сырых пакетов.

## Car Setup

При анализе packet 5 сохраняется в таблицу `car_setups`. Записывается исходный setup и каждое его изменение; неизменившиеся 2 Hz пакеты дедуплицируются. Сохраняются:

- front/rear wing;
- on/off throttle differential и engine braking;
- camber и toe;
- suspension, anti-roll bars и ride height;
- brake pressure и front brake bias;
- четыре tyre pressure;
- ballast, fuel load и next front wing value.

Те же строки автоматически попадают в `exports/all_cars/car_setups.csv`, `chatgpt_pack.sqlite` и создаваемый ZIP. Сессии, записанные старыми версиями, достаточно повторно проанализировать: raw packet 5 уже находится в `session.sqlite`.

## Быстрый старт

Требования для сборки: .NET 10 SDK. Основной сценарий использования рассчитан на Windows x64.

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

В приложении задайте корневую папку, нажмите `Start Recording`, проведите сессию и завершите запись кнопкой `Stop`. После остановки анализ и, при включённом Auto ZIP, упаковка выполняются автоматически.

## Интерфейс

Верхний уровень содержит пять разделов:

1. `Live`: запись и текущая диагностика.
2. `Sessions`: карточки записей, структурированные metadata, качество и источник классификации.
3. `Analysis`: Lap Compare и Track Map со встроенным Track Detail.
4. `Race`: Overview, Car Setup, Driver Compare, Stints и Pits.
5. `Settings`: port, storage root, Auto ZIP, retention, язык, UI scale и псевдонимы гонщиков.

Выбранная сессия является общим контекстом. Доступные гонщики и отчёты загружаются автоматически.

## Сборка и тесты

```powershell
dotnet build F1TelemetryLab.sln --configuration Release
dotnet run --project tests/F1TelemetryLab.SelfTest/F1TelemetryLab.SelfTest.csproj --configuration Release --no-build
dotnet test tests/F1TelemetryLab.Tests/F1TelemetryLab.Tests.csproj --configuration Release --no-build
dotnet publish src/F1TelemetryLab.App/F1TelemetryLab.App.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/F1TelemetryLab-win-x64
```

GitHub Actions выполняет Release-сборку, старые протокольные self-tests, xUnit-регрессии и создаёт Windows x64 artifact. Часовой benchmark запускается вручную через `workflow_dispatch` с параметром `run_long_tests`.

## Структура данных

Каждая запись создаёт папку в `<root>/telemetry_packs/`:

```text
session.sqlite              полная локальная база и сырые UDP-пакеты
manifest.json               актуальные metadata и три оценки качества
analysis_manifest.json      результат последнего анализа
exports/                    CSV-экспорты, включая car_setups.csv
chatgpt_pack.sqlite         компактная аналитическая база, включая car_setups
<session-name>.zip          пакет для передачи или архивирования
```

`session.sqlite` остаётся локально и не включается в ZIP.

Подробности: [архитектура](docs/ARCHITECTURE.md), [схема и правила данных](docs/DATA_MODEL.md), [release notes](docs/RELEASE_0.7.1.md), [история изменений](CHANGELOG.md).

## Корректность и ограничения

- Поддерживается только `packetFormat = 2026`. Другие форматы сохраняются как raw, но не участвуют в анализе.
- Круг подтверждается переходом на следующий круг либо распознанным финишным reset с ненулевым `lastLapTime` и достаточным покрытием дистанции.
- Круг, затронутый flashback, исключается из clean-сравнений.
- Агрегаты топлива, износа, ERS и деградации используют только завершённые непитовые круги и показывают число наблюдений.
- Короткие пропуски трассы интерполируются только при разрыве не более 100 м.
- Точная Racenet-граница сейчас встроена только для Austria. На других трассах интерфейс честно показывает геометрическую центральную линию без условной «точной» границы.
- `estimated_missing_frames` не трактуется как ноль, пока cadence конкретного packet type не измерен; интерфейс показывает `not calculated`.

Структуры парсера сверены с официальной [EA SPORTS F1 2026 Season Pack UDP specification](https://forums.ea.com/blog/f1-games-game-info-hub-en/ea-sports%E2%84%A2-f1%C2%AE25-2026-season-pack-udp-specification/12187347).

## Лицензия и товарные знаки

Проект не аффилирован с Electronic Arts или Formula 1. Названия игр и серий принадлежат соответствующим правообладателям. Перед распространением приложения добавьте выбранную лицензию в `LICENSE`.
