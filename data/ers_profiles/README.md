# Профили автоматического управления ERS

Каждая трасса получает отдельный JSON-профиль. Приложение выбирает профиль по паре `track_id` + `session_types` в начале записи. Изменения читаются при следующем запуске записи, пересборка приложения не нужна.

Начиная с v0.10.3 профиль схемы 2 постоянно проверяет SOC-баланс и сочетает `NEUTRAL`, `ATTACK`, `DEFEND` с энергетическими состояниями `CRITICAL`, `CONSERVE`, `BALANCED`, `SURPLUS`.

`China_Race.json` является первым продвинутым эталоном схемы 2. Он имеет `profile_id: china-race-advanced-v2` и `profile_revision: 4`. Штатный China-профиль предыдущих версий обновляется один раз, а его копия сохраняется рядом как `China_Race.json.pre-0.10.3.bak`. Профиль с другим пользовательским `profile_id` не перезаписывается.

## Как добавить следующую гонку

1. Скопируйте `China_Race.json` под новым именем, например `Japan_Race.json`.
2. Задайте уникальный `profile_id`, правильные `track_id`, `track_name`, `track_length_m` и `session_types`.
3. Оставьте пользовательские названия участков в `segment`, например `T13 -> T14`.
4. Настройте внутренние границы `start_m` и `end_m` по шкале круга из телеметрии.
5. Сначала выберите `Тест без ввода` и проверьте журнал решений.
6. Только после проверки включайте `Управление`.

Если несколько файлов подходят одной трассе и типу сессии, выбирается наибольший `selection_priority`. При равном приоритете используется лексикографически первый `profile_id`.

## Три тактических режима

| Поле | Назначение |
|---|---|
| `attack_gap_ms` | Максимальный отрыв до машины впереди для входа в `ATTACK` |
| `defend_gap_ms` | Максимальный отрыв до машины сзади для входа в `DEFEND` |
| `tactical_exit_margin_ms` | Гистерезис выхода из ATTACK/DEFEND, чтобы режим не дрожал около порога |
| `defend_priority_margin_ms` | Если соперники близко и впереди, и сзади, DEFEND получает приоритет только когда задняя машина ближе минимум на эту величину |
| `battle_gap_ms` | Старый общий порог, сохранённый для совместимости с условиями `battle` и `battleOrHighBattery` |

При одновременной борьбе впереди и сзади приложение обычно сохраняет `ATTACK`. В `DEFEND` оно переключается, когда задняя угроза заметно ближе согласно `defend_priority_margin_ms`.

## Продвинутая схема 2

- `tactical` задаёт пороги `PRESSURE` и `CRITICAL`, окно расчёта closing rate и расширение зоны при быстром сближении.
- `energy_plan.checkpoints` задаёт `target_pct` и `minimum_pct` в контрольных точках круга. Между точками коридор интерполируется.
- `conserve_enter_margin_pct` и `conserve_exit_margin_pct` задают гистерезис состояния `CONSERVE`.
- `learning_rate` управляет адаптацией прогноза по фактическому изменению SOC между сегментами.
- `conserve_mode` задаёт базовый ERS mode при прогнозируемом дефиците.
- `deployment_value` задаёт ценность зоны от 0 до 1. Низкоценная зона требует большего запаса сверх локального минимума.
- `minimum_energy_surplus_pct` разрешает необязательное расходование только при реальном избытке относительно плана.
- `drs_requirement` принимает `any`, `active` или `inactive`.
- `closingLaps` и `finalLap` позволяют ослаблять сохранение энергии в конце гонки.
- `final_lap_minimum_battery_pct` задаёт отдельный нижний порог финального выпуска, при этом `critical_battery_pct` остаётся жёстким предохранителем.

## Основные поля правила

| Поле | Назначение |
|---|---|
| `segment` | Понятное название участка трассы, которое видно в статусе и журнале |
| `start_m`, `end_m` | Внутренние координаты триггера; если начало больше конца, участок пересекает линию старта |
| `target_mode` | `none`, `medium`, `hotlap` или `boost` |
| `condition` | `always`, `criticalBattery`, `lowBattery`, `neutral`, `attack`, `defend`, `battle`, `highBattery`, `attackOrHighBattery`, `defendOrHighBattery` или `battleOrHighBattery` |
| `minimum_battery_pct` | Минимальный заряд для срабатывания |
| `minimum_throttle_pct` | Минимальное положение газа |
| `minimum_speed_kph` | Минимальная скорость |
| `maximum_active_ms` | Жёсткий лимит длительности одного включения |
| `maximum_deploy_pct` | Максимальный фактический расход SOC за одно включение |
| `once_per_lap` | Не разрешать повторное включение на том же круге |
| `priority` | Более высокое число побеждает при пересечении правил |

Порог `recovery_enter_pct` включает режим восстановления, а `recovery_exit_pct` выключает его. Разница между ними создаёт отдельный гистерезис батареи.

Каждая запись решения теперь начинается с `[NEUTRAL]`, `[ATTACK]` или `[DEFEND]`. Поэтому тактический режим остаётся виден и в статусе приложения, и в `ers_control_events.reason` внутри `session.sqlite` без изменения схемы базы данных.

## Ограничения прототипа

- Автоматизируются только стандартные режимы ERS. Новый Overtake Mode 2026 не изменяется.
- Live-ввод полностью заблокирован в сетевой игре, во время паузы, пит-лейна, режима зрителя, Safety Car/VSC/formation lap, при устаревшей телеметрии и для мокрого профиля, если `dry_only` включён.
- Live-ввод требует выключенного игрового `ERS Assist` и активного окна F1 25.
- F12 немедленно блокирует дальнейший ввод до следующей записи.
- F7 и F8 должны быть назначены в F1 25 на уменьшение и увеличение стандартного режима ERS.

