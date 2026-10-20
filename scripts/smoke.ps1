param([int]$Port = 5187, [string]$AppDirectory = '', [switch]$NoSettings, [switch]$SqlFailure)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$appRoot = if ($AppDirectory) { (Resolve-Path -LiteralPath $AppDirectory).Path } else { Join-Path $taskRoot 'src/ApolloBreakdowns' }
$dll = Join-Path $appRoot $(if ($AppDirectory) { 'ApolloBreakdowns.dll' } else { 'bin/Debug/net10.0/ApolloBreakdowns.dll' })
if (-not (Test-Path -LiteralPath $dll)) { throw 'Run dotnet build first.' }
$logs = Join-Path $taskRoot '.test-output'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$originalEnvironment = $env:ASPNETCORE_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$serverProcess = $null
$base = "http://127.0.0.1:$Port"
$admin = @{ 'X-Demo-User-Id' = '1' }
$operator = @{ 'X-Demo-User-Id' = '2' }
function Assert($condition, $label) { if (-not $condition) { throw $label }; Write-Output "PASS $label" }
function Request($path, $headers, $method = 'GET', $body = $null) {
    $arguments = @{ Uri = "$base/api/app$path"; Headers = $headers; Method = $method; SkipHttpErrorCheck = $true }
    if ($null -ne $body) { $arguments.ContentType = 'application/json'; $arguments.Body = $body | ConvertTo-Json -Depth 10 -Compress }
    Invoke-WebRequest @arguments
}
try {
    $serverArguments = @($dll, '--urls', $base)
    if ($SqlFailure) {
        $serverArguments += @('--Telegram:Mode', 'Webhook', '--Telegram:BotToken', 'test-token', '--ConnectionStrings:BotDatabase', '"Server=127.0.0.1,1;Database=unavailable;User Id=test;Password=test;Connect Timeout=1;Encrypt=False"')
    } elseif (-not $NoSettings) { $serverArguments += @('--Telegram:Mode', 'Demo') }
    $serverProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $serverArguments -WorkingDirectory $appRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'server.log') -RedirectStandardError (Join-Path $logs 'server-error.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 160; $attempt++) {
        try { if ((Invoke-WebRequest "$base/health" -TimeoutSec 1).StatusCode -eq 200) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 250
    }
    Assert $ready 'Server starts without required external services in Production'
    Assert ((Invoke-WebRequest "$base/miniapp/" -SkipHttpErrorCheck).Content.Contains('APOLLO')) 'React entry is served independently of storage initialization'
    $storageReady = $false
    for ($attempt = 0; $attempt -lt 160; $attempt++) {
        if ((Invoke-RestMethod "$base/health").storageReady) { $storageReady = $true; break }
        Start-Sleep -Milliseconds 250
    }
    Assert $storageReady 'Storage initializes or falls back without blocking HTTP'
    $health = Invoke-RestMethod "$base/health"
    Assert ($health.storage -eq 'memory' -and $health.demo) 'Health reports temporary storage and browser test mode'
    Assert ((Invoke-WebRequest "$base/miniapp/" -SkipHttpErrorCheck).Content.Contains('APOLLO')) 'React entry is served'
    Assert ((Request '/me' @{}).StatusCode -eq 200) 'Test mode opens in browser without Telegram or demo headers'
    Assert ((Invoke-WebRequest "$base/health/ready" -SkipHttpErrorCheck).StatusCode -eq 200) 'Readiness separately confirms usable storage'
    $badRequest = Invoke-WebRequest "$base/api/app/breakdowns" -Method POST -Body '{broken' -ContentType 'application/json' -SkipHttpErrorCheck
    Assert ($badRequest.StatusCode -eq 400) 'Malformed request is rejected without killing the server'
    Assert ((Invoke-WebRequest "$base/health").StatusCode -eq 200) 'HTTP server remains alive after a request error'
    Assert ((Request '/admin/notifications' $operator).StatusCode -eq 403) 'Operator cannot access administration'
    $options = (Request '/options' $operator).Content | ConvertFrom-Json
    Assert ($options.Count -eq 176) 'Workbook and repair choices loaded'
    $part = $options | Where-Object { $_.kind -eq 'part' -and -not $_.requiresDescription } | Select-Object -First 1
    $node = $options | Where-Object { $_.kind -eq 'node' -and $_.parentId -eq $part.id } | Select-Object -First 1
    $fault = $options | Where-Object { $_.kind -eq 'fault' -and $_.parentId -eq $node.id -and -not $_.requiresDescription } | Select-Object -First 1
    $body = @{
        requestId = [guid]::NewGuid().ToString(); person = 'Тестовий оператор'; description = ''
        siteId = ($options | Where-Object kind -eq 'site' | Select-Object -First 1).id
        shiftId = ($options | Where-Object kind -eq 'shift' | Select-Object -First 1).id
        machineId = ($options | Where-Object kind -eq 'machine' | Select-Object -First 1).id
        partId = $part.id; nodeId = $node.id; faultId = $fault.id
        repairMethodId = ($options | Where-Object { $_.kind -eq 'repairMethod' -and $_.requiresReserve } | Select-Object -First 1).id
        hasReserveApollo = $null
    }
    Assert ((Request '/breakdowns' $operator 'POST' $body).StatusCode -eq 400) 'Reserve answer is mandatory for involvement'
    $body.hasReserveApollo = $false
    $created = Request '/breakdowns' $operator 'POST' $body
    Assert ($created.StatusCode -eq 200) 'Valid report saved'
    $report = $created.Content | ConvertFrom-Json
    $retried = (Request '/breakdowns' $operator 'POST' $body).Content | ConvertFrom-Json
    Assert ($retried.id -eq $report.id) 'Repeated request returns same report'
    $own = (Request '/breakdowns' $operator).Content | ConvertFrom-Json
    Assert ($own.total -eq 1 -and $null -eq $own.items[0].restoredAt -and $own.items[0].hasReserveApollo -eq $false) 'Open report visible with reserve answer'
    $otherBody = $body.Clone(); $otherBody.requestId = [guid]::NewGuid().ToString()
    $other = (Request '/breakdowns' $admin 'POST' $otherBody).Content | ConvertFrom-Json
    Assert (((Request '/breakdowns' $operator).Content | ConvertFrom-Json).total -eq 1) 'Operator cannot see another user reports'
    Assert ((Request "/breakdowns/$($other.id)/restore" $operator 'POST').StatusCode -eq 403) 'Operator cannot close another user report privately'
    $closed = (Request "/breakdowns/$($report.id)/restore" $operator 'POST').Content | ConvertFrom-Json
    $again = (Request "/breakdowns/$($report.id)/restore" $operator 'POST').Content | ConvertFrom-Json
    Assert ($null -ne $closed.restoredAt -and $closed.restoredAt -eq $again.restoredAt) 'Restoration time is fixed and idempotent'
    $newOption = @{ kind = 'site'; parentId = $null; label = 'Тестова дільниця'; active = $true; requiresDescription = $false; requiresReserve = $false; sortOrder = 999 }
    $added = (Request '/admin/options' $admin 'POST' $newOption).Content | ConvertFrom-Json
    Assert ($added.id -gt 0) 'Admin can add a field option'
    $newOption.label = 'Перейменована дільниця'; $newOption.active = $false; $newOption.version = $added.version
    $updated = Request "/admin/options/$($added.id)" $admin 'PUT' $newOption
    Assert ($updated.StatusCode -eq 200) 'Admin can rename and disable option'
    Assert ((Request "/admin/options/$($added.id)" $admin 'PUT' $newOption).StatusCode -eq 409) 'Stale catalog edit rejected'
    $activeOptions = (Request '/options' $operator).Content | ConvertFrom-Json
    Assert (-not ($activeOptions | Where-Object id -eq $added.id)) 'Disabled option hidden from operator'
    Assert (((Request '/admin/audit' $admin).Content | ConvertFrom-Json).Count -eq 2) 'Catalog changes audited'
    $csv = Request '/admin/export' $admin
    Assert ($csv.StatusCode -eq 200 -and $csv.Content.Contains('Простій, сек')) 'CSV contains automatic downtime'
    Assert ((Invoke-WebRequest "$base/api/telegram/webhook" -Method POST -Body '{}' -ContentType 'application/json' -SkipHttpErrorCheck).StatusCode -eq 404) 'Demo webhook rejects Telegram callbacks'
    Write-Output 'All HTTP smoke checks passed.'
} finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id }
    $env:ASPNETCORE_ENVIRONMENT = $originalEnvironment
}
