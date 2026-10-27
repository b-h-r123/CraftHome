param([ValidateRange(1024, 65535)][int]$Port = 5080)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$dotnetCommand = Join-Path $PSScriptRoot '.local/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetCommand)) {
    $dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
}
if (-not ((& $dotnetCommand --list-sdks) -match '^10\.')) {
    throw '需要 .NET 10 SDK。请按 README 安装后重新启动。'
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Write-Host "CraftHome: http://127.0.0.1:$Port"
Write-Host '首次登录信息：src/CraftHome.Web/.local/first-login.txt（首次启动时生成）'
& $dotnetCommand run --project src/CraftHome.Web/CraftHome.Web.csproj --no-launch-profile --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE
