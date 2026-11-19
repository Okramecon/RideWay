# Собирает пакет для сервера Google Cloud: готовый Docker-образ приложения + docker-compose.yml + скрипты сервера.
# Запуск на Windows из любой папки (нужен Docker Desktop):
#   powershell -ExecutionPolicy Bypass -File deploy\gcp\pack.ps1
# Результат: deploy\gcp\out\rideway-deploy.tar.gz — загрузите его на сервер и выполните команды из deploy\gcp\README.md.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $PSScriptRoot 'out'
$stage = Join-Path $out 'stage'
$bundle = Join-Path $out 'rideway-deploy.tar.gz'

Write-Host '▶ Собираю образ приложения (linux/amd64 — как у e2-micro)' -ForegroundColor Green
docker build --platform linux/amd64 -t rideway-gohome:latest $root
if ($LASTEXITCODE -ne 0) { throw 'Сборка образа не удалась' }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Write-Host '▶ Сохраняю образ в файл' -ForegroundColor Green
docker save rideway-gohome:latest -o (Join-Path $stage 'rideway-image.tar')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось сохранить образ' }

# Скрипты и compose — с переводами строк LF, иначе bash на сервере споткнётся о \r.
$utf8 = New-Object System.Text.UTF8Encoding $false
$files = @((Join-Path $root 'docker-compose.yml')) + (Get-ChildItem (Join-Path $PSScriptRoot 'server') -Filter *.sh | ForEach-Object FullName)
foreach ($f in $files) {
    $text = [IO.File]::ReadAllText($f) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $stage (Split-Path $f -Leaf)), $text, $utf8)
}

Write-Host '▶ Упаковываю' -ForegroundColor Green
if (Test-Path $bundle) { Remove-Item $bundle -Force }
tar -czf $bundle -C $stage .
if ($LASTEXITCODE -ne 0) { throw 'Не удалось упаковать' }
Remove-Item $stage -Recurse -Force

$mb = [math]::Round((Get-Item $bundle).Length / 1MB, 1)
Write-Host ''
Write-Host "Готово: $bundle ($mb МБ)" -ForegroundColor Green
Write-Host 'Дальше: загрузите файл на сервер (SSH-окно Google Cloud → «Upload file») и выполните там:'
Write-Host '  mkdir -p ~/rideway && tar -xzf ~/rideway-deploy.tar.gz -C ~/rideway && bash ~/rideway/install.sh'
