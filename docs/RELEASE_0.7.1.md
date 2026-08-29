# F1 Telemetry Lab 0.7.1

## Назначение патча

0.7.1 исправляет потерю высоких vehicle indices в online-сессиях и разделяет подтверждённый Flashback от неподтверждённого reset состояния.

## 24 машины

- Массивы Motion, Lap Data, Car Setup, Car Telemetry, Car Status и Car Damage всегда читаются по всем 24 слотам формата 2026.
- `m_numActiveCars` остаётся диагностикой количества участников и не ограничивает vehicle index.
- Заполненные Participants и Final Classification в слотах 20-23 сохраняются даже при меньшем active count.
- Повторный Analyze восстанавливает ранее потерянные строки из сохранённых raw UDP packets.

## Rewind и reset

- `rewind_events` содержит только подтверждённые `FLBK`.
- `suspected_state_reset_events` содержит обратные изменения frame/session/lap/distance/time без `FLBK`.
- Suspected reset может отделить новую активную ветвь, но не увеличивает `rewind_count` и не присваивает кругу состояние `Rewound`.

## Provisional classification

Если packet 8 отсутствует, приложение явно показывает `PROVISIONAL` и поясняет, что позиции восстановлены из последнего Lap Data. В `final_classification` доступны:

- `classification_source = provisional_latest_lap_data`;
- `classification_is_official = 0`;
- `classification_note` с ограничениями восстановленных результатов.

## Миграция

Schema version: `PRAGMA user_version = 5`. Старые записи необходимо повторно проанализировать, после чего `session.sqlite`, CSV, `chatgpt_pack.sqlite` и ZIP будут пересобраны из исходных raw packets.
