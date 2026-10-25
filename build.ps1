$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) { $scriptRoot = (Get-Location).Path }
Push-Location $scriptRoot
try {
    $env:GITHUB_WORKSPACE = $scriptRoot

    # For public repos you can leave this unset; for rate limits or private repos, set a PAT
    # $env:GH_TOKEN = "ghp_xxx"

    $dccmRoot = Join-Path $scriptRoot 'dccm'
    $dccmZip = Join-Path $scriptRoot 'dccm.zip'

    if (-not (Test-Path $dccmRoot)) {
        if (Test-Path $dccmZip) {
            Write-Host "Falling back to local dccm.zip: $dccmZip"
            Expand-Archive -Path $dccmZip -DestinationPath $dccmRoot -Force
        }
        else {
            $headers = @{ Accept = "application/vnd.github+json" }
            if ($env:GH_TOKEN) {
                $headers.Authorization = "Bearer $env:GH_TOKEN"
            }

            $dccmZip = "win-x64.zip"
            $dccmRoot = "./dccm"
            try {
                $url = "https://github.com/dead-cells-core-modding/core/releases/latest/download/win-x64.zip"
                Invoke-WebRequest -Uri $url -OutFile $dccmZip
                Expand-Archive -Path $dccmZip -DestinationPath $dccmRoot -Force
            }
            catch {
                throw "Download failed. Original error: $($_.Exception.Message)"
            }

        }
    }

    $mdkRoot = Join-Path $dccmRoot 'core/mdk'
    if (-not (Test-Path $mdkRoot)) {
        throw "DCCM MDK root not found: $mdkRoot"
    }

    $env:DCCM_MDK_ROOT = $mdkRoot
    Write-Host "DCCM_MDK_ROOT=$mdkRoot"

    dotnet nuget remove source dccm-local 2>$null | Out-Null
    dotnet nuget add source (Join-Path $mdkRoot 'packages') --name dccm-local

    # Generate the localization .mo catalogs from Resourcefile/lang/main.zh.po so the
    # packed res.pak always reflects the latest .po sources. Requires Python 3.
    $genScript = Join-Path $scriptRoot 'Tools/gen_mo.py'
    if (Test-Path $genScript) {
        $python = $null
        foreach ($candidate in @('python', 'python3', 'py')) {
            if (Get-Command $candidate -ErrorAction SilentlyContinue) {
                $python = $candidate
                break
            }
        }
        if (-not $python) {
            throw "Python 3 is required to generate localization files (Tools/gen_mo.py) but was not found on PATH. Install Python 3 or add it to PATH."
        }
        Write-Host "Generating localization .mo files with $python ($genScript)"
        & $python $genScript
        if ($LASTEXITCODE -ne 0) {
            throw "Localization generation failed (exit code $LASTEXITCODE)."
        }
    }
    else {
        Write-Warning "Tools/gen_mo.py not found; skipping .mo generation. Resourcefile/lang/*.mo may be stale."
    }

    dotnet build -c Release
}
finally {
    Pop-Location
}
