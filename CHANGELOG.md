# Changelog

## 0.6.0

### Recording

- UDP receive loop отделён от SQLite writer ограниченной очередью на 8192 пакета.
- Добавлены WAL, пакетные транзакции, контроль глубины очереди и безопасное дренирование при Stop/Close.
- Повторные запросы остановки ожидают один и тот же lifecycle task.
- Добавлены `session_segments` и `recording_quality`.

### Protocol and data correctness

- Парсер приведён к официальной структуре F1 2026: 29-байтовый header, 24 машины, 60-байтовый Participant и 46-байтовый Final Classification.
- Исправлены 16-битные `driverId`/`teamId`, 32-байтовое имя участника и смещения итоговой классификации.
- Добавлены `overallFrameIdentifier`, packet 8, FLBK target и collision severity.
- Все ключевые join выполняются по `session_uid + overall_frame_identifier + car_idx`.
- Анализ выбирает последнюю логическую сессию, содержащую Lap Data, и не смешивает UID.

### Analysis

- Введены состояния круга `Complete`, `PartialStart`, `PartialEnd`, `Invalid`, `Rewound`.
- Лучшие круги и сравнения исключают pit/invalid/rewound/partial laps.
- Ущерб учитывается только как положительный прирост; ремонт хранится отдельно.
- Официальная итоговая классификация имеет приоритет над provisional Lap Data.
- Исправлены границы стинтов и дедупликация одного пит-стопа, замеченного несколькими сигналами.
- Delta и Track Map интерполируют только короткие разрывы до 100 м.

### Quality and maintenance

- Удалён неиспользуемый legacy SQL projector.
- Версия и schema version централизованы в `AppInfo`.
- Добавлена GitHub Actions Release-сборка и сквозной self-test проект.
- Самотесты покрывают layouts UDP 2026, flashback, завершённость кругов, разделение UID, пит-стратегию, интерполяцию и полный SQLite analysis pipeline.

## 0.5.3

- Race Report, Driver Compare, Stint Report и Pit Report.
- Track Map и базовый экспорт телеметрии.
