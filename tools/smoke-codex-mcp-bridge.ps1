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

$NEW_CLEVERAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $output 'NEW_CLEVER.exe'))

function New-InternalInstance([string]$typeName, [object[]]$arguments) {
    $type = $NEW_CLEVERAssembly.GetType($typeName, $true)
    foreach ($constructor in $type.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic')) {
        if ($constructor.GetParameters().Count -eq $arguments.Count) {
            return $constructor.Invoke([object[]]$arguments)
        }
    }
    throw "No matching constructor found for $typeName."
}

function Invoke-McpRequest($process, [string]$payload) {
    Write-McpLine $process $payload
    $read = $process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(15000)) {
        if (-not $process.HasExited) { $process.Kill() }
        $stderr = $process.StandardError.ReadToEnd()
        $hostError = if ($script:bridgeHost) { $script:bridgeHost.LastError } else { '' }
        throw "MCP bridge response timed out. host: $hostError stderr: $stderr"
    }
    return $read.Result
}

function Write-McpLine($process, [string]$payload) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($payload + "`n")
    $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $process.StandardInput.BaseStream.Flush()
}

$build = New-InternalInstance 'NEW_CLEVER.Model.Agent.BpBuildService' @()
$ev3 = New-InternalInstance 'NEW_CLEVER.Model.Agent.Ev3SessionService' @($build)
$workspace = New-InternalInstance 'NEW_CLEVER.Model.Agent.WorkspaceContextService' @()
$documentation = New-InternalInstance 'NEW_CLEVER.Model.Agent.DocumentationSearchService' @()
$approval = New-InternalInstance 'NEW_CLEVER.Model.Agent.WpfAgentApprovalService' @()
$factory = $NEW_CLEVERAssembly.GetType('NEW_CLEVER.Model.Agent.NEW_CLEVERToolRegistryFactory')
$create = $factory.GetMethod('Create', [Reflection.BindingFlags]'Static,Public,NonPublic')
$registry = $create.Invoke($null, [object[]]@($workspace, $build, $ev3, $documentation, $approval))
$bridgeType = $NEW_CLEVERAssembly.GetType('NEW_CLEVER.Model.Agent.CodexMcpBridgeHost')
$script:bridgeHost = $bridgeType.GetConstructors()[0].Invoke([object[]]@(
    $registry,
    [string](Join-Path $output 'NEW_CLEVER.AgentMcpBridge.exe')))

try {
    $descriptor = $script:bridgeHost.GetDescriptor()
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $descriptor.Command
    $startInfo.Arguments = '--pipe "' + $descriptor.Arguments[1] + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($pair in $descriptor.Environment.GetEnumerator()) {
        $startInfo.EnvironmentVariables[$pair.Key] = $pair.Value
    }
    $startInfo.EnvironmentVariables['NEW_CLEVER_MCP_DIAGNOSTICS'] = '1'

    $bridgeProcess = [Diagnostics.Process]::Start($startInfo)
    try {
        $initialize = Invoke-McpRequest $bridgeProcess '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"NEW_CLEVER-smoke","version":"1"}}}' | ConvertFrom-Json
        Write-McpLine $bridgeProcess '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
        $tools = Invoke-McpRequest $bridgeProcess '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' | ConvertFrom-Json
        $call = Invoke-McpRequest $bridgeProcess '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ev3_status","arguments":{}}}' | ConvertFrom-Json
        $shutdown = Invoke-McpRequest $bridgeProcess '{"jsonrpc":"2.0","id":4,"method":"shutdown","params":{}}' | ConvertFrom-Json
        if (-not $bridgeProcess.WaitForExit(5000)) { $bridgeProcess.Kill() }
        $diagnostics = $bridgeProcess.StandardError.ReadToEnd()

        if ($initialize.result.serverInfo.name -ne 'NEW_CLEVER-codex-tools') {
            throw "Unexpected MCP server identity: $($initialize | ConvertTo-Json -Depth 8 -Compress) diagnostics: $diagnostics"
        }
        if ($tools.result.tools.name -notcontains 'ev3_status') { throw 'ev3_status is missing from MCP tools/list.' }
        if ($call.result.isError -or -not $call.result.structuredContent.success) { throw 'MCP tools/call returned an error.' }
        if ($null -eq $shutdown.result) { throw 'MCP shutdown returned no result.' }
        if ($bridgeProcess.ExitCode -ne 0) { throw "MCP bridge exited with $($bridgeProcess.ExitCode). diagnostics: $diagnostics" }

        [pscustomobject]@{
            Server = $initialize.result.serverInfo.name
            Tools = $tools.result.tools.Count
            HasEv3Status = $true
            CallCode = $call.result.structuredContent.code
            ExitCode = $bridgeProcess.ExitCode
        }
    }
    finally {
        if ($bridgeProcess -and -not $bridgeProcess.HasExited) { $bridgeProcess.Kill() }
        if ($bridgeProcess) { $bridgeProcess.Dispose() }
    }
}
finally {
    $script:bridgeHost.Dispose()
    $ev3.Dispose()
}
