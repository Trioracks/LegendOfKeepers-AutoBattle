# Сборка исходников

Этот репозиторий публикует исходники мода, но намеренно не содержит игровые
файлы и сгенерированные IL2CPP interop-сборки.

Для локальной сборки нужна законно установленная совместимая Steam-версия
игры, BepInEx IL2CPP x86 `6.0.0-be.785` и созданные BepInEx interop-библиотеки.
Поместите нужные ссылки из своей установки в `src/LegendOfKeepers.BattleEventInspector/lib/`
с именами, указанными в `.csproj`, затем выполните:

```powershell
dotnet build src/LegendOfKeepers.BattleEventInspector/LegendOfKeepers.BattleEventInspector.csproj -c Release
```

Не добавляйте файлы игры, interop-сборки, кэши или журналы в Git.
