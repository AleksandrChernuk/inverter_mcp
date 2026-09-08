<#
  setup.ps1 — развёртывание ultra-MCP (форк ipt-mcp + toolset vent) на цеховой Windows-машине.

  Запуск (PowerShell от обычного пользователя, Inventor можно не закрывать):
      powershell -ExecutionPolicy Bypass -File scripts\windows\setup.ps1 -InventorYear 2026

  Что делает:
    1. Клонирует форк ipt-mcp (или использует уже клонированный -RepoDir).
    2. Копирует overlay/ поверх src/ и напоминает про 3 правки (INTEGRATION.md).
    3. Собирает сервер и add-in под выбранный год Inventor.
    4. Регистрирует add-in (копирует .addin + dll в папку Autodesk Add-ins) — по флагу -Install.

  Требует: .NET SDK (8 для 2025/2026, 10 для 2027), git, установленный Autodesk Inventor выбранного года.
#>
param(
    [int]    $InventorYear = 2026,
    [string] $RepoUrl      = "https://github.com/bimwright/ipt-mcp.git",
    [string] $RepoDir      = "$PSScriptRoot\..\..\_forks\ipt-mcp",
    [string] $OverlayDir   = "$PSScriptRoot\..\..\overlay",
    [string] $CatalogRoot  = "",     # напр. D:\Catalog  -> станет VENT_CATALOG_ROOT
    [string] $ExportRoot   = "",     # напр. D:\DXF_OUT  -> разрешённая папка вывода
    [switch] $Install
)

$ErrorActionPreference = "Stop"
function Info($m) { Write-Host "[setup] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[warn ] $m" -ForegroundColor Yellow }

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
Warn "Не забудьте 3 правки базовых файлов из overlay\INTEGRATION.md (ToolsetFilter, Program, InventorCommandRegistry)!"

# 3. Сборка
Push-Location $RepoDir
try {
    Info "Сборка сервера (Release)"
    dotnet build src\server\Bimwright.Ipt.Server.csproj -c Release

    $pluginProj = Get-ChildItem "src\plugin-inv$($InventorYear.ToString().Substring(2))" -Filter *.csproj -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $pluginProj) { throw "Не нашёл проект плагина для $InventorYear (src\plugin-inv$($InventorYear.ToString().Substring(2)))" }
    Info "Сборка add-in $($pluginProj.Name)"
    dotnet build $pluginProj.FullName -c Release
} finally { Pop-Location }

# 4. Регистрация add-in
if ($Install) {
    $addinsDir = "$env:APPDATA\Autodesk\Inventor $InventorYear\Addins"
    New-Item -ItemType Directory -Force -Path $addinsDir | Out-Null
    $yy = $InventorYear.ToString().Substring(2)
    $buildOut = Join-Path $RepoDir "src\plugin-inv$yy\bin\Release"
    Info "Копирую add-in в $addinsDir"
    Copy-Item "$buildOut\*\*.addin" $addinsDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$buildOut\*\*.dll"   $addinsDir -Force -ErrorAction SilentlyContinue
    Warn "Перезапустите Inventor, чтобы add-in загрузился."
}

# 5. Переменные окружения (для текущего пользователя)
if ($CatalogRoot) { [Environment]::SetEnvironmentVariable("VENT_CATALOG_ROOT", $CatalogRoot, "User"); Info "VENT_CATALOG_ROOT=$CatalogRoot" }
if ($ExportRoot)  { [Environment]::SetEnvironmentVariable("BIMWRIGHT_INVENTOR_EXPORT_ROOT", $ExportRoot, "User"); Info "EXPORT_ROOT=$ExportRoot" }

Info "Готово. Сервер: $RepoDir\src\server\bin\Release\...\Bimwright.Ipt.Server.exe"
Info "Дальше пропишите этот путь в конфиг MCP-клиента (см. scripts\windows\claude_mcp_config.example.json)."
