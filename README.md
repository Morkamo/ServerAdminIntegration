# ServerAdminIntegration — руководство по установке

Ubuntu 24.04 x64, PostgreSQL, .NET 10 и уже установленный SCP:SL с LabAPI 1.1.7. Проверено по исходникам на 23.10.2026.

Выполняй разделы 1–9 по порядку. Команды Linux рассчитаны на SSH-консоль от `root`; PowerShell отмечен отдельно. Если интеграция уже установлена, переходи к разделу 11.

Бот работает отдельным процессом и хранит права в локальной БД `server_admin`. Плагин .NET Framework 4.8.1 работает внутри игрового Mono и принимает HTTP-запросы бота. Отдельный Mono ему устанавливать не нужно. С SCP173Integration общих настроек и данных нет.

## 1. Подготовить Ubuntu

```bash
apt update
apt install -y postgresql dotnet-runtime-10.0 curl openssl nano
systemctl enable --now postgresql
pg_lsclusters
dotnet --list-runtimes
command -v dotnet
```

Ожидается PostgreSQL `online` на порту `5432` и `Microsoft.NETCore.App 10.0.x`. В службе используется `/usr/bin/dotnet`; если последняя команда показывает другой путь, впоследствии исправь `ExecStart` в unit-файле.

На Ubuntu 24.04 .NET 10 устанавливается из репозитория Ubuntu. Для запуска достаточно Runtime; SDK нужен на Windows для сборки. [Документация Microsoft](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install?pivots=os-linux-ubuntu-2404).

Используй локальную PostgreSQL. Для этой интеграции открывать порт `5432` наружу не требуется.

## 2. Собрать поставку на Windows

Нужны .NET 10 SDK и игровые библиотеки. В PowerShell:

```powershell
Set-Location 'G:\RiderProjects\ServerAdminIntegration'
.\deploy\build.ps1
```

Если игра находится не по стандартному пути:

```powershell
.\deploy\build.ps1 -GameManagedDirectory 'ПУТЬ\SCPSL_Data\Managed'
```

Скрипт обновит `artifacts`: `bot` — вся поставка бота для Linux x64, `plugins` — одна DLL плагина, `deploy` — конфиги, SQL и systemd-служба. Для переноса используй именно `artifacts`, а не обычный результат сборки Rider в `bin`.

## 3. Создать каталоги и перенести файлы

На Ubuntu:

```bash
id serveradminbot >/dev/null 2>&1 || useradd --system --user-group --home-dir /opt/serveradminbot --no-create-home --shell /usr/sbin/nologin serveradminbot
install -d -m 755 -o root -g root /opt/serveradminbot
install -d -m 700 -o serveradminbot -g serveradminbot /etc/serveradminbot
```

Останови игровой сервер своим обычным способом. Через SFTP перенеси файлы:

