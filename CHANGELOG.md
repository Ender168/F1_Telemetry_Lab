# Changelog

## 0.10.2

### Advanced China ERS reference

- Добавлена совместимая схема профиля 2 с SOC-коридором по контрольным точкам круга.
- ATTACK и DEFEND получили интенсивность PRESSURE / CRITICAL и расчёт скорости изменения gap за настраиваемое окно.
- Правила учитывают DRS, остаток кругов, финальный выпуск и ценность зоны расходования ERS.
- Решение журналирует текущий SOC-план, локальный минимум, прогноз к следующей точке, DRS, closing rate и круги до финиша.
- `China_Race.json` обновлён до `china-race-advanced-v2`, revision 3, и используется как эталон для будущих трасс.
- Старые профили schema 1 продолжают загружаться без изменений.

## 0.10.1

### ERS tactical modes

- Общий Battle разделён на NEUTRAL, ATTACK и DEFEND.
- Добавлены раздельные пороги атаки и защиты, гистерезис и приоритет двухсторонней борьбы.
- Причины решений получают тактический префикс и сохраняются в `ers_control_events`.

## 0.10.0

### Live Race Engineer

- Добавлены последние три подтверждённых завершённых круга в Live UI.
- Ресурс шин оценивается диапазоном до safe wear limit по худшему колесу, live wear rate и Tyre Sets usable life.
- Позиция после пит-стопа рассчитывается диапазоном по текущим live gaps и track-specific pit loss.
- ERS advisor показывает целевой energy corridor участка и рекомендацию Critical, Save, On plan или Aggressive.
- Каждая оценка имеет Low, Medium, High или Unavailable confidence и объяснение в tooltip.
- Добавлен отдельный перемещаемый always-on-top overlay.

### Track profiles and learning

- Добавлены отдельные Race Engineer JSON-профили в `<root>/race_profiles/` и исходный профиль China R03.
- После анализа приложение обучает tyre wear по compound и green-flag pit loss для конкретной трассы.
- Повторный анализ одной `session_uid` не удваивает обучающие наблюдения.
- Незавершённые, invalid, pit и SC/VSC laps не участвуют в tyre learning.

### SQLite-only data and RAR5

- Schema version повышена до 6.
- ERS audit и profile snapshot перенесены из CSV/JSON в `ers_control_events` и `ers_profile_snapshots`.
- Добавлены `race_engineer_laps`, `race_profile_snapshots`, `analysis_runs` и `race_learning_observations`.
- Автоматическое создание CSV, JSON manifest и `chatgpt_pack.sqlite` отключено.
- WinRAR создаёт RAR5 с `-m5` и ровно одним `session.sqlite`, затем выполняет test command.
- ZIP fallback отсутствует. Ошибка упаковки не затрагивает рабочую базу.
- Добавлен ручной небольшой `race_summary.xlsx` с листами Laps, Tyres, Pits, ERS и Quality.

### Regression coverage

- Добавлены тесты completed-lap gate, tyre range/confidence, pit range, ERS aggression, XLSX OpenXML structure и строгого RAR contract.
- Старые тесты обновлены под SQLite-only storage.

## 0.9.1

### ERS Live input correction

- F7/F8 теперь отправляются через аппаратные scan-коды вместо virtual-key событий.
- Key-down удерживается 80 мс и завершается отдельным key-up без блокировки UDP receive loop.
- `telemetry-confirmed` записывается только после появления ожидаемого промежуточного `ersDeployMode` в Packet 7.
- Если профиль сменил цель до подтверждения, команда маркируется `feedback-superseded`, а не ложным подтверждением.
- После повторного отсутствия UDP-подтверждения Live безопасно блокируется с явным указанием, что F1 отвергла синтетический ввод.
- В `ers_profile_used.json` сохраняются input backend и длительность удержания клавиши.
- Добавлен регрессионный тест на короткую ERS-зону, завершившуюся до подтверждения режима.

## 0.9.0

### Track-specific ERS Autopilot prototype

- Добавлены режимы Off, Dry-run и Live. Dry-run является безопасным значением по умолчанию.
- Алгоритмы вынесены в отдельные JSON-профили в `<root>/ers_profiles/`; пользовательские файлы не перезаписываются обновлениями приложения.
- Первый профиль `china-race-r03-v1` реализует базовый план R03 для Китая с Medium как основным режимом, контролируемыми зонами восстановления и ограниченными Boost после T13, T16 и T4.
- Решение учитывает заряд в процентах, гистерезис восстановления, газ, скорость, позицию на круге и интервалы до машин впереди и сзади.
- Переходы выполняются по одному шагу через F7/F8 и продолжаются только после подтверждения нового `ersDeployMode` из Packet 7.
- Правила, пересекающие линию старта, сохраняют единое ограничение длительности и не запускаются повторно из-за смены номера круга.

### Safety and audit

- Live-ввод жёстко заблокирован в online-сессиях, при включённом игровом ERS Assist, паузе, spectator mode, Safety Car/VSC/formation lap, пит-лейне, мокрой погоде для dry-профиля, устаревшей телеметрии и несовпадении длины трассы.
- Клавиши отправляются только в активное окно F1 25. F12 блокирует ввод до следующей записи.
- Новый Overtake Mode 2026 не автоматизируется.
- Каждая сессия получает `ers_control_log.csv` и точный снимок `ers_profile_used.json`; оба файла включаются в analysis pack версии 2.
- Добавлены parser, decision-engine и safety regression tests.

