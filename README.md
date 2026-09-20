# Cursor Telegram — деплой на Windows

Бот в Telegram управляет **локальным** Cursor CLI (`agent acp`) на этом ПК. Правки и терминал идут в твой checkout. Cloud Agents API не используется. Модель Cursor всё равно hosted.

```
Телефон / Telegram
    → этот .NET worker (polling, входящие порты не нужны)
        → agent.cmd acp
            → файлы в выбранном репозитории
```

ПК должен быть **включён, не спать, с интернетом**, worker запущен. Спящий Windows = мёртвый бот.

Все команды ниже — в **PowerShell**. Пути — Windows, например `C:\src\app`.

---

## 0. Что заранее иметь

- Windows 10/11
- Аккаунт Cursor с платным планом
- Telegram
- Git for Windows (`git` в PATH) — нужен для `/diff`
- Хотя бы один репозиторий уже склонированный на диск

---

## 1. Поставить .NET 8 SDK

В PowerShell:

```powershell
dotnet --version
```

Нужна линейка **8.x**. Если команды нет:

```powershell
winget install Microsoft.DotNet.SDK.8
```

Или установщик: [dotnet.microsoft.com/download/dotnet/8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

**Закрой и заново открой PowerShell**, снова `dotnet --version`.

---

## 2. Поставить Cursor CLI

```powershell
irm 'https://cursor.com/install?win32=true' | iex
```

Клиент обычно ставится сюда (часто **не** в PATH):

```
C:\Users\<ты>\AppData\Local\cursor-agent\agent.cmd
```

Проверка:

```powershell
& "$env:LOCALAPPDATA\cursor-agent\agent.cmd" --version
```

Если файл не найден — поищи:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\cursor-agent" -Recurse -Filter agent.cmd | Select-Object FullName
```

Ключ API:

1. [cursor.com/dashboard/api](https://cursor.com/dashboard/api)
2. **User API Key** → копируй **полный** секрет из диалога создания (не маску из таблицы)

Проверка агента **в репозитории**, до бота:

```powershell
$env:CURSOR_API_KEY = "crsr_вставь_полный_ключ"
cd C:\src\your-repo
& "$env:LOCALAPPDATA\cursor-agent\agent.cmd" -p --force "Reply with one short sentence: hello from CLI"
```

Должен прийти текст. Если нет — бот тоже не заведётся.

---

## 3. Создать Telegram-бота

1. [@BotFather](https://t.me/BotFather) → `/newbot` → токен `123456789:AAH...`
2. Напиши боту `/start` (пока молчит — нормально)
3. [@userinfobot](https://t.me/userinfobot) → свой **числовой** id, например `847123456`

Без numeric id worker не стартует. В группы на первом запуске не добавляй.

---

## 4. Склонировать этот проект

```powershell
cd C:\src
git clone https://github.com/EugeneAkhremenka/Cursor.git
cd Cursor
git checkout cursor/dotnet-telegram-acp-8269
dotnet test
```

Тесты не требуют Telegram. Ожидай 25+ passed.

---

## 5. Секреты (не в git)

User-secrets лежат только в твоём профиле Windows.

```powershell
cd C:\src\Cursor

dotnet user-secrets set Telegram:BotToken "123456789:AAH..." --project src\Cursor.TelegramHost
dotnet user-secrets set Telegram:AllowedUserIds:0 "847123456" --project src\Cursor.TelegramHost
dotnet user-secrets set Cursor:ApiKey "crsr_..." --project src\Cursor.TelegramHost
dotnet user-secrets set Cursor:RepoPath "C:\src\your-repo" --project src\Cursor.TelegramHost
dotnet user-secrets set Cursor:AgentPath "$env:LOCALAPPDATA\cursor-agent\agent.cmd" --project src\Cursor.TelegramHost
```

Несколько репо по имени (необязательно):

```powershell
dotnet user-secrets set Cursor:Repos:app "C:\src\app" --project src\Cursor.TelegramHost
dotnet user-secrets set Cursor:Repos:infra "C:\src\infra" --project src\Cursor.TelegramHost
```

В чате потом: `/repo infra`.

`RepoPath` и пути в `Repos` — **абсолютные** `C:\...`, не `~\proj`.

---

## 6. Запустить worker

Из папки `C:\src\Cursor` (это этот репозиторий бота, не целевой проект):

```powershell
cd C:\src\Cursor
dotnet run --project src\Cursor.TelegramHost
```

В логе:

```
Telegram polling started for 1 allowed user(s)
```

Окно PowerShell **не закрывай**. Ctrl+C — стоп.

| Ошибка | Что сделать |
|---|---|
| `Telegram:BotToken is required` | шаг 5, токен BotFather |
| `AllowedUserIds must contain at least one` | шаг 3–5, числовой id |
| `Could not start Cursor CLI` | шаг 2, явный `Cursor:AgentPath` на `agent.cmd` |
| `Repo path does not exist` | `C:\src\...`, не Linux-путь |

---

## 7. Проверить в Telegram

Пиши **своему** боту с того аккаунта, чей id в allowlist:

1. `/start` — справка
2. `/status` — `cwd` = `C:\src\your-repo`
3. Текст: `Напиши одну строку: бот живой`
4. `/diff`
5. Если задан `Repos:infra`: `/repo infra`, снова `/status`

Тишина → не тот Telegram-аккаунт. В логе: `Ignored Telegram user`.

---

## 8. Команды

| Команда | Действие |
|---|---|
| `/start` | справка |
| текст или `/task …` | промпт текущей сессии |
| `/new` | сбросить сессию, тот же репо |
| `/status` | cwd / session |
| `/cancel` | стоп текущего run |
| `/repo` | текущий checkout и список |
| `/repo <имя или путь>` | сменить репо |
| `/diff` | `git status` + `git diff --stat` |

Пока агент работает — «занято», сначала `/cancel`. Инструменты по умолчанию `allow-always` (как удалённый shell).

---

## 9. Чтобы жило без открытого окна

ПК не должен уходить в сон (Параметры → Система → Питание → экран/сон, или `powercfg /change standby-timeout-ac 0` от админа).

Простой автозапуск после логина — ярлык в автозагрузку.

1. Собери:

```powershell
cd C:\src\Cursor
dotnet publish src\Cursor.TelegramHost -c Release -o C:\src\cursor-telegram-host
```

2. Файл `C:\src\cursor-telegram-host\run.cmd`:

```bat
@echo off
cd /d C:\src\cursor-telegram-host
dotnet Cursor.TelegramHost.dll
```

Секреты user-secrets привязаны к проекту `Cursor.TelegramHost`. Для `publish` они **не подхватываются**. Либо оставь `dotnet run` из исходников, либо задай переменные в `run.cmd`:

```bat
@echo off
cd /d C:\src\cursor-telegram-host
set Telegram__BotToken=123:AAH...
set Telegram__AllowedUserIds__0=847123456
set Cursor__ApiKey=crsr_...
set Cursor__RepoPath=C:\src\your-repo
set Cursor__AgentPath=%LOCALAPPDATA%\cursor-agent\agent.cmd
dotnet Cursor.TelegramHost.dll
```

Права на файл — только ты.

3. Win+R → `shell:startup` → ярлык на `run.cmd`.

Или Планировщик заданий: при входе в систему, от твоего пользователя, действие = `run.cmd`.

Windows Service тут хуже: нет интерактивного профиля, git creds и `%LOCALAPPDATA%` легко отваливаются.

---

## 10. Частые поломки на Windows

- **agent не в PATH** — всегда ставь `Cursor:AgentPath` на `...\cursor-agent\agent.cmd`
- **Invalid User API Key** — скопирована маска из таблицы, не полный ключ
- **`/diff` не работает** — нет Git for Windows, или `git` не в PATH того же пользователя
- **Execution Policy** на `irm \| iex` — открой PowerShell и выполни установщик ещё раз; при блоке: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`
- **Сон / быстрый запуск** — бот перестаёт поллить Telegram
- **Антивирус** режет `agent.cmd` child process — исключение на `%LOCALAPPDATA%\cursor-agent` и папку worker

---

## Важно

Это удалённый shell. Только свой Telegram id, ключи не в git. `/repo C:\...` даёт агенту этот каталог. Код модели уходит в облако Cursor; локально остаются файлы и команды.
