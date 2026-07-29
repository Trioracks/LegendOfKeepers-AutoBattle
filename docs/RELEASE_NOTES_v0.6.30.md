# v0.6.30 — one-room trap horizon / горизонт одной ловушки

## English

This tester build adds a conservative one-room prediction for the immediate
next normal AOE trap. AUTO reserves a hero for that room only when direct or
periodic trap effects deterministically defeat the hero. The calculation uses
resistance, effect immunity/dodge, stack reduction, static trap
artefact/talent amplification, and known next-trap death passives.

Conditional, random, special, bounce-based, multi-trap, and unresolved routes
are not guessed. They fail open, so AUTO continues to treat the hero as a
current-fight target. Live testing is still required.

## Русский

Тестовая сборка добавляет консервативный прогноз одной следующей обычной
AOE-ловушки. AUTO оставляет героя на эту ловушку только тогда, когда прямой
урон или периодический эффект гарантированно добивает его. Учитываются
сопротивления, иммунитет/уклонение эффекта, снижение стаков, статические
усиления ловушек от артефактов/талантов и известные смертные пассивки для
следующей ловушки.

Условные, случайные, особые, отскакивающие, многоловушечные и неразобранные
ветки не угадываются. AUTO безопасно продолжает считать такого героя целью
текущего боя. Нужна проверка в живом запуске.

## Test package

- Archive: `LegendOfKeepers_AutoBattle_v0.6.30_TESTERS.zip`
- Archive SHA-256:
  `A83E2021552FF4C87ADCB067B8066E336A888AAB86981323172D4AE7AC1B0AFE`
- Plugin SHA-256:
  `5EEAFEE29409E07D7092EE31B2B102F43C069ADFD07C956BCBC1970CE0AA685B`
