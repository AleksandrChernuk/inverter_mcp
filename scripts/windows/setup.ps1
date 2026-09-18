<#
  setup.ps1 — развёртывание ultra-MCP (форк ipt-mcp + toolset vent) на цеховой Windows-машине.

  Запуск (PowerShell от обычного пользователя, Inventor можно не закрывать):
      powershell -ExecutionPolicy Bypass -File scripts\windows\setup.ps1 -InventorYear 2026

  Что делает:
    1. Клонирует форк ipt-mcp (или использует уже клонированный -RepoDir).
    2. Копирует overlay/ поверх src/ И применяет 3 обязательные правки базы (идемпотентно, до сборки).
       Silent-режим (CommandDispatcher.cs) остаётся ручным/опциональным — см. overlay\INTEGRATION.md.
    3. Собирает сервер и add-in под выбранный год Inventor (падает при ошибке сборки).
    4. Регистрирует add-in (кладёт .addin с абсолютным путём к DLL в папку Autodesk Add-ins) — по флагу -Install.
       Создаёт ExportRoot, если задан. bin плагина не разбирается (нужны .deps.json/.runtimeconfig.json).

  Требует: .NET SDK (8 для 2025/2026, 10 для 2027), git, установленный Autodesk Inventor выбранного года.
#>
param(
    [int]    $InventorYear = 2026,
    [string] $RepoUrl      = "https://github.com/bimwright/ipt-mcp.git",
    [string] $RepoDir      = "$PSScriptRoot\..\..\_forks\ipt-mcp",
    [string] $OverlayDir   = "$PSScriptRoot\..\..\overlay",
    [Parameter(Mandatory=$true)]
    [string] $CatalogRoot,            # напр. D:\Catalog  -> станет VENT_CATALOG_ROOT (обязателен)
    [string] $ExportRoot   = "",      # напр. D:\DXF_OUT  -> разрешённая папка вывода
    [string] $ReleasePython = "",     # напр. C:\Python312\python.exe; включает deep DXF/XLSX release worker
    [switch] $Install
)

# Защита: не разрешаем корень диска как каталог (слишком широкая видимость).
if ($CatalogRoot -match '^[A-Za-z]:\\?$') { throw "CatalogRoot не должен быть корнем диска ($CatalogRoot). Укажите конкретную папку каталога изделий." }
if (-not (Test-Path $CatalogRoot)) { throw "CatalogRoot не существует: $CatalogRoot" }

$ErrorActionPreference = "Stop"
function Info($m) { Write-Host "[setup] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[warn ] $m" -ForegroundColor Yellow }

