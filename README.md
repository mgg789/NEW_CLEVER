# CLEV3R IDE 1.8.0

CLEV3R — Windows IDE для программирования роботов LEGO MINDSTORMS EV3 на
Basic Plus (`.bp`, `.bpi`, `.bpm`). Версия 1.8.0 добавляет встроенного
Codex-ассистента, безопасный Git-flow, диагностику кода и табличный просмотр
CSV с графиками.

Сайт проекта: [clev3r.droidje.com](https://clev3r.droidje.com)

## Возможности

- Редактор Basic Plus с подсветкой, автодополнением и диагностикой ошибок.
- Компиляция, загрузка и запуск программ на EV3.
- Встроенная работа с Git: статус, ветки, коммиты, merge, pull и push.
- Codex-чат для проекта: агент видит только активный проект и вызывает
  ограниченные инструменты IDE.
- Библиотека человеческих примеров `ai-examples/**/*.md`, поиск по проекту и
  документации Basic Plus.
- Рендеринг CSV, выделение диапазонов и графики: линии, распределения и
  круговые диаграммы.

## Установка и первый запуск

1. Скачайте и распакуйте релиз для Windows.
2. Запустите `Clever.exe`.
3. Откройте или создайте папку проекта.
4. Если Git отсутствует, откройте вкладку **Git** и нажмите **Install Git** —
   IDE откроет официальный сайт Git for Windows. После установки нажмите
   обновление состояния во вкладке Git.
5. Если Codex ещё не установлен, откройте **AI settings**. Статус покажет
   отсутствие runtime; нажмите **Install Codex**, затем подтвердите установку
   проверенного релиза. IDE принимает только архивы из официального
   `openai/codex` и проверяет SHA-256.
6. В разделе **Account** нажмите **Sign in with ChatGPT**. Откроется
   официальный экран входа; после завершения вернитесь в IDE и обновите
   статус. Там же доступен **Sign out** для проверки входа другим аккаунтом.

## Безопасность AI-интеграции

- Авторизация Codex выполняется через ваш обычный ChatGPT/Codex-сеанс; CLEV3R
  не просит и не сохраняет вручную введённый API-ключ OpenAI для этого flow.
  Учётные данные входа локально ведёт сам Codex runtime.
- `OPENAI_API_KEY`, ключи Azure OpenAI и связанные идентификаторы намеренно не
  передаются дочернему процессу Codex.
- Агент ограничен активным проектом. Вложения, чтение файлов и поиск путей не
  могут выйти за его границы; служебные и бинарные файлы отсекаются.
- Модель не получает прямой сетевой доступ. Операции Git и EV3, которые меняют
  состояние, проходят через scoped-инструменты CLEV3R и требуют подтверждения
  в интерфейсе IDE.
- Библиотека `ai-examples` и код проекта считаются данными, а не инструкциями;
  документация CLEV3R остаётся источником истины для API Basic Plus.

Не добавляйте токены, ключи и личные данные в исходники, чат или примеры.
Выход отключает OAuth-доступ Codex; если OAuth-flow создал отдельный API-ключ,
отзовите его отдельно в OpenAI API dashboard при необходимости.

## Примеры для AI

Положите Markdown-файлы в `ai-examples` рядом с `Clever.exe`. При запуске IDE
рекурсивно индексирует только `.md`; новые примеры будут доступны после
следующего перезапуска. Шаблон и рекомендации находятся в
[`ai-examples/README.md`](ai-examples/README.md).

## Сборка из исходников

Основное приложение — `Clever/Clever.csproj` (WPF, .NET Framework 4.8).
Для обычной разработки откройте `Clever.sln` в Visual Studio с установленным
набором целевых платформ .NET Framework 4.8.

В этой рабочей среде локальная Release-сборка выполняется так:

```powershell
dotnet msbuild Interpreter/Interpreter.csproj /t:Build /p:Configuration=Release

$refs = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netframework.referenceassemblies.net48\1.0.3\build\.NETFramework\v4.8'
& 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe' `
  Clever/Clever.LocalBuild.codex.csproj /t:Build /p:Configuration=Release /p:Platform='AnyCPU' `
  /p:FrameworkPathOverride="$refs" /p:TargetFrameworkDirectory="$refs" `
  /p:OutputPath='bin\CodexRelease\' /p:IntermediateOutputPath='obj\CodexRelease\'
```

Перед пересборкой закройте запущенный `Clever.exe`. Локальные `CodexRelease`
артефакты и `Clever.LocalBuild*.csproj` не входят в коммиты.

## Проверка

```powershell
dotnet test Clev3r.AgentCore.Tests/Clev3r.AgentCore.Tests.csproj -c Release --no-restore
```

Тесты покрывают Basic Plus-примеры, диагностику редактора, CSV-графики,
JSON-RPC-транспорт, обновление Codex и безопасный контекст агента.

## Зависимости и благодарности

- [EV3Basic](https://github.com/c0pperdragon/EV3Basic)
- [ScintillaNET](https://github.com/jacobslusser/ScintillaNET)
- [OpenAI Codex](https://github.com/openai/codex)

Сайт проекта: <https://clev3r.ru> · Telegram: <https://t.me/clev3r_chat>
