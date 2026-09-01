# Data model and quality rules

Текущий `PRAGMA user_version = 6`.

## Слои базы

| Слой | Таблицы | Назначение |
|---|---|---|
| Raw | `raw_packets` | Восстанавливаемая UDP history |
| Live projection | `car_telemetry`, `lap_data`, `car_status`, `car_damage`, `car_setups` | Текущее состояние и player detail |
| Metadata | `session_metadata`, `session_segments`, `recording_quality`, `data_quality` | Версия, UID segments и качество |
| Analysis | `lap_quality`, `rewind_events`, `lap_summary`, `lap_state_summary`, `analysis_trace_10m`, `final_classification` | Проверенные круги и отчёты |
| Race Engineer | `race_engineer_laps`, `race_profile_snapshots`, `race_learning_observations` | Завершённые live laps, выбранный профиль и обучение |
| ERS | `ers_control_events`, `ers_profile_snapshots` | Решения, команды, feedback и точный профиль |
| Audit | `analysis_runs` | Результат каждого успешного анализа |

## Идентичность и время

Основной составной ключ кадра:

```text
session_uid + overall_frame_identifier + car_idx
```

`frame_identifier` может уменьшиться после flashback. `received_at` различает допустимые multi-event и per-car packets одного overall frame.

## Состояния круга

| State | Условие | Clean compare | Aggregates / learning |
|---|---|---:|---:|
| `Complete` | Подтверждён next-lap transition или finish reset | Да, если valid | Да, если clean, non-pit и без SC |
| `PartialStart` | Запись началась после стартовой зоны | Нет | Нет |
| `PartialEnd` | Нет подтверждённого завершения | Нет | Нет |
| `Invalid` | Игра выставляла invalid | Нет | Нет для tyre learning |
| `Rewound` | На круг повлиял official FLBK | Нет | Нет |

## Race Engineer tables

`race_engineer_laps` хранит только завершённые live laps:

- lap time и completion evidence;
- clean/pit/SC flags;
- visual compound и tyre age;
- worst-wheel wear в начале и конце круга;
- ERS percentage в начале и конце;
- позицию на завершении.

`race_profile_snapshots` содержит точный профиль, использованный в сессии. Внешний learned model хранится в `<root>/race_profiles/learned/Track_<id>.json`, потому что он должен применяться к следующим сессиям той же трассы. В SQLite остаются исходные наблюдения `race_learning_observations`.

Ресурс шин рассчитывается до configurable safe wear limit. Live observations имеют приоритет над baseline prior. Верхняя граница дополнительно ограничивается `usableLife` fitted set из packet 12. Результат всегда содержит low/high range и confidence.

Позиция после пита использует cumulative live gaps и pit-loss model для green/VSC/SC. Неопределённость pit loss переводится в диапазон позиций.

## ERS audit

`ers_control_events` заменяет прежний `ers_control_log.csv` и хранит:

- время, круг и участок;
- заряд, текущий и целевой режим;
- gap впереди и сзади;
- rule id, действие и причину;
- отправку клавиши, superseded feedback и UDP confirmation.

`ers_profile_snapshots` заменяет `ers_profile_used.json` и содержит operating mode, key bindings, input backend и полный track profile.

## Car Setup

`car_setups` содержит initial snapshot и изменения setup. Повтор без изменений не вставляется. `next_front_wing_value` сохраняется только для player row, поскольку это packet-level значение после массива машин.

## Classification and quality

`final_classification.classification_source` принимает `official_udp`, `provisional_latest_lap_data` или `legacy_analysis`. UI показывает источник и причину fallback.

Capture quality учитывает queue loss, invalid headers и packet sequence. Session completeness оценивает начало, финиш и packet 8. Analysis confidence оценивает пригодность подтверждённых кругов. Неизвестный cadence не превращается в нулевую потерю.

## Storage and packaging

`session.sqlite` является единственным автоматически создаваемым аналитическим артефактом. После анализа lean storage удаляет только rebuildable non-player frame projections и сохраняет `raw_packets`, summaries и player detail.

Новый RAR содержит SQLite backup после `VACUUM` и `integrity_check`. Внутри нет Excel, CSV, JSON, compact database или README. `race_summary.xlsx` создаётся только по явной команде пользователя и остаётся рядом с сессией.

Старые sidecar-файлы не удаляются автоматически, но v0.10.0 их больше не создаёт и не включает в новый RAR.
