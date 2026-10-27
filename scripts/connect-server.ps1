param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9.-]+$')][string]$Server,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$User = 'root',
    [ValidateRange(1, 65535)][int]$SshPort = 22,
    [ValidateRange(1024, 65535)][int]$LocalPort = 5081
)
$ErrorActionPreference = 'Stop'
Write-Host "连接后在浏览器打开 http://127.0.0.1:$LocalPort。关闭此终端会关闭隧道。"
& ssh -N -p $SshPort -o ExitOnForwardFailure=yes -L "${LocalPort}:127.0.0.1:5080" "${User}@${Server}"
