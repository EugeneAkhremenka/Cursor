# Tech debt

## Выбор модели и режима из Telegram

Сейчас модель и режим задаются только в конфиге воркера:

- `Cursor:Model` — id из `agent models` (`auto`, `composer-2.5`, …), уходит в `agent --model`
- `Cursor:Mode` — `plan` | `ask`; пусто или `agent` — флаг не передаём (CLI так и работает в полном agent)

Пустые значения = флаги не передаём, CLI берёт свои дефолты (`auto` / `agent`).

Нужно дать пользователю бота:

- `/model` и `/mode` (и кнопки, как у `/repo`)
- список из `agent models`, смена без правки `appsettings` / user-secrets
- сброс ACP-сессии после смены (модель применяется на старте процесса)
- запоминание выбора по Telegram user id

ACP `session/set_config_option` для модели пока ненадёжен; до починки CLI — перезапуск `agent --model … acp`.
