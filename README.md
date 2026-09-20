# Cursor Telegram — локальный деплой с нуля

Бот в Telegram управляет **локальным** Cursor CLI (`agent acp`) на этой же машине. Правки и терминал идут в твой checkout. Cloud Agents API не используется. Модель Cursor всё равно hosted.

```
Телефон / Telegram
    → этот .NET worker (polling, без открытых портов)
        → процесс `agent acp`
            → файлы в выбранном репозитории
```

Машина должна быть включена, с сетью и с живым процессом worker. Спящий ноут = мёртвый бот.

---

## 0. Что заранее иметь

- Linux, macOS или Windows (ниже примеры для Linux/macOS; Windows — те же шаги в PowerShell)
- Аккаунт Cursor с платным планом (нужен API key / агент)
- Telegram-аккаунт
- Хотя бы один git-репозиторий уже склонированный на диск

---

## 1. Поставить .NET 8 SDK

Проверка:

```bash
dotnet --version
```

Нужна линейка **8.x**. Если команды нет:

```bash
# Linux / macOS
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

Windows: [скачать SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0) или `winget install Microsoft.DotNet.SDK.8`.

---

## 2. Поставить Cursor CLI и проверить агента

```bash
# Linux / macOS / WSL
curl https://cursor.com/install -fsS | bash
```

Windows (PowerShell): `irm 'https://cursor.com/install?win32=true' | iex`

Проверка бинарника (часто лежит в `~/.local/bin/agent`):

```bash
agent --version
```

Если `command not found`:

```bash
export PATH="$HOME/.local/bin:$PATH"
# и добавь эту строку в ~/.bashrc или ~/.zshrc
```

Ключ API:

1. Открой [cursor.com/dashboard/api](https://cursor.com/dashboard/api)
2. Создай **User API Key**
3. Скопируй **полный** секрет сразу из диалога (в таблице потом только маска)

Проверка, что CLI вообще работает в репозитории:

```bash
export CURSOR_API_KEY="вставь_полный_ключ"
cd /absolute/path/to/your/repo
agent -p --force "Reply with one short sentence: hello from CLI"
```

Должен прийти текст. Если здесь ошибка — бот тоже не заведётся. Почини CLI до шага 6.

Альтернатива ключу: `agent login` в браузере. Для сервиса удобнее `CURSOR_API_KEY`.

---

## 3. Создать Telegram-бота

1. В Telegram открой [@BotFather](https://t.me/BotFather)
2. `/newbot` → имя и username
3. Скопируй токен вида `123456789:AAH...` — это `Telegram:BotToken`
4. Напиши своему боту `/start` (пока он не ответит — это нормально)

Свой numeric **user id** (не username):

1. Напиши [@userinfobot](https://t.me/userinfobot)
2. Скопируй число вроде `847123456` — это `Telegram:AllowedUserIds`

Без этого id worker не стартует. Чужие аккаунты бот игнорирует.

Не добавляй бота в группы на первом запуске.

---

## 4. Склонировать этот проект

```bash
git clone https://github.com/EugeneAkhremenka/Cursor.git
cd Cursor
# если ставишь с ветки PR:
# git checkout cursor/dotnet-telegram-acp-8269
```

Сборка (проверка, что SDK видит проект):

```bash
dotnet test
```

Тесты не требуют Telegram и `agent`. Должно быть 25 passed.

---

## 5. Прописать секреты (не в git)

Самый простой путь — **user-secrets** (файл только у тебя на машине).

```bash
cd /path/to/Cursor

