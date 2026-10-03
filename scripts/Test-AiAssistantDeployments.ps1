$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$example = Join-Path $root ".env.example"
$base = Join-Path $root "docker-compose.yml"
$remote = Join-Path $root "docker-compose.openrouter.yml"
$mixed = Join-Path ([System.IO.Path]::GetTempPath()) ("taskmaster-mixed-" + [guid]::NewGuid() + ".yml")
$saved = @{}

function Set-TestEnvironment([string]$Name, [string]$Value) {
    if (-not $saved.ContainsKey($Name)) {
        $saved[$Name] = [Environment]::GetEnvironmentVariable($Name, "Process")
    }
    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
}

function Render-Recipe([string[]]$Files, [string]$MissingSetting = "") {
    $arguments = @("compose", "--project-directory", $root, "--env-file", $example)
    foreach ($file in $Files) { $arguments += @("-f", $file) }
    # Windows PowerShell treats redirected native stderr as PowerShell errors.
    # Capture it and classify using the exit code, including expected rejections.
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & docker @arguments config --format json 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorAction
    if ($MissingSetting) {
        if ($exitCode -eq 0) { throw "OpenRouter-only rendered without required settings." }
        if (($output -join "`n") -notmatch [regex]::Escape($MissingSetting)) {
            throw "Compose failed for a reason other than missing $MissingSetting."
        }
        return
    }
    if ($exitCode -ne 0) { throw "Compose recipe failed to render: $output" }
    # Warnings also invalidate the JSON, including unexpected missing-variable warnings.
    return (($output -join "`n") | ConvertFrom-Json)
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Check-Chat($Recipe, [string]$Provider, [string]$Model, [string]$Round, [string]$Request) {
    $environment = $Recipe.services.'ai-assistant-service'.environment
    Require ($environment.AiModels__Tasks__chat__Provider -eq $Provider) "Wrong chat provider."
    Require ($environment.AiModels__Tasks__chat__Model -eq $Model) "Wrong chat model."
    Require ($environment.AiModels__Tasks__chat__TimeoutSeconds -eq $Round) "Wrong round budget."
    Require ($environment.AiModels__Tasks__chat__RequestTimeoutSeconds -eq $Request) "Wrong request budget."
    Require ($environment.AiModels__Tasks__chat__MaxOutputTokens -eq "1024") "Wrong output cap."
}

function Check-Jev($Recipe, [string]$Mode, [string]$Provider, [hashtable]$Settings) {
    $environment = $Recipe.services.'ai-assistant-service'.environment
    Require ($environment.CategoryResolution__Mode -eq $Mode) "Wrong category rollout mode."
    Require ($environment.CategoryResolution__SemanticProvider -eq $Provider) "Wrong category decision provider."
    foreach ($name in $Settings.Keys) {
        $actual = $environment.("CategoryResolution__Jev__" + $name)
        Require ($actual -eq $Settings[$name]) "Jev setting $name was not forwarded."
    }
}

try {
    # Override inherited shell secrets with the sample's disposable placeholders.
    foreach ($line in Get-Content $example) {
        if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            Set-TestEnvironment $Matches[1] $Matches[2]
        }
    }
    Set-TestEnvironment "COMPOSE_PROFILES" ""
    $emptyJev = @{
        Model = ""; Endpoint = ""; TimeoutSeconds = ""; Policy__Version = ""
        Policy__EvaluatedModel = ""; Policy__EvaluatedCatalogVersion = ""
        Policy__MinimumConfidence = ""; Policy__MinimumProbability = ""
        Policy__MinimumMargin = ""; Policy__DistributionTolerance = ""
    }
    $local = Render-Recipe @($base)
    Check-Chat $local "Ollama" "qwen3:8b" "600" "3000"
    Check-Jev $local "legacy" "ChatModel" $emptyJev
    Require ($null -ne $local.services.ollama -and $null -ne $local.services.'ollama-init') "Local Ollama services missing."
    Require ($null -ne $local.services.'ai-assistant-service'.depends_on.ollama) "Local Ollama dependency missing."
    Require ([string]::IsNullOrEmpty($local.services.'ai-assistant-service'.environment.OpenRouter__ApiKey)) "All-Ollama requires a key."

    Set-TestEnvironment "OPENROUTER_API_KEY" "fake-compose-test-key"
    Set-TestEnvironment "OPENROUTER_CHAT_MODEL" "vendor/fake-tool-model"
    Set-TestEnvironment "OPENROUTER_API_KEY" ""
    Render-Recipe @($base, $remote) -MissingSetting "OPENROUTER_API_KEY"
    Set-TestEnvironment "OPENROUTER_API_KEY" "fake-compose-test-key"
    Set-TestEnvironment "OPENROUTER_CHAT_MODEL" ""
    Render-Recipe @($base, $remote) -MissingSetting "OPENROUTER_CHAT_MODEL"
    Set-TestEnvironment "OPENROUTER_CHAT_MODEL" "vendor/fake-tool-model"
    $openRouter = Render-Recipe @($base, $remote)
    Check-Chat $openRouter "OpenRouter" "vendor/fake-tool-model" "60" "300"
    Check-Jev $openRouter "legacy" "ChatModel" $emptyJev
    Require ($null -eq $openRouter.services.ollama -and $null -eq $openRouter.services.'ollama-init') "OpenRouter-only starts Ollama."
    $dependencies = $openRouter.services.'ai-assistant-service'.depends_on
    Require ($null -eq $dependencies.ollama) "OpenRouter-only retains the Ollama dependency."
    foreach ($dependency in $local.services.'ai-assistant-service'.depends_on.PSObject.Properties.Name) {
        if ($dependency -ne "ollama") {
            Require ($null -ne $dependencies.$dependency) "OpenRouter-only removed dependency $dependency."
        }
    }
    Require ($openRouter.services.'ai-assistant-service'.environment.OpenRouter__ApiKey -eq "fake-compose-test-key") "Shared key is not forwarded."

    Set-TestEnvironment "JEV_CATEGORY_MODE" "catalog-llm"
    Set-TestEnvironment "JEV_CATEGORY_SEMANTIC_PROVIDER" "Jev"
    $missingPolicy = Render-Recipe @($base)
    Check-Jev $missingPolicy "catalog-llm" "Jev" $emptyJev

    $jevVariables = @{
        JEV_CATEGORY_MODEL = "typesafe/jev-1.13"
        JEV_CATEGORY_ENDPOINT = "https://openrouter.ai/api/alpha/decisions"
        JEV_CATEGORY_TIMEOUT_SECONDS = "30"
        JEV_POLICY_VERSION = "reviewed-fixture"
        JEV_POLICY_EVALUATED_MODEL = "typesafe/jev-1.13"
        JEV_POLICY_EVALUATED_CATALOG_VERSION = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        JEV_POLICY_MINIMUM_CONFIDENCE = "0.8"
        JEV_POLICY_MINIMUM_PROBABILITY = "0.8"
        JEV_POLICY_MINIMUM_MARGIN = "0.2"
        JEV_POLICY_DISTRIBUTION_TOLERANCE = "0.001"
    }
    foreach ($name in $jevVariables.Keys) { Set-TestEnvironment $name $jevVariables[$name] }
    $configuredJev = @{
        Model = $jevVariables.JEV_CATEGORY_MODEL
        Endpoint = $jevVariables.JEV_CATEGORY_ENDPOINT
        TimeoutSeconds = $jevVariables.JEV_CATEGORY_TIMEOUT_SECONDS
        Policy__Version = $jevVariables.JEV_POLICY_VERSION
        Policy__EvaluatedModel = $jevVariables.JEV_POLICY_EVALUATED_MODEL
        Policy__EvaluatedCatalogVersion = $jevVariables.JEV_POLICY_EVALUATED_CATALOG_VERSION
        Policy__MinimumConfidence = $jevVariables.JEV_POLICY_MINIMUM_CONFIDENCE
        Policy__MinimumProbability = $jevVariables.JEV_POLICY_MINIMUM_PROBABILITY
        Policy__MinimumMargin = $jevVariables.JEV_POLICY_MINIMUM_MARGIN
        Policy__DistributionTolerance = $jevVariables.JEV_POLICY_DISTRIBUTION_TOLERANCE
    }
    $localJev = Render-Recipe @($base)
    Check-Chat $localJev "Ollama" "qwen3:8b" "600" "3000"
    Check-Jev $localJev "catalog-llm" "Jev" $configuredJev
    Require ($localJev.services.'ai-assistant-service'.environment.OpenRouter__ApiKey -eq "fake-compose-test-key") "Jev with Ollama chat lost the shared key."
    $remoteJev = Render-Recipe @($base, $remote)
    Check-Chat $remoteJev "OpenRouter" "vendor/fake-tool-model" "60" "300"
    Check-Jev $remoteJev "catalog-llm" "Jev" $configuredJev
    Require ($null -eq $remoteJev.services.ollama -and $null -eq $remoteJev.services.'ollama-init') "Jev with OpenRouter chat starts Ollama."
    Require ($remoteJev.services.'ai-assistant-service'.environment.OpenRouter__ApiKey -eq "fake-compose-test-key") "Jev with OpenRouter chat lost the shared key."

    Set-TestEnvironment "JEV_CATEGORY_MODE" "legacy"
    Set-TestEnvironment "JEV_CATEGORY_SEMANTIC_PROVIDER" "ChatModel"
    $rolledBack = Render-Recipe @($base)
    Check-Chat $rolledBack "Ollama" "qwen3:8b" "600" "3000"
    Check-Jev $rolledBack "legacy" "ChatModel" $configuredJev
    Require ($null -ne $rolledBack.services.'ai-assistant-service'.depends_on.ollama) "Jev rollback lost Ollama."

    foreach ($direction in @("remote-chat", "local-chat")) {
        $chatProvider = "OpenRouter"
        $chatModel = "vendor/fake-tool-model"
        $round = "60"
        $request = "300"
        $syntheticProvider = "Ollama"
        $syntheticModel = "qwen3:8b"
        if ($direction -eq "local-chat") {
            $chatProvider = "Ollama"
            $chatModel = "qwen3:8b"
            $round = "600"
            $request = "3000"
            $syntheticProvider = "OpenRouter"
            $syntheticModel = "vendor/fake-tool-model"
        }
        @"
services:
  ai-assistant-service:
    environment:
      AiModels__Tasks__chat__Provider: $chatProvider
      AiModels__Tasks__chat__Model: $chatModel
      AiModels__Tasks__chat__TimeoutSeconds: "$round"
      AiModels__Tasks__chat__RequestTimeoutSeconds: "$request"
      AiModels__Tasks__chat__MaxOutputTokens: "1024"
      OpenRouter__ApiKey: fake-compose-test-key
      AiModels__Tasks__synthetic__Provider: $syntheticProvider
      AiModels__Tasks__synthetic__Model: $syntheticModel
      AiModels__Tasks__synthetic__TimeoutSeconds: "600"
      AiModels__Tasks__synthetic__RequestTimeoutSeconds: "3000"
      AiModels__Tasks__synthetic__MaxOutputTokens: "256"
"@ | Set-Content -Path $mixed -Encoding ASCII
        $recipe = Render-Recipe @($base, $mixed)
        Check-Chat $recipe $chatProvider $chatModel $round $request
        Require ($null -ne $recipe.services.ollama -and $null -ne $recipe.services.'ollama-init') "Mixed recipe lost local Ollama."
        Require ($null -ne $recipe.services.'ai-assistant-service'.depends_on.ollama) "Mixed recipe lost the Ollama dependency."
        $environment = $recipe.services.'ai-assistant-service'.environment
        Require ($environment.AiModels__Tasks__synthetic__Provider -eq $syntheticProvider) "Synthetic provider was not forwarded."
        Require ($environment.AiModels__Tasks__synthetic__Model -eq $syntheticModel) "Synthetic model was not forwarded."
        Require ($environment.AiModels__Tasks__synthetic__TimeoutSeconds -eq "600" -and
            $environment.AiModels__Tasks__synthetic__RequestTimeoutSeconds -eq "3000" -and
            $environment.AiModels__Tasks__synthetic__MaxOutputTokens -eq "256") "Synthetic budgets were not forwarded."
        Require ($environment.OpenRouter__ApiKey -eq "fake-compose-test-key") "Mixed recipe lost the shared key."
    }
    Write-Output "AI deployment recipes validated without starting containers or calling models."
}
finally {
    if (Test-Path -LiteralPath $mixed) { Remove-Item -LiteralPath $mixed }
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], "Process")
    }
}
