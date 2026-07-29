# Changelog

## v0.6.30

- Added a conservative one-room trap horizon for monster AUTO. When the next room is a fully known normal AOE trap, AUTO can leave a hero alive only if that trap deterministically defeats the hero.
- The proof includes direct damage/morale, elemental resistance, trap-reduction passives, a pure periodic trap effect, effect immunity/dodge, effect-stack reduction, static trap artefact/talent amplification, and queued next-trap effects from living monsters' death passives.
- Conditional, random, special, bounce, multi-trap, and unrecognised queued effects are fail-open: AUTO keeps treating the hero as a current target.

## v0.6.29

- Исправлено выключение AUTO: нажатие яркой иконки теперь фиксируется до
  штатного скрытия иконки, поэтому выключенное (тусклое) состояние сразу
  очищает все ожидания AUTO для атаки монстра, заклинания хозяина и бедствия.
- Уже подтверждённое штатное действие не отменяется посередине анимации, но
  после выключения AUTO не выбирает ни одного следующего действия.

## v0.6.28

- AUTO больше не считает полезным прямой урон по герою, которого гарантированно
  добьёт уже активный детерминированный периодический эффект на следующем ходу.
- Массовая атака получает тактический приоритет, если штатно снимает активный
  `IgnoreAttack`/уворот с одной цели и одновременно повреждает другого героя.

## v0.6.27

- Исправлена привязка `DisasterBar.Refresh`: AUTO снова получает видимые
  варианты бедствия после их загрузки интерфейсом игры.
- Выбор бедствия остаётся отдельной фазой с нативным preview HP/боевого духа
  и штатным callback активной плитки.
- Подготовлен тестовый Steam-пакет с включённой видимостью AUTO-кнопки.
