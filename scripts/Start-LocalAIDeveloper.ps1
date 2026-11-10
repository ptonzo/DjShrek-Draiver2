param(
    [ValidateRange(1, 8)]
    [int]$Iterations = 2,
    [switch]$InstallScheduledTask
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

if ($InstallScheduledTask) {
    $scriptPath = $PSCommandPath
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Iterations 2"
    $trigger = New-ScheduledTaskTrigger -Daily -At "9:00AM"
    $trigger.Repetition.Interval = "PT2H"
    $trigger.Repetition.Duration = "PT12H"
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 3)
    Register-ScheduledTask -TaskName "DjShrek Local AI Developer" -Action $action -Trigger $trigger -Settings $settings -Description "Runs local Ollama/Aider development cycles for DjShrek-Draiver." -Force | Out-Null
    Write-Host "Scheduled task installed: daily 09:00, repeats every 2 hours for 12 hours while Windows is available."
    exit 0
}

foreach ($command in @("git", "dotnet", "ollama", "aider")) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' was not found. Follow docs/LOCAL-AI-AGENT.md."
    }
}

$ollama = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 5
if (-not ($ollama.models | Where-Object { $_.name -like "qwen2.5-coder:7b*" })) {
    throw "Model qwen2.5-coder:7b is not installed. Run: ollama pull qwen2.5-coder:7b"
}

git fetch origin
if ($LASTEXITCODE -ne 0) { throw "git fetch failed." }

$branch = "ai/local-development"
$existing = git branch --list $branch
if ($existing) {
    git switch $branch
} else {
    git switch -c $branch origin/main
}
if ($LASTEXITCODE -ne 0) { throw "Could not switch to $branch." }

$project = ".\src\DjShrek.Draiver.App\DjShrek.Draiver.App.csproj"
$lastBuildLog = Join-Path $env:TEMP "DjShrek-Draiver-local-agent-build.log"
$lastCyclePassed = $false

for ($i = 1; $i -le $Iterations; $i++) {
    $lastCyclePassed = $false
    Write-Host ""
    Write-Host "===== Local AI development cycle $i / $Iterations =====" -ForegroundColor Cyan

    $previousFailure = ""
    if (Test-Path $lastBuildLog) {
        $previousFailure = (Get-Content $lastBuildLog -Raw)
        if ($previousFailure.Length -gt 12000) { $previousFailure = $previousFailure.Substring($previousFailure.Length - 12000) }
    }

    $prompt = @"
You are the local implementation agent for DjShrek-Draiver Cyber Club.
Read AGENTS.md, README.md, docs/ARCHITECTURE.md and docs/ROADMAP.md.
Choose and implement ONE highest-priority unfinished roadmap item that can be completed safely in this repository.
Do not merely audit, re-test, or rewrite already implemented features. Make a concrete code change.
Preserve existing UI and architecture. Follow every safety rule in AGENTS.md.
Do not run shell commands. Do not edit generated build output. Do not mark a roadmap item complete unless its implementation is genuinely complete.
If the previous build failed, first fix the reported compile/self-test failure, then continue only if time/context allows.
At the end, briefly state changed files and any remaining limitations.

Previous build/self-test output (may be empty):
$previousFailure
"@

    & aider --model "ollama_chat/qwen2.5-coder:7b" --no-suggest-shell-commands --no-auto-commits --message $prompt
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Aider returned exit code $LASTEXITCODE. Continuing to the build gate."
    }

    dotnet restore $project
    if ($LASTEXITCODE -ne 0) { "RESTORE FAILED with exit code $LASTEXITCODE" | Set-Content $lastBuildLog; continue }

    dotnet build $project -c Release --no-restore 2>&1 | Tee-Object -FilePath $lastBuildLog
    if ($LASTEXITCODE -ne 0) { Write-Warning "Release build failed; next cycle will receive the log."; continue }

    dotnet run --project $project -c Release --no-build -- --self-test 2>&1 | Tee-Object -FilePath $lastBuildLog
    if ($LASTEXITCODE -ne 0) { Write-Warning "Self-test failed; next cycle will receive the log."; continue }

    $lastCyclePassed = $true
    Write-Host "Build and self-test passed for cycle $i." -ForegroundColor Green
}

if (-not $lastCyclePassed) {
    throw "The last development cycle did not pass Release build and self-test. Changes were left in the local branch and were NOT pushed."
}

$changes = git status --porcelain
if ($changes) {
    git add -A
    if ($LASTEXITCODE -ne 0) { throw "git add failed." }
    git -c user.name="DjShrek Local AI Developer" -c user.email="local-ai-developer@users.noreply.github.com" commit -m "chore: local AI development iteration"
    if ($LASTEXITCODE -ne 0) { throw "Commit failed." }
    git push -u origin $branch
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Changes are committed locally but push failed. Configure GitHub authentication and run: git push -u origin $branch"
    } else {
        Write-Host "Changes pushed to https://github.com/ptonzo/DjShrek-Draiver2/tree/$branch" -ForegroundColor Green
    }
} else {
    Write-Host "No source changes to commit."
}