## Manual pit-lap rules (0.10.11)

Triangle + Circle toggles runtime state `pitLapBurn`. Use `"condition": "pitLapBurn"` in `rules` to select pit-lap deployment. No JSON file is modified by the button.
All rule zones, priorities, target modes, thresholds, timers, deployment budgets and once-per-lap limits remain effective. These rules use their own `minimum_battery_pct` instead of the normal energy-plan reserve. An explicit `minimum_energy_surplus_pct` is still enforced when an energy plan applies.
Outside matching pit rules, normal rules run. Profiles without this condition behave as before. `pit_lap_strategy` is not an executable configuration block.
The flag clears on pit entry, lap/session changes, session end, recording stop or confirmed flashback. Disabling the flag does not reset once-per-lap limits.

## Player traction conditions (0.10.12)

The controller reads MotionEx (packet 13, 2026 version 1) for the local player only. Runtime values include front-wheel angle (radians), yaw rate (angular velocity Y, rad/s), both rear slip angles and both rear slip ratios. Rear limits use the maximum absolute value across RL/RR. The JSON profile is not rewritten.

Add a top-level configuration like this (initial validation values, not proven safe limits):

```json
"traction_gates": {
  "hotlap": {
    "maximum_front_wheels_angle_rad": 0.10,
    "maximum_yaw_rate_rad_s": 0.65,
    "maximum_rear_slip_angle_rad": 0.15,
    "maximum_rear_slip_ratio": 0.15,
    "stable_for_ms": 200,
    "maximum_sample_gap_ms": 100,
    "maximum_data_age_ms": 150
  },
  "boost": {
    "maximum_front_wheels_angle_rad": 0.08,
    "maximum_yaw_rate_rad_s": 0.50,
    "maximum_rear_slip_angle_rad": 0.12,
    "maximum_rear_slip_ratio": 0.12,
    "stable_for_ms": 300,
    "maximum_sample_gap_ms": 100,
    "maximum_data_age_ms": 150
  }
}
```

A rule can supply its own `traction_gate` object, replacing the target mode's entire gate. Omitted metric limits are not checked; at least one is required. Thresholds are strict absolute upper bounds. Timings are configurable (defaults: stable 250 ms, maximum gap 100 ms, data age 150 ms).
Checks apply to increases to Hotlap/Boost, including Hotlap to Boost, pit rules and high default modes. Missing/stale/non-finite required values prevent an increase; reductions are unaffected. Profiles without gates retain previous behavior. This is an onset gate, not automatic traction control or an automatic reduction after a slide.
Stable time requires fresh sequential MotionEx samples and elapsed session/arrival time; duplicates cannot advance it. Gaps and unsafe readings restart the interval; pauses, lap/session changes and flashbacks clear it.
A waiting rule is not selected and does not consume once_per_lap or start its deployment timer. After a rule has started, its normal limits still apply while a subsequent increase/retry waits. Already-sent input cannot be recalled.
The ERS status detail and audit reason report the target and blocking metric or stable-duration progress.
The packaged `data/ers_profile_examples/Japan_Race_traction.json` is an opt-in candidate based on the supplied Japan profile. Copy it to the recording root's `ers_profiles` folder and restart recording to activate it. It has a higher selection priority than the supplied Japan profile. It is not installed automatically; validate in Dry-run first. No session evidence proves that these initial thresholds prevent every loss of grip.

## Tyre-specific profiles (0.10.13)

Optional top-level filters:

| Intended tyres | JSON filter |
| --- | --- |
| Soft | `"visual_tyre_compounds": [16]` |
| Medium | `"visual_tyre_compounds": [17]` |
| Hard | `"visual_tyre_compounds": [18]` |
| Intermediate | `"actual_tyre_compounds": [7], "dry_only": false` |
| Full Wet | `"actual_tyre_compounds": [8], "dry_only": false` |
| Any standard slick visual type | `"visual_tyre_compounds": [16, 17, 18]` |

Actual compound IDs and visual types are different namespaces: an actual slick compound can have different Soft/Medium/Hard labels on different tracks. Use `actual_tyre_compounds` to target physical compounds C1-C6 (or other game compounds) by their UDP IDs; use `visual_tyre_compounds` for the race's tyre designation. These filters accept unique integer IDs 1-255, including future compounds; unknown runtime values do not match a declared filter. An omitted list means unrestricted; an empty list is invalid. When both lists are present, both must match.

Selection first filters track/session, weather compatibility (`dry_only`) and tyre compatibility. Dry-only profiles are excluded in wet weather or on actual/visual Inter or Wet tyres. Then profiles with more declared tyre filters take precedence, followed by `selection_priority` and profile ID. Both-list profiles rank ahead of single-list profiles; either single-list profile ranks ahead of a generic profile. Missing compound data never matches a tyre-specific profile. Generic profiles remain the fallback; `dry_only: false` alone is not a wet-only filter.

Each tyre profile can have independent rules, energy plans, SOC targets and traction gates. The application does not invent wet strategy values or copy Inter calibration to Full Wet.
The controller reselects using fresh local-player Car Status data, rejects delayed status frames, waits for a fresh status packet after pit exit, resets rule/energy/traction state on compound changes even when the generic profile stays the same, and audits previous/new profile and actual/visual compounds. The ERS status profile ID shows the active strategy. Flashbacks require fresh session and compound data again. The pit-lap flag still cancels at pit entry/flashback; if a compound changes while it remains active, the new profile's pit rules apply.
