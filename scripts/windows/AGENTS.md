# AGENTS.md — scripts/windows/

Развёртывание на цеховой Windows-машине.

## Файлы
- `setup.ps1` — клон форка ipt-mcp, наложение `overlay/`, сборка сервера и add-in под год Inventor,
  (по `-Install`) регистрация add-in, установка env `VENT_CATALOG_ROOT` / `BIMWRIGHT_INVENTOR_EXPORT_ROOT`.
- `claude_mcp_config.example.json` — пример конфига MCP-клиента (путь к `Server.exe`, `--toolsets`, env).

## Запуск
```powershell
powershell -ExecutionPolicy Bypass -File setup.ps1 -InventorYear 2026 -Install `
  -CatalogRoot D:\Catalog -ExportRoot D:\DXF_OUT
```

## Правила / примечания
- TFM зависит от года: .NET 8 (2025/2026), .NET 10 (2027), net48 (2022–2024). setup.ps1 берёт проект
  плагина по папке `plugin-inv<YY>`.
- Скрипт НЕ делает 3 правки базовых файлов автоматически — он про них предупреждает. Их надо сделать
  один раз вручную (см. `overlay/INTEGRATION.md`) или допиши сюда патчер.
- Ничего не требует прав администратора: add-in регистрируется в `%APPDATA%\Autodesk\Inventor <год>\Addins`.
- Меняешь имя toolset/тулзов — синхронно правь `--toolsets` в примере конфига.
