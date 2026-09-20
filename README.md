# Cursor Telegram (локальный ACP)

.NET 8 worker: пишешь боту в Telegram, на машине крутится локальный Cursor CLI (`agent acp`) в выбранном checkout. Cloud Agents API не используется.

```
Telegram → Cursor.TelegramHost → agent acp → локальный репо
```

## Что нужно

- .NET 8 SDK
- Cursor CLI: `curl https://cursor.com/install -fsS | bash`
- API key: [cursor.com/dashboard/api](https://cursor.com/dashboard/api)
- Токен бота от [@BotFather](https://t.me/BotFather) и свой numeric user id ([@userinfobot](https://t.me/userinfobot))

## Запуск

Из корня репозитория:

```bash
export Telegram__BotToken="123:abc"
export Telegram__AllowedUserIds__0="YOUR_NUMERIC_ID"
export Cursor__ApiKey="crsr_..."
export Cursor__RepoPath="/absolute/path/to/repo"
# optional named checkouts for /repo <name>
export Cursor__Repos__app="/absolute/path/to/app"
export Cursor__Repos__infra="/absolute/path/to/infra"

dotnet run --project src/Cursor.TelegramHost
```

Либо user-secrets:

```bash
dotnet user-secrets set Telegram:BotToken "123:abc" --project src/Cursor.TelegramHost
dotnet user-secrets set Telegram:AllowedUserIds:0 "YOUR_NUMERIC_ID" --project src/Cursor.TelegramHost
dotnet user-secrets set Cursor:ApiKey "crsr_..." --project src/Cursor.TelegramHost
dotnet user-secrets set Cursor:RepoPath "/absolute/path/to/repo" --project src/Cursor.TelegramHost
```

`PermissionMode` по умолчанию `allow-always` (инструменты агента не спрашивают подтверждение в чате).

## Команды бота

| Команда | Действие |
|---|---|
| `/start` | справка |
| текст или `/task …` | промпт текущей ACP-сессии |
| `/new` | сбросить сессию |
| `/status` | cwd / session / activity |
| `/cancel` | остановить текущий run |
| `/repo` | текущий checkout и список |
| `/repo <имя\|путь>` | переключить репозиторий (сессия сбрасывается) |
| `/diff` | `git status --short` и `git diff --stat` |

`Cursor:RepoPath` — стартовый cwd. Из Telegram его можно сменить: `/repo infra` по имени из `Cursor:Repos` или `/repo /abs/path` если каталог существует. Следующий промпт поднимет новый `agent acp` уже в этом checkout.

Пока идёт run, второй промпт получает «занято».

## Сборка и тесты

```bash
dotnet test
```

ACP-клиент в тестах говорит с фейковым агентом по JSON-RPC, живой `agent` не нужен.

## Важно

Бот — удалённый shell. Allowlist только своих Telegram id, ключи не коммитить, процесс держать на той же машине, где репозиторий. Модель Cursor по-прежнему hosted; локально остаются файлы и команды.