# Идемпотентный патчер 3 обязательных правок toolset `vent` в базовые файлы форка.
# Нужен, потому что реализация AddVent(...) без объявления partial даёт CS0759 и плагин не собирается.
# Применяется ПОСЛЕ копирования overlay и ДО сборки. Повторный запуск безопасен (правки не дублируются).
# Silent-режим (CommandDispatcher.cs) НЕ трогаем — он опционален, см. overlay\INTEGRATION.md.
function Apply-VentBaseEdits([string]$srcDir) {
    Info "Правки базы для toolset 'vent' (идемпотентно)"

    # --- src/server/ToolsetFilter.cs: "vent" в KnownToolsets, DefaultOn, WriteCapable ---
    $tf = Join-Path $srcDir "server\ToolsetFilter.cs"
    if (-not (Test-Path $tf)) { throw "Нет $tf — структура базового форка изменилась, патчите вручную по INTEGRATION.md." }
    $c = [System.IO.File]::ReadAllText($tf)
    if ($c -notmatch '"assembly_query",\s*"vent"') {
        if ($c -notmatch '"assembly",\s*"assembly_query"') { throw "ToolsetFilter.cs: якорь KnownToolsets/DefaultOn не найден." }
        $c = $c.Replace('"assembly", "assembly_query"', '"assembly", "assembly_query", "vent"')  # KnownToolsets + DefaultOn
    }
    if ($c -notmatch '"toolbaker_write",\s*"assembly",\s*"vent"') {
        if ($c -notmatch '"toolbaker_write",\s*"assembly"') { throw "ToolsetFilter.cs: якорь WriteCapable не найден." }
        $c = $c.Replace('"toolbaker_write", "assembly"', '"toolbaker_write", "assembly", "vent"')  # WriteCapable
    }
    [System.IO.File]::WriteAllText($tf, $c); Info "  ToolsetFilter.cs — ok"

    # --- src/server/Program.cs: регистрация VentTools ---
    $pg = Join-Path $srcDir "server\Program.cs"
    if (-not (Test-Path $pg)) { throw "Нет $pg." }
    $c = [System.IO.File]::ReadAllText($pg)
    $nl = if ($c.Contains("`r`n")) { "`r`n" } else { "`n" }
    if ($c -notmatch 'typeof\(VentTools\)\)\s*return\s*mcp\.WithTools<VentTools>') {
        $anchor = 'if (toolType == typeof(AssemblyQueryTools)) return mcp.WithTools<AssemblyQueryTools>();'
        if (-not $c.Contains($anchor)) { throw "Program.cs: якорь RegisterToolType не найден." }
        $c = $c.Replace($anchor, $anchor + $nl + '        if (toolType == typeof(VentTools)) return mcp.WithTools<VentTools>();')
    }
    if ($c -notmatch 'Add\("vent"') {
        $anchor2 = 'Add("assembly_query",  typeof(AssemblyQueryTools));'
        if (-not $c.Contains($anchor2)) { throw "Program.cs: якорь ResolveToolTypesForRegistration не найден." }
        $c = $c.Replace($anchor2, $anchor2 + $nl + '        Add("vent",            typeof(VentTools));')
    }
    [System.IO.File]::WriteAllText($pg, $c); Info "  Program.cs — ok"

    # --- src/shared/Plugin/InventorCommandRegistry.cs: вызов AddVent + объявление partial ---
    $reg = Join-Path $srcDir "shared\Plugin\InventorCommandRegistry.cs"
    if (-not (Test-Path $reg)) { throw "Нет $reg." }
    $c = [System.IO.File]::ReadAllText($reg)
    $nl = if ($c.Contains("`r`n")) { "`r`n" } else { "`n" }
    if ($c -notmatch 'AddVent\(d,\s*Add\)') {
        $anchor = 'AddAssemblyQuery(d, Add); // Phase 3 Assembly Query'
        if (-not $c.Contains($anchor)) { throw "InventorCommandRegistry.cs: якорь Build() не найден." }
        $c = $c.Replace($anchor, $anchor + $nl + '        AddVent(d, Add);          // vent-factory toolset')
    }
    if ($c -notmatch 'static partial void AddVent') {
        $anchor2 = 'static partial void AddAssemblyQuery(Dictionary<string, IInventorCommand> d, Action<IInventorCommand> add);'
        if (-not $c.Contains($anchor2)) { throw "InventorCommandRegistry.cs: якорь объявлений partial не найден." }
        $c = $c.Replace($anchor2, $anchor2 + $nl + '    static partial void AddVent(Dictionary<string, IInventorCommand> d, Action<IInventorCommand> add);')
    }
    [System.IO.File]::WriteAllText($reg, $c); Info "  InventorCommandRegistry.cs — ok"
}

# 0. Проверки
foreach ($tool in @("git","dotnet")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool не найден в PATH" }
}
Info ".NET SDK: $(dotnet --version)"

# 1. Клон / обновление форка
if (-not (Test-Path $RepoDir)) {
    Info "Клонирую $RepoUrl -> $RepoDir"
    git clone $RepoUrl $RepoDir
} else {
    Info "Форк уже есть: $RepoDir"
}

# 2. Наложить overlay
Info "Копирую overlay -> src"
Copy-Item "$OverlayDir\src\*" "$RepoDir\src\" -Recurse -Force

# 2b. Применить обязательные правки базы (идемпотентно) — ДО сборки, иначе plugin падает с CS0759.
Apply-VentBaseEdits "$RepoDir\src"
Warn "Silent-режим (CommandDispatcher.cs) НЕ применяется автоматически — опционально, см. overlay\INTEGRATION.md."

