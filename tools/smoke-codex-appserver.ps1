param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\NEW_CLEVER\bin\CodexRelease')
)

$ErrorActionPreference = 'Stop'
$output = (Resolve-Path $OutputDirectory).Path
foreach ($name in @(
    'System.Memory.dll',
    'System.Buffers.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encodings.Web.dll',
    'Microsoft.Bcl.AsyncInterfaces.dll',
    'System.Threading.Tasks.Extensions.dll',
    'System.Text.Json.dll',
    'NEW_CLEVER.AgentCore.dll',
    'Interpreter.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $output $name))
}
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $output 'NEW_CLEVER.exe'))

function New-InternalInstance([string]$typeName, [object[]]$arguments) {
    $type = $assembly.GetType($typeName, $true)
    foreach ($constructor in $type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic')) {
        if ($constructor.GetParameters().Count -eq $arguments.Count) {
            return $constructor.Invoke([object[]]$arguments)
        }
    }
    throw "No matching constructor found for $typeName."
}

$build = New-InternalInstance 'NEW_CLEVER.Model.Agent.BpBuildService' @()
$ev3 = New-InternalInstance 'NEW_CLEVER.Model.Agent.Ev3SessionService' @($build)
$workspace = New-InternalInstance 'NEW_CLEVER.Model.Agent.WorkspaceContextService' @()
$documentation = New-InternalInstance 'NEW_CLEVER.Model.Agent.DocumentationSearchService' @()
$approval = New-InternalInstance 'NEW_CLEVER.Model.Agent.WpfAgentApprovalService' @()
$factory = $assembly.GetType('NEW_CLEVER.Model.Agent.NEW_CLEVERToolRegistryFactory')
$registry = $factory.GetMethod('Create', [Reflection.BindingFlags]'Static,Public,NonPublic').Invoke(
    $null,
    [object[]]@($workspace, $build, $ev3, $documentation, $approval))
$runtime = New-InternalInstance 'NEW_CLEVER.Model.Agent.CodexRuntimeManager' @(
    [string](Join-Path $output 'Resources\Codex\runtime\codex.exe'))
$bridge = New-InternalInstance 'NEW_CLEVER.Model.Agent.CodexMcpBridgeHost' @(
    $registry,
    [string](Join-Path $output 'NEW_CLEVER.AgentMcpBridge.exe'))
$backend = New-InternalInstance 'NEW_CLEVER.Model.Agent.CodexAppServerBackend' @($runtime, $bridge, $registry)

try {
    $startTask = $backend.StartAsync([Threading.CancellationToken]::None)
    if (-not $startTask.Wait(60000)) { throw 'Codex backend start timed out.' }
    $start = $startTask.Result
    if (-not $start.Success) { throw $start.Message }

    $modelsTask = $backend.ListModelsAsync([Threading.CancellationToken]::None)
    if (-not $modelsTask.Wait(30000)) { throw 'Codex model/list timed out.' }
    $threadsTask = $backend.ListThreadsAsync(
        (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
        $null,
        $null,
        [Threading.CancellationToken]::None)
    if (-not $threadsTask.Wait(30000)) { throw 'Codex thread/list timed out.' }

    [pscustomobject]@{
        Connected = $start.Success
        State = $start.Data.ConnectionState
        Version = $start.Data.Version
        LoggedIn = $start.Data.Auth.LoggedIn
        AuthMode = $start.Data.Auth.AuthMode
        Models = @($modelsTask.Result.Data).Count
        ProjectThreads = @($threadsTask.Result.Data.Threads).Count
    }
}
finally {
    $backend.Dispose()
    $bridge.Dispose()
    $runtime.Dispose()
    $ev3.Dispose()
}
