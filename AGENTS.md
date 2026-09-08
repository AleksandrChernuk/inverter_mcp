# AGENTS.md — inverter_mcp (корень)

Ultra-MCP для вентзавода: конструктор из чата управляет Autodesk Inventor —
правит параметрию изделий, крутит разворот корпуса, проверяет контуры, делает габаритку,
пакетно выгружает DXF на резку. Плюс оффлайн-слой анализа готовых DXF.

## Может ли оно менять параметрию в .ipt командами/скриптами — ДА
- `inventor_set_parameter name value` → ставит `Parameter.Expression` и `doc.Update()` (база ipt-mcp).
- `inventor_create_parameter` → новый пользовательский параметр.
- `inventor_vent_set_casing_discharge angle hand` → доменная обёртка: гонит параметр разворота корпуса.
- `inventor_send_code` (opt-in, `--enable-send-code`) → произвольный C#-скрипт против Inventor API.
Всё это — команды MCP из чата; правки применяются к активному .ipt/.iam и пересчитываются.

## Ключевые факты (не потерять)
- **Inventor только Windows.** На Mac (машина разработки) C#/Inventor-часть НЕ собирается и НЕ
  запускается: `dotnet` тут нет. На Mac живёт и тестируется только `dxf_tools/` (Python + ezdxf).
- **База:** форк `bimwright/ipt-mcp` (Apache-2.0). Два процесса: сервер (без ссылки на Inventor) +
  add-in внутри Inventor, связь Named Pipe/TCP, команды на STA-потоке.
- **Единицы:** наружу мм и градусы; внутри Inventor см и радианы — конверсия на границе хендлера (×/÷10).
- **Наш вклад:** toolset `vent` (см. `overlay/`) — то, чего в базе нет (габаритка, разворот, домен-проверки, каталог).

## Раскладка
- `dxf_tools/` — кросс-платформенный анализ DXF (работает сейчас, на Mac). См. его AGENTS.md.
- `overlay/` — C#-исходники toolset `vent` для копирования в форк + INTEGRATION.md. См. его AGENTS.md.
- `scripts/windows/` — установка на Windows + пример конфига клиента. См. его AGENTS.md.
- `docs/` — ARCHITECTURE.md, VERSIONS.md, OPERATING.md, пример спецификации. См. его AGENTS.md.

## Правила для агента в этом репозитории
- Не пытайся собирать/запускать .NET на Mac — только читай/пиши C#. Сборка и тесты — на Windows.
- Меняешь оверлей — синхронно обновляй `overlay/INTEGRATION.md` и `docs/VERSIONS.md`.
- Логику проверок перед резкой держи единой между `dxf_tools` (DXF) и `vent_check_part` (модель).
- Единицы наружу — только мм/градусы. Никогда не возвращай COM-объекты, только DTO/JSON.
- Пути экспорта — только под разрешённым корнем (`BIMWRIGHT_INVENTOR_EXPORT_ROOT`).
