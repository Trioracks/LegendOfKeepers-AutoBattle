# AUTO Battle v0.6.33 — EN / RU

## English

### Automatic updates

Install this ZIP once. On a normal future launch, AUTO Battle checks the
official GitHub release manifest. When a newer version exists, a visible
Windows notice names the version and explains that the game will restart. It
counts down for six seconds and offers **Update now** and **Skip this version**.

The external helper waits for the game to close, downloads the fixed official
release asset over HTTPS, verifies the published SHA-256 for the archive and
the plugin DLL, replaces **only** the AUTO Battle plugin DLL, then restarts
Legend of Keepers. Saves and original game files are never part of this path.
If the network, manifest, hash, or helper fails, the installed DLL is left in
place and the game starts normally. Its separate diagnostic log is
`BepInEx\LogOutput.AutoUpdate.log`.

### Conditional combat value

Monster AUTO now relies on the game's native current-state preview for direct
conditional damage and target routing, including status-gated bonuses and
eligible bounce routes. Deterministic primary-status gates are checked against
the live target: malus count, strict armour threshold, morale percentage, and
launcher shield. An inactive gate gets no invented future status value.

The evaluator also audits the exact `Attack` database loaded by this game
build. That gives future refinements a factual list of active condition and
synergy fields instead of relying on translated tooltip wording.

## Русский

### Автообновление

Этот ZIP достаточно установить один раз. При следующих обычных запусках AUTO
Battle проверяет официальный манифест GitHub-релиза. Если версия новее,
появляется заметное окно с номером версии и объяснением, что игра сейчас
перезапустится. Есть отсчёт в шесть секунд, кнопки **Update now** и
**Skip this version**.

Внешний помощник ждёт закрытия игры, скачивает фиксированный официальный
релиз по HTTPS, проверяет опубликированные SHA-256 архива и DLL мода,
заменяет **только** DLL AUTO Battle и снова запускает Legend of Keepers.
Сохранения и оригинальные файлы игры в этот процесс не входят. При ошибке
сети, манифеста, хэша или помощника старая DLL остаётся на месте, а игра
запускается как обычно. Отдельный журнал: `BepInEx\LogOutput.AutoUpdate.log`.

### Условная ценность навыков

Для прямого условного урона и маршрутов целей AUTO монстров теперь опирается
на нативный preview текущего состояния игры: в том числе бонусы по статусу и
разрешённые условиями отскоки. Детерминированные условия основного статуса
сверяются с живой целью: число негативных эффектов, строгий порог брони,
процент боевого духа и щит запускающего. Неактивное условие не получает
выдуманной будущей ценности.

Оценщик также делает аудит фактической базы `Attack`, загруженной именно этой
версией игры. Следующие улучшения будут опираться на реальный список
активных условных и синергетических полей, а не на переведённый текст tooltip.
