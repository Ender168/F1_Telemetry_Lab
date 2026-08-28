# Data model and quality rules

## Слои базы

| Слой | Таблицы | Назначение |
|---|---|---|
| Raw | `raw_packets`, `car_telemetry` | Lossless UDP history и live read model |
| Recording metadata | `session_metadata`, `session_segments`, `recording_quality`, `data_quality` | Schema, UID segments и три измерения качества |
| Parsed | `lap_data`, `motion_data`, `car_status`, `car_damage`, `car_setups`, `events`, `participants`, `final_classification_packet` | Декодированные packet rows |
| Quality | `lap_quality`, `rewind_events` | Active branch, состояние круга и rollback |
| Projection | `analysis_context`, `analysis_samples`, `lap_summary`, `lap_state_summary`, `analysis_trace_10m`, `final_classification` | UI, reports и export |

Текущий `PRAGMA user_version = 4`.

## Идентичность и время

Основной составной ключ кадра:

```text
session_uid + overall_frame_identifier + car_idx
```

`frame_identifier` может уменьшиться после flashback. `received_at` разрешает допустимые multi-event и per-car packets одного overall frame.

## Состояния круга

| State | Условие | Clean compare | Aggregates |
|---|---|---:|---:|
| `Complete` | Подтверждён next-lap transition или finish reset, покрыты start/end | Да, если не invalid/pit | Да, если не pit |
| `PartialStart` | Запись началась после стартовой зоны | Нет | Нет |
| `PartialEnd` | Нет подтверждённого завершения | Нет | Нет |
| `Invalid` | Игра выставляла invalid | Нет | Только явно безопасные поля |
| `Rewound` | На круг повлиял official FLBK или fallback rollback | Нет | Нет |

При official FLBK сохраняются target frame/time и причина `official_flbk`. Эвристическая причина начинается с `heuristic:`.

## Car Setup

Таблица `car_setups` содержит initial snapshot и изменения setup:

| Группа | Колонки |
|---|---|
| Identity | `received_at`, `session_uid`, `session_time`, frame IDs, player/car IDs |
| Aero | `front_wing`, `rear_wing`, `next_front_wing_value` |
| Differential | `on_throttle`, `off_throttle`, `engine_braking` |
| Alignment | `front_camber`, `rear_camber`, `front_toe`, `rear_toe` |
| Chassis | front/rear suspension, anti-roll bar и ride height |
| Brakes | `brake_pressure`, `brake_bias` |
| Tyres | rear-left, rear-right, front-left, front-right pressure |
| Other | `ballast`, `fuel_load` |

Строки сравниваются по всем setup fields. Повтор без изменений не вставляется. `next_front_wing_value` сохраняется только для player row, потому что в протоколе это одно значение после массива из 24 машин.

## Delta and track distance

Reference lap задаёт сетку расстояния:

```text
delta_ms = compare_time_ms(distance) - reference_time_ms(distance)
```

Положительный `delta_ms` означает более медленный compare. Интерполяция разрешена только через разрыв не более 100 m.

Для профилей трассы расстояние вычисляется накопленной длиной X/Z polyline и нормализуется на `track_length_m` игры. Source corner distances переводятся тем же mapping.

## Classification

`final_classification.classification_source` принимает:

- `official_udp`, если получен packet 8;
- `provisional_latest_lap_data`, если packet 8 отсутствует;
- `legacy_analysis` только при чтении старой схемы без source column.

UI показывает источник, причину fallback и время последнего raw packet.

## Recording quality

| Rating | Capture |
|---|---|
| Good | нет queue loss, invalid headers и подтверждённого packet sequence damage |
| Usable with warnings | есть ограниченные предупреждения, не делающие запись непригодной |
| Unreliable | queue drops или серьёзное повреждение transport stream |

Packet 3 допускает несколько событий одного кадра, packet 11 является per-car, terminal packets могут иметь frame 0. Такие случаи не считаются duplicate/out-of-order.

`estimated_missing_frames` используется только вместе с `missing_frames_estimated = 1`. Иначе UI показывает `not calculated`.

## Compact pack

`chatgpt_pack.sqlite` исключает `raw_packets`, но включает metadata, quality dimensions, lap summaries, setup changes, rewinds/events, participants, 10 m trace, classification и aliases. `PRAGMA user_version` compact DB совпадает с основной схемой.
