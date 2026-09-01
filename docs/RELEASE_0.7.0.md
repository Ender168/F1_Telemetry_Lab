# F1 Telemetry Lab 0.7.0

Дата подготовки: 28 августа 2026 года.

## Результат

Релиз объединяет запланированные этапы 0.6.1, 0.6.2 и 0.7.0. Главный приоритет: отчёты должны быть доказуемо воспроизводимыми из raw UDP, а интерфейс не должен скрывать источник или ограничения результата.

## Исправления для контрольной Melbourne-сессии

Ожидаемая регрессия зафиксирована в `melbourne_3lap_golden.json`:

| Проверка | Ожидается |
|---|---:|
| Lap 1 | Rewound, 4 official FLBK |
| Lap 2 | Complete, 1:24.161 |
| Lap 3 | Complete, 1:23.559 |
| Russell best | 1:21.081 |
| Player best gap | +2.478 s |
| Classification source | provisional latest Lap Data |
| Track length | 5 276 m |

Финишный reset больше не удаляет третий круг. Зоны 4 450-5 276 m используют геометрическую шкалу и не схлопываются в Start/Finish.

## Миграция

- База обновляется идемпотентно через `PRAGMA user_version = 4`.
- Исходные raw packets не удаляются.
- Старую записанную сессию нужно один раз открыть и нажать `Analyze selected session`.
- Повторный анализ построит исправленные круги, classification source, quality dimensions и `car_setups` из сохранённого packet 5.
- После анализа ZIP и `chatgpt_pack.sqlite` пересоздаются с актуальным manifest.

## Car Setup в compact pack

`chatgpt_pack.sqlite` содержит таблицу `car_setups` с initial/config-change snapshots. Для каждой строки доступны wings, differential, alignment, suspension, ARB, ride height, brakes, pressures, ballast и fuel. Packet-level `next_front_wing_value` относится к player row.

Проверить содержимое можно запросом:

```sql
SELECT received_at, car_idx, is_player,
       front_wing, rear_wing, on_throttle, off_throttle,
       front_camber, rear_camber, front_toe, rear_toe,
       front_suspension, rear_suspension,
       front_anti_roll_bar, rear_anti_roll_bar,
       front_ride_height, rear_ride_height,
       brake_pressure, brake_bias, engine_braking,
       rear_left_tyre_pressure, rear_right_tyre_pressure,
       front_left_tyre_pressure, front_right_tyre_pressure,
       ballast, fuel_load, next_front_wing_value
FROM car_setups
ORDER BY overall_frame_identifier, car_idx;
```

## Проверка перед распространением

1. GitHub Actions: Release build, 21 self-tests и xUnit suite должны пройти.
2. Запустить приложение на Windows x64 и повторно проанализировать Melbourne fixture.
3. Проверить UI при 1150x680 и scale 100%, 125%, 150%.
4. Проверить, что provisional badge виден при отсутствии packet 8.
5. Открыть Car Setup и убедиться, что выбран player и виден последний snapshot.
6. Открыть созданный `chatgpt_pack.sqlite` и выполнить SQL выше.
7. Для длинной записи вручную запустить workflow с `run_long_tests = true`.

## Известные ограничения

- Поддерживается только UDP format 2026.
- Точные spline boundaries встроены только для Austria; остальные трассы используют геометрическую центральную линию или telemetry fallback.
- RU/EN переключатель локализует основной shell; названия protocol fields и часть аналитических сообщений остаются английскими, чтобы не менять смысл экспортов.
- Установщик, подпись сборки, иконка, выбранная лицензия и auto-update остаются отдельным последующим релизом.