## 0.7.1

### Online telemetry correctness

- Фиксированные массивы packet 0/2/5/6/7/10 разбираются по всем 24 vehicle slots независимо от `m_numActiveCars`.
- `m_numActiveCars` больше не используется как верхняя граница vehicle index: online grid может иметь разрывы после disconnect, включая player в `car_idx 21-23`.
- Participants и Final Classification сохраняют заполненные высокие индексы даже при меньшем active count.
- Добавлен интеграционный тест sparse online grid: 20 active cars, player `car_idx 21`, 24 строки во всех основных производных таблицах.

### Timeline semantics

- `rewind_events` теперь содержит только подтверждённые события с официальным `FLBK`.
- Обратный session/frame/lap/distance counter без `FLBK` сохраняется отдельно в `suspected_state_reset_events` и не делает круг `Rewound`.
- Formation-to-race reset может отделить новую ветвь круга, не создавая ложный Flashback.

### Classification

- При отсутствии packet 8 `final_classification` содержит явные `classification_is_official=0` и `classification_note`.
- Интерфейс прямо сообщает, что позиции восстановлены из последнего Lap Data и какие официальные поля недоступны.
- Schema version повышена до 5.

## 0.7.0

### Interface

- Верхний уровень сокращён до пяти разделов: Live, Sessions, Analysis, Race и Settings.
- Lap Compare и Track Map объединены в Analysis; Track Detail встроен в выбранную зону карты.
- Overview, Car Setup, Driver Compare, Stints и Pits объединены в Race.
- Добавлена постоянная строка контекста выбранной сессии.
- Sessions показывает карточки metadata, размер, длительность, качество, setup snapshots и источник классификации; raw manifest вынесен в технический Expander.
- Выбор сессии автоматически загружает доступных гонщиков и отчёты.
- Lap Compare по умолчанию показывает Reference + Compare, дополнительные серии открываются явно; добавлены metric help и интерактивный cursor tooltip.
- Убраны дубли вида `YOU YOU` и синтетические `C08 #08`.
- Псевдонимы сохраняются одной операцией с индикатором несохранённых изменений.
- Settings теперь сохраняет port, root, Auto ZIP, retention, RU/EN shell language и UI scale; измеренная частота отделена от рекомендуемых 60 Hz.
- Retention имеет preview, исключает выбранную сессию и требует отдельного подтверждения удаления.

### Car Setup

- Добавлен официальный 50-байтовый parser packet 5 для формата 2026.
- Таблица `car_setups` хранит исходную конфигурацию и изменения для активных машин.
- Сохраняются wings, differential, engine braking, camber/toe, suspension, ARB, ride height, brakes, tyre pressures, ballast, fuel load и next front wing.
- Неизменившиеся 2 Hz setup-пакеты дедуплицируются.
- Setup включён в `car_setups.csv`, manifest, экран приложения, `chatgpt_pack.sqlite` и ZIP.
- Schema version повышена до 4.

## 0.6.2

### Regression and scale

- Добавлен xUnit-проект с независимыми тестами, не удаляя 21 старый self-test.
- Golden fixture фиксирует четыре FLBK, завершённые круги 2 и 3, лучший круг игрока 1:23.559 и gap 2.478 s до Russell.
- Добавлены тесты pit/compound stints, packet 8 с penalty/DNF/multiple tyre stints, packet-aware sequence rules, schema migration и compact setup pack.
- Добавлен opt-in часовой fixture на 72 000 packet 6 с проверкой времени, памяти, размера raw DB, compact DB и ZIP.
- Анализ читает raw packets потоково, учитывает active car count и создаёт индексы перед тяжёлыми join.
- Derived tables строятся в staging SQLite; исходная база заменяется атомарно только после успешного анализа.
- Введены формальные миграции через `PRAGMA user_version`.
- GitHub Actions запускает self-tests, xUnit regression suite и опциональный long-session benchmark.

## 0.6.1

### Data correctness

- Официальный `FLBK` стал основным сигналом rollback; эвристика оставлена как fallback с явной причиной.
- Финишный reset больше не считается flashback. Последний круг подтверждается через `last_lap_time_ms`, driver/result status и покрытие дистанции.
- Геометрическая дистанция Track Map пересчитывается по X/Z и нормализуется на `track_length_m`; corner markers переносятся в ту же шкалу.
- Условный 12 m corridor больше не описывается как точная граница трассы.
- Packet sequence диагностика учитывает multi-event packet 3, per-car packet 11 и terminal frame 0.
- Качество разделено на Capture, Session completeness и Analysis confidence; неизвестный missing-frame estimate показывается как not calculated.
- Fuel, wear, ERS и degradation aggregates используют только завершённые непитовые круги и показывают `n`.
- Manifest обновляется после анализа и упаковки.
- Classification явно маркируется как `official_udp` или `provisional_latest_lap_data`.

## 0.6.0

- UDP receive loop отделён от SQLite writer ограниченной очередью.
- Добавлены WAL, пакетные транзакции, safe Stop/Close, `session_segments` и `recording_quality`.
- Парсер приведён к структурам F1 2026; добавлены overall frame, packet 8, FLBK target и damage.
- Введены lap states, Race Report, Driver Compare, Stint/Pit reports и Track Map.

## 0.5.3

- Race Report, Driver Compare, Stint Report и Pit Report.
- Track Map и базовый экспорт телеметрии.
