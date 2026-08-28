# F1 Telemetry Lab v0.6.0

Desktop-приложение на C# и Avalonia для записи, нормализации и анализа UDP-телеметрии EA SPORTS F1 с форматом пакетов 2026.

Версия 0.6.0 переводит проект из состояния MVP в воспроизводимый аналитический инструмент: запись больше не блокируется SQLite-операциями, данные разных сессий и веток flashback не смешиваются, а итоговые отчёты строятся из подтверждённых кругов.

## Возможности

- Асинхронная UDP-запись через ограниченную очередь и один последовательный SQLite writer.
- Безопасная остановка: очередь дренируется, транзакции сбрасываются, затем запускаются анализ и упаковка.
- Хранение исходных UDP-пакетов для повторного анализа без повторной поездки.
- Поддержка Motion, Session, Lap Data, Event, Participants, Car Telemetry, Car Status, Final Classification и Car Damage.
- Консервативная обработка flashback, invalid, незавершённых и частично записанных кругов.
- Официальная итоговая классификация из packet 8 с пометкой provisional при отсутствии этого пакета.
- Lap Compare с единой reference-серией и интерполяцией коротких пропусков до 100 м.
- Track Map с cumulative delta, локальной потерей времени, потерей скорости и top-зонами.
- Race Report, Driver Compare, Stint Report и Pit Report.
- Диагностика качества записи: переполнение очереди, некорректные заголовки, дубликаты, нарушение порядка и смены UID.
- Компактный `chatgpt_pack.sqlite` и ZIP без тяжёлой базы сырых пакетов.

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

В приложении укажите корневую папку, нажмите `Start Recording`, проведите сессию и завершите запись кнопкой `Stop`. Не закрывайте игру до появления пакетов в live-панели.

## Сборка Release

```powershell
dotnet build F1TelemetryLab.sln --configuration Release
dotnet run --project tests/F1TelemetryLab.SelfTest/F1TelemetryLab.SelfTest.csproj --configuration Release --no-build
dotnet publish src/F1TelemetryLab.App/F1TelemetryLab.App.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/F1TelemetryLab-win-x64
```

GitHub Actions выполняет Release-сборку, сквозные самотесты и создаёт готовый Windows x64 artifact.

## Структура данных

Каждая запись создаёт папку в `<root>/telemetry_packs/`:

```text
session.sqlite              полная локальная база и сырые UDP-пакеты
manifest.json               метаданные записи и оценка качества
analysis_manifest.json      результат последнего анализа
exports/                    CSV-экспорты
chatgpt_pack.sqlite         компактная аналитическая база
<session-name>.zip          пакет для передачи или архивирования
```

`session.sqlite` остаётся локально и не включается в ZIP. Это одновременно уменьшает архив и не раскрывает полный поток сырых данных без явного действия пользователя.

Подробности: [архитектура](docs/ARCHITECTURE.md), [схема и правила данных](docs/DATA_MODEL.md), [изменения версии](CHANGELOG.md).

## Корректность и ограничения

- Поддерживается только `packetFormat = 2026`. Другие форматы сохраняются как raw, но не участвуют в анализе.
- Круг считается завершённым только после перехода на следующий круг с ненулевым `lastLapTime` и достаточным покрытием дистанции.
- Круг, затронутый flashback, намеренно исключается из clean-сравнений.
- Короткие пропуски трассы интерполируются линейно только при разрыве не более 100 м. Большие пробелы не дорисовываются.
- Точная Racenet-граница трассы сейчас встроена только для Austria; для остальных трасс используется профиль центральной линии или телеметрический fallback.
- `estimated_missing_frames` зарезервирован в схеме, но пока не вычисляется: разные типы UDP-пакетов имеют разные допустимые интервалы кадров.

Структуры парсера сверены с официальной [EA SPORTS F1 2026 Season Pack UDP specification](https://forums.ea.com/blog/f1-games-game-info-hub-en/ea-sports%E2%84%A2-f1%C2%AE25-2026-season-pack-udp-specification/12187347).

## Лицензия и товарные знаки

Проект не аффилирован с Electronic Arts или Formula 1. Названия игр и серий принадлежат соответствующим правообладателям. Перед распространением приложения добавьте выбранную лицензию в `LICENSE`.
