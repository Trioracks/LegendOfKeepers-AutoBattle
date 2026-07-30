# Changelog

## v0.6.31
## v0.6.33

- Added an opt-out automatic updater. After the one-time ZIP install, a normal game launch checks the fixed official GitHub release manifest. A visible countdown notice explains the automatic restart and offers **Update now** or **Skip this version**.
- The updater waits for the game process, downloads a fixed release asset over HTTPS, verifies the archive and DLL SHA-256 values, replaces only the AUTO Battle plugin DLL, and restarts the game. Network, manifest, integrity, or helper failures leave the installed DLL untouched and start the game normally.
- Added `BepInEx\\LogOutput.AutoUpdate.log` for the external updater and a per-version suppression state to prevent a failed or skipped release from causing a restart loop.
- Monster AUTO now uses the game's native previews for direct condition-dependent damage and target routes, including effect-gated bonus damage and eligible bounce routes.
- Deterministic primary status gates now verify live target malus count, strict armour threshold, morale percentage, and launcher shield before assigning two-turn periodic value. Inactive gates receive no speculative value.
- Added a read-only audit of the loaded `Attack` database so future condition/synergy support follows the exact game build rather than localized tooltip text.


- Reworked monster AUTO's periodic-effect utility into target-relative progress on the faster defeat axis (health or morale). A morale DoT can now correctly outrank raw health damage when it brings heroes closer to fleeing.
- Added a bounded forecast for deterministic periodic statuses supplied by a monster passive, including the target's live immunity and status-stack modifier.
- Added expected per-hero periodic value for active artefacts that can apply a status after a monster morale attack; AOE attacks receive that value for every hero actually affected by morale damage.
- Added a two-target-turn setup forecast for deterministic effects that amplify later monster health or morale damage, so a Panic-style setup can be preferred before its stronger follow-up.
- Unsupported, conditional, random, or unresolved branches still fail open and receive no invented strategic value.

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