dotnet user-secrets set Telegram:BotToken "123456789:AAH..." --project src/Cursor.TelegramHost
dotnet user-secrets set Telegram:AllowedUserIds:0 "847123456" --project src/Cursor.TelegramHost
dotnet user-secrets set Cursor:ApiKey "crsr_..." --project src/Cursor.TelegramHost
dotnet user-secrets set Cursor:RepoPath "/absolute/path/to/your/repo" --project src/Cursor.TelegramHost
```

`RepoPath` — **абсолютный** путь к checkout, с которым агент стартует. Относительный `~/proj` в env лучше не использовать; в user-secrets `~` развернётся при `/repo`, но для старта надёжнее полный путь.

Несколько репо по имени (необязательно):

```bash
dotnet user-secrets set Cursor:Repos:app "/absolute/path/to/app" --project src/Cursor.TelegramHost
dotnet user-secrets set Cursor:Repos:infra "/absolute/path/to/infra" --project src/Cursor.TelegramHost
```

Потом в чате: `/repo infra`.

### Вариант: переменные окружения

Имена с **двойным** подчёркиванием — так .NET биндит конфиг.

```bash
export Telegram__BotToken="123456789:AAH..."
export Telegram__AllowedUserIds__0="847123456"
export Cursor__ApiKey="crsr_..."
export Cursor__RepoPath="/absolute/path/to/your/repo"
export Cursor__Repos__app="/absolute/path/to/app"
```

Windows PowerShell:

```powershell
$env:Telegram__BotToken="123456789:AAH..."
$env:Telegram__AllowedUserIds__0="847123456"
$env:Cursor__ApiKey="crsr_..."
$env:Cursor__RepoPath="C:\src\your-repo"
```

Не клади токены в `appsettings.json` и не коммить их.

---

## 6. Запустить worker

Из корня репозитория **Cursor** (не из целевого git-проекта):

```bash
dotnet run --project src/Cursor.TelegramHost
```

Успешный старт в логе:

```
Telegram polling started for 1 allowed user(s)
```

Если сразу падает:

| Сообщение | Что сделать |
|---|---|
| `Telegram:BotToken is required` | шаг 5, токен BotFather |
| `Telegram:AllowedUserIds must contain at least one` | шаг 3–5, numeric id |
| `Could not start Cursor CLI` | шаг 2, `agent` в PATH или `Cursor:AgentPath` |
| `Repo path does not exist` | абсолютный `Cursor:RepoPath` |

Оставь терминал открытым. Ctrl+C — стоп.

---

## 7. Проверить в Telegram

Напиши **своему** боту (тому, чей токен в конфиге):

1. `/start` — справка
2. `/status` — `cwd` должен быть твой `RepoPath`
3. `/repo` — список (default + имена из `Repos`)
4. Любой текст, например: `Напиши в чат одну строку: бот живой`  
   Должен прийти ответ агента. В логе worker появится `ACP session ... started`
5. `/diff` — `git status` этого checkout
6. Если настроен `Repos:infra`: `/repo infra`, потом `/status`

Если тишина — ты пишешь не с того Telegram-аккаунта (id не в allowlist). Worker в логе напишет `Ignored Telegram user ...`.

---

## 8. Команды бота

| Команда | Действие |
|---|---|
| `/start` | справка |
| текст или `/task …` | промпт текущей сессии |
| `/new` | сбросить ACP-сессию, тот же репо |
| `/status` | cwd / session / activity |
| `/cancel` | остановить текущий run |
| `/repo` | текущий checkout и список |
| `/repo <имя или путь>` | сменить репо, сессия сбросится |
| `/diff` | `git status --short` и `git diff --stat` |

Пока агент работает, второй промпт получит «занято». Сначала `/cancel`.

`PermissionMode` по умолчанию `allow-always`: агент сам выполняет shell и правки файлов, без кнопок в чате.

---

## 9. Чтобы жило после закрытия терминала

### Linux (systemd user)

`~/.config/systemd/user/cursor-telegram.service`:

```ini
[Unit]
Description=Cursor Telegram local ACP bot

[Service]
WorkingDirectory=%h/src/Cursor
ExecStart=%h/.dotnet/dotnet run --project src/Cursor.TelegramHost --no-launch-profile
Restart=on-failure
RestartSec=5
Environment=DOTNET_ROOT=%h/.dotnet
Environment=PATH=%h/.dotnet:%h/.local/bin:/usr/bin

[Install]
WantedBy=default.target
```

Подставь свой `WorkingDirectory`. Секреты удобнее оставить в user-secrets (они читаются при `dotnet run` из того же проекта) **или** добавить `Environment=Telegram__BotToken=...` в unit (файл будет содержать секреты — права `600`).

```bash
systemctl --user daemon-reload
systemctl --user enable --now cursor-telegram.service
journalctl --user -u cursor-telegram.service -f
```

Чтобы работало после logout: `loginctl enable-linger $USER`.

Прод-вариант без `dotnet run`: `dotnet publish src/Cursor.TelegramHost -c Release -o $HOME/opt/cursor-telegram` и `ExecStart=/usr/bin/dotnet $HOME/opt/cursor-telegram/Cursor.TelegramHost.dll`.

### macOS

`launchd` или просто `tmux` / `screen`:

```bash
tmux new -s cursor-bot
cd ~/src/Cursor
dotnet run --project src/Cursor.TelegramHost
# Ctrl+B, D — отсоединиться
```

Не давай Mac уснуть, если бот должен отвечать с телефона.

---

## 10. Частые поломки

- **Бот молчит, в логе Ignored Telegram user** — в allowlist не тот id. Свой id с @userinfobot, индекс `AllowedUserIds:0`.
- **CLI не находится** — `which agent`; иначе `dotnet user-secrets set Cursor:AgentPath "$HOME/.local/bin/agent" --project src/Cursor.TelegramHost`
- **Invalid User API Key** — скопирован обрезанный ключ из таблицы, не из диалога создания. Создай ключ заново.
- **ACP handshake timeout** — нет сети до Cursor, неверный ключ, или `agent acp` не стартует. Снова шаг 2.
- **Правки не там** — `/status` и `/repo`. Агент работает в cwd сессии, не в папке, откуда запущен worker.

---

## Важно

Это удалённый shell с полным доступом агента к диску в рамках checkout (и `/repo /любой/путь`, если каталог существует). Только свой Telegram id, только своя машина, ключи не в git. Код модели уходит в облако Cursor; локально остаются файлы и команды.