# 3. Сборка
Push-Location $RepoDir
try {
    Info "Сборка сервера (Release)"
    dotnet build src\server\Bimwright.Ipt.Server.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Сборка сервера провалилась (exit $LASTEXITCODE)." }

    $pluginProj = Get-ChildItem "src\plugin-inv$($InventorYear.ToString().Substring(2))" -Filter *.csproj -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $pluginProj) { throw "Не нашёл проект плагина для $InventorYear (src\plugin-inv$($InventorYear.ToString().Substring(2)))" }
    Info "Сборка add-in $($pluginProj.Name)"
    dotnet build $pluginProj.FullName -c Release
    if ($LASTEXITCODE -ne 0) { throw "Сборка плагина провалилась (exit $LASTEXITCODE)." }
} finally { Pop-Location }

# 4. Регистрация add-in
# .NET 8 add-in грузится со своими зависимостями (.deps.json/.runtimeconfig.json), поэтому bin НЕ
# разбираем плоско — оставляем как есть, а в Addins кладём только .addin с абсолютным путём к DLL.
# Важно: .addin лежит в ИСХОДНИКАХ плагина (src\plugin-inv$yy), в bin его нет.
if ($Install) {
    $addinsDir = "$env:APPDATA\Autodesk\Inventor $InventorYear\Addins"
    New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null
    $yy = $InventorYear.ToString().Substring(2)
    $pluginDir = Join-Path $RepoDir "src\plugin-inv$yy"

    $dll = Get-ChildItem "$pluginDir\bin\Release" -Recurse -Filter "Bimwright.Ipt.Plugin.Inv$yy.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $dll) { throw "Не найден собранный плагин Bimwright.Ipt.Plugin.Inv$yy.dll (сначала успешная сборка)." }
    $srcAddin = Get-ChildItem $pluginDir -Filter "*.addin" | Select-Object -First 1
    if ($null -eq $srcAddin) { throw "Не найден .addin в $pluginDir." }

    $manifest = [System.IO.File]::ReadAllText($srcAddin.FullName)
    $manifest = $manifest -replace '<Assembly>[^<]*</Assembly>', ("<Assembly>" + $dll.FullName + "</Assembly>")
    $target = Join-Path $addinsDir $srcAddin.Name
    [System.IO.File]::WriteAllText($target, $manifest)
    Info "Add-in зарегистрирован: $target"
    Info "  -> Assembly: $($dll.FullName)"
    Warn "В Inventor подтвердите загрузку неподписанного add-in (Tools -> Add-Ins), затем перезапустите Inventor."
}

# 5. Переменные окружения (для текущего пользователя)
if ($CatalogRoot) { [Environment]::SetEnvironmentVariable("VENT_CATALOG_ROOT", $CatalogRoot, "User"); Info "VENT_CATALOG_ROOT=$CatalogRoot" }
if ($ExportRoot)  {
    if (-not (Test-Path $ExportRoot)) { New-Item -ItemType Directory -Force -Path $ExportRoot | Out-Null; Info "Создана папка вывода: $ExportRoot" }
    [Environment]::SetEnvironmentVariable("BIMWRIGHT_INVENTOR_EXPORT_ROOT", $ExportRoot, "User"); Info "EXPORT_ROOT=$ExportRoot"
}
if ($ReleasePython) {
    if (-not (Test-Path $ReleasePython)) { throw "ReleasePython не найден: $ReleasePython" }
    $releaseScript = (Resolve-Path "$PSScriptRoot\..\..\dxf_tools\release.py").Path
    [Environment]::SetEnvironmentVariable("RELEASE_PYTHON", $ReleasePython, "User")
    [Environment]::SetEnvironmentVariable("RELEASE_SCRIPT", $releaseScript, "User")
    Info "Проверяю offline release worker"
    & $ReleasePython "$PSScriptRoot\..\..\dxf_tools\selftest.py"
    if ($LASTEXITCODE -ne 0) { throw "dxf_tools selftest завершился с кодом $LASTEXITCODE" }
    Info "RELEASE_SCRIPT=$releaseScript"
}

Info "Готово. Сервер: $RepoDir\src\server\bin\Release\...\Bimwright.Ipt.Server.exe"
Info "Дальше пропишите этот путь в конфиг MCP-клиента (см. scripts\windows\claude_mcp_config.example.json)."
