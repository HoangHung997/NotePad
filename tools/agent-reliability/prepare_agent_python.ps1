param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$Python = ""
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$requirements=Join-Path $root 'experiments\H2AgentLab\agent-python-requirements.txt'
if(-not (Test-Path -LiteralPath $requirements -PathType Leaf)){throw 'Pinned Agent Python requirements are missing.'}
if([string]::IsNullOrWhiteSpace($Python)){
    $command=Get-Command python -ErrorAction Stop
    $Python=$command.Source
}
$Python=[IO.Path]::GetFullPath($Python)
if(-not (Test-Path -LiteralPath $Python -PathType Leaf)){throw "Python executable not found: $Python"}
$source=Split-Path -Parent $Python
if(-not (Test-Path -LiteralPath (Join-Path $source 'python312.dll') -PathType Leaf)){
    throw 'AR-082 Agent Python source must be CPython 3.12 x64 from actions/setup-python.'
}
& $Python -I -B -c 'import sys,struct; assert sys.version_info[:2]==(3,12); assert struct.calcsize("P")==8; print(sys.version)'
if($LASTEXITCODE){throw 'Python 3.12 x64 identity check failed.'}
& $Python -m pip install --disable-pip-version-check --no-input --requirement $requirements
if($LASTEXITCODE){throw 'Pinned Agent Python dependency install failed.'}
Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
& (Join-Path $root 'experiments\H2AgentLab\Setup-Runtime.ps1') -SourcePython $source -Destination $Destination
if($LASTEXITCODE){throw 'Curated Agent Python runtime preparation failed.'}
$required=@(
    '.ready','python.exe','python312.dll','Lib\encodings\__init__.py',
    'Lib\site-packages\docx\__init__.py','Lib\site-packages\openpyxl\__init__.py',
    'Lib\site-packages\pypdf\__init__.py','Lib\site-packages\pypdfium2\__init__.py',
    'Lib\site-packages\PIL\__init__.py','Lib\site-packages\reportlab\__init__.py',
    'Lib\site-packages\lxml\__init__.py'
)
$missing=@($required|Where-Object{-not (Test-Path -LiteralPath (Join-Path $Destination $_) -PathType Leaf)})
if($missing.Count){throw ('Curated Agent Python runtime is incomplete: '+($missing -join ', '))}
& (Join-Path $Destination 'python.exe') -I -B -c 'import openpyxl,docx,lxml,pypdf,PIL,pypdfium2,reportlab; print("AR082_AGENT_PYTHON_READY")'
if($LASTEXITCODE){throw 'Packaged Agent Python import verification failed.'}
Write-Host "Prepared curated Agent Python: $Destination"
