# Data model and quality rules

## Слои базы

| Слой | Таблицы | Назначение |
|---|---|---|
| Raw | `raw_packets`, `car_telemetry` | Lossless UDP history и live read model |
| Recording metadata | `session_metadata`, `session_segments`, `recording_quality` | Версия схемы, UID-сегменты и качество записи |
| Parsed | `lap_data`, `motion_data`, `car_status`, `car_damage`, `events`, `participants`, `final_classification_packet` | Декодированные packet rows |
| Quality | `lap_quality`, `rewind_events` | Состояние круга и точки rollback |
| Projection | `analysis_context`, `analysis_samples`, `lap_summary`, `lap_state_summary`, `analysis_trace_10m`, `final_classification` | Запросы UI и экспорт |

## Идентичность и время

Основной составной ключ кадра:

```text
session_uid + overall_frame_identifier + car_idx
```

`frame_identifier` может уменьшиться после flashback и не используется как единственный join key. `received_at` разрешает редкие дубликаты одного overall frame.

## Состояния круга

| State | Условие | Допуск в clean compare |
|---|---|---|
| `Complete` | Есть переход на следующий lap, `lastLapTime > 0`, покрыты start и end | Да |
| `PartialStart` | Запись началась после стартовой зоны | Нет |
| `PartialEnd` | Нет подтверждённого завершения | Нет |
| `Invalid` | Игра выставляла current-lap-invalid | Нет |
| `Rewound` | На круг повлиял rollback/flashback | Нет |

Pit lap хранится как завершённый круг, но исключается из выбора best clean pace и сравнений.

## Delta

Reference lap задаёт сетку расстояния. Значение compare интерполируется между соседними samples только если расстояние между ними не превышает 100 м:

```text
delta_ms = compare_time_ms(distance) - reference_time_ms(distance)
```

Положительный `delta_ms` означает, что compare медленнее reference. `segment_loss_ms` показывает изменение cumulative delta приблизительно за последние 30 м.

## Classification

`final_classification.classification_source` принимает:

- `official_udp`, если получен packet 8;
- `provisional_lap_data`, если официального пакета нет.

Псевдонимы меняют только display fields и не перезаписывают исходное имя участника.

## Recording quality

- `Good`: критических и предупреждающих счётчиков нет.
- `Usable with warnings`: единичные invalid/unsupported headers, duplicate/out-of-order frames или смена UID.
- `Unreliable`: была потеря из bounded queue, invalid headers превысили адаптивный порог или missing-frame estimate превысил порог.

Оценка относится к записи, а не к мастерству пилота и не к валидности отдельного круга.
