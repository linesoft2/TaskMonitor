# 通过 CNB OpenAPI 创建（或复用）tag 对应的 Release 并上传附件。
# 在 GitHub Actions release.yml 中调用，也可本地手动运行（需环境变量 CNB_TOKEN）。
# API 参考: https://api.cnb.cool (swagger.json) —— POST /{repo}/-/releases, asset-upload-url, verify_url
[CmdletBinding()]
param(
    # 要发布的 tag（必须已存在于 CNB 仓库，调用方负责先推送）
    [Parameter(Mandatory)][string]$Tag,
    # Release 正文（变更日志）
    [string]$Notes = '',
    # 要上传的附件路径，默认按约定取仓库根下的 task_monitor-<tag>-x64.exe
    [string]$AssetPath = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # PS5.1 下 Invoke-RestMethod 进度条极慢
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$api   = if ($env:CNB_API_ENDPOINT) { $env:CNB_API_ENDPOINT } else { 'https://api.cnb.cool' }
$slug  = if ($env:CNB_REPO_SLUG)    { $env:CNB_REPO_SLUG }    else { 'linesoft2/TaskMonitor' }
$token = $env:CNB_TOKEN
if (-not $token) { throw 'CNB_TOKEN is not set' }

if (-not $AssetPath) { $AssetPath = "task_monitor-$Tag-x64.exe" }
$file = Get-Item $AssetPath -ErrorAction SilentlyContinue
if (-not $file) { throw "asset not found: $AssetPath" }

$headers = @{ Authorization = "Bearer $token"; Accept = 'application/json' }

# 1. 查询 release；不存在（404）才创建 —— 重跑流水线时幂等
$release = $null
try {
    $release = Invoke-RestMethod -Headers $headers -Uri "$api/$slug/-/releases/tags/$Tag"
    Write-Host "release for $Tag already exists (id=$($release.id)), reusing"
} catch {
    if ("$($_.Exception.Response.StatusCode)" -ne 'NotFound' -and "$($_.Exception.Response.StatusCode.value__)" -ne '404') { throw }
}
if (-not $release) {
    $body = @{
        tag_name         = $Tag
        name             = $Tag
        body             = $Notes
        target_commitish = $Tag
        make_latest      = 'true'
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
        -Body $body -Uri "$api/$slug/-/releases"
    Write-Host "created release id=$($release.id)"
}

# 2. 申请附件上传地址（同名覆盖，保证重跑可修复）
$uploadReq = @{
    asset_name = $file.Name
    size       = $file.Length
    overwrite  = $true
} | ConvertTo-Json
$upload = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
    -Body $uploadReq -Uri "$api/$slug/-/releases/$($release.id)/asset-upload-url"

# 3. PUT 文件内容到预签名 URL（认证在 URL 内，不带 Bearer）
# 4. 确认上传
# 这两步走 curl.exe（Win10+/GitHub runner 自带）：verify_url 的 asset_path 段是 %2F 编码的，
# .NET System.Uri 会把它解码回 / 再发送，路径变形导致服务端 500 —— Invoke-RestMethod 不可用于这两步。
$curl = "$env:SystemRoot\System32\curl.exe"
if (-not (Test-Path $curl)) { $curl = 'curl.exe' }

$putCode = & $curl -sS -o NUL -w '%{http_code}' -X PUT -H 'Content-Type: application/octet-stream' `
    --data-binary "@$($file.FullName)" $upload.upload_url
if ($LASTEXITCODE -ne 0 -or -not ($putCode -match '^2\d\d$')) { throw "asset PUT failed (curl exit=$LASTEXITCODE, http=$putCode)" }

$confirmCode = & $curl -sS -o NUL -w '%{http_code}' -X POST -H "Authorization: Bearer $token" `
    -H 'Accept: application/json' $upload.verify_url
if ($LASTEXITCODE -ne 0 -or -not ($confirmCode -match '^2\d\d$')) { throw "upload confirmation failed (curl exit=$LASTEXITCODE, http=$confirmCode)" }

$mb = [math]::Round($file.Length / 1MB, 2)
Write-Host "uploaded $($file.Name) ($mb MB) -> CNB release $Tag"