| Источник относительно проекта Windows | Назначение на Ubuntu |
| --- | --- |
| Всё содержимое `artifacts\bot\` | `/opt/serveradminbot/` |
| `artifacts\deploy\bootstrap.sql` | `/opt/serveradminbot/bootstrap.sql` |
| `artifacts\deploy\serveradminbot.service` | `/etc/systemd/system/serveradminbot.service` |
| `artifacts\deploy\bot.example.json` | `/etc/serveradminbot/config.json` |
| `artifacts\plugins\ServerAdminIntegration.dll` | Используемый `LabAPI/plugins/global/` либо каталог конкретного игрового порта |

Корень LabAPI смотри в игровых логах. Частый путь: `/home/ИГРОВОЙ_ПОЛЬЗОВАТЕЛЬ/.config/SCP Secret Laboratory/LabAPI`. Размещай DLL только в одной области — `global` или каталог порта. Дополнительных DLL для плагина из этой поставки нет.

После переноса:

```bash
chown -R root:root /opt/serveradminbot
chmod -R a+rX /opt/serveradminbot
chown serveradminbot:serveradminbot /etc/serveradminbot/config.json
chmod 600 /etc/serveradminbot/config.json
chmod 644 /etc/systemd/system/serveradminbot.service
```

Плагин должен быть доступен на чтение пользователю, от которого работает игра.

## 4. Создать отдельную БД — один раз

**Этот шаг только для первой установки.** Скрипт создаёт БД `server_admin` и PostgreSQL-пользователя `server_admin_bot`. Повторно при обновлении его не запускай.

В SSH-консоли введи пароль БД длиной не менее 16 символов. Сохрани его для следующего раздела:

```bash
read -r -s -p 'Пароль PostgreSQL для server_admin_bot: ' SERVERADMIN_DB_PASSWORD
printf '\n'
export SERVERADMIN_DB_PASSWORD
sudo --preserve-env=SERVERADMIN_DB_PASSWORD -u postgres psql -d postgres -v ON_ERROR_STOP=1 -f /opt/serveradminbot/bootstrap.sql
unset SERVERADMIN_DB_PASSWORD
```

Проверь вход, введя тот же пароль:

```bash
psql -h 127.0.0.1 -p 5432 -U server_admin_bot -d server_admin -W -c 'SELECT current_user, current_database();'
```

Ожидается `server_admin_bot | server_admin`. Таблицы бот создаст на шаге 7; сам плагин к PostgreSQL не подключается.

## 5. Создать Discord-бота и заполнить его конфиг

В [Discord Developer Portal](https://discord.com/developers/applications) создай **отдельное приложение** для ServerAdminIntegration:

1. В разделе `Bot` получи токен.
2. В `Installation` включи `Guild Install`, добавь scopes `bot` и `applications.commands`, установи бота на свой Discord-сервер. [Установка приложений Discord](https://docs.discord.com/developers/quick-start/getting-started).
3. Разреши боту просмотр нужного канала и отправку сообщений. Privileged Gateway Intents этому боту не нужны.
4. Включи режим разработчика в Discord и скопируй ID своего Discord-сервера — это `guild_id`.

Сгенерируй отдельный ключ связи с плагином:

```bash
openssl rand -hex 32
```

Сохрани полученную строку: её нужно одинаково указать в JSON бота и YAML плагина.

```bash
nano /etc/serveradminbot/config.json
```

Конфиг для одного сервера:

```json
{
  "token": "DISCORD_BOT_TOKEN",
  "guild_id": 0,
  "database": {
    "host": "127.0.0.1",
    "port": 5432,
    "database": "server_admin",
    "username": "server_admin_bot",
    "password": "DB_PASSWORD",
    "maximum_pool_size": 10
  },
  "servers": {
    "Classic": {
      "ip": "127.0.0.1",
      "port": 7777,
      "api_key": "API_KEY_FROM_OPENSSL"
    }
  }
}
```

Замени `DISCORD_BOT_TOKEN`, `DB_PASSWORD`, `API_KEY_FROM_OPENSSL` и `guild_id: 0` на свои значения. `guild_id` — число без кавычек. JSON не допускает комментарии и запятые после последних элементов.

`serveradminbot` — пользователь Linux, `server_admin_bot` — пользователь PostgreSQL. Рабочий конфиг службы — `/etc/serveradminbot/config.json`.

## 6. Настроить плагин

Один раз запусти игровой сервер с DLL, дождись загрузки LabAPI и останови его. Найди созданный для `ServerAdminIntegration` файл `Config.yml` в конфигурациях LabAPI. При первом запуске ошибка о пустом API-ключе ожидаема.

Заполни файл:

```yaml
is_enabled: true
ip: 127.0.0.1
port: 7777
api_key: 'API_KEY_FROM_OPENSSL'
maximum_concurrent_requests: 32
```

Укажи **тот же ключ**, что в `servers.Classic.api_key` у бота. IP и порт тоже должны совпадать с записью `Classic`. Сохрани доступ игрового пользователя к конфигу.

Запусти игровой сервер. В логе должно появиться:

```text
API listening on 127.0.0.1:7777.
```

`7777` здесь — TCP-порт HTTP API плагина. Если он уже занят другой TCP-службой, выбери свободный порт и измени его в обоих конфигах.

## 7. Создать таблицы бота

```bash
sudo -u serveradminbot /usr/bin/dotnet /opt/serveradminbot/ServerAdminBot.dll /etc/serveradminbot/config.json --init-db
sudo -u postgres psql -d server_admin -c '\dt bot.*'
```

Ожидается сообщение `Таблицы ServerAdminBot готовы.` и таблица `bot.command_permissions`. Дополнительные grants между ботом и плагином не нужны.

## 8. Запустить службу

```bash
systemctl daemon-reload
systemctl enable --now serveradminbot
systemctl status serveradminbot --no-pager
journalctl -u serveradminbot -n 50 --no-pager
```

Ожидается `active (running)` и сообщение `ServerAdminBot запущен.`. Если бот не запустился, исправь причину по журналу и выполни `systemctl restart serveradminbot`.

## 9. Проверить команды и настроить доступ

На Discord-сервере из `guild_id` последовательно вызови:

| Команда | Ожидаемый результат |
| --- | --- |
| `/ping` | `pong` после обращения к Discord API |
| `/pingserver` | `pong` от плагина игрового сервера |
| `/permissionscontrol` | Личная панель настройки прав для Discord Administrator |

Обе ping-команды изначально доступны всем. В панели выбери команду, затем роли или общий доступ `@everyone`. Изменения сохраняются сразу. Роли разбиты на страницы по 25; панель действует 15 минут. Discord Administrator всегда имеет полный доступ.

Если сервер не ответит на запрос бота за 10 секунд, `/pingserver` вернёт timeout. Команда проверяет API плагина; работоспособность игрового потока отдельно не проверяется.

## 10. Добавить другие игровые серверы — при необходимости

В `/etc/serveradminbot/config.json` замени только объект `servers`, сохранив остальные настройки:

```json
"servers": {
  "Classic": { "ip": "127.0.0.1", "port": 7777, "api_key": "CLASSIC_API_KEY" },
  "Modded": { "ip": "127.0.0.1", "port": 7778, "api_key": "MODDED_API_KEY" },
  "OnlyEvents": { "ip": "127.0.0.1", "port": 7779, "api_key": "EVENTS_API_KEY" }
}
```

Названия произвольные и должны быть уникальны без учёта регистра. Подставь настоящие ключи из `openssl rand -hex 32`. В каждом игровом процессе настрой плагину соответствующие IP, порт и ключ. На одном IP каждому экземпляру API нужен отдельный TCP-порт; для разных конфигов используй область LabAPI конкретного игрового порта.

Перезапусти изменённые игровые серверы и бота:

```bash
systemctl restart serveradminbot
```

При нескольких серверах указывай параметр: `/pingserver server:Classic`. При единственном сервере его можно пропустить. Текущая инструкция рассчитана на размещение бота и игры на одном хосте.

## 11. Обновление и диагностика

Для обновления собери новую поставку на Windows, останови `serveradminbot` и игровой сервер, замени содержимое `/opt/serveradminbot/` и DLL плагина. Рабочие конфиги сохраняй. Запусти команду `--init-db` из раздела 7, затем игровой сервер и бота. БД заново не создавай. Если менялся unit-файл, перед запуском выполни `systemctl daemon-reload`.

Основные команды:

```bash
systemctl stop serveradminbot
systemctl start serveradminbot
systemctl restart serveradminbot
journalctl -u serveradminbot -f
ss -ltnp 'sport = :7777'
```

Последняя команда показывает TCP-слушатель API. Для проверки API напрямую введи ключ в SSH-консоли:

```bash
read -r -s -p 'Ключ API плагина: ' API_KEY
printf '\n'
printf 'X-Api-Key: %s\n' "$API_KEY" | curl --noproxy '*' --max-time 10 --fail-with-body --header @- -X POST http://127.0.0.1:7777/api/commands/ping
printf '\n'
unset API_KEY
```

Ожидается `pong`.

| Проблема | Что проверить |
| --- | --- |
| Служба не запускается | Журнал, .NET 10, доступ `serveradminbot` к рабочему конфигу, token и guild_id |
| Ошибка подключения к БД | `pg_lsclusters`, пароль и проверку входа из раздела 4 |
| PostgreSQL сообщает об отсутствии правила доступа | Файл из `SHOW hba_file;`: правило `host server_admin server_admin_bot 127.0.0.1/32 scram-sha-256`, затем reload PostgreSQL. [Правила pg_hba.conf](https://www.postgresql.org/docs/current/auth-pg-hba-conf.html) |
| Нет slash-команд | Бот установлен в нужный Discord-сервер с `applications.commands`; guild_id совпадает с этим сервером |
| `/ping` работает, `/pingserver` — нет | Игра запущена, API слушает нужный TCP-порт, IP и port совпадают в конфигах |
| Ошибка ключа или HTTP 401 | Одинаковый api_key в боте и плагине, затем перезапуск обоих |
| Timeout | API не ответил за 10 секунд; проверь прямой запрос curl и игровой журнал |
| API не запускается из-за занятого адреса | Свободный TCP-порт и отсутствие второй копии плагина |
