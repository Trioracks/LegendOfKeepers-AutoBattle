# v0.6.31 — combat-effect synergies / синергии боевых эффектов

## English

This tester build improves the monster AUTO scorer in four linked areas:

- Periodic health and morale damage is compared as target-relative progress on
  the faster real defeat path, not as raw incompatible values.
- A deterministic periodic effect added by a monster passive is now valued on
  the actual resolved target route, with immunity and stack modifiers checked.
- Active artefacts that can apply an effect after a monster morale attack add
  expected value separately for every hero that actually took morale damage.
  This makes morale AOE attacks correctly benefit from those artefacts.
- A deterministic debuff that increases later monster health or morale damage
  receives a bounded two-target-turn setup value. This lets a Panic-style
  setup be chosen before the stronger follow-up it enables.

Conditional, random, and unrecognised branches are still fail-open: AUTO does
not invent a value for them. This is a tester release; please report the
monster, attack names, visible target statuses, and a screenshot/log when a
choice looks wrong.

## Русский

В этой тестовой сборке улучшена оценка атак монстров сразу в четырёх связанных
местах:

- Периодический урон здоровью и боевому духу теперь сравнивается как
  относительный прогресс к реальному выходу цели из боя, а не как «сырые»
  несопоставимые числа.
- Детерминированный периодический эффект от пассивки монстра учитывается по
  фактическому маршруту целей игры, с проверкой иммунитета и модификаторов
  стаков.
- Активные артефакты, накладывающие эффект после моральной атаки монстра,
  дают ожидаемую ценность отдельно для каждого героя, который действительно
  получил урон боевому духу. Поэтому массовые моральные атаки корректно
  получают синергию с такими артефактами.
- Детерминированный дебафф, усиливающий последующий урон монстров здоровью
  или боевому духу, получает ограниченную ценность на два хода цели. Поэтому
  подготовка наподобие паники может быть выбрана перед усиленной атакой.

Условные, случайные и неразобранные ветки по-прежнему безопасно не
угадываются: AUTO не получает для них придуманной ценности. Это тестовый
релиз; если выбор выглядит неверным, пришлите монстра, названия атак,
видимые статусы целей и скриншот/лог.

